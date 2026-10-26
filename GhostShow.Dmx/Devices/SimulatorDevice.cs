namespace GhostShow.Dmx.Devices;

/// <summary>
/// Software stand-in for DMX hardware: accepts frames like a real device and remembers the last one, so the
/// monitor, tests and development work without anything plugged in. It does not model any hardware's timing.
/// </summary>
public sealed class SimulatorDevice : IDmxOutputDevice
{
    readonly Lock _lock = new();
    readonly byte[] _lastFrame = new byte[512];
    long _framesReceived;

    public DmxDeviceInfo Info { get; } = new("Simulator", "Software output; no hardware");

    public bool IsConnected { get; private set; }

    public long FramesReceived
    {
        get { lock (_lock) return _framesReceived; }
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void SendFrame(ReadOnlySpan<byte> channels)
    {
        if (!IsConnected) throw new DmxDeviceException("Simulator is not connected.");
        lock (_lock)
        {
            int n = Math.Min(channels.Length, _lastFrame.Length);
            channels[..n].CopyTo(_lastFrame);
            _lastFrame.AsSpan(n).Clear();
            _framesReceived++;
        }
    }

    /// <summary>Thread-safe: copies the last frame received into destination (at least 512 bytes).</summary>
    public void CopyLastFrame(Span<byte> destination)
    {
        lock (_lock) _lastFrame.CopyTo(destination);
    }

    public void Dispose() => IsConnected = false;
}
