namespace GhostShow.Dmx;

/// <summary>What the UI and logs show about a device. Port is where it was last found, which can change.</summary>
public sealed record DmxDeviceInfo(string Kind, string Description, string? Port = null, string? SerialNumber = null)
{
    public override string ToString() =>
        Port is null ? $"{Kind}: {Description}" : $"{Kind} on {Port}{(SerialNumber is null ? "" : $" (S/N {SerialNumber})")}";
}

/// <summary>
/// One piece of DMX output hardware (or the simulator) driving one universe.
///
/// A device is owned and called by exactly one <see cref="DmxOutputService"/> thread, so implementations don't need
/// to be thread-safe. Connection state and error reporting live in the service, not here: a device just connects,
/// sends, and throws <see cref="DmxDeviceException"/> when it can't.
/// </summary>
public interface IDmxOutputDevice : IDisposable
{
    DmxDeviceInfo Info { get; }

    /// <summary>Opens the device. May take a while (port lookup, USB enumeration), hence async.</summary>
    /// <exception cref="DmxDeviceException">The device isn't there, is in use, or refused to open.</exception>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Closes the device. Safe to call when not connected.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// Sends one frame of 512 channel values (channel 1 first). Synchronous on purpose: it runs on the output
    /// thread, and for Open DMX the call itself is the frame timing (it blocks ~23 ms while the bytes go out).
    /// </summary>
    /// <exception cref="DmxDeviceException">The device was lost or rejected the write.</exception>
    void SendFrame(ReadOnlySpan<byte> channels);
}

/// <summary>A device-level failure: unplugged, in use, permission denied, timed out.</summary>
public sealed class DmxDeviceException(string message, Exception? innerException = null) : Exception(message, innerException);
