using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.App.Controls;
using GhostShow.Core.Engine;

namespace GhostShow.App.ViewModels;

/// <summary>
/// All 512 channels of the active universe, as last sent to the output device (or held by the engine if the
/// universe has no running output), for <see cref="DmxMonitorGrid"/>. The arrays are reused; <see cref="Revision"/>
/// goes up only when a value changes or a change highlight expires, so a steady look costs nothing to display.
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    readonly ILiveLevels _levels;
    readonly byte[] _frame = new byte[512];

    public MonitorViewModel(ILiveLevels levels) => _levels = levels;

    /// <summary>Channel values, index = address - 1.</summary>
    public byte[] Values { get; } = new byte[512];

    /// <summary>Stopwatch timestamp of each channel's last change; 0 once the highlight has expired.</summary>
    public long[] ChangedAt { get; } = new long[512];

    [ObservableProperty]
    public partial long Revision { get; private set; }

    [ObservableProperty]
    public partial string Source { get; set; } = "";

    public void Tick(int universe)
    {
        Source = _levels.CopyAsSent(universe, _frame);

        long now = Stopwatch.GetTimestamp();
        bool changed = false;
        for (int i = 0; i < Values.Length; i++)
        {
            if (Values[i] != _frame[i])
            {
                Values[i] = _frame[i];
                ChangedAt[i] = now;
                changed = true;
            }
            else if (ChangedAt[i] != 0 && Stopwatch.GetElapsedTime(ChangedAt[i], now).TotalMilliseconds > DmxMonitorGrid.HighlightMs)
            {
                ChangedAt[i] = 0;   // highlight expired: redraw once more to remove it
                changed = true;
            }
        }
        if (changed) Revision++;
    }
}
