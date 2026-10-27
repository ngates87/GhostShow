using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.App.Controls;
using GhostShow.Core.Engine;
using GhostShow.Dmx;

namespace GhostShow.App.ViewModels;

/// <summary>
/// All 512 channels of the active universe, as last sent to the output device (or held by the engine if the
/// universe has no running output), for <see cref="DmxMonitorGrid"/>. The arrays are reused; <see cref="Revision"/>
/// goes up only when a value changes or a change highlight expires, so a steady look costs nothing to display.
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    readonly ShowEngine _engine;
    readonly Func<int, DmxOutputService?> _findOutput;
    readonly byte[] _frame = new byte[512];

    public MonitorViewModel(ShowEngine engine, Func<int, DmxOutputService?> findOutput)
    {
        _engine = engine;
        _findOutput = findOutput;
    }

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
        var output = _findOutput(universe);
        if (output is not null)
        {
            output.CopyLastSentFrame(_frame);
            Source = $"Universe {universe} as sent to {output.Device} ({output.Status})";
        }
        else if (_engine.TryCopyUniverse(universe, _frame, out _))
        {
            Source = $"Universe {universe} as held by the engine (no output running)";
        }
        else
        {
            Array.Clear(_frame);
            Source = $"The show has no universe {universe}.";
        }

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
