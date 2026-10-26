using System.Diagnostics;
using GhostShow.Core.Model;
using GhostShow.Core.Output;

namespace GhostShow.Core.Engine;

/// <summary>
/// Plays one scene on a dedicated timing thread. The scene's tracks are snapshotted at Start();
/// live editing while running is a later step.
/// </summary>
public sealed class SceneRunner : IDisposable
{
    readonly Scene _scene;
    readonly IDmxOutput _dmx;
    readonly IAudioPlayer _audio;
    readonly string _baseDir;
    readonly int _fps;
    Thread? _thread;
    volatile bool _stop;

    public event Action? Finished;
    public bool IsRunning => _thread is { IsAlive: true };

    public SceneRunner(Scene scene, IDmxOutput dmx, IAudioPlayer audio, string baseDir, int fps = 44)
    {
        _scene = scene; _dmx = dmx; _audio = audio; _baseDir = baseDir; _fps = Math.Clamp(fps, 1, 44);
    }

    public void Start()
    {
        if (IsRunning) return;
        _stop = false;
        _thread = new Thread(Run) { Name = $"scene:{_scene.Name}", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join();
        _audio.StopAll();
    }

    /// <summary>Blocks until the scene ends on its own (non-looping) or Stop() is called.</summary>
    public void Wait() => _thread?.Join();

    void Run()
    {
        var channelTracks = _scene.Tracks.OfType<ChannelTrack>().ToArray();
        var audioTracks = _scene.Tracks.OfType<AudioTrack>().ToArray();
        var buffers = SceneRenderer.CreateBuffers(channelTracks);

        var sw = Stopwatch.StartNew();
        long freq = Stopwatch.Frequency;
        long interval = freq / _fps;
        long durTicks = _scene.DurationMs * freq / 1000;
        long origin = 0, tick = 0;
        int lastMs = -1;

        void PlayClip(AudioClip c) =>
            _audio.Play(Path.IsPathRooted(c.File) ? c.File : Path.Combine(_baseDir, c.File), c.Volume);

        void Emit(int ms)
        {
            SceneRenderer.Render(channelTracks, ms, buffers);
            foreach (var (u, buf) in buffers) _dmx.Send(u, buf);
        }

        try
        {
            while (!_stop)
            {
                WaitUntil(sw, ++tick * interval);
                if (_stop) break;

                long el = sw.ElapsedTicks - origin;
                if (el >= durTicks)
                {
                    SceneRenderer.ForEachDueClip(audioTracks, lastMs, _scene.DurationMs, PlayClip);
                    if (!_scene.Loop) { Emit(_scene.DurationMs); break; }
                    origin += durTicks;
                    el = sw.ElapsedTicks - origin;
                    lastMs = -1;
                }

                int ms = (int)(el * 1000 / freq);
                SceneRenderer.ForEachDueClip(audioTracks, lastMs, ms, PlayClip);
                Emit(ms);
                lastMs = ms;
            }
        }
        finally
        {
            // Safe state: blackout. (A real rig will want a per-channel "safe value".)
            foreach (var (u, buf) in buffers) { Array.Clear(buf); _dmx.Send(u, buf); }
            Finished?.Invoke();
        }
    }

    void WaitUntil(Stopwatch sw, long targetTicks)
    {
        while (!_stop)
        {
            long rem = targetTicks - sw.ElapsedTicks;
            if (rem <= 0) return;
            if (rem * 1000.0 / Stopwatch.Frequency > 2) Thread.Sleep(1);
            else Thread.SpinWait(50);
        }
    }

    public void Dispose() { Stop(); }
}
