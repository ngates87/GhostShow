using GhostShow.Dmx;

namespace GhostShow.Tests.Dmx;

/// <summary>A device whose failures tests can script: refuse to connect, drop out mid-show, or hang.</summary>
sealed class FakeDevice : IDmxOutputDevice
{
    readonly Lock _lock = new();
    readonly List<byte[]> _frames = [];

    public DmxDeviceInfo Info { get; } = new("Fake", "Test device");

    public int ConnectAttempts;
    public int Disconnects;
    public bool Disposed;
    public volatile bool RefuseConnect;
    public volatile bool FailNextSend;
    public volatile bool HangOnSend;
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ConnectAttempts);
        if (RefuseConnect) throw new DmxDeviceException("Fake device is unplugged.");
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        Interlocked.Increment(ref Disconnects);
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void SendFrame(ReadOnlySpan<byte> channels)
    {
        while (HangOnSend) Thread.Sleep(5);
        if (FailNextSend)
        {
            FailNextSend = false;
            throw new DmxDeviceException("Fake device was unplugged mid-show.");
        }
        lock (_lock) _frames.Add(channels.ToArray());
    }

    public int FrameCount
    {
        get { lock (_lock) return _frames.Count; }
    }

    /// <summary>The most recent frame, or all zeros if none has been sent yet.</summary>
    public byte[] LastFrame
    {
        get { lock (_lock) return _frames.Count == 0 ? new byte[512] : _frames[^1]; }
    }

    public void Dispose() => Disposed = true;
}
