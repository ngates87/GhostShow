using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Live control of one fixture: a fader per logical control (intensity, colours) the fixture's mode has, then a
/// fader per channel for everything else. Both kinds write through engine commands and both refresh from the
/// engine, so they always agree.
/// </summary>
public sealed partial class FixtureControlViewModel : ObservableObject
{
    static readonly ChannelFunction[] LogicalControls =
    [
        ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue,
        ChannelFunction.White, ChannelFunction.Amber, ChannelFunction.UV,
    ];

    readonly ShowSession _session;
    readonly Action<string> _reportError;
    readonly byte[] _frame = new byte[512];
    readonly bool[] _timelineControlled = new bool[512];

    public FixtureControlViewModel(ShowSession session, Action<string> reportError)
    {
        _session = session;
        _reportError = reportError;
        _session.Applied += RefreshFixtures;
        RefreshFixtures();
    }

    public ObservableCollection<PatchedFixture> Fixtures { get; } = new();
    public ObservableCollection<FaderViewModel> Controls { get; } = new();
    public ObservableCollection<FaderViewModel> Channels { get; } = new();

    [ObservableProperty]
    public partial PatchedFixture? SelectedFixture { get; set; }

    public string Heading => SelectedFixture is { } f
        ? $"{f.Name}: {f.Profile} ({f.Mode.Name}), universe {f.Universe}, {f.StartAddress}-{f.EndAddress}"
        : Fixtures.Count == 0 ? "No fixtures are patched. Add some under Fixtures." : "Pick a fixture.";

    partial void OnSelectedFixtureChanged(PatchedFixture? value)
    {
        Controls.Clear();
        Channels.Clear();
        OnPropertyChanged(nameof(Heading));
        if (value is null) return;

        var id = value.Id;
        foreach (var function in LogicalControls)
        {
            if (!value.TryGetAddress(function, out int address)) continue;
            var f = function;
            Controls.Add(new FaderViewModel(address, function.ToString(), $"ch {address - value.StartAddress + 1}",
                v => _session.Engine.SetFixtureControl(id, f, v), _reportError));
        }
        for (int i = 0; i < value.ChannelCount; i++)
        {
            int channelNumber = i + 1;
            var channel = value.Mode.Channels[i];
            Channels.Add(new FaderViewModel(value.StartAddress + i, channel.Name, $"ch {channelNumber} · {value.StartAddress + i}",
                v => _session.Engine.SetFixtureChannel(id, channelNumber, v), _reportError));
        }
        Tick();
    }

    [RelayCommand]
    void ZeroFixture()
    {
        foreach (var fader in Channels) fader.Value = 0;
    }

    [RelayCommand]
    void FullWhite()
    {
        foreach (var fader in Controls) fader.Value = 255;
    }

    public void Tick()
    {
        if (SelectedFixture is not { } fixture || !_session.Engine.TryCopyUniverse(fixture.Universe, _frame, out _)) return;
        _session.Engine.CopyTimelineControlled(fixture.Universe, _timelineControlled);
        foreach (var fader in Controls) fader.ShowValue(_frame[fader.Address - 1], _timelineControlled[fader.Address - 1]);
        foreach (var fader in Channels) fader.ShowValue(_frame[fader.Address - 1], _timelineControlled[fader.Address - 1]);
    }

    /// <summary>After a re-patch the old PatchedFixture objects are stale; reselect the same fixture by ID.</summary>
    void RefreshFixtures()
    {
        var selectedId = SelectedFixture?.Id;
        Fixtures.Clear();
        foreach (var fixture in _session.Engine.Fixtures) Fixtures.Add(fixture);
        // Keep the same fixture; otherwise the first, so the screen opens on faders rather than "Pick a fixture".
        SelectedFixture = Fixtures.FirstOrDefault(f => f.Id == selectedId) ?? Fixtures.FirstOrDefault();
        OnPropertyChanged(nameof(Heading));
    }
}
