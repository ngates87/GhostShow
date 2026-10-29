using System.Collections.Concurrent;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
using SoundFlow.Providers;
using SoundFlow.Structs;
using SoundFlow.Structs.Events;

namespace GhostShow.Audio;

public enum AudioDeviceState { Closed, Open, Failed }

/// <param name="DeviceName">The default output's device in use (or last tried).</param>
/// <param name="Playing">Instances currently playing or paused, on every device.</param>
/// <param name="Outputs">The other devices tracks play on, and whether each is open.</param>
public sealed record AudioStatus(AudioDeviceState State, string? DeviceName, int Playing, string? LastError,
                                 IReadOnlyList<RoutedOutputStatus>? Outputs = null)
{
    public IReadOnlyList<RoutedOutputStatus> Outputs { get; init; } = Outputs ?? [];

    /// <summary>Routed devices that aren't open (not plugged in, or failed): their tracks play on the default output.</summary>
    public IEnumerable<string> MissingOutputs => Outputs.Where(o => !o.Open).Select(o => o.Name);
}

/// <param name="Open">False: not connected or failed to open, retried every few seconds.</param>
public sealed record RoutedOutputStatus(string Name, bool Open, int Playing);

/// <summary>
/// Show audio through SoundFlow (MIT; miniaudio underneath: WASAPI, Core Audio, PulseAudio/ALSA). One SoundFlow
/// player per audio event, mixed by its device's master mixer, so any number can overlap.
///
/// Devices: the default output (this computer's choice, <see cref="OpenAsync"/>) plays every cue without a
/// <see cref="AudioCue.Device"/>. A cue routed to another device (its track's output, VenueMagic-style) plays there
/// once that device is open; devices are opened in the background (<see cref="UseOutputs"/>, or on first use), never
/// on the playback thread. Until a routed device is open (not plugged in, or still opening) its cues play on the
/// default output, and the device is retried every 3 s; when it opens or is lost, <see cref="Generation"/> goes up and
/// the player restarts its sounds where they belong.
///
/// Files are streamed (not loaded whole) and decoded by miniaudio, converting to the device's sample rate and channels:
/// WAV and MP3, plus FLAC. Opening a file takes ~15 ms;
/// that happens on the playback thread when a cue is due (measured; see docs/timeline.md).
///
/// Failures never throw into the player: a missing or undecodable file, or no device, makes that instance Failed and
/// is logged once per file. If the device stops unexpectedly (unplugged), the service reports it and tries to reopen it
/// every 3 s; <see cref="Generation"/> goes up when it's back, and the player restarts whatever should be playing.
///
/// Threading: the playback thread calls the IAudioOutput methods, the UI calls device and volume methods; a lock
/// guards the device and instances. SoundFlow mixes on its own audio thread.
/// </summary>
public sealed class AudioService : IAudioOutput, IDisposable
{
    static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// miniaudio's null backend (a silent real-time device). SoundFlow 1.4.2's MiniAudioBackend enum is offset by one
    /// from the bundled miniaudio's: its "Null" opens nothing and its "Custom" opens the null device (verified by
    /// probing every value; "PulseAudio" likewise opens ALSA). The default backend order is unaffected. If SoundFlow
    /// fixes the enum, the audio tests fail here and this should become MiniAudioBackend.Null.
    /// </summary>
    const MiniAudioBackend NullBackend = MiniAudioBackend.Custom;

    readonly ILogger _logger;
    readonly MiniAudioEngine _engine;
    readonly AudioFormat _format = AudioFormat.DvdHq;
    readonly Lock _lock = new();
    readonly Dictionary<Guid, Instance> _instances = new();
    readonly ConcurrentDictionary<string, TimeSpan?> _lengths = new();
    readonly ConcurrentDictionary<string, Waveform> _waveforms = new();
    readonly ConcurrentDictionary<string, byte> _reported = new();   // "path: error" already logged
    readonly Timer _recovery;
    /// <summary>Routed devices by name; null while not open (missing, failed, or opening).</summary>
    readonly Dictionary<string, AudioPlaybackDevice?> _routed = new();
    readonly HashSet<string> _opening = new();
    AudioPlaybackDevice? _device;
    string? _requestedDevice;
    double _masterVolume = 1.0;
    volatile int _generation;
    AudioStatus _status = new(AudioDeviceState.Closed, null, 0, null);
    bool _disposed;

    /// <param name="device">The device whose mixer plays it; null when it failed.</param>
    /// <param name="fallback">A routed cue playing on the default output because its device isn't open.</param>
    sealed class Instance(SoundPlayer? player, StreamDataProvider? provider, Stream? stream, AudioPlaybackDevice? device = null, string? fallback = null)
    {
        public AudioPlaybackDevice? Device { get; } = device;
        public string? FallbackFor { get; } = fallback;
        public SoundPlayer? Player { get; } = player;
        public StreamDataProvider? Provider { get; } = provider;
        public Stream? Stream { get; } = stream;
        public volatile bool Finished;
        public bool Failed => Player is null;
    }

    /// <param name="useNullDevice">Use miniaudio's null backend: a real-time device that outputs nothing (tests, headless runs).</param>
    public AudioService(ILogger logger, bool useNullDevice = false)
    {
        _logger = logger;
        _engine = new MiniAudioEngine(useNullDevice ? [NullBackend] : null);
        _engine.DeviceStopped += OnDeviceStopped;
        _recovery = new Timer(_ => TryRecover(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AudioStatus Status
    {
        get
        {
            lock (_lock)
            {
                var playing = _instances.Values.Where(i => !i.Failed && !i.Finished).ToList();
                var outputs = _routed.OrderBy(r => r.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(r => new RoutedOutputStatus(r.Key, r.Value is not null, r.Value is null ? 0 : playing.Count(i => i.Device == r.Value)))
                    .ToList();
                return _status with { Playing = playing.Count, Outputs = outputs };
            }
        }
    }

    /// <summary>Raised (on any thread) when the device opens, fails or recovers.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised (on a worker thread) when a file's length has been measured, so timelines can be recompiled.</summary>
    public event Action<string>? LengthMeasured;

    public int Generation => _generation;

    public double MasterVolume
    {
        get { lock (_lock) return _masterVolume; }
        set
        {
            lock (_lock)
            {
                _masterVolume = Math.Clamp(value, 0, 1);
                foreach (var device in AllDevices()) device.MasterMixer.Volume = (float)_masterVolume;
            }
        }
    }

    /// <summary>Output device names; the default device first. Runs off the calling thread.</summary>
    public Task<IReadOnlyList<string>> ListDevicesAsync() => Task.Run(() =>
    {
        lock (_lock)
        {
            _engine.UpdateAudioDevicesInfo();
            return (IReadOnlyList<string>)_engine.PlaybackDevices.OrderByDescending(d => d.IsDefault).Select(d => d.Name).ToList();
        }
    });

    /// <summary>
    /// Opens the default output by name (null: the system default; a name that isn't present falls back to the system
    /// default, with a warning). Its instances are dropped and <see cref="Generation"/> goes up, so the player restarts
    /// them on the new device. Runs off the calling thread. Returns whether a device is now open.
    /// </summary>
    public Task<bool> OpenAsync(string? deviceName) => Task.Run(() =>
    {
        _requestedDevice = deviceName;
        bool opened = Open(deviceName);
        if (!opened) _recovery.Change(RecoveryInterval, RecoveryInterval);
        return opened;
    });

    bool Open(string? deviceName)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            CloseDevice();
            try
            {
                _engine.UpdateAudioDevicesInfo();
                var devices = _engine.PlaybackDevices;
                DeviceInfo? chosen = devices.Cast<DeviceInfo?>().FirstOrDefault(d => d!.Value.Name == deviceName)
                                     ?? devices.Cast<DeviceInfo?>().FirstOrDefault(d => d!.Value.IsDefault)
                                     ?? devices.Cast<DeviceInfo?>().FirstOrDefault();
                if (deviceName is not null && chosen?.Name != deviceName)
                    _logger.LogWarning("Audio device '{Device}' isn't available; using {Fallback}.", deviceName, chosen?.Name ?? "the system default");

                _device = _engine.InitializePlaybackDevice(chosen, _format, new MiniAudioDeviceConfig());
                _device.MasterMixer.Volume = (float)_masterVolume;
                _device.Start();
                _generation++;
                SetStatus(new AudioStatus(AudioDeviceState.Open, _device.Info?.Name ?? chosen?.Name, 0, null));
                _logger.LogInformation("Audio output: {Device}.", _status.DeviceName);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // miniaudio reports device problems as various exception types; none of them should take the app down.
                CloseDevice();
                SetStatus(new AudioStatus(AudioDeviceState.Failed, deviceName, 0, $"Couldn't open the audio device: {ex.Message}"));
                _logger.LogError("Couldn't open audio device {Device}: {Error}", deviceName ?? "(default)", ex.Message);
                return false;
            }
        }
    }

    public void Start(AudioCue cue, TimeSpan fileOffset, double volume)
    {
        lock (_lock)
        {
            Remove(cue.EventId);
            var (device, fallback) = DeviceFor(cue.Device);
            if (device is null)
            {
                _instances[cue.EventId] = new Instance(null, null, null);
                ReportOnce(cue.Path, "no audio device is open");
                return;
            }

            Stream? stream = null;
            try
            {
                stream = File.OpenRead(cue.Path);
                _instances[cue.EventId] = Play(device, stream, volume, cue.Loop ? TimeSpan.FromTicks(cue.Offset) : null, fileOffset, fallback);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                stream?.Dispose();
                _instances[cue.EventId] = new Instance(null, null, null);
                ReportOnce(cue.Path, ex is NotSupportedException ? "not a supported audio file (WAV or MP3)" : ex.Message);
            }
        }
    }

    /// <summary>
    /// Under _lock: where a cue for this device plays now. A routed device that isn't open yet is opened in the
    /// background (unless it's being tried already) and the cue plays on the default output meanwhile.
    /// </summary>
    (AudioPlaybackDevice? Device, string? FallbackFor) DeviceFor(string? name)
    {
        if (name is null || IsDefaultOutput(name)) return (_device, null);
        if (_routed.TryGetValue(name, out var routed) && routed is not null) return (routed, null);
        if (!_routed.ContainsKey(name))
        {
            _routed[name] = null;
            OpenRoutedInBackground(name);
        }
        return (_device, name);
    }

    /// <summary>Under _lock: the default output is this device already, so routing to it needs no second device.</summary>
    bool IsDefaultOutput(string name) => name == _requestedDevice || (_device is not null && name == _status.DeviceName);

    /// <summary>
    /// The devices the show's tracks play on (besides the default output): opens the ones not open yet, in the
    /// background, and closes routed devices no longer used. Cheap to call often (e.g. once a second).
    /// </summary>
    public void UseOutputs(IEnumerable<string> names)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n) && !IsDefaultOutput(n)).ToHashSet();
            bool changed = false;
            foreach (var name in _routed.Keys.Where(n => !wanted.Contains(n)).ToList())
            {
                if (_instances.Values.Any(i => (i.Device is not null && i.Device == _routed[name]) || i.FallbackFor == name)) continue;   // still playing: next time
                CloseRouted(name);
                _routed.Remove(name);
                changed = true;
            }
            foreach (var name in wanted.Where(n => !_routed.ContainsKey(n)))
            {
                _routed[name] = null;
                OpenRoutedInBackground(name);
                changed = true;
            }
            if (changed) StatusChanged?.Invoke();
        }
    }

    void OpenRoutedInBackground(string name)
    {
        if (!_opening.Add(name)) return;
        Task.Run(() =>
        {
            try
            {
                if (!OpenRouted(name)) _recovery.Change(RecoveryInterval, RecoveryInterval);
            }
            finally
            {
                lock (_lock) _opening.Remove(name);
            }
        });
    }

    /// <summary>Opens a routed device if it's wanted and not open. Returns whether it's open (or no longer wanted).</summary>
    bool OpenRouted(string name)
    {
        lock (_lock)
        {
            if (_disposed || !_routed.TryGetValue(name, out var existing)) return true;
            if (existing is not null) return true;
            try
            {
                _engine.UpdateAudioDevicesInfo();
                DeviceInfo? info = _engine.PlaybackDevices.Cast<DeviceInfo?>().FirstOrDefault(d => d!.Value.Name == name);
                if (info is null)
                {
                    ReportOutputOnce(name, "it isn't connected; its tracks play on the default output until it is");
                    return false;
                }
                var device = _engine.InitializePlaybackDevice(info, _format, new MiniAudioDeviceConfig());
                device.MasterMixer.Volume = (float)_masterVolume;
                device.Start();
                _routed[name] = device;
                _logger.LogInformation("Audio output opened: {Device}.", name);
                // Sounds meant for it are on the default output: the player moves them (restarting every sound once).
                if (_instances.Values.Any(i => i.FallbackFor == name)) _generation++;
                StatusChanged?.Invoke();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportOutputOnce(name, $"couldn't open it ({ex.Message}); its tracks play on the default output");
                return false;
            }
        }
    }

    /// <summary>
    /// Plays a short tone (A440, half a second) at the master volume, so an operator can check the right speakers are
    /// connected: on the default output, or on a routed device by name. Returns false if that device isn't open.
    /// </summary>
    public bool PlayTestSound(string? deviceName = null)
    {
        lock (_lock)
        {
            Remove(TestSoundId);
            var device = deviceName is null || IsDefaultOutput(deviceName) ? _device
                       : _routed.GetValueOrDefault(deviceName);
            if (device is null) return false;
            _instances[TestSoundId] = Play(device, TestTone(), 0.5, loopFrom: null, TimeSpan.Zero, fallback: null);
            return true;
        }
    }

    static readonly Guid TestSoundId = new("7e57501d-0000-4000-8000-000000000001");

    /// <summary>Starts a player for a stream on a device (caller holds the lock). Throws if the stream can't be decoded.</summary>
    Instance Play(AudioPlaybackDevice device, Stream stream, double volume, TimeSpan? loopFrom, TimeSpan fileOffset, string? fallback)
    {
        // The format overload makes miniaudio convert to the device's rate and channels while decoding. The
        // other constructor doesn't: a 44.1 kHz or mono file then plays at the wrong speed (measured).
        var provider = new StreamDataProvider(_engine, _format, stream);
        try
        {
            var player = new SoundPlayer(_engine, _format, provider) { Volume = (float)volume, IsLooping = loopFrom is not null };
            if (loopFrom is { } loopStart) player.SetLoopPoints(loopStart, null);
            var instance = new Instance(player, provider, stream, device, fallback);
            player.PlaybackEnded += (_, _) => { if (loopFrom is null) instance.Finished = true; };
            device.MasterMixer.AddComponent(player);
            if (fileOffset > TimeSpan.Zero) player.Seek(fileOffset, SeekOrigin.Begin);
            player.Play();
            return instance;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>A 16-bit mono WAV in memory: 0.5 s of 440 Hz with short fades so it doesn't click.</summary>
    static MemoryStream TestTone()
    {
        const int rate = 48000, samples = rate / 2, fade = rate / 50;
        var wav = new MemoryStream(44 + samples * 2);
        using (var w = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double envelope = Math.Min(1, Math.Min(i, samples - 1 - i) / (double)fade);
                w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * envelope * 0.5 * short.MaxValue));
            }
        }
        wav.Position = 0;
        return wav;
    }

    public void Seek(Guid eventId, TimeSpan fileOffset)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(eventId, out var instance) || instance.Player is null) return;
            instance.Finished = false;
            instance.Player.Seek(fileOffset, SeekOrigin.Begin);
        }
    }

    public void Pause(Guid eventId)
    {
        lock (_lock) if (_instances.TryGetValue(eventId, out var i)) i.Player?.Pause();
    }

    public void Resume(Guid eventId)
    {
        lock (_lock) if (_instances.TryGetValue(eventId, out var i) && !i.Finished) i.Player?.Play();
    }

    public void SetVolume(Guid eventId, double volume)
    {
        lock (_lock) if (_instances.TryGetValue(eventId, out var i) && i.Player is not null) i.Player.Volume = (float)Math.Clamp(volume, 0, 1);
    }

    public void Stop(Guid eventId)
    {
        lock (_lock) Remove(eventId);
    }

    public void StopAll()
    {
        lock (_lock) foreach (var id in _instances.Keys.ToList()) Remove(id);
    }

    public AudioInstanceState GetState(Guid eventId)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(eventId, out var i)) return AudioInstanceState.None;
            if (i.Failed) return AudioInstanceState.Failed;
            if (i.Finished) return AudioInstanceState.Finished;
            return i.Player!.State == SoundFlow.Enums.PlaybackState.Paused ? AudioInstanceState.Paused : AudioInstanceState.Playing;
        }
    }

    /// <summary>Cached length; on first request the file is measured in the background and null is returned meanwhile.</summary>
    public TimeSpan? GetLength(string path)
    {
        if (_lengths.TryGetValue(path, out var known)) return known;
        if (_lengths.TryAdd(path, null)) Task.Run(() => Measure(path));
        return null;
    }

    /// <summary>
    /// A file's waveform, made while it was measured; null until then (asking starts the measuring, like
    /// <see cref="GetLength"/>). <see cref="LengthMeasured"/> is raised once it's ready.
    /// </summary>
    public Waveform? GetWaveform(string path)
    {
        if (_waveforms.TryGetValue(path, out var waveform)) return waveform;
        GetLength(path);
        return null;
    }

    /// <summary>Measures a file now (exact duration, scanning MP3s fully). For callers already off the UI thread.</summary>
    public TimeSpan? MeasureNow(string path) => Measure(path);

    /// <summary>
    /// Decodes the whole file and counts samples. MP3 headers only give an estimate (off by a factor of two or more on
    /// short files, measured), and a cue's end time must be right, so this pays for a full decode, off the playback path.
    /// The same pass collects the file's waveform for the timeline.
    /// </summary>
    TimeSpan? Measure(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var provider = new StreamDataProvider(_engine, _format, stream);
            var buffer = new float[16384];
            var waveform = new WaveformBuilder(_format.SampleRate, _format.Channels);
            long samples = 0;
            int read;
            while ((read = provider.ReadBytes(buffer)) > 0)
            {
                samples += read;
                waveform.Add(buffer.AsSpan(0, read));
            }
            var length = TimeSpan.FromSeconds((double)samples / (_format.SampleRate * _format.Channels));
            _waveforms[path] = waveform.Build();
            _lengths[path] = length;
            LengthMeasured?.Invoke(path);
            return length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ArgumentException)
        {
            ReportOnce(path, ex is NotSupportedException ? "not a supported audio file (WAV or MP3)" : ex.Message);
            return null;
        }
    }

    void OnDeviceStopped(object? sender, DeviceEventArgs e)
    {
        string? routed;
        lock (_lock)
        {
            if (_disposed) return;
            routed = _routed.FirstOrDefault(r => r.Value is not null && r.Value == e.Device).Key;
            if (routed is null && (e.Device != _device || _status.State != AudioDeviceState.Open)) return;
        }
        if (routed is not null) HandleRoutedLost(routed);
        else HandleDeviceLost("The audio device stopped (unplugged or taken by the system).");
    }

    /// <summary>A routed device went away: its sounds move to the default output, and it's retried until it's back.</summary>
    internal void HandleRoutedLost(string name)
    {
        lock (_lock)
        {
            if (!_routed.ContainsKey(name)) return;
            CloseRouted(name);
            _routed[name] = null;
            _generation++;
            StatusChanged?.Invoke();
        }
        _logger.LogError("Audio output {Device} stopped (unplugged or taken by the system); its tracks play on the default output. Retrying every {Seconds} s.",
                         name, RecoveryInterval.TotalSeconds);
        _recovery.Change(RecoveryInterval, RecoveryInterval);
    }

    /// <summary>The device went away: drop it and its instances, report it, and start trying to reopen it.</summary>
    internal void HandleDeviceLost(string reason)
    {
        lock (_lock)
        {
            CloseDevice();
            SetStatus(_status with { State = AudioDeviceState.Failed, LastError = reason });
        }
        _logger.LogError("{Reason} Retrying every {Seconds} s; lighting is unaffected.", reason, RecoveryInterval.TotalSeconds);
        _recovery.Change(RecoveryInterval, RecoveryInterval);
    }

    void TryRecover()
    {
        bool ok = true;
        bool defaultDown;
        List<string> missing;
        lock (_lock)
        {
            if (_disposed) return;
            defaultDown = _device is null;
            missing = _routed.Where(r => r.Value is null && !_opening.Contains(r.Key)).Select(r => r.Key).ToList();
        }
        if (defaultDown)
        {
            if (Open(_requestedDevice)) _logger.LogInformation("Audio device is back; playing audio resumes.");
            else ok = false;
        }
        foreach (var name in missing) ok &= OpenRouted(name);
        if (ok) _recovery.Change(Timeout.Infinite, Timeout.Infinite);
    }

    void Remove(Guid eventId)
    {
        if (!_instances.Remove(eventId, out var instance)) return;
        Dispose(instance);
    }

    void Dispose(Instance instance)
    {
        if (instance.Player is { } player)
        {
            player.Stop();
            instance.Device?.MasterMixer.RemoveComponent(player);
            player.Dispose();
        }
        instance.Provider?.Dispose();
        instance.Stream?.Dispose();
    }

    /// <summary>Under _lock: drops the default output and its instances (sounds on routed devices carry on).</summary>
    void CloseDevice()
    {
        DropInstances(i => i.Device is null || i.Device == _device);
        if (_device is null) return;
        var device = _device;
        _device = null;
        StopAndDispose(device);
    }

    /// <summary>Under _lock: drops a routed device and its instances (the entry stays, for the caller to decide).</summary>
    void CloseRouted(string name)
    {
        if (_routed.GetValueOrDefault(name) is not { } device) return;
        DropInstances(i => i.Device == device);
        _routed[name] = null;
        StopAndDispose(device);
    }

    void DropInstances(Func<Instance, bool> which)
    {
        foreach (var (id, instance) in _instances.Where(kv => which(kv.Value)).ToList())
        {
            _instances.Remove(id);
            Dispose(instance);
        }
    }

    void StopAndDispose(AudioPlaybackDevice device)
    {
        try
        {
            device.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Stopping the audio device failed (it may already be gone): {Error}", ex.Message);
        }
        device.Dispose();
    }

    /// <summary>Under _lock: every open device, default output first.</summary>
    IEnumerable<AudioPlaybackDevice> AllDevices()
    {
        if (_device is not null) yield return _device;
        foreach (var device in _routed.Values) if (device is not null) yield return device;
    }

    void ReportOnce(string path, string error)
    {
        if (_reported.TryAdd($"{path}: {error}", 0)) _logger.LogError("Audio file {Path}: {Error}", path, error);
    }

    void ReportOutputOnce(string device, string error)
    {
        if (_reported.TryAdd($"output {device}: {error}", 0)) _logger.LogError("Audio output {Device}: {Error}", device, error);
    }

    void SetStatus(AudioStatus status)
    {
        _status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _recovery.Dispose();
            foreach (var name in _routed.Keys.ToList()) CloseRouted(name);
            _routed.Clear();
            CloseDevice();
            SetStatus(new AudioStatus(AudioDeviceState.Closed, null, 0, null));
        }
        _engine.Dispose();
    }
}
