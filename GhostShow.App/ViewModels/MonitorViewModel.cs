using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Engine;
using GhostShow.Dmx;

namespace GhostShow.App.ViewModels;

public sealed partial class MonitorCellViewModel(int address) : ObservableObject
{
    public int Address { get; } = address;

    [ObservableProperty]
    public partial byte Value { get; set; }

    /// <summary>0-1, for the cell's fill.</summary>
    public double Level => Value / 255.0;

    [ObservableProperty]
    public partial bool RecentlyChanged { get; set; }

    internal long ChangedAt;

    partial void OnValueChanged(byte value) => OnPropertyChanged(nameof(Level));
}

/// <summary>
/// All 512 channels of the active universe, as last sent to the output device (or held by the engine if the
/// universe has no running output). Only cells whose value changed are touched each tick, so a steady look costs
/// almost nothing to display. A cell stays highlighted for <see cref="HighlightMs"/> after it changes.
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    const long HighlightMs = 600;

    readonly ShowEngine _engine;
    readonly Func<int, DmxOutputService?> _findOutput;
    readonly byte[] _frame = new byte[512];

    public MonitorViewModel(ShowEngine engine, Func<int, DmxOutputService?> findOutput)
    {
        _engine = engine;
        _findOutput = findOutput;
        Cells = Enumerable.Range(1, 512).Select(a => new MonitorCellViewModel(a)).ToArray();
    }

    public IReadOnlyList<MonitorCellViewModel> Cells { get; }

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
        for (int i = 0; i < Cells.Count; i++)
        {
            var cell = Cells[i];
            if (cell.Value != _frame[i])
            {
                cell.Value = _frame[i];
                cell.ChangedAt = now;
                cell.RecentlyChanged = true;
            }
            else if (cell.RecentlyChanged && Stopwatch.GetElapsedTime(cell.ChangedAt, now).TotalMilliseconds > HighlightMs)
            {
                cell.RecentlyChanged = false;
            }
        }
    }
}
