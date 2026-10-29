using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GhostShow.Core.Diagnostics;

/// <summary>
/// How busy the whole computer's CPU is (all cores, every process), sampled between calls to <see cref="Sample"/>:
/// /proc/stat on Linux, GetSystemTimes on Windows. Elsewhere (macOS) it falls back to this process's own CPU use,
/// which is what GhostShow can do something about anyway.
/// </summary>
public sealed class CpuUsageSampler
{
    (long Busy, long Total)? _last;

    /// <summary>
    /// 0-1, the fraction of CPU time spent busy since the previous call; null on the first call (nothing to compare
    /// with yet) or if the counters can't be read.
    /// </summary>
    public double? Sample()
    {
        var now = Read();
        if (now is null) return null;
        var last = _last;
        _last = now;
        if (last is not { } previous) return null;
        long total = now.Value.Total - previous.Total, busy = now.Value.Busy - previous.Busy;
        return total <= 0 ? null : Math.Clamp((double)busy / total, 0, 1);
    }

    static (long Busy, long Total)? Read()
    {
        try
        {
            if (OperatingSystem.IsLinux()) return ParseProcStat(File.ReadLines("/proc/stat").FirstOrDefault());
            if (OperatingSystem.IsWindows() && GetSystemTimes(out long idle, out long kernel, out long user))
                return (kernel + user - idle, kernel + user);   // kernel time includes idle time
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ProcessTimes();
    }

    /// <summary>This process's CPU time against wall-clock time on every core.</summary>
    static (long Busy, long Total) ProcessTimes()
    {
        using var process = Process.GetCurrentProcess();
        return (process.TotalProcessorTime.Ticks, Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency * Environment.ProcessorCount);
    }

    /// <summary>The "cpu" line of /proc/stat: user nice system idle iowait irq softirq steal …; idle and iowait aren't busy.</summary>
    internal static (long Busy, long Total)? ParseProcStat(string? line)
    {
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return null;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(long.Parse).ToArray();
        if (fields.Length < 4) return null;
        long total = fields.Sum();
        long idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
        return (total - idle, total);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
