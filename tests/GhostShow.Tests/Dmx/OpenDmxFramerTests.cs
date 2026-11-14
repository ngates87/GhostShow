using System.Diagnostics;
using GhostShow.Dmx.Devices;

namespace GhostShow.Tests.Dmx;

/// <summary>
/// What Open DMX framing puts on the line, and when. Fixtures ignore a frame without a proper break, while the output
/// still reports Connected, so these are the only check short of a DMX tester.
/// </summary>
public class OpenDmxFramerTests
{
    [Fact]
    public void With_the_break_signal_a_frame_is_a_break_then_the_start_code_and_512_channels_at_250_kbaud()
    {
        var line = new RecordingLine();

        new OpenDmxFramer(useBreakSignal: true).Send(line, [10, 20, 30]);

        Assert.Equal(["break on", "break off", "write 513", "flush"], line.Calls.Select(c => c.What));
        var frame = line.Calls.Single(c => c.Bytes is not null).Bytes!;
        Assert.Equal(0, frame[0]);                              // the start code
        Assert.Equal([10, 20, 30], frame[1..4]);
        Assert.All(frame[4..], b => Assert.Equal(0, b));       // channels not given are sent dark
    }

    [Fact]
    public void The_break_signal_is_held_long_enough_for_DMX()
    {
        var line = new RecordingLine();

        new OpenDmxFramer(useBreakSignal: true).Send(line, new byte[512]);

        var held = line.TimeOf("break off") - line.TimeOf("break on");
        Assert.True(held >= TimeSpan.FromMicroseconds(88), $"The break lasted {held.TotalMicroseconds:0} µs; DMX needs at least 88.");
    }

    [Fact]
    public void The_next_break_waits_until_the_last_frame_has_left_the_chip()
    {
        // On Windows, Flush returns while the frame is still going out (the line's Flush here returns at once, like
        // that), so a break straight after it would cut the frame's end off.
        var line = new RecordingLine();
        var framer = new OpenDmxFramer(useBreakSignal: true);

        framer.Send(line, new byte[512]);
        framer.Send(line, new byte[512]);

        var gap = line.TimeOf("break on", nth: 1) - line.TimeOf("write 513");
        Assert.True(gap >= OpenDmxFramer.FrameWireTime,
                    $"The second break came {gap.TotalMilliseconds:0.0} ms after the frame was written; it takes {OpenDmxFramer.FrameWireTime.TotalMilliseconds:0.0} ms to send.");
    }

    [Fact]
    public void A_frame_s_wire_time_is_513_bytes_of_11_bits_at_250_kbaud()
    {
        Assert.Equal(22.572, OpenDmxFramer.FrameWireTime.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Without_the_break_signal_the_break_is_a_zero_sent_at_90_kbaud_and_the_line_never_breaks()
    {
        var line = new RecordingLine();

        new OpenDmxFramer(useBreakSignal: false).Send(line, [255]);

        Assert.Equal(["baud 90000", "write 1", "flush", "baud 250000", "write 513", "flush"], line.Calls.Select(c => c.What));
        Assert.Equal([0], line.Calls[1].Bytes);
        Assert.Equal(255, line.Calls[4].Bytes![1]);
    }

    [Fact]
    public void More_than_512_channels_are_cut_to_512()
    {
        var line = new RecordingLine();

        new OpenDmxFramer(useBreakSignal: true).Send(line, Enumerable.Repeat((byte)7, 600).ToArray());

        var frame = line.Calls.Single(c => c.Bytes is not null).Bytes!;
        Assert.Equal(513, frame.Length);
        Assert.All(frame[1..], b => Assert.Equal(7, b));
    }

    /// <summary>A serial line that writes nowhere and returns at once, noting each call and when it was made.</summary>
    sealed class RecordingLine : ISerialLine
    {
        public List<(string What, long At, byte[]? Bytes)> Calls { get; } = [];

        public int BaudRate { set => Note($"baud {value}"); }
        public bool BreakState { set => Note(value ? "break on" : "break off"); }
        public void Write(byte[] buffer, int offset, int count) => Note($"write {count}", buffer.AsSpan(offset, count).ToArray());
        public void Flush() => Note("flush");

        public TimeSpan TimeOf(string what, int nth = 0) =>
            Stopwatch.GetElapsedTime(0, Calls.Where(c => c.What == what).ElementAt(nth).At);

        void Note(string what, byte[]? bytes = null) => Calls.Add((what, Stopwatch.GetTimestamp(), bytes));
    }
}
