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
/// (Intensity, Red, Green, Blue), and a marker track next to the default lighting and audio tracks.
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

    readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "ghostshow-app-tests", Guid.NewGuid().ToString("N"));
    readonly UiResponsivenessProbe _probe = new();

    public TimelineRig()
    {
        Session = new ShowSession(new ShowEngine(), new FixtureProfileLibrary([TestRig.Par, TestRig.Dimmer, RgbProfile]), NullLogger<ShowSession>.Instance);
        Par = new FixtureInstance { Name = "Par 1", ProfileId = TestRig.Par.Id, ModeName = "5ch", StartAddress = 10 };
        Rgb = new FixtureInstance { Name = "Entry LED", ProfileId = RgbProfile.Id, ModeName = "4ch", StartAddress = 20 };
        Session.Project.Fixtures.AddRange([Par, Rgb]);
        Session.Project.Timelines[0].Tracks.Add(new TimelineTrack { Name = "Cues", Kind = TrackKind.Markers });
        Session.Apply();
        Audio = new AudioService(NullLogger.Instance, useNullDevice: true);
        Player = new ShowPlayer(Session.Engine, Audio, new StopwatchClock());
        var services = new AppServices(Session, new OutputManager(Session.Engine, NullLoggerFactory.Instance), Player,
                                       new PlaybackLoop(Player, NullLogger.Instance), Audio, new MachineSettings(),
                                       new AppPaths(_configDirectory), new RecentEventsLog(), _probe);
        Playback = new PlaybackViewModel(services, NullLogger.Instance);
        Editor = new TimelineEditorViewModel(Session, Playback, () => Task.FromResult(PickedFile), () => Dirty = true);
    }

    public ShowSession Session { get; }
    public FixtureInstance Par { get; }
    public FixtureInstance Rgb { get; }
    public AudioService Audio { get; }
    public ShowPlayer Player { get; }
    public PlaybackViewModel Playback { get; }
    public TimelineEditorViewModel Editor { get; }
    public string? PickedFile { get; set; }
    public bool Dirty { get; set; }

    public TrackRowViewModel Track(TrackKind kind) => Editor.Tracks.First(t => t.Kind == kind);

    public IList<TimelineEvent> Events(TrackKind kind) => Track(kind).Track.Events;

    public TimelineTrack LightingTrack => Track(TrackKind.Lighting).Track;

    /// <summary>Adds a marker at a time through the editor (as a double-click on its row would).</summary>
    public TimelineEvent AddMarker(double seconds)
    {
        Editor.AddEventAtAsync(Editor.Tracks.IndexOf(Track(TrackKind.Markers)), TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
        return Events(TrackKind.Markers)[^1];
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
        Player.Tick();
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
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio, TrackKind.Markers], Editor.Tracks.Select(t => t.Kind));
        Assert.False(_rig.Dirty);
    }

    // ---- events (audio and markers) ----

    [Fact]
    public void Add_event_puts_it_at_the_playhead_on_the_selected_track_selects_it_and_is_undoable()
    {
        _rig.Playback.Seek(S(2));
        Editor.SelectedTrack = _rig.Track(TrackKind.Markers);
        Editor.AddEventCommand.Execute(null);

        var added = Assert.IsType<MarkerEvent>(Assert.Single(_rig.Events(TrackKind.Markers)));
        Assert.Equal(S(2), added.Start);
        Assert.Equal([added.Id], Editor.Selection);
        Assert.Same(added, Editor.Properties?.Event);
        Assert.True(_rig.Dirty);
        Assert.Equal("Undo Add marker", Editor.UndoLabel);

        Editor.UndoCommand.Execute(null);
        Assert.Empty(_rig.Events(TrackKind.Markers));
        Assert.Empty(Editor.Selection);
        Assert.Null(Editor.Properties);

        Editor.RedoCommand.Execute(null);
        Assert.Same(added, Assert.Single(_rig.Events(TrackKind.Markers)));
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
        var a = _rig.AddMarker(1);
        var b = _rig.AddMarker(2);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(b.Id, additive: true);
        Editor.Snap = TimelineEditorViewModel.SnapOptions.Single(o => o.Interval == S(0.1));

        Editor.MoveSelection(S(0.53));
        Assert.Equal((S(1.5), S(2.5)), (a.Start, b.Start));

        Editor.MoveSelection(S(-10));
        Assert.Equal((S(0), S(1)), (a.Start, b.Start));

        Editor.UndoCommand.Execute(null);
        Assert.Equal((S(1.5), S(2.5)), (a.Start, b.Start));
        Assert.Equal("Undo Move 2 events", Editor.UndoLabel);
    }

    [Fact]
    public void Ctrl_click_toggles_an_event_in_the_selection_and_properties_show_only_for_one()
    {
        var a = _rig.AddMarker(1);
        var b = _rig.AddMarker(2);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(b.Id, additive: true);
        Assert.Null(Editor.Properties);
        Assert.Equal("2 events selected", Editor.SelectionLabel);

        Editor.SelectEvent(a.Id, additive: true);
        Assert.Equal([b.Id], Editor.Selection);
        Assert.Same(b, Editor.Properties?.Event);
    }

    [Fact]
    public void Copy_and_paste_at_the_playhead_keeps_spacing_and_tracks()
    {
        _rig.AddMarker(1);
        _rig.AddMarker(1.5);
        Editor.SelectAllCommand.Execute(null);
        Editor.CopyCommand.Execute(null);

        _rig.Playback.Seek(S(4));
        Editor.PasteCommand.Execute(null);

        var events = _rig.Events(TrackKind.Markers);
        Assert.Equal([S(1), S(1.5), S(4), S(4.5)], events.Select(e => e.Start));
        Assert.Equal(4, events.Select(e => e.Id).Distinct().Count());   // copies, not the same events again
        Assert.Equal(events.Skip(2).Select(e => e.Id).ToHashSet(), Editor.Selection);   // the pasted ones are selected
    }

    [Fact]
    public void Duplicate_places_copies_right_after_the_selection()
    {
        var a = _rig.AddMarker(1);
        var b = _rig.AddMarker(3);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(b.Id, additive: true);

        Editor.DuplicateCommand.Execute(null);

        Assert.Equal([S(1), S(3), S(3), S(5)], _rig.Events(TrackKind.Markers).Select(e => e.Start));
    }

    [Fact]
    public void Delete_and_undo_restores_events_in_their_original_order()
    {
        var a = _rig.AddMarker(1);
        var b = _rig.AddMarker(2);
        var c = _rig.AddMarker(3);
        Editor.SelectEvent(a.Id, additive: false);
        Editor.SelectEvent(c.Id, additive: true);

        Editor.DeleteCommand.Execute(null);
        Assert.Equal([b], _rig.Events(TrackKind.Markers));

        Editor.UndoCommand.Execute(null);
        Assert.Equal([a, b, c], _rig.Events(TrackKind.Markers));
    }

    [Fact]
    public void Typing_into_a_property_merges_into_one_undo_step()
    {
        var e = _rig.AddMarker(1);
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
        Assert.True(row.IsExpanded);   // its first channels: expanded, ready for points
        Assert.Equal("#FF5252", row.Lanes[0].Color);
        Assert.Equal(TimelineEditorViewModel.TrackRowHeight + 5 * TimelineEditorViewModel.LaneRowHeight, row.Height);

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
    public void Changing_channels_keeps_the_points_of_channels_that_stay()
    {
        var red = _rig.RedLane();
        var point = Editor.AddPoint(red, S(1), 200);
        Editor.EditChannels(_rig.Track(TrackKind.Lighting));
        var picker = Editor.ChannelPicker!;
        Assert.True(picker.Fixtures.Single(f => f.Name == "Entry LED").Channels[1].IsChecked);   // Red, already on the track
        picker.Fixtures.Single(f => f.Name == "Entry LED").Channels[2].IsChecked = true;        // add Green

        picker.OkCommand.Execute(null);

        Assert.Same(red, _rig.LightingTrack.Channels[0]);
        Assert.Same(point, _rig.LightingTrack.Events.OfType<LightingClip>().Single().LaneFor(red.Target)!.Points.Single());
        Assert.Equal(2, _rig.LightingTrack.Channels.Count);
    }

    [Fact]
    public void Rows_line_up_tracks_and_expanded_channels_top_to_bottom()
    {
        _rig.RedLane();
        var lighting = _rig.Track(TrackKind.Lighting);

        Assert.Equal([(0, 46.0, false), (46, 40, true), (86, 46, false), (132, 46, false)],
                     Editor.Rows.Select(r => (r.Top, r.Height, r.Lane is not null)));
        lighting.IsExpanded = false;
        Assert.Equal(3, Editor.Rows.Count);
        Assert.Equal(3 * TimelineEditorViewModel.TrackRowHeight, Editor.ContentHeight);
    }

    // ---- lighting: blocks and points ----

    LightingClip Block(int index = 0) => _rig.LightingTrack.Events.OfType<LightingClip>().ElementAt(index);

    /// <summary>A channel's points in every block, as (time on the timeline, level).</summary>
    IEnumerable<(TimeSpan, int)> Line(ChannelLane trackLane) =>
        _rig.LightingTrack.Events.OfType<LightingClip>().OrderBy(c => c.Start)
            .SelectMany(c => (c.LaneFor(trackLane.Target)?.Points ?? []).Select(p => (c.Start + p.Time, (int)p.Value)));

    [Fact]
    public void Double_clicking_a_channel_outside_any_block_starts_a_block_holding_the_point()
    {
        var red = _rig.RedLane();

        var point = Editor.AddPoint(red, S(2), 120);

        var block = Assert.IsType<LightingClip>(Assert.Single(_rig.LightingTrack.Events));
        Assert.Equal((S(2), LightingClip.DefaultLength), (block.Start, block.Length));
        Assert.Same(point, Assert.Single(block.LaneFor(red.Target)!.Points));
        Assert.Equal(TimeSpan.Zero, point.Time);   // relative to the block
        Assert.Equal(S(2), Editor.AbsoluteTime(point));
        Assert.Equal("Undo Add lighting block", Editor.UndoLabel);
    }

    [Fact]
    public void Points_inside_a_block_play_as_a_line_and_edits_apply_to_the_output_straight_away()
    {
        var red = _rig.RedLane();
        Editor.AddPoint(red, S(0), 0);
        var end = Editor.AddPoint(red, S(4), 200);   // inside the block started at 0 (4 s long)
        Assert.Single(_rig.LightingTrack.Events);
        _rig.Player.Play();
        _rig.Playback.Seek(S(2));

        Assert.Equal(100, _rig.Output(21));   // Red of the fixture at 20

        Editor.SelectPoint(end, additive: false);
        Editor.PointProperties!.Dmx = 100;
        _rig.Player.Seek(S(2));
        Assert.Equal(50, _rig.Output(21));
        Assert.Equal(4, Editor.PointProperties.TimeSeconds);   // shown as time on the timeline
    }

    [Fact]
    public void Moving_a_block_moves_its_lines()
    {
        var red = _rig.RedLane();
        Editor.AddPoint(red, S(0), 0);
        Editor.AddPoint(red, S(4), 200);
        Editor.SelectEvent(Block().Id, additive: false);

        Editor.MoveSelection(S(10));

        Assert.Equal([(S(10), 0), (S(14), 200)], Line(red));
        _rig.Player.Play();
        _rig.Playback.Seek(S(12));
        Assert.Equal(100, _rig.Output(21));
    }

    [Fact]
    public void Dragging_a_block_s_edges_stretches_its_lines_and_the_left_edge_keeps_the_end()
    {
        var red = _rig.RedLane();
        Editor.AddPoint(red, S(0), 0);
        Editor.AddPoint(red, S(2), 100);
        Editor.AddPoint(red, S(4), 200);
        var block = Block();

        Editor.ResizeEvent(block.Id, S(8));   // right edge: twice as long
        Assert.Equal([(S(0), 0), (S(4), 100), (S(8), 200)], Line(red));

        Editor.ResizeEventStart(block.Id, S(4));   // left edge in by 4 s: same end, half as long
        Assert.Equal((S(4), S(4)), (block.Start, block.Length));
        Assert.Equal([(S(4), 0), (S(6), 100), (S(8), 200)], Line(red));

        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([(S(0), 0), (S(2), 100), (S(4), 200)], Line(red));
        Assert.Equal(LightingClip.DefaultLength, block.Length);
    }

    [Fact]
    public void A_block_s_length_in_the_properties_panel_stretches_it_too()
    {
        var red = _rig.RedLane();
        Editor.AddPoint(red, S(0), 0);
        Editor.AddPoint(red, S(4), 200);
        Editor.SelectEvent(Block().Id, additive: false);

        Editor.Properties!.BlockLengthSeconds = 2;

        Assert.Equal([(S(0), 0), (S(2), 200)], Line(red));
        Assert.Equal("Lighting block", Editor.Properties.Kind);
    }

    [Fact]
    public void Add_event_on_a_lighting_track_adds_a_block_holding_each_channel_where_it_is()
    {
        var red = _rig.RedLane();
        Editor.AddPoint(red, S(0), 0);
        Editor.AddPoint(red, S(4), 200);
        _rig.Playback.Seek(S(6));
        Editor.SelectedTrack = _rig.Track(TrackKind.Lighting);

        Editor.AddEventCommand.Execute(null);

        var added = Block(1);
        Assert.Equal(S(6), added.Start);
        Assert.Equal(200, Assert.Single(added.LaneFor(red.Target)!.Points).Value);   // held from the first block
        Assert.Equal([added.Id], Editor.Selection);
    }

    [Fact]
    public void Copy_and_paste_of_a_block_makes_an_independent_copy()
    {
        var red = _rig.RedLane();
        var original = Editor.AddPoint(red, S(0), 50);
        Editor.SelectEvent(Block().Id, additive: false);
        Editor.CopyCommand.Execute(null);
        _rig.Playback.Seek(S(10));

        Editor.PasteCommand.Execute(null);
        Block(1).Lanes[0].Points[0].Value = 99;

        Assert.Equal(S(10), Block(1).Start);
        Assert.Equal(50, original.Value);
    }

    [Fact]
    public void Dragging_points_moves_them_in_time_snapped_and_kept_within_their_block_and_level_clamped()
    {
        var red = _rig.RedLane();
        var a = Editor.AddPoint(red, S(1), 100);   // block 1-5
        var b = Editor.AddPoint(red, S(2), 240);
        Editor.SelectPoint(a, additive: false);
        Editor.SelectPoint(b, additive: true);
        Editor.Snap = TimelineEditorViewModel.SnapOptions.Single(o => o.Interval == S(0.5));

        Editor.MoveSelectedPoints(S(0.7), +30);
        Assert.Equal([(S(1.5), 130), (S(2.5), 255)], Line(red));

        Editor.MoveSelectedPoints(S(10), 0);   // can't leave the block
        Assert.Equal([(S(5), 130), (S(5), 255)], Line(red));

        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([(S(1), 100), (S(2), 240)], Line(red));
    }

    [Fact]
    public void A_point_dragged_past_another_keeps_the_line_in_time_order()
    {
        var red = _rig.RedLane();
        var a = Editor.AddPoint(red, S(1), 10);
        var b = Editor.AddPoint(red, S(2), 20);
        Editor.SelectPoint(a, additive: false);

        Editor.MoveSelectedPoints(S(2), 0);

        Assert.Equal([b, a], Block().LaneFor(red.Target)!.Points);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([a, b], Block().LaneFor(red.Target)!.Points);
    }

    [Fact]
    public void Step_and_delete_are_undoable_and_delete_clears_the_point_selection()
    {
        var red = _rig.RedLane();
        var a = Editor.AddPoint(red, S(1), 10);
        var b = Editor.AddPoint(red, S(2), 20);
        var lane = Block().LaneFor(red.Target)!;
        Editor.SetStep(b, true);
        Assert.True(b.Step);
        Assert.Equal("Undo Make point a step", Editor.UndoLabel);

        Editor.SelectPoint(b, additive: false);
        Editor.DeleteCommand.Execute(null);
        Assert.Equal([a], lane.Points);
        Assert.Empty(Editor.SelectedPoints);
        Assert.Null(Editor.PointProperties);

        Editor.UndoCommand.Execute(null);
        Assert.Equal([a, b], lane.Points);
        Editor.UndoCommand.Execute(null);
        Assert.False(b.Step);
    }

    [Fact]
    public void Typing_a_point_s_level_merges_into_one_undo_step_and_percent_and_dmx_agree()
    {
        var red = _rig.RedLane();
        var a = Editor.AddPoint(red, S(1), 10);
        Editor.AddPoint(red, S(2), 0);
        Editor.SelectPoint(a, additive: false);
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
    public void Selecting_a_point_clears_selected_events_and_the_other_way_round()
    {
        var marker = _rig.AddMarker(1);
        var red = _rig.RedLane();
        var point = Editor.AddPoint(red, S(1), 0);
        Assert.Empty(Editor.Selection);

        Editor.SelectEvent(marker.Id, additive: false);
        Assert.Empty(Editor.SelectedPoints);
        Assert.Null(Editor.PointProperties);

        Editor.SelectPoint(point, additive: false);
        Assert.Empty(Editor.Selection);
        Assert.NotNull(Editor.PointProperties);
    }

    [Fact]
    public void Taking_a_channel_off_the_track_removes_its_lines_from_blocks_and_undo_brings_them_back()
    {
        var red = _rig.RedLane();
        Editor.AddPoint(red, S(1), 77);
        var block = Block();

        Editor.SetTrackChannels(_rig.LightingTrack, []);
        Assert.Empty(block.Lanes);

        Editor.UndoCommand.Execute(null);
        Assert.Equal(77, block.LaneFor(red.Target)!.Points.Single().Value);
    }

    [Fact]
    public void Dragging_an_audio_block_s_left_edge_trims_into_the_file_keeping_its_end()
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
        Editor.AddPoint(red, S(0), 255);
        _rig.Session.Project.Fixtures.Remove(_rig.Rgb);
        _rig.Session.Apply();

        Assert.Contains(red.Id, Editor.ErrorEvents);
        Assert.Equal("Missing fixture · Red", _rig.Track(TrackKind.Lighting).Lanes.Single().Label);   // the header follows the patch
    }

    [Fact]
    public void Edits_during_playback_never_move_the_playhead()
    {
        var red = _rig.RedLane();
        _rig.Playback.Seek(S(0.5));
        Editor.AddPoint(red, S(3), 99);
        Assert.Equal(S(0.5), _rig.Player.Status.Position);
    }

    // ---- tracks and tabs ----

    [Fact]
    public void Track_add_move_and_remove_are_undoable()
    {
        var timeline = Editor.SelectedTimeline!;
        Editor.AddTrackCommand.Execute(TrackKind.Audio);
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio, TrackKind.Markers, TrackKind.Audio], Editor.Tracks.Select(t => t.Kind));
        Assert.Equal("Audio 2", Editor.SelectedTrack?.Name);

        Editor.MoveTrackUpCommand.Execute(null);
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio, TrackKind.Audio, TrackKind.Markers], timeline.Tracks.Select(t => t.Kind));

        Editor.RemoveTrackCommand.Execute(null);
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio, TrackKind.Markers], Editor.Tracks.Select(t => t.Kind));

        Editor.UndoCommand.Execute(null);
        Editor.UndoCommand.Execute(null);
        Assert.Equal([TrackKind.Lighting, TrackKind.Audio, TrackKind.Markers, TrackKind.Audio], Editor.Tracks.Select(t => t.Kind));
    }

    [Fact]
    public void Each_open_timeline_keeps_its_own_undo_history_across_tab_switches()
    {
        var main = Editor.SelectedTimeline!;
        _rig.AddMarker(1);
        Editor.Tree.NewTimelineCommand.Execute(null);   // opens the new one in a tab
        var other = Editor.SelectedTimeline!;
        Assert.NotSame(main, other);
        Assert.Equal("Undo", Editor.UndoLabel);

        Editor.SelectedTab = Editor.Tabs.Single(t => t.Timeline == main);

        Assert.Equal("Undo Add marker", Editor.UndoLabel);
        Assert.Same(main, _rig.Playback.Timeline);
        Editor.UndoCommand.Execute(null);
        Assert.Empty(_rig.Events(TrackKind.Markers));
    }

    [Fact]
    public void Edits_mark_only_the_edited_timeline_unsaved_in_its_tab_title()
    {
        Editor.Tree.NewTimelineCommand.Execute(null);
        var other = Editor.Tabs.Single(t => t.Timeline != Editor.Tabs[0].Timeline);
        foreach (var t in _rig.Session.Project.Timelines) t.HasUnsavedChanges = false;
        Editor.RefreshTitles();
        Editor.SelectedTab = Editor.Tabs[0];

        _rig.AddMarker(1);

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
