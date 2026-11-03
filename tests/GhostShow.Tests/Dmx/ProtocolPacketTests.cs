using GhostShow.Dmx.Discovery;
using GhostShow.Dmx.Protocol;

namespace GhostShow.Tests.Dmx;

public class ArtNetPacketTests
{
    [Fact]
    public void Header_identifies_an_ArtDmx_packet_at_protocol_version_14()
    {
        var packet = ArtNetPacket.Build(0x0102, 7, [1, 2, 3]);

        Assert.Equal("Art-Net\0"u8.ToArray(), packet[..8]);
        Assert.Equal(0x00, packet[8]);
        Assert.Equal(0x50, packet[9]);
        Assert.Equal(14, packet[11]);
    }

    [Fact]
    public void Universe_sequence_and_even_padded_length_are_encoded()
    {
        var packet = ArtNetPacket.Build(0x0102, 7, [1, 2, 3]);

        Assert.Equal(7, packet[12]);
        Assert.Equal(0x02, packet[14]);    // SubUni
        Assert.Equal(0x01, packet[15]);    // Net
        Assert.Equal(4, packet[16] << 8 | packet[17]);
        Assert.Equal(22, packet.Length);
    }
}

public class EnttecProPacketTests
{
    [Fact]
    public void Dmx_frame_is_wrapped_with_label_6_and_a_start_code()
    {
        var packet = EnttecProPacket.Build([10, 20, 30]);

        Assert.Equal([0x7E, 6, 4, 0, 0, 10, 20, 30, 0xE7], packet);
    }

    [Fact]
    public void Full_universe_packet_declares_513_bytes()
    {
        var packet = EnttecProPacket.Build(new byte[512]);

        Assert.Equal(518, packet.Length);
        Assert.Equal(0x01, packet[2]);
        Assert.Equal(0x02, packet[3]);
        Assert.Equal(0xE7, packet[^1]);
    }

    [Fact]
    public void Request_without_payload_is_five_bytes()
    {
        var packet = EnttecProPacket.Build(EnttecProPacket.GetSerialNumber, ReadOnlySpan<byte>.Empty);

        Assert.Equal([0x7E, 10, 0, 0, 0xE7], packet);
    }

    [Fact]
    public void Parser_skips_leading_garbage_and_reads_the_serial_number()
    {
        byte[] received = [0xFF, 0x00, 0x7E, 10, 4, 0, 0x78, 0x56, 0x34, 0x12, 0xE7, 0x7E];

        Assert.True(EnttecProPacket.TryParse(received, out var label, out var payload, out int consumed));
        Assert.Equal(EnttecProPacket.GetSerialNumber, label);
        Assert.Equal(11, consumed);
        Assert.Equal("12345678", DmxAdapterDetector.FormatSerial(payload));
    }

    [Fact]
    public void Parser_waits_for_a_complete_message()
    {
        byte[] partial = [0x7E, 10, 4, 0, 0x78];

        Assert.False(EnttecProPacket.TryParse(partial, out _, out _, out int consumed));
        Assert.Equal(0, consumed);
    }
}
