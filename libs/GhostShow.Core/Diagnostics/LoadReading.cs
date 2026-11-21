namespace GhostShow.Core.Diagnostics;

/// <summary>
/// How hard the show computer is working, for the status bar's load meter on its screen and every device's: the worst
/// of its CPU use, how late its playback loop runs and how long its show thread keeps work waiting, 0-1 (see
/// GhostShow.Server's ShowLoad). Always the show computer's: a device's own load doesn't affect the show.
/// </summary>
/// <param name="Tip">Each part in words, for the meter's tooltip.</param>
public sealed record LoadReading(double Load, string Tip)
{
    /// <summary>Where the meter turns yellow, then red.</summary>
    public const double YellowFrom = 0.6, RedFrom = 0.85;

    public static readonly LoadReading Measuring = new(0, "Measuring the show computer…");
}
