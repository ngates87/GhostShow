using GhostShow.App.Editing;
using GhostShow.App.ViewModels;
using GhostShow.Audio;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Tests;

/// <summary>
/// The timeline editor on a real session and player (audio on the null device, no playback thread: the player only
/// moves when told to seek), with a patched 5-channel fixture (channels without functions), an RGB fixture
/// (Intensity, Red, Green, Blue), and the default lighting and audio tracks.
/// </summary>
sealed class TimelineRig : IDisposable
{
    public static readonly FixtureProfile RgbProfile = new()
    {
        Id = "test/acme/rgb", Manufacturer = "Acme", Model = "RGB", Type = "Color Changer",
        Modes =
        [
            new FixtureMode
            {
                Name = "4ch",
                Channels =
                [
                    new FixtureChannel { Name = "Dimmer", Function = ChannelFunction.Intensity },
                    new FixtureChannel { Name = "Red", Function = ChannelFunction.Red },
                    new FixtureChannel { Name = "Green", Function = ChannelFunction.Green },
                    new FixtureChannel { Name = "Blue", Function = ChannelFunction.Blue },
                ],
            },
        ],
    };

    public static readonly FixtureProfile MoverProfile = new()
    {
        Id = "test/acme/mover", Manufacturer = "Acme", Model = "Mover", Type = "Moving Head",
        Modes =
        [
            new FixtureMode
            {
                Name = "3ch",
                Channels =
                [
                    new FixtureChannel { Name = "Pan", Function = ChannelFunction.Pan },
                    new FixtureChannel { Name = "Tilt", Function = ChannelFunction.Tilt },
                    new FixtureChannel { Name = "Dimmer", Function = ChannelFunction.Intensity },
                ],
            },
        ],
    };

    readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "ghostshow-app-tests", Guid.NewGuid().ToString("N"));
    readonly UiResponsivenessProbe _probe = new();

    public TimelineRig()
    {
        Session = new ShowSession(new ShowEngine(), new FixtureProfileLibrary([TestRig.Par, TestRig.Dimmer, RgbProfile, MoverProfile]), NullLogger<ShowSession>.Instance);
        Par = new FixtureInstance { Name = "Par 1", ProfileId = TestRig.Par.Id, ModeName = "5ch", StartAddress = 10 };
        Rgb = new FixtureInstance { Name = "Entry LED", ProfileId = RgbProfile.Id, ModeName = "4ch", StartAddress = 20 };
        Session.Project.Fixtures.AddRange([Par, Rgb]);
        Session.Apply();
        Audio = new AudioService(NullLogger.Instance, useNullDevice: true);
        Manager = new PlaybackManager(Session.Engine, Audio, new StopwatchClock());
        var services = new AppServices(Session, new OutputManager(Session.Engine, NullLoggerFactory.Instance), Manager,
                                       new PlaybackLoop(Manager, NullLogger.Instance), Audio, new MachineSettings(),
                                       new AppPaths(_configDirectory), new RecentEventsLog(), _probe);
        Playback = new PlaybackViewModel(services, NullLogger.Instance);
        Editor = new TimelineEditorViewModel(Session, Playback, () => Task.FromResult(PickedFile), () => Dirty = true);
    }

    public ShowSession Session { get; }
    public FixtureInstance Par { get; }
    public FixtureInstance Rgb { get; }
    public AudioService Audio { get; }
    public PlaybackManager Manager { get; }

    // The open timeline's transport (what the toolbar controls).
    Guid Open => Editor.SelectedTimeline!.Id;
    public void Play() => Manager.Play(Open);
    public void Seek(TimeSpan position) => Manager.Seek(Open, position);
    public PlaybackStatus Status => Manager.StatusOf(Open);
    public CompiledTimeline Compiled => Manager.TimelineOf(Open)!;
    public PlaybackViewModel Playback { get; }
    public TimelineEditorViewModel Editor { get; }
    public string? PickedFile { get; set; }
    public bool Dirty { get; set; }

    public TrackRowViewModel Track(TrackKind kind) => Editor.Tracks.First(t => t.Kind == kind);

    public IList<TimelineEvent> Events(TrackKind kind) => Track(kind).Track.Events;

    public TimelineTrack LightingTrack => Track(TrackKind.Lighting).Track;

    /// <summary>
    /// Adds a sound at a time through the editor (as a double-click on its row would). The file isn't there, so its
    /// length isn't known: it's a zero-length clip.
    /// </summary>
    public TimelineEvent AddSound(double seconds)
    {
        PickedFile ??= Path.GetFullPath("thunder.wav");
        Editor.AddEventAtAsync(Editor.Tracks.IndexOf(Track(TrackKind.Audio)), TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
        return Events(TrackKind.Audio)[^1];
    }

    /// <summary>Writes a real WAV (16-bit mono, 8 kHz): a 440 Hz tone at the given level for that long.</summary>
    public string ToneFile(double seconds, double level)
    {
        Directory.CreateDirectory(_configDirectory);
        string path = Path.Combine(_configDirectory, $"tone-{Guid.NewGuid():N}.wav");
        const int rate = 8000;
        int samples = (int)(seconds * rate);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * level * short.MaxValue));
        return path;
    }

    /// <summary>Gives the lighting track the RGB fixture's Red channel and returns its lane.</summary>
    public ChannelLane RedLane()
    {
        Editor.SetTrackChannels(LightingTrack, [LaneTarget.FixtureControl(Rgb.Id, ChannelFunction.Red)]);
        return LightingTrack.Channels.Single();
    }

    /// <summary>What the engine outputs on a universe-1 address right now (after one playback update).</summary>
    public byte Output(int address)
    {
        Manager.Tick();
        var frame = new byte[512];
        Session.Engine.TryCopyUniverse(1, frame, out _);
        return frame[address - 1];
    }

    public void Dispose()
    {
        Audio.Dispose();
        _probe.Dispose();
        if (Directory.Exists(_configDirectory)) Directory.Delete(_configDirectory, recursive: true);
    }
}

public class TimelineEditorTests : IDisposable
{
    readonly TimelineRig _rig = new();
    TimelineEditorViewModel Editor => _rig.Editor;

    public void Dispose() => _rig.Dispose();

    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Opens_on_the_show_s_first_timeline_and_loads_it_into_the_player()
    {
        Assert.Same(_rig.Session.Project.Timelines[0], Editor.SelectedTimeline);
        Assert.Same(Editor.SelectedTimeline, _rig.Playback.Timeline);
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio], Editor.Tracks.Select(t => t.Kind));
        Assert.False(_rig.Dirty);
    }

    // ---- clips on any track ----

    [Fact]
    public void Add_event_puts_it_at_the_playhead_on_the_selected_track_selects_it_and_is_undoable()
    {
        _rig.Playback.Seek(S(2));
        _rig.PickedFile = Path.GetFullPath("thunder.wav");
        Editor.SelectedTrack = _rig.Track(TrackKind.Audio);
        Editor.AddEventCommand.Execute(null);

        var added = Assert.IsType<AudioEvent>(Assert.Single(_rig.Events(TrackKind.Audio)));
        Assert.Equal(S(2), added.Start);
        Assert.Equal([added.Id], Editor.Selection);
        Assert.Same(added, Editor.Properties?.Event);
        Assert.True(_rig.Dirty);
        Assert.Equal("Undo Add audio clip", Editor.UndoLabel);

        Editor.UndoCommand.Execute(null);
        Assert.Empty(_rig.Events(TrackKind.Audio));
        Assert.Empty(Editor.Selection);
        Assert.Null(Editor.Properties);

        Editor.RedoCommand.Execute(null);
        Assert.Same(added, Assert.Single(_rig.Events(TrackKind.Audio)));
    }

    [Fact]
    public void Adding_audio_asks_for_a_file_and_cancelling_adds_nothing()
    {
        Editor.SelectedTrack = _rig.Track(TrackKind.Audio);
        _rig.PickedFile = null;
        Editor.AddEventCommand.Execute(null);
        Assert.Empty(_rig.Events(TrackKind.Audio));
        Assert.Equal("Undo", Editor.UndoLabel);

        _rig.PickedFile = Path.GetFullPath("thunder.wav");
        Editor.AddEventCommand.Execute(null);
        var audio = Assert.IsType<AudioEvent>(Assert.Single(_rig.Events(TrackKind.Audio)));
        Assert.Equal(_rig.PickedFile, audio.File);   // show not saved yet: stored as an absolute path
    }

    [Fact]
    public void Moving_a_selection_snaps_each_event_never_goes_before_zero_and_is_one_undo_step()
    {
        var a = _rig.AddSound(1);
        var b = _rig.AddSound(2);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(b.Id, additive: true);
        Editor.Snap = TimelineEditorViewModel.SnapOptions.Single(o => o.Interval == S(0.1));

        Editor.MoveSelection(S(0.53));
        Assert.Equal((S(1.5), S(2.5)), (a.Start, b.Start));

        Editor.MoveSelection(S(-10));
        Assert.Equal((S(0), S(1)), (a.Start, b.Start));

        Editor.UndoCommand.Execute(null);
        Assert.Equal((S(1.5), S(2.5)), (a.Start, b.Start));
        Assert.Equal("Undo Move 2 clips", Editor.UndoLabel);
    }

    [Fact]
    public void Ctrl_click_toggles_an_event_in_the_selection_and_properties_show_only_for_one()
    {
        var a = _rig.AddSound(1);
        var b = _rig.AddSound(2);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(b.Id, additive: true);
        Assert.Null(Editor.Properties);
        Assert.Equal("2 sounds selected", Editor.SelectionLabel);

        Editor.SelectEvent(a.Id, additive: true);
        Assert.Equal([b.Id], Editor.Selection);
        Assert.Same(b, Editor.Properties?.Event);
    }

    [Fact]
    public void Copy_and_paste_at_the_playhead_keeps_spacing_and_tracks()
    {
        _rig.AddSound(1);
        _rig.AddSound(1.5);
        Editor.SelectAllCommand.Execute(null);
        Editor.CopyCommand.Execute(null);

        _rig.Playback.Seek(S(4));
        Editor.PasteCommand.Execute(null);

        var events = _rig.Events(TrackKind.Audio);
        Assert.Equal([S(1), S(1.5), S(4), S(4.5)], events.Select(e => e.Start));
        Assert.Equal(4, events.Select(e => e.Id).Distinct().Count());   // copies, not the same events again
        Assert.Equal(events.Skip(2).Select(e => e.Id).ToHashSet(), Editor.Selection);   // the pasted ones are selected
    }

    [Fact]
    public void Duplicate_places_copies_right_after_the_selection()
    {
        var a = _rig.AddSound(1);
        var b = _rig.AddSound(3);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(b.Id, additive: true);

        Editor.DuplicateCommand.Execute(null);

        Assert.Equal([S(1), S(3), S(3), S(5)], _rig.Events(TrackKind.Audio).Select(e => e.Start));
    }

    [Fact]
    public void Delete_and_undo_restores_events_in_their_original_order()
    {
        var a = _rig.AddSound(1);
        var b = _rig.AddSound(2);
        var c = _rig.AddSound(3);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(c.Id, additive: true);

        Editor.DeleteCommand.Execute(null);
        Assert.Equal([b], _rig.Events(TrackKind.Audio));

        Editor.UndoCommand.Execute(null);
        Assert.Equal([a, b, c], _rig.Events(TrackKind.Audio));
    }

    [Fact]
    public void Typing_into_a_property_merges_into_one_undo_step()
    {
        var e = _rig.AddSound(1);
        var properties = Editor.Properties!;

        properties.StartSeconds = 2;
        properties.StartSeconds = 2.5m;
        properties.StartSeconds = 3;
        Assert.Equal(S(3), e.Start);

        Editor.UndoCommand.Execute(null);
        Assert.Equal(S(1), e.Start);
    }

    // ---- lighting: channels ----

    [Fact]
    public void A_lighting_track_starts_without_channels_and_add_event_opens_the_channel_picker()
    {
        Editor.SelectedTrack = _rig.Track(TrackKind.Lighting);
        Assert.Equal("No channels: right-click → Channels…", Editor.SelectedTrack.ChannelSummary);

        Editor.AddEventCommand.Execute(null);

        Assert.NotNull(Editor.ChannelPicker);
        Assert.False(Editor.ShowPropertiesHint);
        Editor.ChannelPicker!.CancelCommand.Execute(null);
        Assert.Null(Editor.ChannelPicker);
        Assert.Empty(_rig.LightingTrack.Channels);
    }

    [Fact]
    public void The_picker_offers_fixture_controls_by_function_and_other_channels_by_number()
    {
        Editor.EditChannels(_rig.Track(TrackKind.Lighting));
        var picker = Editor.ChannelPicker!;

        var rgb = picker.Fixtures.Single(f => f.Name == "Entry LED");
        Assert.Equal([LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Intensity), LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Red),
                      LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Green), LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Blue)],
                     rgb.Channels.Select(c => c.Target));
        Assert.Equal("U1 @20 · 4 ch", rgb.Details);
        var par = picker.Fixtures.Single(f => f.Name == "Par 1");
        Assert.Equal(Enumerable.Range(1, 5).Select(n => LaneTarget.FixtureChannel(_rig.Par.Id, n)), par.Channels.Select(c => c.Target));
    }

    [Fact]
    public void Ticking_a_fixture_picks_all_its_channels_and_ok_gives_the_track_a_lane_each_as_one_undo_step()
    {
        var row = _rig.Track(TrackKind.Lighting);
        Editor.EditChannels(row);
        var picker = Editor.ChannelPicker!;
        var rgb = picker.Fixtures.Single(f => f.Name == "Entry LED");

        rgb.IsChecked = true;
        rgb.Channels[0].IsChecked = false;   // not the dimmer
        Assert.Null(rgb.IsChecked);          // partly picked
        picker.RawFrom = 100;
        picker.RawTo = 101;
        picker.AddRawCommand.Execute(null);
        picker.OkCommand.Execute(null);

        Assert.Null(Editor.ChannelPicker);
        Assert.Equal(["Entry LED · Red", "Entry LED · Green", "Entry LED · Blue", "U1 · 100", "U1 · 101"], row.Lanes.Select(l => l.Label));
        Assert.True(row.IsExpanded);   // its first channels: expanded, showing envelopes
        Assert.Equal("#FF5252", row.Lanes[0].Color);
        Assert.Equal(TimelineEditorViewModel.ExpandedTrackHeight, row.Height);

        Editor.UndoCommand.Execute(null);
        Assert.Empty(_rig.LightingTrack.Channels);
    }

    [Fact]
    public void The_picker_s_map_highlights_picked_channels_and_clicking_a_fixture_ticks_it()
    {
        Editor.EditChannels(_rig.Track(TrackKind.Lighting));
        var picker = Editor.ChannelPicker!;
        var rgb = picker.Fixtures.Single(f => f.Name == "Entry LED");
        Assert.Equal("Nothing picked yet.", picker.MapSummary);
        Assert.Equal(2, picker.MapBlocks.Count);   // both fixtures are on universe 1

        rgb.Channels[1].IsChecked = true;   // Red at 21
        rgb.Channels[2].IsChecked = true;   // Green at 22
        picker.RawFrom = 100;
        picker.RawTo = 100;
        picker.AddRawCommand.Execute(null);

        Assert.Equal([21, 22, 100], picker.MapHighlights.Order());
        Assert.Equal("Picked on universe 1: 21–22, 100", picker.MapSummary);

        picker.ToggleFixtureCommand.Execute(_rig.Rgb.Id);   // a click on its block: all of it
        Assert.Equal([20, 21, 22, 23, 100], picker.MapHighlights.Order());
        picker.ToggleFixtureCommand.Execute(_rig.Rgb.Id);   // again: none of it
        Assert.Equal([100], picker.MapHighlights);
    }

    [Fact]
    public void Changing_channels_keeps_the_envelopes_of_channels_that_stay()
    {
        var red = _rig.RedLane();
        var handle = Handle(AddClip(0), red, 1, 200);
        Editor.EditChannels(_rig.Track(TrackKind.Lighting));
        var picker = Editor.ChannelPicker!;
        Assert.True(picker.Fixtures.Single(f => f.Name == "Entry LED").Channels[1].IsChecked);   // Red, already on the track
        picker.Fixtures.Single(f => f.Name == "Entry LED").Channels[2].IsChecked = true;        // add Green

        picker.OkCommand.Execute(null);

        Assert.Same(red, _rig.LightingTrack.Channels[0]);
        Assert.Same(handle, Clip().LaneFor(red.Target)!.Points[0]);
        Assert.Equal(2, _rig.LightingTrack.Channels.Count);
    }

    [Fact]
    public void An_audio_tracks_output_is_picked_from_its_dropdown_and_undoable()
    {
        var row = _rig.Track(TrackKind.Audio);
        Assert.Equal([new AudioDeviceChoice("Default output", null)], row.Outputs);   // no device list (tests): just the default
        Assert.Null(row.SelectedOutput!.Name);

        _rig.Editor.SetTrackAudioDevice(row.Track, "USB Audio CODEC");

        Assert.Equal("USB Audio CODEC", row.Track.AudioDevice);
        // Not on this computer: it stays chosen, marked as not connected.
        Assert.Equal("USB Audio CODEC (not connected)", row.SelectedOutput!.Label);
        Assert.Equal("Undo Change track output", Editor.UndoLabel);

        Editor.UndoCommand.Execute(null);
        Assert.Null(row.Track.AudioDevice);
        Assert.Equal("Default output", row.SelectedOutput!.Label);
    }

    [Fact]
    public void Clearing_the_output_list_under_the_dropdown_doesnt_change_the_track()
    {
        var row = _rig.Track(TrackKind.Audio);
        row.Track.AudioDevice = "Lobby";

        row.SelectedOutput = null;

        Assert.Equal("Lobby", row.Track.AudioDevice);
    }

    [Fact]
    public void Rows_are_one_per_track_and_a_lighting_track_is_taller_expanded()
    {
        _rig.RedLane();
        var lighting = _rig.Track(TrackKind.Lighting);

        Assert.Equal([(0, 150.0), (150, 64)], Editor.Rows.Select(r => (r.Top, r.Height)));
        lighting.IsExpanded = false;
        Assert.Equal([(0, 64.0), (64, 64)], Editor.Rows.Select(r => (r.Top, r.Height)));   // room for a preview of the envelopes
        Assert.Equal(128, Editor.ContentHeight);
    }

    // ---- lighting: Level Control clips and their envelopes ----

    LightingClip Clip(int index = 0) => _rig.LightingTrack.Events.OfType<LightingClip>().OrderBy(c => c.Start).ElementAt(index);

    /// <summary>A Level Control clip added at a time on the lighting track (as Add does at the playhead).</summary>
    LightingClip AddClip(double start, double length = 4)
    {
        _rig.Playback.Seek(S(start));
        Editor.SelectedTrack = _rig.Track(TrackKind.Lighting);
        Editor.AddEventCommand.Execute(null);
        var clip = _rig.LightingTrack.Events.OfType<LightingClip>().Single(c => c.Start == S(start));
        if (length != 4) Editor.ResizeEvent(clip.Id, S(length));
        return clip;
    }

    /// <summary>Adds a handle on a channel's envelope (right-click → Add Envelope Handle) and sets its level, as dragging it up would.</summary>
    CurvePoint Handle(LightingClip clip, ChannelLane channel, double at, byte level)
    {
        var handle = Editor.AddHandle(clip, channel.Target, S(at))!;
        Editor.PointProperties!.Dmx = level;
        return handle;
    }

    CurvePoint EndHandle(LightingClip clip, ChannelLane channel) => clip.LaneFor(channel.Target)!.Points[^1];

    /// <summary>A clip's envelope for a channel, as (time on the timeline, level), without the start handle.</summary>
    static IEnumerable<(TimeSpan, int)> Envelope(LightingClip clip, ChannelLane channel) =>
        (clip.LaneFor(channel.Target)?.Points ?? []).Select(p => (clip.Start + p.Time, (int)p.Value));

    [Fact]
    public void Add_on_a_lighting_track_adds_a_Level_Control_clip_holding_every_channel_where_it_is()
    {
        var red = _rig.RedLane();
        var first = AddClip(0);
        Editor.SelectPoint(Editor.AddHandle(first, red.Target, S(2))!, additive: false);
        Editor.SelectPoint(EndHandle(first, red), additive: false);
        Editor.PointProperties!.Dmx = 200;

        var second = AddClip(6);

        Assert.Equal(("Level Control", S(6), LightingClip.DefaultLength), (second.Name, second.Start, second.Length));
        Assert.Empty(second.Lanes);   // no envelopes yet: every channel holds its level through it
        Assert.Equal([second.Id], Editor.Selection);
        Assert.Equal("Undo Add Level Control clip", Editor.UndoLabel);
        _rig.Play();
        _rig.Playback.Seek(S(8));
        Assert.Equal(200, _rig.Output(21));
    }

    [Fact]
    public void A_new_handle_keeps_the_level_until_it_is_dragged_and_edits_play_straight_away()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);

        var handle = Editor.AddHandle(clip, red.Target, S(2))!;

        Assert.Equal([(S(2), 0), (S(4), 0)], Envelope(clip, red));   // and an end handle, holding the start level
        Assert.Equal(S(2), handle.Time);
        Assert.Same(handle, Editor.PointProperties?.Point);
        Assert.Equal(2, Editor.PointProperties!.TimeSeconds);   // shown as time on the timeline
        Assert.Equal("Undo Add envelope handle", Editor.UndoLabel);
        _rig.Play();
        _rig.Playback.Seek(S(1));
        Assert.Equal(0, _rig.Output(21));

        Editor.PointProperties.Dmx = 200;
        _rig.Seek(S(1));
        Assert.Equal(100, _rig.Output(21));   // up from the start level to the handle...
        _rig.Seek(S(3));
        Assert.Equal(100, _rig.Output(21));   // ...and back down to the end handle
    }

    [Fact]
    public void A_handle_added_on_a_curve_takes_its_curve_type()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        var first = Handle(clip, red, 1, 100);
        Editor.SetCurve([EndHandle(clip, red)], CurveKind.Spline);

        var second = Editor.AddHandle(clip, red.Target, S(3))!;

        Assert.Equal(CurveKind.Spline, second.Curve);
        Assert.Equal(CurveKind.Linear, first.Curve);
    }

    [Fact]
    public void Handles_move_snapped_and_stay_between_their_neighbours_and_an_end_handle_only_changes_level()
    {
        var red = _rig.RedLane();
        var clip = AddClip(1);
        var a = Handle(clip, red, 2, 100);
        var b = Handle(clip, red, 3, 240);
        Editor.SelectPoint(a, additive: false);
        Editor.SelectPoint(b, additive: true);
        Editor.Snap = TimelineEditorViewModel.SnapOptions.Single(o => o.Interval == S(0.5));

        Editor.MoveSelectedPoints(S(0.7), +30, anchor: a);   // a lands on the grid at 2.5 s; b keeps its distance
        Assert.Equal([(S(2.5), 130), (S(3.5), 255), (S(5), 0)], Envelope(clip, red));

        Editor.MoveSelectedPoints(S(10), 0, anchor: a);   // no further than the end handle
        Assert.Equal([(S(4), 130), (S(5), 255), (S(5), 0)], Envelope(clip, red));

        var end = EndHandle(clip, red);
        Editor.SelectPoint(end, additive: false);
        Editor.MoveSelectedPoints(S(-2), +50);
        Assert.Equal((S(4), 50), (end.Time, (int)end.Value));

        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([(S(2), 100), (S(3), 240), (S(5), 0)], Envelope(clip, red));
    }

    [Fact]
    public void A_handle_can_t_be_dragged_past_its_neighbour()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        var a = Handle(clip, red, 1, 10);
        var b = Handle(clip, red, 2, 20);
        Editor.SelectPoint(a, additive: false);

        Editor.MoveSelectedPoints(S(2), 0);

        Assert.Equal(b.Time, a.Time);
        Assert.Equal([a, b, EndHandle(clip, red)], clip.LaneFor(red.Target)!.Points);
    }

    [Fact]
    public void Curve_types_and_delete_are_undoable_and_an_end_handle_can_t_be_deleted()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        var a = Handle(clip, red, 1, 10);
        var b = Handle(clip, red, 2, 20);
        var lane = clip.LaneFor(red.Target)!;
        Editor.SetCurve([b], CurveKind.Step);
        Assert.Equal(CurveKind.Step, b.Curve);
        Assert.Equal("Undo Make handle step", Editor.UndoLabel);

        Editor.SelectPoint(b, additive: false);
        Editor.DeleteCommand.Execute(null);
        Assert.Equal([a, lane.Points[^1]], lane.Points);
        Assert.Empty(Editor.SelectedPoints);
        Assert.Null(Editor.PointProperties);

        Editor.SelectPoint(lane.Points[^1], additive: false);
        Assert.False(Editor.PointProperties!.CanDelete);
        Editor.DeleteCommand.Execute(null);
        Assert.Equal(2, lane.Points.Count);

        Editor.UndoCommand.Execute(null);
        Assert.Equal([a, b, lane.Points[^1]], lane.Points);
        Editor.UndoCommand.Execute(null);
        Assert.Equal(CurveKind.Linear, b.Curve);
    }

    [Fact]
    public void Typing_a_handle_s_level_merges_into_one_undo_step_and_percent_and_dmx_agree()
    {
        var red = _rig.RedLane();
        var a = Handle(AddClip(0), red, 1, 10);
        var properties = Editor.PointProperties!;

        properties.Percent = 5;
        properties.Percent = 50;
        properties.Percent = 100;

        Assert.Equal(255, a.Value);
        Assert.Equal(255, properties.Dmx);
        Editor.UndoCommand.Execute(null);
        Assert.Equal(10, a.Value);
    }

    [Fact]
    public void An_audio_track_s_volume_scales_its_clips_and_a_slider_drag_undoes_in_one_step()
    {
        _rig.PickedFile = _rig.ToneFile(1, 0.5);
        _rig.AddSound(1);
        var row = _rig.Track(TrackKind.Audio);

        foreach (double percent in new[] { 90.0, 75, 60, 50 }) row.VolumePercent = percent;   // as a slider drag sends them

        Assert.Equal((0.5, "50%"), (row.Track.Volume, row.VolumeLabel));
        Assert.Equal(0.5, Assert.Single(_rig.Compiled.AudioCues).Volume, 6);
        Editor.EditTrackProperties(row);
        Assert.Equal(50m, Editor.TrackProperties!.VolumePercent);
        Assert.True(Editor.TrackProperties.IsAudio);

        Editor.UndoCommand.Execute(null);

        Assert.Equal(1.0, row.Track.Volume);
        Assert.Equal(1.0, Assert.Single(_rig.Compiled.AudioCues).Volume, 6);
        Assert.Equal(100m, Editor.TrackProperties.VolumePercent);

        Editor.TrackProperties.VolumePercent = 30;
        Assert.Equal((0.3, 30.0), (row.Track.Volume, row.VolumePercent));
    }

    [Fact]
    public async Task A_sound_s_waveform_is_ready_once_its_file_has_been_measured()
    {
        _rig.PickedFile = _rig.ToneFile(2, 0.5);
        var sound = (AudioEvent)_rig.AddSound(1);

        Waveform? waveform = null;
        for (int i = 0; i < 100 && (waveform = Editor.WaveformOf(sound)) is null; i++) await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.NotNull(waveform);
        Assert.Equal(2, waveform.Length.TotalSeconds, 0.05);
        Assert.Equal(0.5, waveform.PeakBetween(S(0.5), S(1.5)), 0.05);
    }

    [Fact]
    public void An_effect_is_added_at_the_playhead_and_its_settings_change_how_it_plays_undoably()
    {
        _rig.RedLane();
        _rig.Playback.Seek(S(2));
        Editor.SelectedTrack = _rig.Track(TrackKind.Lighting);

        Editor.AddEffectCommand.Execute(EffectKind.Lightning);

        var clip = Clip();
        Assert.Equal(("Lightning", S(2), S(4), EffectKind.Lightning), (clip.Name, clip.Start, clip.Length, clip.Effect!.Kind));
        int Peak() => Editor.LevelsOf(_rig.LightingTrack)[0].Find(clip)!.Handles.Max(p => p.Value);
        Assert.Equal(0xDC, Peak());   // red's share of the cold white flash (no dimmer on the track: the colour carries it)
        var properties = Editor.Properties!;
        Assert.Equal((true, false, "Lightning effect"), (properties.IsEffect, properties.IsLevelControl, properties.Kind));

        properties.StrengthPercent = 60;
        properties.StrengthPercent = 50;   // typed: one undo step
        Assert.Equal(110, Peak());
        properties.EffectColor = "#FF0000";
        Assert.Equal(128, Peak());
        properties.SelectedEffect = EffectInfo.Of(EffectKind.Strobe);
        Assert.Equal((EffectKind.Strobe, 10.0, (string?)null), (clip.Effect!.Kind, clip.Effect.Rate, clip.Effect.Color));

        for (int i = 0; i < 3; i++) Editor.UndoCommand.Execute(null);

        Assert.Equal((EffectKind.Lightning, 1.0, (string?)null), (clip.Effect!.Kind, clip.Effect.Strength, clip.Effect.Color));
        Assert.Equal(0xDC, Peak());
    }

    [Fact]
    public void Converting_an_effect_gives_a_level_control_clip_that_plays_the_same_with_handles_to_edit()
    {
        var red = _rig.RedLane();
        var before = AddClip(0, 2);
        Handle(before, red, 1, 100);
        Editor.SelectPoint(EndHandle(before, red), additive: false);
        Editor.PointProperties!.Dmx = 80;
        _rig.Playback.Seek(S(3));
        Editor.AddEffectCommand.Execute(EffectKind.Fire);
        var fire = Clip(1);
        IEnumerable<(TimeSpan, byte)> Played() => Editor.LevelsOf(_rig.LightingTrack)[0].Find(fire)!.Handles.Select(p => (p.Time, p.Value)).ToList();
        var played = Played();

        Assert.Equal(80, played.First().Item2);   // it starts where the clip before left the light
        Assert.Null(Editor.AddHandle(fire, red.Target, S(4)));   // an effect's envelopes are made, not edited
        Editor.SplitAt(S(5));
        Assert.Equal(2, _rig.LightingTrack.Events.Count);        // nor split

        Editor.ConvertToLevelControl(fire);

        Assert.Null(fire.Effect);
        Assert.Equal(played, Played());
        Assert.Equal(red.Target, Assert.Single(fire.Lanes).Target);
        Assert.NotNull(Editor.AddHandle(fire, red.Target, S(4.01)));

        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.Equal(EffectKind.Fire, fire.Effect!.Kind);
        Assert.Empty(fire.Lanes);
        Assert.Equal(played, Played());
    }

    [Fact]
    public void Deleting_the_first_clip_resets_the_starting_levels_it_used_but_not_fixed_ones_or_for_later_clips()
    {
        var red = _rig.RedLane();
        Editor.SetTrackChannels(_rig.LightingTrack, [red.Target, LaneTarget.Raw(1, 100)]);
        var (start, held) = (_rig.LightingTrack.Channels[0], _rig.LightingTrack.Channels[1]);
        Editor.SetStartLevel(start, 194);
        Editor.SetChannelStart(held, ChannelStart.Fixed);
        Editor.SetStartLevel(held, 60);
        var first = AddClip(0, 2);
        var second = AddClip(5, 2);

        Editor.SelectEvent(second.Id, additive: false);
        Editor.DeleteCommand.Execute(null);
        Assert.Equal((194, 60), (start.StartLevel, held.StartLevel));   // not the first clip: nothing to reset

        Editor.SelectEvent(first.Id, additive: false);
        Editor.DeleteCommand.Execute(null);
        Assert.Equal((0, 60), (start.StartLevel, held.StartLevel));     // a fixed level is the track's own

        Editor.UndoCommand.Execute(null);
        Assert.Equal(194, start.StartLevel);
        Assert.Contains(first, _rig.LightingTrack.Events);
    }

    LaneTarget RgbControl(ChannelFunction control) => LaneTarget.FixtureControl(_rig.Rgb.Id, control);

    [Fact]
    public void A_colour_handle_sets_a_fixture_s_colour_channels_together_and_moves_ungroups_and_deletes_as_one()
    {
        Editor.SetTrackChannels(_rig.LightingTrack, [RgbControl(ChannelFunction.Intensity), RgbControl(ChannelFunction.Red), RgbControl(ChannelFunction.Green), RgbControl(ChannelFunction.Blue)]);
        var clip = AddClip(0, 4);

        var group = Editor.AddGroupHandle(clip, _rig.Rgb.Id, S(2), HandleGroupKind.Colour);

        Assert.Equal(3, group.Count);   // red, green and blue: not the dimmer
        Assert.All(group, p => Assert.Equal((S(2), group[0].Group), (p.Time, p.Group)));
        Assert.Equal(3, Editor.SelectedPoints.Count);
        var panel = Editor.SelectedHandleGroup!;
        Assert.Equal((true, "Colour handle · Entry LED"), (panel.IsColour, panel.Heading));

        panel.Colour = Avalonia.Media.Color.FromRgb(255, 128, 0);
        panel.Colour = Avalonia.Media.Color.FromRgb(255, 100, 0);   // dragging in the picker: one undo step
        Assert.Equal([(byte)255, (byte)100, (byte)0], group.Select(p => p.Value));
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 100, 0), Editor.SelectedHandleGroup!.Colour);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([(byte)0, (byte)0, (byte)0], group.Select(p => p.Value));

        Editor.SelectPoint(group[1], additive: false);   // one brings the group
        Assert.Equal(3, Editor.SelectedPoints.Count);
        Editor.MoveSelectedPoints(S(1), 0, group[1]);
        Assert.All(group, p => Assert.Equal(S(3), p.Time));

        Editor.SelectedHandleGroup!.UngroupCommand.Execute(null);
        Editor.SelectPoint(group[0], additive: false);
        Assert.Single(Editor.SelectedPoints);
        Editor.UndoCommand.Execute(null);
        Editor.SelectPoint(group[0], additive: false);
        Assert.Equal(3, Editor.SelectedPoints.Count);

        Editor.DeleteCommand.Execute(null);
        Assert.All(clip.Lanes, l => Assert.Equal([S(4)], l.Points.Select(p => p.Time)));   // only their end handles are left
    }

    [Fact]
    public void Snapshot_puts_the_levels_set_by_hand_into_grouped_handles_at_a_time()
    {
        Editor.SetTrackChannels(_rig.LightingTrack, [RgbControl(ChannelFunction.Red), RgbControl(ChannelFunction.Green)]);
        var clip = AddClip(0, 4);
        _rig.Session.Engine.SetFixtureControl(_rig.Rgb.Id, ChannelFunction.Red, 200);
        _rig.Session.Engine.SetFixtureControl(_rig.Rgb.Id, ChannelFunction.Green, 50);
        var targets = _rig.LightingTrack.Channels.Select(c => c.Target).ToList();

        var look = Editor.Snapshot(clip, S(1.5), targets);

        Assert.Equal([(S(1.5), (byte)200), (S(1.5), (byte)50)], look.Select(p => (p.Time, p.Value)));
        Assert.NotNull(look[0].Group);
        Assert.Equal(look[0].Group, look[1].Group);
        Assert.Equal(2, Editor.SelectedPoints.Count);

        var ends = Editor.Snapshot(clip, S(4), targets);   // at the clip's end: its end handles
        Assert.All(ends, p => Assert.True(clip.IsEndHandle(p) && p.Group is null));
        Assert.Equal([(byte)200, (byte)50], ends.Select(p => p.Value));
        Assert.Empty(Editor.Snapshot(clip, S(6), targets));   // outside the clip

        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.All(clip.Lanes, l => Assert.Equal([(S(4), (byte)0)], l.Points.Select(p => (p.Time, p.Value))));
    }

    [Fact]
    public void A_position_handle_points_a_moving_light_with_pan_and_tilt_together()
    {
        var mover = new FixtureInstance { Name = "Mover", ProfileId = TimelineRig.MoverProfile.Id, ModeName = "3ch", StartAddress = 60 };
        _rig.Session.Project.Fixtures.Add(mover);
        _rig.Session.Apply();
        Editor.SetTrackChannels(_rig.LightingTrack, [LaneTarget.FixtureControl(mover.Id, ChannelFunction.Pan), LaneTarget.FixtureControl(mover.Id, ChannelFunction.Tilt),
                                                     LaneTarget.FixtureControl(mover.Id, ChannelFunction.Intensity)]);
        var clip = AddClip(0, 4);
        Assert.Empty(Editor.AddGroupHandle(clip, mover.Id, S(1), HandleGroupKind.Colour));   // it has no colours

        var position = Editor.AddGroupHandle(clip, mover.Id, S(1), HandleGroupKind.Position);
        var panel = Editor.SelectedHandleGroup!;
        panel.Pan = 64;
        panel.Tilt = 200;

        Assert.Equal((true, "Position handle · Mover"), (panel.IsPosition, panel.Heading));
        Assert.Equal([(byte)64, (byte)200], position.Select(p => p.Value));
        Editor.UndoCommand.Execute(null);   // pan and tilt set together: one step
        Assert.Equal([(byte)0, (byte)0], position.Select(p => p.Value));
    }

    [Fact]
    public void The_envelope_editor_snapshots_the_ticked_channels_at_the_playhead_and_says_why_when_it_cannot()
    {
        Editor.SetTrackChannels(_rig.LightingTrack, [RgbControl(ChannelFunction.Red), RgbControl(ChannelFunction.Green)]);
        var clip = AddClip(2, 4);
        Editor.OpenEnvelopeEditor(clip);
        var envelopes = Editor.EnvelopeEditor!;
        _rig.Session.Engine.SetFixtureControl(_rig.Rgb.Id, ChannelFunction.Red, 180);

        _rig.Playback.Seek(S(1));
        envelopes.SnapshotCommand.Execute(null);
        Assert.Contains("playhead into the clip", envelopes.Status);

        envelopes.Fixtures[0].Channels[1].IsChecked = false;   // Green unticked: left alone
        _rig.Playback.Seek(S(3));
        envelopes.SnapshotCommand.Execute(null);

        Assert.StartsWith("Snapshot: 1 handle at", envelopes.Status);
        Assert.Equal([(S(1), (byte)180)], clip.LaneFor(RgbControl(ChannelFunction.Red))!.Points.Where(p => !clip.IsEndHandle(p)).Select(p => (p.Time, p.Value)));
        Assert.Null(clip.LaneFor(RgbControl(ChannelFunction.Green)));
        Assert.True(envelopes.CanAddColourHandle);   // red and green: enough for a colour handle
    }

    [Fact]
    public void Selecting_a_handle_clears_selected_clips_and_the_other_way_round()
    {
        var sound = _rig.AddSound(1);
        var red = _rig.RedLane();
        var handle = Editor.AddHandle(AddClip(0), red.Target, S(1))!;

        Editor.SelectEvent(sound.Id, additive: false);
        Assert.Empty(Editor.SelectedPoints);
        Assert.Null(Editor.PointProperties);

        Editor.SelectPoint(handle, additive: false);
        Assert.Empty(Editor.Selection);
        Assert.NotNull(Editor.PointProperties);
    }

    [Fact]
    public void A_clip_starts_where_the_previous_clip_left_the_channel_and_follows_it_when_that_changes()
    {
        var red = _rig.RedLane();
        var first = AddClip(0);
        Editor.AddHandle(first, red.Target, S(2));
        Editor.SelectPoint(EndHandle(first, red), additive: false);
        Editor.PointProperties!.Dmx = 200;
        var second = AddClip(6);
        Handle(second, red, 8, 0);

        Assert.Equal(200, Editor.LevelsOf(_rig.LightingTrack)[0].Find(second)!.StartLevel);
        _rig.Play();
        _rig.Playback.Seek(S(7));
        Assert.Equal(100, _rig.Output(21));   // 200 → 0 between 6 and 8 s

        Editor.SelectPoint(EndHandle(first, red), additive: false);
        Editor.PointProperties.Dmx = 100;
        _rig.Seek(S(7));
        Assert.Equal(50, _rig.Output(21));
    }

    [Fact]
    public void Moving_a_clip_moves_its_envelopes()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        Handle(clip, red, 2, 200);
        Editor.SelectEvent(clip.Id, additive: false);

        Editor.MoveSelection(S(10));

        Assert.Equal([(S(12), 200), (S(14), 0)], Envelope(clip, red));
        _rig.Play();
        _rig.Playback.Seek(S(11));
        Assert.Equal(100, _rig.Output(21));
    }

    [Fact]
    public void Resizing_a_clip_scales_its_envelopes_or_with_scaling_off_crops_them()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        Handle(clip, red, 2, 200);

        Editor.ResizeEvent(clip.Id, S(8));   // right edge: twice as long, scaled
        Assert.Equal([(S(4), 200), (S(8), 0)], Envelope(clip, red));
        Editor.ResizeEventStart(clip.Id, S(4));   // left edge in by 4 s: the same end, half as long
        Assert.Equal([(S(6), 200), (S(8), 0)], Envelope(clip, red));
        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([(S(2), 200), (S(4), 0)], Envelope(clip, red));

        Editor.SelectEvent(clip.Id, additive: false);
        Editor.Properties!.ScaleOnResize = false;
        Editor.Properties.ClipLengthSeconds = 3;   // the properties panel resizes the same way
        Assert.Equal([(S(2), 200), (S(3), 100)], Envelope(clip, red));
        Assert.Equal("Undo Crop clip", Editor.UndoLabel);
        Assert.Equal("Level Control", Editor.Properties.Kind);
    }

    [Fact]
    public void Splitting_at_the_playhead_gives_two_clips_that_play_as_the_one_did_and_undo_joins_them()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        Handle(clip, red, 2, 200);
        _rig.Play();
        var before = new[] { 0.5, 1.5, 2.5, 3.5 }.Select(t => { _rig.Seek(S(t)); return _rig.Output(21); }).ToList();
        _rig.Manager.Stop(Editor.SelectedTimeline!.Id);   // so the playhead stays exactly where it's put
        Editor.SelectEvent(clip.Id, additive: false);
        _rig.Playback.Seek(S(1));

        Editor.SplitCommand.Execute(null);

        Assert.Equal([(S(0), S(1)), (S(1), S(3))], _rig.LightingTrack.Events.OfType<LightingClip>().Select(c => (c.Start, c.Length)));
        _rig.Play();
        Assert.Equal(before, new[] { 0.5, 1.5, 2.5, 3.5 }.Select(t => { _rig.Seek(S(t)); return _rig.Output(21); }));
        Assert.Equal([Clip(1).Id], Editor.Selection);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([(S(2), 200), (S(4), 0)], Envelope(Assert.IsType<LightingClip>(Assert.Single(_rig.LightingTrack.Events)), red));
    }

    [Fact]
    public void A_locked_clip_can_t_be_moved_resized_split_deleted_or_edited_until_unlocked()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        var handle = Handle(clip, red, 2, 200);
        Editor.SelectEvent(clip.Id, additive: false);
        Editor.LockCommand.Execute(null);
        Assert.True(clip.Locked);

        Editor.MoveSelection(S(5));
        Editor.ResizeEvent(clip.Id, S(8));
        _rig.Playback.Seek(S(1));
        Editor.SplitCommand.Execute(null);
        Editor.DeleteCommand.Execute(null);
        Assert.Null(Editor.AddHandle(clip, red.Target, S(1)));
        Editor.SelectPoint(handle, additive: false);
        Editor.MoveSelectedPoints(S(0.5), 10);

        Assert.Equal((S(0), S(4)), (clip.Start, clip.Length));
        Assert.Same(clip, Assert.Single(_rig.LightingTrack.Events));
        Assert.Equal((S(2), 200), (handle.Time, (int)handle.Value));

        Editor.SelectEvent(clip.Id, additive: false);
        Editor.LockCommand.Execute(null);
        Editor.MoveSelection(S(5));
        Assert.Equal(S(5), clip.Start);
    }

    [Fact]
    public void Linked_clips_move_together_until_unlinked()
    {
        _rig.RedLane();
        var clip = AddClip(0);
        var sound = _rig.AddSound(6);
        Editor.SelectEvent(clip.Id, additive: false);
        Editor.SelectEvent(sound.Id, additive: true);
        Assert.True(Editor.CanLink);
        Editor.LinkClipsCommand.Execute(null);

        Editor.SelectEvent(clip.Id, additive: false);
        Editor.MoveSelection(S(2));
        Assert.Equal((S(2), S(8)), (clip.Start, sound.Start));

        Editor.UnlinkClipsCommand.Execute(null);
        Editor.MoveSelection(S(2));
        Assert.Equal((S(4), S(8)), (clip.Start, sound.Start));
    }

    [Fact]
    public void A_channel_holds_its_start_level_from_the_start_unless_it_is_left_alone_until_its_first_clip()
    {
        var red = _rig.RedLane();
        var clip = AddClip(2);
        Handle(clip, red, 4, 0);
        Editor.EditTrackProperties(_rig.Track(TrackKind.Lighting));
        var channel = Editor.TrackProperties!.Channels.Single();
        Assert.Equal("Entry LED · Red", channel.Label);

        channel.Percent = 100;
        Assert.Equal(255, red.StartLevel);
        _rig.Play();
        _rig.Playback.Seek(S(3));
        Assert.InRange(_rig.Output(21), 127, 128);   // half way from 255 to 0 between 2 and 4 s
        _rig.Seek(S(1));
        Assert.Equal(255, _rig.Output(21));   // held from the timeline's start (VenueMagic)

        channel.SourceIndex = 2;   // leave alone
        Assert.Equal(ChannelStart.LeaveAlone, red.Start);
        _rig.Seek(S(1));
        _rig.Output(21);   // one playback update
        Assert.False(_rig.Session.Engine.IsTimelineControlled(1, 21));   // not touched before the first clip
        Editor.UndoCommand.Execute(null);
        Assert.Equal(ChannelStart.FirstClip, red.Start);
        channel.SourceIndex = 1;
        Assert.Equal(ChannelStart.Fixed, red.Start);
    }

    [Fact]
    public void Copy_and_paste_of_a_clip_makes_an_independent_copy()
    {
        var red = _rig.RedLane();
        var original = Handle(AddClip(0), red, 1, 50);
        Editor.SelectEvent(Clip().Id, additive: false);
        Editor.CopyCommand.Execute(null);
        _rig.Playback.Seek(S(10));

        Editor.PasteCommand.Execute(null);
        Clip(1).Lanes[0].Points[0].Value = 99;

        Assert.Equal(S(10), Clip(1).Start);
        Assert.Equal(50, original.Value);
    }

    [Fact]
    public void Taking_a_channel_off_the_track_removes_its_envelopes_from_clips_and_undo_brings_them_back()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        Handle(clip, red, 1, 77);

        Editor.SetTrackChannels(_rig.LightingTrack, []);
        Assert.Empty(clip.Lanes);

        Editor.UndoCommand.Execute(null);
        Assert.Equal(77, clip.LaneFor(red.Target)!.Points[0].Value);
    }

    [Fact]
    public void Dragging_an_audio_clip_s_left_edge_trims_into_the_file_keeping_its_end()
    {
        _rig.PickedFile = Path.GetFullPath("thunder.wav");
        Editor.SelectedTrack = _rig.Track(TrackKind.Audio);
        Editor.AddEventCommand.Execute(null);
        var audio = (AudioEvent)_rig.Events(TrackKind.Audio).Single();
        audio.Length = S(10);

        Editor.ResizeEventStart(audio.Id, S(3));
        Assert.Equal((S(3), S(3), S(7)), (audio.Start, audio.Offset, audio.Length!.Value));

        Editor.ResizeEventStart(audio.Id, S(-5));   // not before the start of the file
        Assert.Equal((S(0), S(0), S(10)), (audio.Start, audio.Offset, audio.Length!.Value));
    }

    [Fact]
    public void A_channel_whose_fixture_is_removed_is_marked_and_skipped()
    {
        var red = _rig.RedLane();
        Handle(AddClip(0), red, 1, 255);
        _rig.Session.Project.Fixtures.Remove(_rig.Rgb);
        _rig.Session.Apply();

        Assert.Contains(red.Id, Editor.ErrorEvents);
        Assert.Equal("Missing fixture · Red", _rig.Track(TrackKind.Lighting).Lanes.Single().Label);   // the header follows the patch
    }

    [Fact]
    public void Edits_during_playback_never_move_the_playhead()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        _rig.Playback.Seek(S(0.5));
        Editor.AddHandle(clip, red.Target, S(3));
        Assert.Equal(S(0.5), _rig.Status.Position);
    }

    // ---- the Envelope Editor ----

    [Fact]
    public void The_envelope_editor_shows_a_clip_s_channels_by_fixture_and_follows_the_selection()
    {
        _rig.RedLane();
        Editor.SetTrackChannels(_rig.LightingTrack, [LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Red), LaneTarget.FixtureChannel(_rig.Par.Id, 2), LaneTarget.Raw(1, 300)]);
        var first = AddClip(0);
        var second = AddClip(6);
        var sound = _rig.AddSound(3);

        Editor.OpenEnvelopeEditor(first);
        var envelopes = Editor.EnvelopeEditor!;

        Assert.Same(first, envelopes.Clip);
        Assert.Equal([first.Id], Editor.Selection);
        Assert.Equal(["Entry LED", "Par 1", "Raw DMX channels"], envelopes.Fixtures.Select(f => f.Name));
        Assert.Equal(["Red"], envelopes.Fixtures[0].Channels.Select(c => c.Label));
        Assert.StartsWith("Envelope Editor · Level Control · Lighting", envelopes.Title);

        Editor.SelectEvent(second.Id, additive: false);
        Assert.Same(second, envelopes.Clip);
        Editor.SelectEvent(sound.Id, additive: false);   // not a lighting clip: it stays
        Assert.Same(second, envelopes.Clip);
        Editor.SelectPoint(Editor.AddHandle(first, LaneTarget.Raw(1, 300), S(1))!, additive: false);   // a handle in another clip
        Assert.Same(first, envelopes.Clip);

        Editor.SelectEvent(first.Id, additive: false);
        Editor.DeleteCommand.Execute(null);
        Assert.Null(envelopes.Clip);   // deleted: nothing to show

        Editor.CloseEnvelopeEditor();
        Assert.Null(Editor.EnvelopeEditor);
    }

    [Fact]
    public void Unticked_channels_are_hidden_and_a_fixture_s_tick_covers_its_channels()
    {
        _rig.RedLane();
        Editor.SetTrackChannels(_rig.LightingTrack, [LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Red), LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Blue)]);
        Editor.OpenEnvelopeEditor(AddClip(0));
        var envelopes = Editor.EnvelopeEditor!;
        var rgb = envelopes.Fixtures.Single();

        rgb.Channels[1].IsChecked = false;
        Assert.Null(rgb.IsChecked);
        Assert.Equal([ChannelFunction.Red], envelopes.ShownLevels.Select(l => l.Channel.Target.Control!.Value));

        rgb.IsChecked = false;
        Assert.Empty(envelopes.ShownLevels);
        envelopes.ShowAllCommand.Execute(null);
        Assert.Equal(2, envelopes.ShownLevels.Count);
    }

    [Fact]
    public void Adding_a_handle_in_the_envelope_editor_can_set_its_level_and_is_one_edit_per_change()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        Editor.OpenEnvelopeEditor(clip);

        var handle = Editor.EnvelopeEditor!.AddHandle(red.Target, S(2), level: 200)!;

        Assert.Equal((S(2), 200), (handle.Time, (int)handle.Value));
        Editor.UndoCommand.Execute(null);   // the level
        Assert.Equal(0, handle.Value);
        Editor.UndoCommand.Execute(null);   // the handle
        Assert.Empty(clip.Lanes);
    }

    [Fact]
    public void With_update_lamps_on_selecting_a_handle_shows_its_level_on_the_lights()
    {
        var red = _rig.RedLane();
        var clip = AddClip(0);
        var handle = Handle(clip, red, 2, 200);
        Editor.OpenEnvelopeEditor(clip);
        var envelopes = Editor.EnvelopeEditor!;
        Assert.Equal(TransportState.Stopped, _rig.Status.State);

        envelopes.UpdateLamps = true;
        Editor.SelectPoint(handle, additive: false);

        Assert.Equal((TransportState.Paused, S(2)), (_rig.Status.State, _rig.Status.Position));
        Assert.Equal(200, _rig.Output(21));
    }

    [Fact]
    public void New_handles_go_to_the_active_channel_which_follows_the_list_and_the_selected_handle()
    {
        _rig.RedLane();
        var red = LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Red);
        var blue = LaneTarget.FixtureControl(_rig.Rgb.Id, ChannelFunction.Blue);
        Editor.SetTrackChannels(_rig.LightingTrack, [red, blue]);
        var clip = AddClip(0);
        Editor.OpenEnvelopeEditor(clip);
        var envelopes = Editor.EnvelopeEditor!;
        Assert.Equal(red, envelopes.ActiveTarget);   // the first shown

        var blueRow = envelopes.Fixtures.Single().Channels[1];
        blueRow.MakeActiveCommand.Execute(null);
        Assert.True(blueRow.IsActive);
        Assert.Equal(blue, envelopes.ActiveLevels?.Channel.Target);

        Editor.SelectPoint(Editor.AddHandle(clip, red, S(1))!, additive: false);   // a red handle selected: red is active
        Assert.Equal(red, envelopes.ActiveTarget);

        envelopes.Fixtures.Single().Channels[0].IsChecked = false;   // hiding the active channel: another becomes active
        Assert.Equal(blue, envelopes.ActiveTarget);
    }

    [Fact]
    public void The_view_fits_the_clip_zooms_around_a_time_and_steps_between_clips()
    {
        _rig.RedLane();
        var first = AddClip(0, length: 10);
        var second = AddClip(12);
        Editor.OpenEnvelopeEditor(first);
        var envelopes = Editor.EnvelopeEditor!;

        Assert.True(envelopes.ViewStart < TimeSpan.Zero && envelopes.ViewStart + envelopes.ViewLength > S(10));
        envelopes.ZoomAround(S(5), 0.5);
        var at = envelopes.ViewStart + envelopes.ViewLength * ((5 - (-0.6)) / 11.2);   // 5 s stays where it was on screen
        Assert.InRange((at - S(5)).Duration().TotalMilliseconds, 0, 1);

        envelopes.NextClipCommand.Execute(null);
        Assert.Same(second, envelopes.Clip);
        Assert.Equal([second.Id], Editor.Selection);
        envelopes.PreviousClipCommand.Execute(null);
        Assert.Same(first, envelopes.Clip);
    }

    // ---- tracks and tabs ----

    [Fact]
    public void Track_add_move_and_remove_are_undoable()
    {
        var timeline = Editor.SelectedTimeline!;
        string[] names = [.. timeline.Tracks.Select(t => t.Name)];
        Editor.AddTrackCommand.Execute(TrackKind.Lighting);
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio, TrackKind.Lighting], Editor.Tracks.Select(t => t.Kind));
        Assert.Equal("Lighting 2", Editor.SelectedTrack?.Name);

        Editor.MoveTrackUpCommand.Execute(null);
        Assert.Equal([TrackKind.Lighting, TrackKind.Lighting, TrackKind.Audio], timeline.Tracks.Select(t => t.Kind));

        Editor.RemoveTrackCommand.Execute(null);
        Assert.Equal(names, Editor.Tracks.Select(t => t.Name));

        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio, TrackKind.Lighting], Editor.Tracks.Select(t => t.Kind));
    }

    [Fact]
    public void Each_open_timeline_keeps_its_own_undo_history_across_tab_switches()
    {
        var main = Editor.SelectedTimeline!;
        _rig.AddSound(1);
        Editor.Tree.NewTimelineCommand.Execute(null);   // opens the new one in a tab
        var other = Editor.SelectedTimeline!;
        Assert.NotSame(main, other);
        Assert.Equal("Undo", Editor.UndoLabel);

        Editor.SelectedTab = Editor.Tabs.Single(t => t.Timeline == main);

        Assert.Equal("Undo Add audio clip", Editor.UndoLabel);
        Assert.Same(main, _rig.Playback.Timeline);
        Editor.UndoCommand.Execute(null);
        Assert.Empty(_rig.Events(TrackKind.Audio));
    }

    [Fact]
    public void Edits_mark_only_the_edited_timeline_unsaved_in_its_tab_title()
    {
        Editor.Tree.NewTimelineCommand.Execute(null);
        var other = Editor.Tabs.Single(t => t.Timeline != Editor.Tabs[0].Timeline);
        foreach (var t in _rig.Session.Project.Timelines) t.HasUnsavedChanges = false;
        Editor.RefreshTitles();
        Editor.SelectedTab = Editor.Tabs[0];

        _rig.AddSound(1);

        Assert.EndsWith(" *", Editor.Tabs[0].Title);
        Assert.False(other.Title.EndsWith('*'));
    }

    [Fact]
    public void Closing_a_tab_keeps_the_timeline_and_selects_a_neighbour()
    {
        var main = Editor.SelectedTimeline!;
        Editor.Tree.NewTimelineCommand.Execute(null);
        var second = Editor.SelectedTimeline!;

        Editor.CloseTabCommand.Execute(Editor.SelectedTab);

        Assert.Same(main, Editor.SelectedTimeline);
        Assert.Contains(second, _rig.Session.Project.Timelines);
        Editor.CloseTabCommand.Execute(Editor.SelectedTab);
        Assert.Null(Editor.SelectedTimeline);
        Assert.Null(_rig.Playback.Timeline);
    }

    // ---- several timelines at once ----

    [Fact]
    public void Switching_tabs_leaves_the_first_timeline_playing_and_the_playing_list_shows_both()
    {
        var main = Editor.SelectedTimeline!;
        Editor.LengthSeconds = 10;   // something to play (an empty timeline ends at once)
        _rig.Play();
        Editor.Tree.NewTimelineCommand.Execute(null);   // a second timeline, now in focus
        var second = Editor.SelectedTimeline!;
        second.Length = S(10);
        Editor.Playback.Recompile();
        _rig.Play();
        _rig.Manager.Tick();
        _rig.Playback.Tick();

        Assert.Equal(TransportState.Playing, _rig.Manager.StatusOf(main.Id).State);
        Assert.Equal(TransportState.Playing, _rig.Status.State);
        Assert.Equal(2, _rig.Playback.Running.Count);
        Assert.True(_rig.Playback.Running.Single(r => r.Id == second.Id).IsFocused);

        _rig.Playback.Running.Single(r => r.Id == main.Id).OpenCommand.Execute(null);
        Assert.Same(main, Editor.SelectedTimeline);

        _rig.Playback.StopAllCommand.Execute(null);
        _rig.Manager.Tick();
        _rig.Playback.Tick();
        Assert.Empty(_rig.Playback.Running);
    }

    [Fact]
    public void Playback_settings_are_undoable_edits_summarised_on_the_button()
    {
        Assert.Equal("Normal", Editor.PlaybackSummary);

        Editor.Priority = 10;
        Editor.PauseOverridden = true;
        Editor.ReleaseFadeSeconds = 2;
        Editor.DuckPercent = 25;
        Editor.ReleaseAtEnd = true;

        Assert.Equal(new TimelinePlayback(10, true, S(2), 0.25, true), Editor.SelectedTimeline!.Playback);
        Assert.Equal("Priority 10 · pauses others · ducks to 25% · releases at end · 2 s release", Editor.PlaybackSummary);
        Assert.Equal(10, _rig.Compiled.Playback.Priority);   // the player has it straight away

        Editor.UndoCommand.Execute(null);
        Assert.False(Editor.ReleaseAtEnd);
    }

    [Fact]
    public void A_deleted_timeline_stops()
    {
        var main = Editor.SelectedTimeline!;
        _rig.Play();
        _rig.Manager.Tick();
        Editor.Tree.SelectedNode = Editor.Tree.AllNodes().OfType<TimelineNode>().Single(n => n.Timeline == main);

        Editor.Tree.DeleteCommand.Execute(null);
        _rig.Manager.Tick();

        Assert.False(_rig.Manager.IsLoaded(main.Id));
        Assert.Empty(_rig.Manager.Running);
    }

    [Fact]
    public void Reload_after_opening_a_show_does_not_mark_it_changed()
    {
        _rig.Dirty = false;
        Editor.Reload();
        Assert.False(_rig.Dirty);
    }
}

public class EditHistoryTests
{
    [Fact]
    public void Undo_and_redo_apply_in_order_and_a_new_edit_clears_redo()
    {
        var history = new EditHistory();
        int value = 0;
        history.Execute("one", () => value = 1, () => value = 0);
        history.Execute("two", () => value = 2, () => value = 1);

        history.Undo();
        Assert.Equal(1, value);
        Assert.Equal("two", history.RedoDescription);

        history.Execute("three", () => value = 3, () => value = 1);
        Assert.False(history.CanRedo);
        history.Undo();
        history.Undo();
        Assert.Equal(0, value);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Edits_with_the_same_key_merge_and_undo_restores_the_value_before_the_first()
    {
        var history = new EditHistory();
        int value = 0;
        for (int i = 1; i <= 5; i++)
        {
            int previous = value, next = i;
            history.Execute("type", () => value = next, () => value = previous, mergeKey: "field");
        }
        history.Execute("other", () => value = 99, () => value = 5, mergeKey: "another field");

        history.Undo();
        Assert.Equal(5, value);
        history.Undo();
        Assert.Equal(0, value);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void A_redone_edit_does_not_merge_with_the_next()
    {
        var history = new EditHistory();
        int value = 0;
        history.Execute("a", () => value = 1, () => value = 0, mergeKey: "k");
        history.Undo();
        history.Redo();
        history.Execute("b", () => value = 2, () => value = 1, mergeKey: "k");

        history.Undo();
        Assert.Equal(1, value);
    }

    [Fact]
    public void Keeps_at_most_500_steps_dropping_the_oldest()
    {
        var history = new EditHistory();
        int value = 0;
        for (int i = 1; i <= 600; i++)
        {
            int previous = value, next = i;
            history.Execute($"{i}", () => value = next, () => value = previous);
        }
        int undone = 0;
        while (history.CanUndo)
        {
            history.Undo();
            undone++;
        }
        Assert.Equal(500, undone);
        Assert.Equal(100, value);
    }

    [Fact]
    public void Clear_forgets_everything_without_reporting_a_change()
    {
        var history = new EditHistory();
        int changes = 0;
        history.Changed += () => changes++;
        history.Execute("a", () => { }, () => { });
        history.Clear();
        Assert.Equal(1, changes);
        Assert.False(history.CanUndo);
    }
}
