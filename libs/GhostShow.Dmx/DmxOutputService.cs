using System.Diagnostics;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Timing;
using Microsoft.Extensions.Logging;

namespace GhostShow.Dmx;

public enum DmxOutputStatus
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,
    Connecting,
    Connected,
    /// <summary>The device failed or isn't there; the service keeps retrying.</summary>
    Faulted,
}

public sealed record DmxOutputOptions
{
    /// <summary>Target time between frames. 25 ms = 40 frames/s, a little under DMX's ~44 maximum.</summary>
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(25);

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long all-zero frames are sent when the service stops, so fixtures go dark instead of holding the last
    /// look or following their own DMX-loss behaviour. Zero disables it.
    /// </summary>
    public TimeSpan BlackoutOnStop { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long StopAsync waits for the output thread. A hung driver can exceed it; that's logged.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Sends one universe from the <see cref="ShowEngine"/> to one device, continuously, on a dedicated thread.
///
/// Each frame it copies the engine's latest values into a buffer it owns and hands that to the device; nothing is
/// queued, so output is always the newest state and never a backlog. A dedicated thread (not the thread pool)
/// because Open DMX output blocks for most of every frame, and frame timing must not depend on pool load.
///
/// Failures: if the device fails or is missing, the service reports Faulted, logs once, and retries every
/// <see cref="DmxOutputOptions.ReconnectDelay"/> until it comes back or the service is stopped.
///
/// Shutdown: StopAsync sends blackout frames for <see cref="DmxOutputOptions.BlackoutOnStop"/>, disconnects,
/// and waits up to <see cref="DmxOutputOptions.StopTimeout"/> for the thread to end.
///
/// The service owns the device it is given and disposes it when the output thread ends.
/// </summary>
public sealed class DmxOutputService
{
    readonly ShowEngine _engine;
    readonly IDmxOutputDevice _device;
    readonly ILogger _logger;
    readonly DmxOutputOptions _options;
    readonly FrameTimingRecorder _timing = new();
    readonly ManualResetEventSlim _stopSignal = new();
    readonly CancellationTokenSource _stopToken = new();
    readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _lastSentLock = new();
    readonly byte[] _lastSent = new byte[512];
    Thread? _thread;
    volatile DmxOutputStatus _status = DmxOutputStatus.Stopped;
    volatile string? _lastError;

    public DmxOutputService(ShowEngine engine, int universe, IDmxOutputDevice device, ILogger logger,
                            DmxOutputOptions? options = null)
    {
        _engine = engine;
        Universe = universe;
        _device = device;
        _logger = logger;
        _options = options ?? new DmxOutputOptions();
    }

    public int Universe { get; }

    public DmxDeviceInfo Device => _device.Info;

    public DmxOutputStatus Status => _status;

    /// <summary>The most recent failure, kept after recovery so the UI can show what happened.</summary>
    public string? LastError => _lastError;

    public FrameTimingSnapshot Timing => _timing.Snapshot();

    /// <summary>Raised on the output thread whenever Status changes. UI subscribers must marshal to their own thread.</summary>
    public event Action<DmxOutputService>? StatusChanged;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("The output service has already been started.");
        _thread = new Thread(Run)
        {
            Name = $"dmx-out:{Universe}",
            IsBackground = true,                       // never keeps the process alive on its own
            Priority = ThreadPriority.AboveNormal,     // frame timing matters more than UI work
        };
        _thread.Start();
    }

    /// <summary>Thread-safe: copies the last frame actually handed to the device (512 bytes) into destination.</summary>
    public void CopyLastSentFrame(Span<byte> destination)
    {
        lock (_lastSentLock) _lastSent.CopyTo(destination);
    }

    /// <summary>Returns false if the output thread didn't finish within the stop timeout.</summary>
    public async Task<bool> StopAsync()
    {
        if (_thread is null) return true;
        _stopSignal.Set();
        await _stopToken.CancelAsync();

        var finished = await Task.WhenAny(_stopped.Task, Task.Delay(_options.StopTimeout)) == _stopped.Task;
        if (!finished)
            _logger.LogError("Output for universe {Universe} ({Device}) didn't stop within {Timeout}; the driver may be hung.",
                             Universe, _device.Info, _options.StopTimeout);
        return finished;
    }

    void Run()
    {
        var frame = new byte[512];
        bool crashed = false;
        long frameIntervalTicks = (long)(_options.FrameInterval.TotalSeconds * Stopwatch.Frequency);
        try
        {
            while (!_stopSignal.IsSet)
            {
                if (_status != DmxOutputStatus.Connected && !TryConnect()) continue;

                long frameStart = Stopwatch.GetTimestamp();
                if (!_engine.TryCopyUniverse(Universe, frame, out _)) Array.Clear(frame);   // universe removed: send dark
                if (!TrySend(frame)) continue;
                PreciseDelay.WaitUntil(frameStart + frameIntervalTicks, _stopSignal);
            }
            SendBlackoutAndDisconnect(frame, frameIntervalTicks);
        }
        catch (Exception ex)
        {
            // A bug, not a device failure (those are DmxDeviceException and handled above). Log it rather than let
            // it take down the process from a background thread.
            _logger.LogCritical(ex, "Output thread for universe {Universe} crashed.", Universe);
            crashed = true;
            SetStatus(DmxOutputStatus.Faulted, $"Output thread crashed: {ex.Message}");
        }
        finally
        {
            _device.Dispose();
            if (!crashed) SetStatus(DmxOutputStatus.Stopped);
            _stopped.TrySetResult();
        }
    }

    bool TryConnect()
    {
        bool retrying = _status == DmxOutputStatus.Faulted;
        if (retrying && _stopSignal.Wait(_options.ReconnectDelay)) return false;

        SetStatus(DmxOutputStatus.Connecting);
        try
        {
            _device.ConnectAsync(_stopToken.Token).GetAwaiter().GetResult();   // dedicated thread: blocking is fine
        }
        catch (DmxDeviceException ex)
        {
            // Log the first failure loudly; repeats every few seconds would bury everything else.
            if (retrying) _logger.LogDebug("Universe {Universe}: still can't connect: {Error}", Universe, ex.Message);
            else _logger.LogWarning("Universe {Universe}: can't connect to {Device}: {Error}", Universe, _device.Info, ex.Message);
            SetStatus(DmxOutputStatus.Faulted, ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;   // stopping
        }

        _timing.ResetInterval();
        if (retrying) _logger.LogInformation("Universe {Universe}: reconnected to {Device}.", Universe, _device.Info);
        else _logger.LogInformation("Universe {Universe}: output started on {Device}.", Universe, _device.Info);
        SetStatus(DmxOutputStatus.Connected);
        return true;
    }

    bool TrySend(byte[] frame)
    {
        try
        {
            _device.SendFrame(frame);
        }
        catch (DmxDeviceException ex)
        {
            _logger.LogError("Universe {Universe}: output failed: {Error}", Universe, ex.Message);
            SetStatus(DmxOutputStatus.Faulted, ex.Message);
            _device.DisconnectAsync().GetAwaiter().GetResult();
            return false;
        }

        _timing.RecordFrame();
        lock (_lastSentLock) frame.CopyTo(_lastSent, 0);
        return true;
    }

    void SendBlackoutAndDisconnect(byte[] frame, long frameIntervalTicks)
    {
        if (_status == DmxOutputStatus.Connected && _options.BlackoutOnStop > TimeSpan.Zero)
        {
            Array.Clear(frame);
            long until = Stopwatch.GetTimestamp() + (long)(_options.BlackoutOnStop.TotalSeconds * Stopwatch.Frequency);
            using var neverSet = new ManualResetEventSlim();
            while (Stopwatch.GetTimestamp() < until && TrySend(frame))
                PreciseDelay.WaitUntil(Stopwatch.GetTimestamp() + frameIntervalTicks, neverSet);
        }
        _device.DisconnectAsync().GetAwaiter().GetResult();
        _logger.LogInformation("Universe {Universe}: output stopped.", Universe);
    }

    void SetStatus(DmxOutputStatus status, string? error = null)
    {
        if (error is not null) _lastError = error;
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this);
    }
}
