using System.Diagnostics;

namespace GhostShow.Core.Timing;

/// <summary>
/// Waits for a Stopwatch timestamp with roughly 0.1 ms accuracy. Sleeping alone is too coarse for a 23 ms DMX
/// frame period (the OS may oversleep by a millisecond or more), and spinning alone burns a core, so this
/// sleeps until about 2 ms remain and spins for the rest.
///
/// On Windows the default timer resolution is ~15.6 ms, so the sleep phase can overshoot; the 2 ms spin margin
/// does not cover that. Measured on Linux only so far; see docs/performance.md.
/// </summary>
public static class PreciseDelay
{
    const double SpinThresholdMs = 2.0;

    /// <summary>Returns false if stopSignal was set before the target time.</summary>
    public static bool WaitUntil(long targetTimestamp, ManualResetEventSlim stopSignal)
    {
        while (true)
        {
            long remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return !stopSignal.IsSet;

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > SpinThresholdMs)
            {
                if (stopSignal.Wait(1)) return false;
            }
            else
            {
                if (stopSignal.IsSet) return false;
                Thread.SpinWait(50);
            }
        }
    }
}
