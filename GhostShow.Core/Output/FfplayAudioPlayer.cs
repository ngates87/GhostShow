using System.Diagnostics;

namespace GhostShow.Core.Output;

/// <summary>
/// Placeholder audio backend: shells out to ffplay (ships with FFmpeg; Windows/macOS/Linux).
/// Zero dependencies, but each cue pays process start-up latency (tens to hundreds of ms).
/// Replace with an in-process engine (ManagedBass, miniaudio, OpenAL) behind IAudioPlayer.
/// </summary>
public sealed class FfplayAudioPlayer : IAudioPlayer
{
    readonly List<Process> _procs = new();
    readonly object _lock = new();
    public string Executable { get; init; } = "ffplay";

    public void Play(string file, int volume)
    {
        // Don't block the timing thread on process creation.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var psi = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-nodisp", "-autoexit", "-loglevel", "quiet",
                                      "-volume", Math.Clamp(volume, 0, 100).ToString(), file })
                psi.ArgumentList.Add(a);
            try
            {
                var p = Process.Start(psi);
                if (p is null) return;
                lock (_lock) { _procs.RemoveAll(x => x.HasExited); _procs.Add(p); }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[audio] could not start '{Executable}': {ex.Message}");
            }
        });
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var p in _procs)
                try { if (!p.HasExited) p.Kill(); } catch { /* already gone */ }
            _procs.Clear();
        }
    }

    public void Dispose() => StopAll();
}
