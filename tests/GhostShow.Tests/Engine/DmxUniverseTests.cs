using GhostShow.Core.Dmx;

namespace GhostShow.Tests.Engine;

public class DmxUniverseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Channel_holds_the_extreme_values(byte value)
    {
        var universe = new DmxUniverse(0);

        universe.SetChannel(1, value);

        Assert.Equal(value, universe.GetChannel(1));
    }

    [Fact]
    public void Every_address_from_1_to_512_is_independent()
    {
        var universe = new DmxUniverse(0);

        for (int address = 1; address <= 512; address++) universe.SetChannel(address, (byte)(address % 256));

        for (int address = 1; address <= 512; address++) Assert.Equal((byte)(address % 256), universe.GetChannel(address));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    [InlineData(-1)]
    public void Addresses_outside_1_to_512_are_rejected(int address)
    {
        var universe = new DmxUniverse(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => universe.SetChannel(address, 1));
        Assert.False(DmxUniverse.IsValidAddress(address));
    }

    [Fact]
    public void Clear_sets_every_channel_to_zero()
    {
        var universe = new DmxUniverse(0);
        for (int address = 1; address <= 512; address++) universe.SetChannel(address, 200);

        universe.Clear();

        var frame = new byte[512];
        universe.CopyTo(frame);
        Assert.All(frame, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Frame_puts_channel_1_first_and_channel_512_last()
    {
        var universe = new DmxUniverse(0);
        universe.SetChannel(1, 11);
        universe.SetChannel(2, 22);
        universe.SetChannel(512, 99);

        var frame = new byte[512];
        universe.CopyTo(frame);

        Assert.Equal(11, frame[0]);
        Assert.Equal(22, frame[1]);
        Assert.Equal(99, frame[511]);
        Assert.Equal(132, frame.Sum(v => v));
    }

    [Fact]
    public void Changing_a_copied_frame_does_not_change_the_universe()
    {
        var universe = new DmxUniverse(0);
        universe.SetChannel(1, 50);
        var frame = new byte[512];
        universe.CopyTo(frame);

        frame[0] = 255;

        Assert.Equal(50, universe.GetChannel(1));
    }

    [Fact]
    public void Version_increases_only_when_a_value_actually_changes()
    {
        var universe = new DmxUniverse(0);
        universe.SetChannel(1, 50);
        long before = universe.Version;

        universe.SetChannel(1, 50);
        Assert.Equal(before, universe.Version);

        universe.SetChannel(1, 51);
        Assert.True(universe.Version > before);
    }

    [Fact]
    public void Copying_into_a_too_small_buffer_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DmxUniverse(0).CopyTo(new byte[511]));
    }
}
