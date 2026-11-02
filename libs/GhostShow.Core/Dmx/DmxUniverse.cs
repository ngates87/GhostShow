namespace GhostShow.Core.Dmx;

/// <summary>
/// The 512 channel values of one DMX universe. Addresses are 1-based, as on fixtures and consoles.
///
/// Not thread-safe: <see cref="Engine.ShowEngine"/> owns every instance and serializes access. The values array is
/// never handed out; readers get a copy through <see cref="CopyTo"/>, so nothing outside can corrupt the state.
/// </summary>
public sealed class DmxUniverse
{
    public const int ChannelCount = 512;
    public const int FirstAddress = 1;
    public const int LastAddress = ChannelCount;

    readonly byte[] _values = new byte[ChannelCount];

    public DmxUniverse(int number) => Number = number;

    public int Number { get; }

    /// <summary>Increases on every change. Lets the UI skip redraws and lets tests see that something happened.</summary>
    public long Version { get; private set; }

    public static bool IsValidAddress(int address) => address is >= FirstAddress and <= LastAddress;

    public byte GetChannel(int address) => _values[ToIndex(address)];

    public void SetChannel(int address, byte value)
    {
        int index = ToIndex(address);
        if (_values[index] == value) return;
        _values[index] = value;
        Version++;
    }

    /// <summary>Sets all channels to zero.</summary>
    public void Clear()
    {
        Array.Clear(_values);
        Version++;
    }

    /// <summary>Overwrites the universe from channel 1 onward; channels past the end of values are set to zero.</summary>
    public void SetAll(ReadOnlySpan<byte> values)
    {
        int count = Math.Min(values.Length, ChannelCount);
        values[..count].CopyTo(_values);
        _values.AsSpan(count).Clear();
        Version++;
    }

    /// <summary>Copies all 512 values (channel 1 first) into destination, which must hold at least 512 bytes.</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < ChannelCount)
            throw new ArgumentException($"Destination must hold {ChannelCount} bytes; it holds {destination.Length}.", nameof(destination));
        _values.CopyTo(destination);
    }

    static int ToIndex(int address)
    {
        if (!IsValidAddress(address))
            throw new ArgumentOutOfRangeException(nameof(address), address, $"DMX addresses run from {FirstAddress} to {LastAddress}.");
        return address - 1;
    }
}
