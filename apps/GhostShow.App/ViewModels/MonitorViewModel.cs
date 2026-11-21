using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.App.Controls;
using GhostShow.Core.Engine;

namespace GhostShow.App.ViewModels;

/// <summary>
/// All 512 channels of the active universe, as last sent to the output device (or held by the engine if the
/// universe has no running output), for <see cref="DmxMonitorGrid"/>. The arrays are reused; <see cref="Revision"/>
/// goes up only when a value changes or a change highlight expires, so a steady look costs nothing to display.
/// Show fixtures draws the patch over it, so it's clear which fixture's channels are moving.
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    readonly ILiveLevels _levels;
    readonly ShowSession _session;
    readonly byte[] _frame = new byte[512];
    int? _universe;
    IReadOnlyList<PatchMapBlock> _fixtures = [];

    public MonitorViewModel(ILiveLevels levels, ShowSession session)
    {
        _levels = levels;
        _session = session;
        session.Applied += MapFixtures;   // a fixture added, moved or re-typed (here, undone, or from another device)
    }

    /// <summary>Draw each fixture over its channels: its name, its colour, and what each channel does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Fixtures))]
    public partial bool ShowFixtures { get; set; }

    /// <summary>The fixtures patched on the universe shown, for the grid; null while Show fixtures is off.</summary>
    public IReadOnlyList<PatchMapBlock>? Fixtures => ShowFixtures ? _fixtures : null;

    /// <summary>
    /// The patched fixtures on the universe shown (only those that are output: enabled, with their type), coloured by
    /// their place in the show's list, as the Fixtures screen colours them.
    /// </summary>
    void MapFixtures()
    {
        if (_universe is not { } universe) return;
        var place = _session.Project.Fixtures.Select((fixture, index) => (fixture.Id, index)).ToDictionary(p => p.Id, p => p.index);
        _fixtures = [.. _session.Patch.Fixtures
            .Where(f => f.Universe == universe)
            .OrderBy(f => f.StartAddress)
            .Select(f => new PatchMapBlock(f.Id, f.Name, f.StartAddress, [.. f.Mode.Channels.Select(c => c.Name)], place.GetValueOrDefault(f.Id), true))];
        OnPropertyChanged(nameof(Fixtures));
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
        if (universe != _universe)
        {
            _universe = universe;
            MapFixtures();
        }
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
