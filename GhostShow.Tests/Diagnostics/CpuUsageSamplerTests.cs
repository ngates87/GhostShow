using GhostShow.Core.Diagnostics;

namespace GhostShow.Tests.Diagnostics;

public class CpuUsageSamplerTests
{
    [Fact]
    public void Proc_stat_idle_and_iowait_count_as_not_busy()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice (guest is already in user: not counted twice)
        var times = CpuUsageSampler.ParseProcStat("cpu  100 20 30 800 50 0 0 0 7 0");

        Assert.Equal((150L, 1000L), times);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("intr 12345")]
    [InlineData("cpu0 1 2 3 4")]   // a single core's line, not the total
    public void Other_lines_are_not_read_as_totals(string? line) => Assert.Null(CpuUsageSampler.ParseProcStat(line));

    [Fact]
    public void Samples_are_fractions_after_the_first()
    {
        var sampler = new CpuUsageSampler();
        Assert.Null(sampler.Sample());   // nothing to compare with yet
        Thread.Sleep(50);

        Assert.InRange(sampler.Sample() ?? 0, 0, 1);
    }
}
