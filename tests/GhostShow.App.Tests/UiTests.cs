using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using GhostShow.App.ViewModels;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging.Abstractions;
using Page = GhostShow.App.ViewModels.Page;

namespace GhostShow.App.Tests;

/// <summary>
/// The real window driven by mouse and keyboard on the headless platform: the interactions view-model tests can't
/// see (hit testing, drags, focus, shortcuts, context menus).
/// </summary>
public class UiTests
{
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>Saving and opening finish off the UI thread, so the test keeps the UI running until they have.</summary>
    static string? Finish(Task<string?> command)
    {
        while (!command.IsCompleted) WindowRig.Pump(20);
        return command.Result;
    }

    [Fact]
    public Task A_track_with_more_channels_than_fit_scrolls_its_list_and_then_the_tracks() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        for (int i = 0; i < 6; i++) rig.Editor.AddTrackCommand.Execute(TrackKind.Lighting);   // more than fit: the track list has to scroll too
        var row = rig.Editor.Tracks.First(t => t.IsLighting);
        rig.Editor.EditChannels(row);
        var picker = rig.Editor.ChannelPicker!;
        picker.RawFrom = 100;
        picker.RawTo = 119;
        picker.AddRawCommand.Execute(null);
        picker.OkCommand.Execute(null);
        if (!row.IsExpanded) row.ToggleExpandedCommand.Execute(null);   // it already had a channel, so it stays as it was
        WindowRig.Pump(200);

        var headers = rig.Find<ListBox>(l => l.Name == "TrackHeaders");
        var tracksScroll = headers.FindAncestorOfType<ScrollViewer>()!;
        var channelList = headers.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Content is ItemsControl && s.DataContext == row);
        Assert.True(channelList.Extent.Height > channelList.Viewport.Height);

        var over = rig.CenterOf(channelList);
        double tracksBefore = tracksScroll.Offset.Y;   // selecting a track may already have scrolled it into view
        rig.Window.MouseWheel(over, new Vector(0, -1));
        WindowRig.Pump(100);
        Assert.True(channelList.Offset.Y > 0);
        Assert.Equal(tracksBefore, tracksScroll.Offset.Y);   // the list takes the wheel while it can still move

        // At the end of the list, the wheel moves on to the track list rather than getting stuck.
        channelList.Offset = new Vector(0, channelList.Extent.Height);
        WindowRig.Pump(100);
        rig.Window.MouseWheel(over, new Vector(0, -1));
        WindowRig.Pump(100);
        Assert.True(tracksScroll.Offset.Y > tracksBefore);
    });

    [Fact]
    public Task The_channel_picker_opens_with_fixtures_showing_and_raw_channels_closed() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.EditChannels(rig.Editor.Tracks.First(t => t.IsLighting));
        WindowRig.Pump(200);

        var fixtures = rig.Find<Expander>(e => e.Header as string == "Fixtures");
        var raw = rig.Find<Expander>(e => e.Header as string == "Raw DMX channels");
        Assert.True(fixtures.IsExpanded);
        Assert.False(raw.IsExpanded);
        Assert.True(rig.Find<CheckBox>(c => c.DataContext is FixtureChoiceViewModel).IsEffectivelyVisible);

        rig.Editor.ChannelPicker!.IsRawExpanded = true;
        WindowRig.Pump(100);
        Assert.True(raw.IsExpanded);   // the section follows the view model both ways
    });

    [Fact]
    public Task A_show_made_on_a_device_can_be_named_renamed_and_opened_again() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var app = rig.ViewModel;
        rig.ViewModel.Files.MarkDirty();
        Assert.NotNull(Finish(app.NewShowForDeviceAsync(discardUnsaved: false)));   // it was never saved: the person has to agree

        Assert.Null(Finish(app.NewShowForDeviceAsync(discardUnsaved: true)));
        Assert.Null(Finish(app.SaveShowAsForDeviceAsync("Scares")));
        Assert.Equal("Scares", rig.Session.Project.Name);
        Assert.Equal(["Scares"], app.SavedShowsForDevice());
        Assert.NotNull(Finish(app.SaveShowAsForDeviceAsync("Again")));   // it has a name now: Rename is for that

        Assert.Null(Finish(app.RenameShowForDeviceAsync("Big scares")));
        Assert.Equal(["Big scares"], app.SavedShowsForDevice());
        Assert.Equal("Big scares", Path.GetFileName(rig.Session.ShowFolder));

        Assert.Null(Finish(app.NewShowForDeviceAsync(discardUnsaved: false)));
        Assert.Null(rig.Session.ShowFolder);
        Assert.NotNull(Finish(app.SaveShowAsForDeviceAsync("big SCARES")));   // that name is taken, and nothing is overwritten
        Assert.Null(Finish(app.OpenShowForDeviceAsync("Big scares", discardUnsaved: true)));
        Assert.Equal("Big scares", rig.Session.Project.Name);
    });

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public Task A_device_cant_name_a_show_outside_the_shows_folder(string name) => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        Assert.NotNull(Finish(rig.ViewModel.SaveShowAsForDeviceAsync(name)));
        Assert.NotNull(Finish(rig.ViewModel.OpenShowForDeviceAsync(name, discardUnsaved: true)));
        Assert.Null(rig.Session.ShowFolder);
    });

    [Fact]
    public Task F2_renames_a_timeline_in_the_tree_by_typing() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var tree = rig.Find<TreeView>();
        var node = rig.Editor.Tree.AllNodes().OfType<TimelineNode>().Single();
        var label = tree.GetVisualDescendants().OfType<TextBlock>().First(t => t.DataContext == node && t.IsEffectivelyVisible);

        rig.Click(rig.CenterOf(label));
        rig.Key(Key.F2);
        Assert.True(node.IsEditing);
        Assert.IsType<TextBox>(rig.Window.FocusManager?.GetFocusedElement());
        rig.Type("Lobby loop");
        rig.Key(Key.Enter);

        Assert.Equal("Lobby loop", rig.Session.Project.Timelines[0].Name);
        Assert.False(node.IsEditing);
    });

    [Fact]
    public Task Delete_in_the_tree_asks_before_removing_anything() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var tree = rig.Find<TreeView>();
        var node = rig.Editor.Tree.AllNodes().OfType<TimelineNode>().Single();
        rig.Click(rig.CenterOf(tree.GetVisualDescendants().OfType<TextBlock>().First(t => t.DataContext == node && t.IsEffectivelyVisible)));

        rig.Key(Key.Delete);

        Assert.True(rig.Editor.Tree.IsConfirmingDelete);
        Assert.Single(rig.Session.Project.Timelines);
    });

    [Fact]
    public Task Dragging_an_audio_track_s_volume_slider_sets_its_volume_in_one_undo_step() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var header = rig.Find<ListBoxItem>(i => i.DataContext is TrackRowViewModel { IsAudio: true });
        var slider = header.GetVisualDescendants().OfType<Slider>().Single(s => s.IsEffectivelyVisible);
        var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().First();
        var track = rig.TrackRow("Audio").Track.Track;

        rig.Drag(rig.CenterOf(thumb), slider.TranslatePoint(new Point(slider.Bounds.Width / 2, slider.Bounds.Height / 2), rig.Window)!.Value);

        Assert.InRange(track.Volume, 0.3, 0.7);
        Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == $"{track.Volume * 100:0}%");
        rig.Editor.UndoCommand.Execute(null);
        Assert.Equal(1.0, track.Volume);
    });

    [Fact]
    public Task Right_clicking_a_lighting_track_header_offers_channels() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var header = rig.Find<ListBoxItem>(i => i.DataContext is TrackRowViewModel { IsLighting: true });

        var summary = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Entry LED");   // its fixtures, under the name (not the name box: it has its own menu)
        rig.Click(rig.CenterOf(summary), MouseButton.Right);
        var menu = summary.GetVisualAncestors().OfType<Control>().Select(c => c.ContextMenu).OfType<ContextMenu>().First();
        WindowRig.Pump();
        Assert.True(menu.IsOpen);   // opened by the right-click
        var item = menu.Items.OfType<MenuItem>().First(m => m.Header as string == "Channels…");
        var popup = TopLevel.GetTopLevel(item)!;   // the menu is its own (popup) surface
        var at = item.TranslatePoint(new Avalonia.Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(at, MouseButton.Left);
        popup.MouseUp(at, MouseButton.Left);
        WindowRig.Pump();

        Assert.NotNull(rig.Editor.ChannelPicker);
        Assert.True(rig.Editor.ChannelPicker!.Fixtures.Single(f => f.Name == "Entry LED").Channels[1].IsChecked);   // Red is on the track
    });

    [Fact]
    public Task Pressing_a_tilt_move_s_start_or_end_card_picks_the_handle_the_arc_sets() => Ui.Run(() =>
    {
        FixtureInstance bar = new() { Name = "Beam bar", ProfileId = "shehds/led-beam-bar-10x40w-rgbw", ModeName = "14 channel", StartAddress = 100 };
        using var rig = new WindowRig(setUp: session => session.Project.Fixtures.Add(bar));
        var row = rig.TrackRow("Lighting");
        rig.Editor.SetTrackChannels(rig.Lighting, [LaneTarget.FixtureChannel(bar.Id, 3)]);   // its dimmer: tilt clips add its tilt
        rig.Editor.AddTiltAt(row.Track, S(1));
        WindowRig.Pump();
        Assert.True(rig.Editor.Properties!.IsEndSelected);   // the end, to start with

        rig.Click(rig.CenterOf(rig.Find<Border>(b => b.Name == "TiltStartCard")));
        Assert.False(rig.Editor.Properties.IsEndSelected);
        Assert.False(rig.Find<Controls.TiltArc>().IsEndSelected);

        rig.Click(rig.CenterOf(rig.Find<Border>(b => b.Name == "TiltEndCard")));
        Assert.True(rig.Editor.Properties.IsEndSelected);
    });

    [Fact]
    public Task A_clip_too_narrow_to_see_is_still_moved_by_its_middle_and_stretched_by_an_edge() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var row = rig.TrackRow("Lighting");
        rig.Editor.DropEffect(TimelineEditorViewModel.EffectChoices[0], row.Track, S(10));   // Level Control
        var clip = rig.Lighting.Events.OfType<LightingClip>().Single();
        clip.Length = S(1);
        row.Track.IsExpanded = true;            // handles shown: they don't get in the way of a clip this thin
        rig.Editor.PixelsPerSecond = 10;        // zoomed out: the clip is 10 px wide
        WindowRig.Pump();

        rig.Drag(rig.At(row, 10.5), rig.At(row, 20.5));
        Assert.Equal((S(20), S(1)), (clip.Start, clip.Length));   // moved, not stretched

        var end = rig.At(row, 21);
        rig.Drag(end + new Vector(4, 0), end + new Vector(44, 0));   // just past its end: still its edge
        Assert.Equal((S(20), S(5)), (clip.Start, clip.Length));
    });

    [Fact]
    public Task A_new_lighting_track_asks_for_its_fixtures_takes_their_name_and_lists_them() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.IsTimelineTab = true;
        rig.Editor.AddTrackCommand.Execute(TrackKind.Lighting);
        WindowRig.Pump();

        var picker = Assert.IsType<ChannelPickerViewModel>(rig.Editor.ChannelPicker);   // asked straight away, where it shows
        Assert.False(rig.Editor.IsTimelineTab);
        var row = rig.Editor.SelectedTrack!;
        Assert.Equal("Lighting 2", row.Name);
        foreach (var channel in picker.Fixtures.Single(f => f.Name == "Entry LED").Channels) channel.IsChecked = true;
        picker.OkCommand.Execute(null);
        WindowRig.Pump();

        Assert.Equal("Entry LED", row.Name);   // named after it, in the same undo step
        Assert.Equal("5 channels", row.Subtitle);   // not its name again
        Assert.Contains("5 channels", row.SubtitleTip);
        rig.Editor.UndoCommand.Execute(null);
        Assert.Equal(("Lighting 2", 0), (row.Track.Name, row.Track.Channels.Count));

        // The first track (Red of Entry LED, already named): what it controls is under its name.
        Assert.Equal("Entry LED", rig.Editor.Tracks[0].Subtitle);
    });

    [Fact]
    public Task A_lighting_row_draws_its_lights_colour_strip_under_its_clips() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var row = rig.TrackRow("Lighting");
        rig.Editor.AddEffectAt(row.Track, S(1), EffectKind.Look);
        WindowRig.Pump();

        var lights = rig.Editor.LightsOf(rig.Lighting);
        Assert.Equal(rig.Rgb.Id, Assert.Single(lights).FixtureId);
        // The clip's bottom stops above the strip, and the envelopes with it.
        Assert.True(Controls.TimelineCanvas.LevelY(row, 0) < Controls.TimelineCanvas.RulerHeight + row.Bottom - 9);
    });

    [Fact]
    public Task Right_clicking_under_the_tracks_adds_a_track() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        int tracks = rig.Editor.Tracks.Count;

        // Under the lanes.
        rig.Click(rig.Canvas.TranslatePoint(new Point(200, Controls.TimelineCanvas.RulerHeight + rig.Editor.ContentHeight + 30), rig.Window)!.Value, MouseButton.Right);
        rig.ClickMenuItem(rig.MenuItem("Add audio track"));
        Assert.Equal((tracks + 1, TrackKind.Audio), (rig.Editor.Tracks.Count, rig.Editor.Tracks[^1].Kind));

        // Under the track headers, on the left.
        var area = rig.Find<Border>(b => b.Name == "LanesArea");
        rig.Click(area.TranslatePoint(new Point(90, area.Bounds.Height - 30), rig.Window)!.Value, MouseButton.Right);
        var menu = rig.Find<ListBox>(l => l.Name == "TrackHeaders").ContextMenu!;
        Assert.True(menu.IsOpen);
        rig.ClickMenuItem(menu.Items.OfType<MenuItem>().First(i => i.Header as string == "Add lighting track"));
        Assert.Equal((tracks + 2, TrackKind.Lighting), (rig.Editor.Tracks.Count, rig.Editor.Tracks[^1].Kind));
    });

    [Fact]
    public Task Right_clicking_an_empty_part_of_a_lighting_row_adds_an_effect_there() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var row = rig.TrackRow("Lighting");

        rig.Click(rig.At(row, 6), MouseButton.Right);
        // As the Effects menu: the controls (no moving heads here: no tilt or home), then the effects.
        var top = rig.Canvas.LastMenu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Add Level Control clip here", "Add look here", "Add tilt here", "Add home here", "Add effect here", "Paste here"],
                     top.Select(i => i.Header as string));
        Assert.Equal([true, true, false, false, true], top.Take(5).Select(i => i.IsEnabled));
        Assert.Equal(TimelineEditorViewModel.PatternChoices.Select(c => c.Name), rig.MenuItem("Add effect here").Items.OfType<MenuItem>().Select(i => i.Header as string));
        rig.ClickMenuItem(rig.MenuItem("Add effect here"));
        rig.ClickMenuItem(rig.MenuItem("Fire"));

        var clip = Assert.IsType<LightingClip>(Assert.Single(row.Track.Track.Events));
        Assert.Equal((EffectKind.Fire, S(6), "Fire"), (clip.Effect!.Kind, clip.Start, clip.Name));
        Assert.Equal("Fire effect", rig.Editor.Properties!.Kind);

        rig.Click(rig.At(row, 7), MouseButton.Right);   // on it now: its own menu
        Assert.Contains(rig.Canvas.LastMenu!.Items.OfType<MenuItem>(), i => i.Header as string == "Convert to Level Control (editable handles)");
        Assert.DoesNotContain(rig.Canvas.LastMenu.Items.OfType<MenuItem>(), i => i.Header as string == "Add Envelope Handle");
        rig.Canvas.LastMenu.Close();
    });

    [Fact]
    public Task Right_clicking_a_clip_adds_an_envelope_handle_where_clicked() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Tracks[0].IsExpanded = true;
        var clip = AddClip(rig, 1, 4);
        var row = rig.TrackRow("Lighting");

        rig.Click(rig.At(row, 3, level: 0.5), MouseButton.Right);
        Assert.True(rig.Canvas.LastMenu?.IsOpen);
        rig.ClickMenuItem(rig.MenuItem("Add Envelope Handle"));
        rig.ClickMenuItem(rig.MenuItem("Entry LED · Red"));

        Assert.Equal([(2.0, 0), (4, 0)], clip.Lanes.Single().Points.Select(p => (p.Time.TotalSeconds, (int)p.Value)));   // and its end handle
        Assert.Equal("1 handle selected", rig.Editor.SelectionLabel);
    });

    [Fact]
    public Task Dragging_a_handle_moves_it_but_not_past_its_neighbour_and_an_end_handle_only_up_and_down() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Tracks[0].IsExpanded = true;
        var clip = AddClip(rig, 0, 8, (2, 100), (4, 100), (8, 100));
        var (a, end) = (clip.Lanes[0].Points[0], clip.Lanes[0].Points[2]);
        var row = rig.TrackRow("Lighting");

        rig.Drag(rig.At(row, 2, level: 100 / 255.0), rig.At(row, 3, level: 200 / 255.0));
        Assert.Equal(S(3), a.Time);
        Assert.InRange(a.Value, 195, 205);

        rig.Drag(rig.At(row, 3, level: a.Value / 255.0), rig.At(row, 6, level: a.Value / 255.0));
        Assert.Equal(S(4), a.Time);   // stopped by the handle at 4 s

        rig.Drag(rig.At(row, 8, level: 100 / 255.0) + new Point(-1, 0), rig.At(row, 6, level: 0));
        Assert.Equal((S(8), (byte)0), (end.Time, end.Value));
    });

    [Fact]
    public Task Right_clicking_a_handle_offers_its_curve_type() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Tracks[0].IsExpanded = true;
        var clip = AddClip(rig, 0, 8, (2, 100), (8, 0));
        var row = rig.TrackRow("Lighting");

        rig.Click(rig.At(row, 2, level: 100 / 255.0), MouseButton.Right);
        Assert.True(rig.MenuItem("Linear").IsChecked);
        rig.ClickMenuItem(rig.MenuItem("Spline"));

        Assert.Equal(CurveKind.Spline, clip.Lanes[0].Points[0].Curve);
    });

    [Fact]
    public Task Dragging_the_first_clip_s_start_handle_sets_the_channel_s_start_level() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Tracks[0].IsExpanded = true;
        AddClip(rig, 2, 4, (4, 0));
        var row = rig.TrackRow("Lighting");

        rig.Drag(rig.At(row, 2, level: 0), rig.At(row, 2, level: 1));

        Assert.Equal(255, rig.Lighting.Channels[0].StartLevel);
        rig.Key(Key.Z, RawInputModifiers.Control);   // the whole drag is one undo step
        Assert.Equal(0, rig.Lighting.Channels[0].StartLevel);
    });

    [Fact]
    public Task A_first_effect_clip_s_start_dot_sets_the_start_level_and_deleting_the_clip_puts_it_back_to_0() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Tracks[0].IsExpanded = true;
        var row = rig.TrackRow("Lighting");
        var lightning = rig.Editor.AddEffectAt(row.Track, S(2), EffectKind.Lightning)!;
        WindowRig.Pump();

        rig.Drag(rig.At(row, 2, level: 0), rig.At(row, 2, level: 1));   // its start dot, as on a Level Control clip
        Assert.Equal(255, rig.Lighting.Channels[0].StartLevel);

        rig.Editor.SelectEvent(lightning.Id, additive: false);
        rig.Editor.DeleteCommand.Execute(null);

        Assert.Empty(rig.Lighting.Events);
        Assert.Equal(0, rig.Lighting.Channels[0].StartLevel);
        rig.Editor.UndoCommand.Execute(null);   // one step brings both back
        Assert.Single(rig.Lighting.Events);
        Assert.Equal(255, rig.Lighting.Channels[0].StartLevel);
    });

    [Fact]
    public Task A_show_button_s_key_runs_its_timeline_from_any_screen_but_not_while_typing() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var scare = new ShowTimeline { Name = "Scare", Length = S(10) };
        rig.Session.Project.Timelines.Add(scare);
        rig.ViewModel.Buttons.AddCommand.Execute(null);
        var button = rig.ViewModel.Buttons.Buttons.Single();
        button.AddActionCommand.Execute("run");
        button.Actions[0].Timeline = rig.ViewModel.Buttons.Timelines.Single(t => t.Id == scare.Id);
        Assert.True(button.Triggers.IsLearning);   // a new button goes straight on to its key
        rig.Key(Key.F5);   // the key is learned: this press doesn't run anything
        Assert.Equal("F5", button.KeyLabel);
        Assert.False(rig.Manager.IsRunning(scare.Id));
        rig.ViewModel.Buttons.IsEditing = false;
        WindowRig.Pump();

        var tile = rig.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("showButton"));
        Assert.True(tile.IsEffectivelyVisible);
        rig.Click(rig.CenterOf(tile));
        Assert.True(rig.Manager.IsRunning(scare.Id));
        rig.Manager.StopTimeline(scare.Id);
        rig.Manager.Tick();

        rig.ViewModel.CurrentPage = ViewModels.Page.Monitor;   // another screen
        WindowRig.Pump();
        rig.Key(Key.F5);
        Assert.True(rig.Manager.IsRunning(scare.Id));
        Assert.True(rig.ViewModel.IsDirty);   // adding the button changed the show

        rig.Manager.StopTimeline(scare.Id);
        rig.Manager.Tick();
        rig.ViewModel.CurrentPage = ViewModels.Page.Timeline;
        rig.ViewModel.Buttons.IsEditing = true;
        WindowRig.Pump();
        var name = rig.Window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == button.Name);
        name.Focus();
        rig.Key(Key.F5);   // typing in a field: an F key types nothing, so it still presses the button
        Assert.True(rig.Manager.IsRunning(scare.Id));
    });

    [Fact]
    public Task A_field_keeps_letter_keys_until_enter_esc_or_a_click_elsewhere_takes_the_cursor_out() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var scare = new ShowTimeline { Name = "Scare", Length = S(10) };
        rig.Session.Project.Timelines.Add(scare);
        var buttons = rig.ViewModel.Buttons;
        buttons.AddCommand.Execute(null);
        var button = buttons.Buttons.Single();
        rig.Key(Key.Q);   // a new button waits for its key
        button.AddActionCommand.Execute("run");
        button.Actions[0].Timeline = buttons.Timelines.Single(t => t.Id == scare.Id);
        WindowRig.Pump();
        bool Typing() => rig.Window.FocusManager?.GetFocusedElement() is TextBox;
        void StopIt() { rig.Manager.StopTimeline(scare.Id); rig.Manager.Tick(); }

        var name = rig.Window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == button.Name);
        name.Focus();
        rig.Key(Key.Q);   // typing: the letter goes to the field
        Assert.False(rig.Manager.IsRunning(scare.Id));

        rig.Key(Key.Enter);   // done with the field
        Assert.False(Typing());
        rig.Key(Key.Q);
        Assert.True(rig.Manager.IsRunning(scare.Id));
        StopIt();

        name.Focus();
        rig.Key(Key.Escape);   // first Esc leaves the field, and editing goes on
        Assert.False(Typing());
        Assert.True(buttons.IsEditing);

        name.Focus();
        rig.Click(rig.CenterOf(rig.Find<Controls.TimelineCanvas>()));   // a click on the lanes
        Assert.False(Typing());

        rig.Key(Key.Escape);   // not in a field: Esc finishes editing
        Assert.False(buttons.IsEditing);
    });

    [Fact]
    public Task Right_clicking_a_button_offers_its_editor_and_a_key_without_pressing_it() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var buttons = rig.ViewModel.Buttons;
        var scare = new ShowTimeline { Name = "Scare", Length = S(10) };
        rig.Session.Project.Timelines.Add(scare);
        buttons.AddCommand.Execute(null);
        var button = buttons.Buttons.Single();
        button.AddActionCommand.Execute("run");
        button.Actions[0].Timeline = buttons.Timelines.Single(t => t.Id == scare.Id);
        buttons.CancelLearningKey();
        buttons.IsEditing = false;
        WindowRig.Pump();
        var tile = rig.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("showButton"));

        rig.Click(rig.CenterOf(tile), MouseButton.Right);

        var menu = tile.ContextMenu!;
        Assert.True(menu.IsOpen);
        Assert.False(rig.Manager.IsRunning(scare.Id));
        var items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header!);

        items["Edit"].Command!.Execute(null);
        menu.Close();
        WindowRig.Pump();
        Assert.True(buttons.IsEditing);
        Assert.Same(button, buttons.Selected);
        Assert.False(button.Triggers.IsLearning);

        // The name's field has focus, as it would after renaming: the key still goes to the button.
        var name = rig.Window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == button.Name);
        name.Focus();
        items["Add key…"].Command!.Execute(null);
        rig.Key(Key.F5);
        Assert.Equal("F5", button.KeyLabel);
        Assert.False(rig.Manager.IsRunning(scare.Id));
    });

    [Fact]
    public Task Clicking_into_a_field_or_elsewhere_stops_a_button_waiting_for_a_key() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var buttons = rig.ViewModel.Buttons;
        buttons.AddCommand.Execute(null);
        var button = buttons.Buttons.Single();
        WindowRig.Pump();
        var tile = rig.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("showButton"));

        rig.Click(rig.CenterOf(tile));   // the panel: still waiting
        Assert.True(button.Triggers.IsLearning);

        var name = rig.Window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == button.Name);
        rig.Click(rig.CenterOf(name));
        Assert.False(button.Triggers.IsLearning);
        rig.Key(Key.F5);   // typing: goes to the field
        Assert.Equal("", button.KeyLabel);

        button.Triggers.LearnCommand.Execute(null);
        rig.Click(rig.CenterOf(rig.Find<Controls.TimelineCanvas>()));
        Assert.False(button.Triggers.IsLearning);
    });

    [Fact]
    public Task Choosing_an_action_s_timeline_in_its_list_sets_it_and_stays_shown() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var buttons = rig.ViewModel.Buttons;
        var scare = new ShowTimeline { Name = "Scare", Length = S(10) };
        rig.Session.Project.Timelines.Add(scare);
        buttons.AddCommand.Execute(null);
        buttons.CancelLearningKey();
        var button = buttons.Buttons.Single();
        button.AddActionCommand.Execute("run");
        WindowRig.Pump();
        var action = (Core.Model.RunTimelineAction)button.Button.Actions.Single();
        var list = rig.Window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.DataContext is ViewModels.ButtonActionViewModel);
        Assert.Equal("Main", list.SelectedItem?.ToString());

        // As a person does: open it, and click the item.
        rig.Click(rig.CenterOf(list));
        Assert.True(list.IsDropDownOpen);
        var item = list.GetLogicalDescendants().OfType<ComboBoxItem>().Concat(list.GetVisualDescendants().OfType<ComboBoxItem>())
                       .Distinct().Single(i => i.Content?.ToString() == "Scare");
        rig.Click(rig.CenterOf(item));
        WindowRig.Pump();

        Assert.Equal(scare.Id, action.TimelineId);
        Assert.False(list.IsDropDownOpen);
        Assert.Equal("Scare", list.SelectedItem?.ToString());

        // A timeline made afterwards is in the list too.
        rig.Editor.Tree.NewTimelineCommand.Execute(null);
        WindowRig.Pump();
        Assert.Equal(3, button.Actions[0].Timelines.Count);
        Assert.Equal("Scare", list.SelectedItem?.ToString());
    });

    [Fact]
    public Task Turning_triggers_off_shows_on_every_screen_and_a_click_there_arms_them_again() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var buttons = rig.ViewModel.Buttons;
        var off = rig.Find<Button>(b => b.Name == "TriggersOffButton");
        Assert.False(off.IsEffectivelyVisible);

        buttons.ToggleArmedCommand.Execute(null);
        rig.ViewModel.CurrentPage = ViewModels.Page.Monitor;   // another screen
        WindowRig.Pump();
        Assert.True(off.IsEffectivelyVisible);

        rig.Click(rig.CenterOf(off));
        Assert.True(buttons.TriggersArmed);
        Assert.False(off.IsEffectivelyVisible);

        // The focus warning: armed key triggers while another window has the keyboard.
        buttons.AddCommand.Execute(null);
        rig.Key(Key.F5);
        buttons.IsAppFocused = false;
        WindowRig.Pump();
        Assert.Contains(rig.Window.GetVisualDescendants().OfType<TextBlock>(),
                        t => t.IsEffectivelyVisible && t.Text?.StartsWith("GhostShow isn't the active window") == true);
    });

    [Fact]
    public Task Keys_show_in_the_input_monitor_with_what_they_did_but_shortcuts_and_learning_do_not() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.ViewModel.Buttons.AddCommand.Execute(null);   // a new button waits for its key
        rig.Key(Key.F5);                                   // learned, so not an input
        rig.Key(Key.F5);
        rig.Key(Key.Q);
        rig.Key(Key.D4, RawInputModifiers.Control);        // Ctrl+4: the Inputs screen, and a shortcut isn't an input
        WindowRig.Pump();

        Assert.Equal(ViewModels.Page.InputMonitor, rig.ViewModel.CurrentPage);
        Assert.Equal(
        [
            ("F5", "pressed", "Pressed Button 1"),
            ("F5", "released", ""),
            ("Q", "pressed", "Nothing uses it"),
            ("Q", "released", ""),
        ], rig.ViewModel.Inputs.Monitor.Rows.Reverse().Select(r => (r.Address, r.Change, r.Outcome)));
        var list = rig.Find<ListBox>(l => l.Name == "InputRows");
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal(4, list.ItemCount);
    });

    /// <summary>A serial port the test sends commands through.</summary>
    sealed class ScriptedSerialLink : GhostShow.Inputs.ISerialLink
    {
        readonly System.Collections.Concurrent.BlockingCollection<byte[]> _chunks = new();
        public void Send(string text) => _chunks.Add(System.Text.Encoding.Latin1.GetBytes(text));

        public int Read(byte[] buffer)
        {
            if (!_chunks.TryTake(out var chunk, 20)) return 0;
            chunk.CopyTo(buffer, 0);
            return chunk.Length;
        }

        public void Dispose() { }
    }

    [Fact]
    public Task A_serial_command_shows_in_the_monitor_is_learned_by_a_waiting_button_then_presses_it() => Ui.Run(() =>
    {
        var link = new ScriptedSerialLink();
        using var serial = new GhostShow.Inputs.SerialInputs(NullLogger.Instance, (_, _) => link);
        var ethereal = new SerialInputDevice { Name = "Ethereal FX", BaudRate = 57600 };
        using var rig = new WindowRig(serial, new MachineSettings { InputPorts = [new InputPortBinding(ethereal.Id, "COM3")] },
                                      session => session.Project.Inputs.Add(ethereal));
        void PumpUntil(Func<bool> done)
        {
            for (int i = 0; i < 100 && !done(); i++) WindowRig.Pump(20);
            Assert.True(done());
        }

        PumpUntil(() => rig.ViewModel.Inputs.IsListening);
        rig.Key(Key.D4, RawInputModifiers.Control);
        WindowRig.Pump();
        var status = rig.Find<TextBlock>(t => t.Name == "PART_Status" && t.DataContext is InputDeviceRowViewModel);   // on its card, without opening it
        Assert.True(status.IsEffectivelyVisible);
        Assert.Equal("Listening on COM3 at 57600 baud.", status.Text);

        rig.ViewModel.Buttons.AddCommand.Execute(null);   // a new button waits for its trigger: a key or a command
        var button = rig.ViewModel.Buttons.Buttons.Single();
        Assert.Equal("Press a key or send a command… (Esc cancels)", button.Triggers.LearnLabel);
        link.Send("BTN24\r");
        PumpUntil(() => button.Triggers.Count == 1);
        Assert.Equal("Ethereal FX “BTN24”", button.Triggers[0].Label);

        link.Send("BTN24\r");
        var monitor = rig.ViewModel.Inputs.Monitor;
        PumpUntil(() => monitor.Rows.Count == 2);
        Assert.Equal(
        [
            ("Ethereal FX", "BTN24", "received", "Learned by Button 1"),
            ("Ethereal FX", "BTN24", "received", "Pressed Button 1"),
        ], monitor.Rows.Reverse().Select(r => (r.Source, r.Address, r.Change, r.Outcome)));
        Assert.StartsWith("Last: “BTN24”", rig.ViewModel.Inputs.Devices.Single().LastReceived);
    });

    [Fact]
    public Task A_missing_DMX_adapter_s_old_port_can_be_the_serial_input() => Ui.Run(() =>
    {
        // Universe 1's adapter was last on COM3 and isn't plugged in; another device (a sensor box) is on COM3 now.
        var link = new ScriptedSerialLink();
        using var serial = new GhostShow.Inputs.SerialInputs(NullLogger.Instance, (_, _) => link);
        var box = new SerialInputDevice { Name = "Sensor box" };
        var settings = new MachineSettings
        {
            InputPorts = [new InputPortBinding(box.Id, "COM3")],
            Outputs = [new OutputBinding(1, new OpenDmxDeviceConfig("COM3", "NOT-PLUGGED-IN"))],
        };
        using var rig = new WindowRig(serial, settings, session => session.Project.Inputs.Add(box));

        for (int i = 0; i < 100 && !rig.ViewModel.Inputs.IsListening; i++) WindowRig.Pump(20);

        Assert.Equal("Listening on COM3 at 9600 baud.", rig.ViewModel.Inputs.Devices.Single().StatusLabel);
    });

    [Fact]
    public Task Holding_a_button_s_key_presses_it_once() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var scare = new ShowTimeline { Name = "Scare", Length = S(10) };
        rig.Session.Project.Timelines.Add(scare);
        rig.ViewModel.Buttons.AddCommand.Execute(null);
        var button = rig.ViewModel.Buttons.Buttons.Single();
        button.AddActionCommand.Execute("run");
        button.Actions[0].Timeline = rig.ViewModel.Buttons.Timelines.Single(t => t.Id == scare.Id);
        rig.Key(Key.F5);
        rig.ViewModel.Buttons.IsEditing = false;
        WindowRig.Pump();
        int Playing() => rig.Manager.Running.Count(r => r.TimelineId == scare.Id);

        rig.KeyDown(Key.F5);
        rig.KeyDown(Key.F5);   // the key repeating
        rig.KeyDown(Key.F5);
        Assert.Equal(1, Playing());

        rig.Window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.None, null);
        rig.Key(Key.F5);   // let go, and pressed again
        Assert.Equal(2, Playing());
    });

    [Fact]
    public Task Dragging_a_timeline_from_the_tree_onto_the_lanes_adds_a_run_timeline_event_there() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var main = rig.Editor.SelectedTimeline!;
        rig.Editor.Tree.NewTimelineCommand.Execute(null);   // a second timeline in the tree
        WindowRig.Pump();
        var room = rig.Session.Project.Timelines.Single(t => t != main);
        rig.Editor.OpenTimeline(main);
        WindowRig.Pump();
        var node = rig.Editor.Tree.AllNodes().OfType<TimelineNode>().Single(n => n.Timeline == room);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Controls.TimelineCanvas.TreeNodeFormat, (TimelineTreeNode)node));
        var at = rig.At(rig.TrackRow("Lighting"), 4);

        rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragEnter, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragOver, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.Drop, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        WindowRig.Pump();

        var e = Assert.IsType<TimelineCommandEvent>(Assert.Single(rig.Editor.SelectedTimeline!.Tracks.Single(t => t.Kind == TrackKind.Event).Events));
        Assert.Equal((4.0, room.Id), (Math.Round(e.Start.TotalSeconds, 1), e.TimelineId));
    });

    [Fact]
    public Task A_tree_drag_that_arrives_without_its_in_process_data_still_drops_the_timeline() => Ui.Run(() =>
    {
        // On Linux a drag can go through XDND even inside the app, which carries only text: the lanes fall back to
        // the node the tree says it's dragging.
        using var rig = new WindowRig();
        var main = rig.Editor.SelectedTimeline!;
        rig.Editor.Tree.NewTimelineCommand.Execute(null);
        WindowRig.Pump();
        var room = rig.Session.Project.Timelines.Single(t => t != main);
        rig.Editor.OpenTimeline(main);
        WindowRig.Pump();
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DataFormat.Text, room.Name));
        var at = rig.At(rig.TrackRow("Lighting"), 3);
        Controls.TimelineCanvas.DraggedNode = rig.Editor.Tree.AllNodes().OfType<TimelineNode>().Single(n => n.Timeline == room);
        try
        {
            rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragEnter, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
            rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragOver, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
            rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.Drop, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        }
        finally
        {
            Controls.TimelineCanvas.DraggedNode = null;
        }
        WindowRig.Pump();

        var e = Assert.IsType<TimelineCommandEvent>(Assert.Single(main.Tracks.Single(t => t.Kind == TrackKind.Event).Events));
        Assert.Equal(room.Id, e.TimelineId);
    });

    [Fact]
    public Task Remote_access_switches_on_lists_a_device_that_pairs_and_removes_it() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var remote = rig.ViewModel.Remote;
        static void Await(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until) WindowRig.Pump(20);
            task.GetAwaiter().GetResult();
        }
        remote.Port = 20000 + Random.Shared.Next(20000);
        Await(remote.SetEnabledAsync(true));
        Assert.True(remote.IsOn);
        Assert.Equal("Remote on", remote.ConnectedLabel);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remote.Server.Port}/") };
        var pairing = http.PostAsync("api/pair", new StringContent($$"""{"pin":"{{remote.Pin}}","name":"Stage iPad","role":"operator"}""", System.Text.Encoding.UTF8, "application/json"));
        Await(pairing);
        Assert.True(pairing.Result.IsSuccessStatusCode);
        WindowRig.Pump(200);

        var row = Assert.Single(remote.Devices);
        Assert.Equal(("Stage iPad", 1), (row.Name, row.RoleIndex));
        row.RemoveCommand.Execute(null);
        WindowRig.Pump(200);
        Assert.Empty(remote.Devices);

        Await(remote.SetEnabledAsync(false));
        Assert.False(remote.IsOn);
    });

    [Fact]
    public Task A_device_s_commands_work_the_desktop_s_own_controls() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        static T Await<T>(Task<T> task)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until) WindowRig.Pump(10);
            return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("No answer from the web remote in 10 s.");
        }
        var main = rig.Editor.SelectedTimeline!;
        var room = new ShowTimeline { Name = "Room", Length = TimeSpan.FromSeconds(30) };
        rig.Session.Project.Timelines.Add(room);
        rig.Editor.Tree.Rebuild();
        rig.Session.Project.Buttons.Add(new Core.Model.ShowButton { Name = "Scare", Actions = [new Core.Model.RunTimelineAction { TimelineId = room.Id }] });
        rig.ViewModel.Buttons.Reload();
        var remote = rig.ViewModel.Remote;
        remote.Port = 20000 + Random.Shared.Next(20000);
        Await(remote.SetEnabledAsync(true).ContinueWith(_ => true));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remote.Server.Port}/") };
        var paired = Await(http.PostAsync("api/pair", new StringContent($$"""{"pin":"{{remote.Pin}}","name":"iPad"}""", System.Text.Encoding.UTF8, "application/json")));
        var token = System.Text.Json.Nodes.JsonNode.Parse(Await(paired.Content.ReadAsStringAsync()))!["token"]!.GetValue<string>();
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        Await(socket.ConnectAsync(new Uri($"ws://127.0.0.1:{remote.Server.Port}/api/live?token={token}"), CancellationToken.None).ContinueWith(_ => true));
        var buffer = new byte[1 << 20];
        int id = 0;
        string Command(string action, string? name = null, Guid? value = null)
        {
            var json = new System.Text.Json.Nodes.JsonObject { ["type"] = "command", ["id"] = ++id, ["action"] = action };
            if (name is not null) json[name] = value.ToString();
            var text = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
            Await(socket.SendAsync(text, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).ContinueWith(_ => true));
            while (true)
            {
                var received = Await(socket.ReceiveAsync(buffer, CancellationToken.None));
                var message = System.Text.Json.Nodes.JsonNode.Parse(buffer.AsSpan(0, received.Count))!;
                if (message["type"]!.GetValue<string>() == "result" && message["id"]!.GetValue<int>() == id)
                    return message["ok"]!.GetValue<bool>() ? "ok" : message["error"]!.GetValue<string>();
            }
        }

        Assert.Equal("ok", Command("run", "timeline", room.Id));
        Assert.True(rig.Manager.IsRunning(room.Id));
        Assert.Equal("ok", Command("stopTimeline", "timeline", room.Id));
        WindowRig.Pump(200);
        Assert.False(rig.Manager.IsRunning(room.Id));
        Assert.Equal("ok", Command("press", "button", rig.Session.Project.Buttons[0].Id));
        Assert.True(rig.Manager.IsRunning(room.Id));
        Assert.Equal("ok", Command("play"));
        Assert.True(rig.ViewModel.Playback.IsPlaying);
        Assert.Equal("ok", Command("blackout"));
        Assert.Empty(rig.Manager.Running);
        Assert.Equal("ok", Command("open", "timeline", room.Id));
        Assert.Same(room, rig.Editor.SelectedTimeline);
        Assert.NotSame(main, rig.ViewModel.Playback.Timeline);
        Assert.Equal("That timeline isn't in the show any more.", Command("run", "timeline", Guid.NewGuid()));

        Await(remote.SetEnabledAsync(false).ContinueWith(_ => true));
    });

    [Fact]
    public Task Dragging_the_open_timeline_onto_its_own_lanes_is_refused() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var main = rig.Editor.SelectedTimeline!;
        var node = rig.Editor.Tree.AllNodes().OfType<TimelineNode>().Single(n => n.Timeline == main);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Controls.TimelineCanvas.TreeNodeFormat, (TimelineTreeNode)node));
        var at = rig.At(rig.TrackRow("Lighting"), 4);

        rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragEnter, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragOver, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        rig.Window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.Drop, data, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        WindowRig.Pump();

        Assert.DoesNotContain(main.Tracks, t => t.Kind == TrackKind.Event);
    });

    // ---- the setup screens' undo ----

    [Fact]
    public Task Ctrl_Z_on_the_fixtures_screen_undoes_a_fixture_edit() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.ViewModel.CurrentPage = Page.Fixtures;
        WindowRig.Pump(100);
        var fixtures = rig.ViewModel.Fixtures;
        var row = fixtures.Fixtures[0];
        string name = row.Name;
        fixtures.Selected = row;
        WindowRig.Pump(50);

        row.Name = "Renamed";
        rig.Key(Key.Z, RawInputModifiers.Control);   // with nothing in particular focused

        Assert.Equal(name, row.Model.Name);
        Assert.Same(row, fixtures.Selected);

        rig.Key(Key.Y, RawInputModifiers.Control);

        Assert.Equal("Renamed", row.Model.Name);
    });

    [Fact]
    public Task Each_Lighting_tab_shows_its_own_screen() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        (Page Page, Func<Control> Screen)[] screens =
        [
            (Page.DmxSetup, () => rig.Find<Views.OutputsView>()),
            (Page.Fixtures, () => rig.Find<Views.FixtureSetupView>()),
            (Page.Control, () => rig.Find<Views.FixtureControlView>()),
            (Page.Channels, () => rig.Find<Views.ChannelsView>()),
            (Page.Monitor, () => rig.Find<Views.MonitorView>()),
        ];
        foreach (var (page, _) in screens)
        {
            rig.ViewModel.CurrentPage = page;
            WindowRig.Pump(50);
            Assert.Equal(page, rig.ViewModel.CurrentPage);
            Assert.Equal([page], screens.Where(s => s.Screen().IsEffectivelyVisible).Select(s => s.Page));
        }

        rig.ViewModel.CurrentPage = Page.Timeline;
        WindowRig.Pump(50);
        Assert.False(rig.Find<Views.LightingView>().IsEffectivelyVisible);
    });

    [Fact]
    public Task Remote_access_shows_its_addresses_as_links_to_themselves() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var remote = rig.ViewModel.Remote;
        var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start();
        remote.Port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        remote.IsEnabled = true;
        for (var i = 0; i < 100 && !remote.IsOn; i++) WindowRig.Pump(50);
        Assert.True(remote.IsOn, remote.Status);

        var window = new Views.RemoteWindow { DataContext = remote };
        window.Show();
        WindowRig.Pump(50);
        var links = window.GetVisualDescendants().OfType<HyperlinkButton>().Where(l => l.IsEffectivelyVisible).ToList();

        // Text and destination have to agree: a link that opens somewhere other than what it says is worse than none.
        Assert.Equal(remote.Addresses, links.Select(l => (string?)l.Content).Take(remote.Addresses.Count));
        Assert.All(links, l => Assert.Equal((string?)l.Content, l.NavigateUri?.ToString()));

        // A link's text can't be selected, so its menu has to copy it.
        foreach (var link in links)
        {
            link.ContextMenu!.Open(link);
            WindowRig.Pump(50);
            ((MenuItem)link.ContextMenu.Items[0]!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            WindowRig.Pump(50);
            link.ContextMenu.Close();
            var copied = window.Clipboard!.TryGetTextAsync();
            while (!copied.IsCompleted) WindowRig.Pump(10);
            Assert.Equal((string?)link.Content, copied.Result);
        }

        window.Close();
        remote.IsEnabled = false;
        for (var i = 0; i < 100 && remote.IsOn; i++) WindowRig.Pump(50);
    });

    [Fact]
    public Task An_effect_in_the_menu_is_dragged_onto_a_track_or_clicked_in_at_the_playhead() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var menu = rig.Find<ToggleButton>(b => b.Name == "EffectsButton");
        Border Entry(EffectKind kind)
        {
            rig.Click(rig.CenterOf(menu));
            WindowRig.Pump(100);
            return rig.Window.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is EffectChoice c && c.Kind == kind && b.IsEffectivelyVisible);
        }
        var track = rig.TrackRow("Lighting").Track.Track;
        int before = track.Events.Count;

        rig.Drag(rig.CenterOf(Entry(EffectKind.Strobe)), rig.At(rig.TrackRow("Lighting"), 4));

        var clip = Assert.IsType<LightingClip>(track.Events[before]);
        Assert.Equal(EffectKind.Strobe, clip.Effect?.Kind);
        Assert.Equal(4, clip.Start.TotalSeconds, 0.5);
        Assert.False(menu.IsChecked);   // the menu got out of the way as the drag started

        // Let go over a track that can't take it: nothing is added.
        rig.Drag(rig.CenterOf(Entry(EffectKind.Fire)), rig.At(rig.TrackRow("Audio"), 4));
        Assert.Equal(before + 1, track.Events.Count);

        // A click: at the playhead (0:00, free up to the strobe dropped at 4 s), on the lighting track.
        rig.Click(rig.CenterOf(Entry(EffectKind.Strobe)));
        Assert.Equal(before + 2, track.Events.Count);
        Assert.Equal(TimeSpan.Zero, track.Events.Min(e => e.Start));
        Assert.False(menu.IsChecked);
    });

    [Fact]
    public Task The_dmx_setup_screen_adds_a_universe_and_undoes_it() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.ViewModel.CurrentPage = Page.DmxSetup;
        WindowRig.Pump(100);
        var outputs = rig.ViewModel.Outputs;
        int before = outputs.Universes.Count;
        var kept = outputs.Universes[0];

        outputs.AddUniverseCommand.Execute(null);
        WindowRig.Pump(100);
        Assert.Equal(before + 1, outputs.Universes.Count);
        Assert.Equal(before + 1, rig.Session.Project.Universes.Count);

        outputs.History.UndoCommand.Execute(null);
        WindowRig.Pump(100);
        Assert.Equal(before, outputs.Universes.Count);
        Assert.Same(kept, outputs.Universes[0]);   // the rows that stay aren't rebuilt

        kept.Name = "Stage";
        WindowRig.Pump(50);
        outputs.History.UndoCommand.Execute(null);
        WindowRig.Pump(100);
        Assert.NotEqual("Stage", kept.Name);
        Assert.NotEqual("Stage", kept.Model.Name);
    });

    // ---- the Envelope Editor window ----

    /// <summary>Opens the Envelope Editor by double-clicking a clip, and returns its window and plot.</summary>
    static (Window Window, Controls.EnvelopePlot Plot) OpenEnvelopeEditor(WindowRig rig, double at)
    {
        rig.DoubleClick(rig.At(rig.TrackRow("Lighting"), at));
        var window = rig.Find<Views.TimelineView>().EnvelopeWindow ?? throw new InvalidOperationException("The Envelope Editor didn't open.");
        WindowRig.Pump(200);
        return (window, window.GetVisualDescendants().OfType<Controls.EnvelopePlot>().Single());
    }

    static Point On(Window window, Controls.EnvelopePlot plot, double seconds, double level) =>
        plot.TranslatePoint(plot.PointOf(S(seconds), level), window)!.Value;

    static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        WindowRig.Pump(20);
        for (int i = 1; i <= 8; i++)
        {
            window.MouseMove(from + (to - from) * (i / 8.0));
            WindowRig.Pump(15);
        }
        window.MouseUp(to, MouseButton.Left);
        WindowRig.Pump(100);
    }

    [Fact]
    public Task The_envelope_editor_s_colour_handle_button_adds_a_colour_handle_set_with_a_colour_picker() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.SetTrackChannels(rig.Lighting, new[] { ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue }.Select(f => LaneTarget.FixtureControl(rig.Rgb.Id, f)).ToList());
        var clip = AddClip(rig, 1, 4);
        rig.Editor.Playback.Seek(S(3));
        var (window, _) = OpenEnvelopeEditor(rig, 2);

        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Colour handle");
        Assert.True(button.IsEffectivelyEnabled);
        var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Assert.InRange(at.X, 0, window.Bounds.Width);   // the toolbar wraps rather than pushing it out of the window
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        WindowRig.Pump(200);

        Assert.Equal(3, clip.Lanes.Count);
        Assert.All(clip.Lanes, l => Assert.Equal(S(2), l.Points[0].Time));   // at the playhead (3 s is 2 s into the clip)
        Assert.True(rig.Editor.SelectedHandleGroup?.IsColour);
        Assert.Contains(window.GetVisualDescendants().OfType<ColorPicker>(), p => p.IsEffectivelyVisible);
    });

    [Fact]
    public Task The_envelope_editor_s_start_handle_is_grabbed_from_a_little_way_off_even_at_0_on_the_clip_s_edge() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        AddClip(rig, 1, 4);   // no handles: a flat line at the start level (0), from the clip's start line
        var (window, plot) = OpenEnvelopeEditor(rig, 2);

        // Pressed 10 px up and to the right of it (further than an ordinary handle is grabbed from), and dragged up.
        var start = On(window, plot, 0, 0);
        Drag(window, start + new Point(7, -7), start + new Point(7, -7) + (On(window, plot, 0, 128) - start));

        Assert.InRange(rig.Lighting.Channels[0].StartLevel, 120, 136);
    });

    [Fact]
    public Task Double_clicking_a_clip_opens_the_envelope_editor_where_handles_are_dragged() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var clip = AddClip(rig, 1, 4, (2, 100), (4, 100));
        var handle = clip.Lanes[0].Points[0];

        var (window, plot) = OpenEnvelopeEditor(rig, 2);
        Assert.Same(clip, rig.Editor.EnvelopeEditor?.Clip);
        Assert.False(rig.Editor.Tracks[0].IsExpanded);   // the track stays as it was

        Drag(window, On(window, plot, 2, 100), On(window, plot, 3, 200));
        Assert.Equal(S(3), handle.Time);   // relative to the clip
        Assert.InRange(handle.Value, 195, 205);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);   // undo works in the window too
        WindowRig.Pump(80);
        Assert.Equal((S(2), (byte)100), (handle.Time, handle.Value));
    });

    [Fact]
    public Task Double_clicking_a_curve_in_the_envelope_editor_adds_a_handle_on_it() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var clip = AddClip(rig, 1, 4, (2, 100), (4, 100));
        var (window, plot) = OpenEnvelopeEditor(rig, 2);
        var on = On(window, plot, 1, 50);   // half way up the fade from 0 to 100 (2 s in)

        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        WindowRig.Pump(100);

        Assert.Equal([(1.0, 50), (2, 100), (4, 100)], clip.Lanes[0].Points.Select(p => (p.Time.TotalSeconds, (int)p.Value)));
        Assert.Equal("1 handle selected", rig.Editor.SelectionLabel);
    });

    [Fact]
    public Task Double_clicking_away_from_the_curves_adds_a_handle_to_the_active_channel_where_clicked() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var clip = AddClip(rig, 1, 4, (4, 0));
        var (window, plot) = OpenEnvelopeEditor(rig, 2);
        var at = On(window, plot, 2, 200);   // well above the red line (which is at 0)

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        WindowRig.Pump(100);

        var added = clip.Lanes[0].Points[0];
        Assert.Equal(S(2), added.Time);
        Assert.InRange(added.Value, 195, 205);
    });

    [Fact]
    public Task Delete_in_the_envelope_editor_takes_handles_but_never_the_clip() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var clip = AddClip(rig, 1, 4, (2, 100), (4, 100));
        var (window, _) = OpenEnvelopeEditor(rig, 2);
        Assert.Empty(rig.Editor.SelectedPoints);

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        WindowRig.Pump(80);

        Assert.Contains(clip, rig.Lighting.Events);
        Assert.Equal(2, clip.Lanes[0].Points.Count);
    });

    [Fact]
    public Task On_an_expanded_track_double_clicking_an_envelope_line_adds_a_handle_and_elsewhere_opens_the_editor() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Tracks[0].IsExpanded = true;
        var clip = AddClip(rig, 1, 4, (4, 200));   // red rises from 0 to 200 over 4 s
        var row = rig.TrackRow("Lighting");

        rig.DoubleClick(rig.At(row, 3, level: 100 / 255.0));   // 2 s in: on the line, at 100
        Assert.Equal([(2.0, 100), (4, 200)], clip.Lanes[0].Points.Select(p => (p.Time.TotalSeconds, (int)p.Value)));
        Assert.Null(rig.Editor.EnvelopeEditor);

        rig.DoubleClick(rig.At(row, 3, level: 0.95));   // away from it
        Assert.Same(clip, rig.Editor.EnvelopeEditor?.Clip);
    });

    [Fact]
    public Task Closing_the_envelope_editor_window_lets_go_of_it() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        AddClip(rig, 1, 4, (4, 100));
        var (window, _) = OpenEnvelopeEditor(rig, 2);

        window.Close();
        WindowRig.Pump(100);

        Assert.Null(rig.Editor.EnvelopeEditor);
        Assert.Null(rig.Find<Views.TimelineView>().EnvelopeWindow);
    });

    [Fact]
    public Task Dragging_a_clip_moves_it_and_dragging_its_edges_resizes_it() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var clip = AddClip(rig, 1, 4, (4, 200));
        var row = rig.TrackRow("Lighting");

        rig.Drag(rig.At(row, 3), rig.At(row, 4));   // body: 1 s later
        Assert.Equal(S(2), clip.Start);

        rig.Drag(rig.At(row, 6) + new Avalonia.Point(-2, 0), rig.At(row, 8));   // right edge: 6 s → 8 s
        Assert.Equal((S(2), S(6)), (clip.Start, clip.Length));
        Assert.Equal(S(6), clip.Lanes[0].Points[^1].Time);   // the envelope scaled with it

        rig.Drag(rig.At(row, 2) + new Avalonia.Point(2, 0), rig.At(row, 5));   // left edge: 2 s → 5 s, end kept
        Assert.Equal((S(5), S(3)), (clip.Start, clip.Length));

        rig.Key(Key.Z, RawInputModifiers.Control);
        Assert.Equal((S(2), S(6)), (clip.Start, clip.Length));
    });

    [Fact]
    public Task The_custom_fixture_type_editor_s_buttons_add_move_and_remove_channels() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.ViewModel.CurrentPage = Page.Fixtures;
        WindowRig.Pump(300);
        var setup = rig.ViewModel.Fixtures;
        setup.StartAddCommand.Execute(null);
        WindowRig.Pump(200);
        rig.Click(rig.CenterOf(rig.Find<Button>(b => b.Content as string == "+ New fixture type…")));
        var mode = setup.ProfileEditor!.SelectedMode!;
        Button Quick(string label) => rig.Find<Button>(b => b.Content as string == $"+ {label}" && b.IsEffectivelyVisible);

        rig.Click(rig.CenterOf(Quick("Red")));
        rig.Click(rig.CenterOf(Quick("Dimmer (intensity)")));
        rig.Click(rig.CenterOf(Quick("Other")));
        Assert.Equal(["Red", "Dimmer", "Channel 3"], mode.Channels.Select(c => c.Name));

        var dimmerUp = rig.Find<Button>(b => b.DataContext == mode.Channels[1] && ToolTip.GetTip(b) as string == "Move up");
        rig.Click(rig.CenterOf(dimmerUp));   // the dimmer first, as on most fixtures
        Assert.Equal(["Dimmer", "Red", "Channel 3"], mode.Channels.Select(c => c.Name));

        var removeLast = rig.Find<Button>(b => b.DataContext == mode.Channels[2] && ToolTip.GetTip(b) as string == "Remove");
        rig.Click(rig.CenterOf(removeLast));
        Assert.Equal(["Dimmer", "Red"], mode.Channels.Select(c => c.Name));
    });

    [Fact]
    public Task Ctrl_W_closes_the_show_and_shows_home() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        Assert.Equal(Page.Timeline, rig.ViewModel.CurrentPage);

        rig.Key(Key.W, RawInputModifiers.Control);

        Assert.False(rig.ViewModel.IsShowOpen);
        Assert.Equal(Page.Home, rig.ViewModel.CurrentPage);
        Assert.Equal("GhostShow", rig.ViewModel.Title);
    });

    [Fact]
    public Task Space_on_the_timeline_plays_and_pauses() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Click(rig.At(rig.TrackRow("Audio"), 8));   // focus the lanes (an empty spot)

        rig.Key(Key.Space);
        Assert.Equal(Core.Playback.TransportState.Playing, rig.Status.State);
        rig.Key(Key.Space);
        Assert.Equal(Core.Playback.TransportState.Paused, rig.Status.State);
    });

    // ---- editor polish: box select, snapping, nudging, renaming, Space, the Add button ----

    /// <summary>A Level Control clip on the lighting track with Red handles at (time in the clip, level); the last is its end handle.</summary>
    static LightingClip AddClip(WindowRig rig, double start, double length, params (double At, byte Level)[] handles)
    {
        var clip = new LightingClip { Start = S(start), Length = S(length) };
        if (handles.Length > 0)
            clip.Lanes.Add(new ChannelLane { Target = rig.Lighting.Channels[0].Target, Points = handles.Select(p => new CurvePoint { Time = S(p.At), Value = p.Level }).ToList() });
        rig.Lighting.Events.Add(clip);
        rig.Editor.Relayout();
        WindowRig.Pump();
        return clip;
    }

    [Fact]
    public Task Dragging_a_box_over_an_expanded_clip_selects_the_handles_inside() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Tracks[0].IsExpanded = true;
        var clip = AddClip(rig, 1, 8, (1, 50), (3, 150), (6, 250), (8, 0));
        var row = rig.TrackRow("Lighting");

        // From empty space before the clip (a drag that starts on a clip moves it).
        rig.Drag(rig.At(row, 0.5, level: 1) + new Avalonia.Point(0, -4), rig.At(row, 5, level: 0) + new Avalonia.Point(0, 4));

        Assert.Equal(clip.Lanes[0].Points.Take(2).ToHashSet(), rig.Editor.SelectedPoints);
    });

    [Fact]
    public Task A_dragged_clip_snaps_its_start_to_the_end_of_another_clip() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.Editor.Snap = TimelineEditorViewModel.SnapOptions[0];   // grid off: only edges snap
        var first = AddClip(rig, 0, 2.37, (2.37, 10));
        var second = AddClip(rig, 5, 2, (2, 20));
        var row = rig.TrackRow("Lighting");

        // Grab the second clip in its middle and drop it so its start lands 4 px past the first one's end.
        rig.Drag(rig.At(row, 6), rig.At(row, 6 - 5 + 2.37) + new Avalonia.Point(4, 0));

        Assert.Equal(first.Start + first.Length, second.Start);
    });

    [Fact]
    public Task Arrow_keys_nudge_the_selected_clip_by_the_snap_step() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var clip = AddClip(rig, 2, 2, (2, 10));
        rig.Click(rig.At(rig.TrackRow("Lighting"), 3));   // select it
        rig.Editor.Snap = TimelineEditorViewModel.SnapOptions.Single(o => o.Interval == S(0.5));

        rig.Key(Key.Right);
        rig.Key(Key.Right);
        rig.Key(Key.Left, RawInputModifiers.Shift);   // fine: 0.01 s

        Assert.Equal(S(2.99), clip.Start);
    });

    [Fact]
    public Task Double_clicking_a_track_name_renames_it_in_place() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var name = rig.Find<TextBlock>(t => t.Classes.Contains("trackName") && t.Text == "Audio");

        rig.DoubleClick(rig.CenterOf(name));
        Assert.True(rig.Editor.Tracks.Single(t => t.Kind == TrackKind.Audio).IsRenaming);
        rig.Window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);   // replace the whole name
        rig.Type("Thunder");
        rig.Key(Key.Enter);

        Assert.Equal("Thunder", rig.Session.Project.Timelines[0].Tracks[1].Name);
    });

    [Fact]
    public Task Space_plays_and_pauses_from_other_screens_too() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.ViewModel.CurrentPage = Page.Fixtures;
        WindowRig.Pump(200);

        rig.Key(Key.Space);

        Assert.Equal(Core.Playback.TransportState.Playing, rig.Status.State);
    });

    [Fact]
    public void The_inspector_shows_the_selection_or_the_timeline_and_folds_to_a_strip() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var strip = rig.Find<Button>(b => b.Name == "InspectorStrip");
        var panel = rig.Find<Border>(b => b.Name == "InspectorPanel");
        var selectionTab = rig.Find<Button>(b => b.Name == "SelectionTab");
        Assert.True(rig.Editor.IsInspectorOpen);   // out from the start: it's where everything is edited
        Assert.False(strip.IsVisible);
        Assert.Equal(320, panel.GetVisualParent<Border>()!.Bounds.Width);

        // Nothing selected: the timeline's properties, and they reach the timeline.
        Assert.True(rig.Editor.IsTimelineTab);
        Assert.False(selectionTab.IsEnabled);
        var scare = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Make it a scare");
        rig.Click(rig.CenterOf(scare));
        WindowRig.Pump();
        Assert.Equal(10, rig.Editor.SelectedTimeline!.Playback.Priority);

        // Selecting a track shows it; the Timeline tab is a click away, and the selection tab brings it back.
        rig.Editor.EditTrackProperties(rig.Editor.Tracks.First(t => t.IsAudio));
        WindowRig.Pump();
        Assert.Equal((false, "Track", true), (rig.Editor.IsTimelineTab, selectionTab.Content as string, selectionTab.IsEnabled));
        Assert.Equal("Audio", rig.Find<TextBlock>(t => t.Name == "InspectorTitle").Text);   // the tab says what sort; the title, which
        rig.Click(rig.CenterOf(rig.Find<Button>(b => b.Name == "TimelineTab")));
        Assert.True(rig.Editor.IsTimelineTab);
        rig.Click(rig.CenterOf(selectionTab));
        Assert.False(rig.Editor.IsTimelineTab);
        rig.Editor.TrackProperties!.CloseCommand.Execute(null);
        WindowRig.Pump();
        Assert.True(rig.Editor.IsTimelineTab);   // nothing selected again: the timeline

        var fold = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "»");
        rig.Click(rig.CenterOf(fold));
        WindowRig.Pump(300);   // the slide takes 150 ms
        Assert.False(rig.Editor.IsInspectorOpen);
        Assert.True(strip.IsVisible);
        Assert.False(panel.IsEnabled);   // closed: nothing in it can take focus
        Assert.Equal(0, panel.GetVisualParent<Border>()!.Bounds.Width);

        rig.Click(rig.CenterOf(strip));
        WindowRig.Pump(300);
        Assert.True(rig.Editor.IsInspectorOpen);
    });

    [Fact]
    public Task Side_panels_are_resized_by_their_grips_and_fold_when_dragged_past_their_narrowest() => Ui.Run(() =>
    {
        var settings = new MachineSettings();
        using var rig = new WindowRig(settings: settings);
        rig.Window.Width = 1700;   // room for the lanes with every panel out (else the tree folds to make it)
        rig.ViewModel.Buttons.IsOpen = true;
        WindowRig.Pump(300);
        var editor = rig.Editor;
        Assert.True(editor.IsTreeOpen);
        var inspector = rig.Find<Controls.PanelGrip>(g => g.Name == "InspectorGrip");
        var tree = rig.Find<Controls.PanelGrip>(g => g.Name == "TreeGrip");

        // The inspector's grip is on its left edge: left makes it wider.
        rig.Drag(rig.CenterOf(inspector), rig.CenterOf(inspector) - new Point(100, 0));
        Assert.Equal(TimelineEditorViewModel.UsualInspectorWidth + 100, editor.InspectorWidth);
        WindowRig.Pump(300);
        Assert.Equal(editor.InspectorWidth, rig.Find<Border>(b => b.Name == "InspectorPanel").Bounds.Width);

        // The tree's is on its right edge, and it stops at its narrowest...
        rig.Drag(rig.CenterOf(tree), rig.CenterOf(tree) - new Point(60, 0));
        Assert.Equal(170, editor.TreeWidth);
        rig.Drag(rig.CenterOf(tree), rig.CenterOf(tree) - new Point(30, 0));
        Assert.Equal(160, editor.TreeWidth);
        // ...until it's dragged well past it: then it folds away, keeping its width for next time.
        rig.Drag(rig.CenterOf(tree), rig.CenterOf(tree) - new Point(120, 0));
        Assert.False(editor.IsTreeOpen);
        Assert.Equal(160, editor.TreeWidth);
        rig.Click(rig.CenterOf(rig.Find<Button>(b => b.Name == "TreeStrip")));
        Assert.True(editor.IsTreeOpen);

        // However far it's dragged, the lanes keep room to work in (and the tree, opened by hand, stays out).
        rig.Window.Width = 1400;
        WindowRig.Pump(300);
        rig.Drag(rig.CenterOf(inspector), rig.CenterOf(inspector) - new Point(800, 0));
        WindowRig.Pump(300);
        Assert.InRange(rig.Find<Border>(b => b.Name == "LanesArea").Bounds.Width, 415, 425);

        // A double-click: the usual width.
        rig.DoubleClick(rig.CenterOf(inspector));
        Assert.Equal(TimelineEditorViewModel.UsualInspectorWidth, editor.InspectorWidth);

        // The Buttons panel's grip too.
        var buttons = rig.Window.GetVisualDescendants().OfType<Controls.PanelGrip>().Single(g => g.DataContext is ButtonsViewModel);
        rig.Drag(rig.CenterOf(buttons), rig.CenterOf(buttons) - new Point(50, 0));
        Assert.Equal(ButtonsViewModel.UsualWidth + 50, rig.ViewModel.Buttons.PanelWidth);

        // This computer remembers them, once they've been let go a moment.
        WindowRig.Pump(1300);
        Assert.Equal((160.0, TimelineEditorViewModel.UsualInspectorWidth, ButtonsViewModel.UsualWidth + 50),
                     (settings.Panels.Timelines, settings.Panels.Inspector, settings.Panels.Buttons));
    });

    [Fact]
    public Task The_workspace_sits_under_the_timeline_only_and_its_views_are_tabs() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var workspace = rig.ViewModel.BottomWorkspace;
        workspace.IsOpen = true;
        WindowRig.Pump(300);
        Rect Of(string name) => rig.Find<Control>(c => c.Name == name) is var c ? new Rect(c.TranslatePoint(new Point(), rig.Window)!.Value, c.Bounds.Size) : default;

        // Under the timeline only: the tree and the inspector beside it keep the full height.
        var bottom = Of("Workspace");
        var lanes = Of("LanesArea");
        Assert.InRange(bottom.Left, lanes.Left - 1, lanes.Left + 1);
        Assert.InRange(bottom.Right, lanes.Right - 1, lanes.Right + 1);
        Assert.True(Of("InspectorPanel").Bottom >= bottom.Bottom - 1);
        Assert.True(Of("TimelineTree").Bottom > bottom.Top);

        // On the show computer, Playing is the Buttons panel's, not a view here.
        Assert.False(workspace.OffersPlaying);
        Assert.DoesNotContain(workspace.Panes, p => p is PlayingPaneViewModel);
        workspace.AddPaneCommand.Execute(BottomPaneKind.Playing);
        Assert.DoesNotContain(workspace.Panes, p => p is PlayingPaneViewModel);

        // A view of faders makes room for them; a tab clicked while folded opens it.
        workspace.AddPaneCommand.Execute(BottomPaneKind.RawChannels);
        Assert.Equal(BottomWorkspaceViewModel.FaderHeight, workspace.PanelHeight);
        workspace.IsOpen = false;
        workspace.SelectedPane = workspace.Panes[0];
        Assert.True(workspace.IsOpen);

        // Its scrollbar reaches any channel, a whole channel at a time.
        var channels = rig.ViewModel.Lighting.Channels;
        channels.ScrollStart = 300.4;
        Assert.Equal(300m, channels.BankStart);
        channels.ScrollStart = 9999;
        Assert.Equal(channels.MaxBankStart, channels.BankStart);

        // The Buttons panel: its buttons, or what's playing.
        var buttons = rig.ViewModel.Buttons;
        buttons.IsEditing = true;
        buttons.ShowPlayingTabCommand.Execute(null);
        Assert.Equal((true, false), (buttons.IsPlayingTab, buttons.IsEditing));   // editing is for the buttons
        buttons.IsEditing = true;
        Assert.False(buttons.IsPlayingTab);
    });

    [Fact]
    public Task The_add_button_is_for_sounds_and_level_control_is_first_in_the_effects_menu() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var add = rig.Find<Button>(b => b.Command == rig.Editor.AddEventCommand);
        rig.Editor.SelectedTrack = rig.Editor.Tracks.Single(t => t.Kind == TrackKind.Audio);
        WindowRig.Pump();
        Assert.Equal("+ Sound…", add.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.True(add.IsVisible);
        rig.Editor.SelectedTrack = rig.Editor.Tracks[0];
        WindowRig.Pump();
        Assert.False(add.IsVisible);   // lighting clips come from the Effects menu

        var menu = rig.Find<ToggleButton>(t => t.Name == "EffectsButton");
        List<Border> Entries()
        {
            rig.Click(rig.CenterOf(menu));
            WindowRig.Pump(100);
            return rig.Window.GetVisualDescendants().OfType<Border>().Where(b => b.DataContext is EffectChoice && b.IsEffectivelyVisible).ToList();
        }
        var entries = Entries();
        Assert.Equal(["Level Control", "Look", "Tilt", "Home", .. EffectInfo.All.Where(i => !i.IsLook).Select(i => i.Name)], entries.Select(b => ((EffectChoice)b.DataContext!).Name));
        Assert.Equal([true, true, false, false, true], entries.Take(5).Select(b => b.IsEnabled));   // no moving heads here: no Tilt or Home
        // In two sections: the controls (Level Control to Home), then the effects.
        var sections = rig.Find<StackPanel>(p => p.Name == "EffectMenu").GetVisualDescendants().OfType<TextBlock>()
                          .Where(t => t.Classes.Contains("menuSection")).ToList();
        Assert.Equal(["CONTROLS", "EFFECTS"], sections.Select(t => t.Text));
        Assert.True(sections[1].TranslatePoint(default, rig.Window)!.Value.Y > entries[3].TranslatePoint(default, rig.Window)!.Value.Y);
        Assert.True(sections[1].TranslatePoint(default, rig.Window)!.Value.Y < entries[4].TranslatePoint(default, rig.Window)!.Value.Y);
        rig.Click(rig.CenterOf(entries[0]));   // a click adds it
        rig.Editor.Playback.Seek(S(10));
        rig.Click(rig.CenterOf(Entries()[4]));   // Lightning

        Assert.Equal(["Level Control", "Lightning"], rig.Lighting.Events.Select(e => e.Name));
        Assert.Equal([null, EffectKind.Lightning], rig.Lighting.Events.Cast<LightingClip>().Select(c => c.Effect?.Kind));
    });

    [Fact]
    public Task The_panels_follow_the_open_timeline_and_the_highlighted_track() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var main = rig.Editor.SelectedTimeline!;
        rig.Editor.Tree.NewTimelineCommand.Execute(null);
        WindowRig.Pump();
        var other = rig.Session.Project.Timelines.Single(t => t != main);
        rig.Editor.Priority = 10;
        rig.Editor.OpenTimeline(main);
        WindowRig.Pump();

        // Clicking another track's name while a track's properties are open: they're that track's.
        var headers = rig.Find<ListBox>(l => l.Name == "TrackHeaders");
        void ClickName(TrackRowViewModel row) =>
            rig.Click(headers.ContainerFromItem(row)!.TranslatePoint(new Point(20, 10), rig.Window)!.Value);
        var lighting = rig.Editor.Tracks.First(t => t.IsLighting);
        var audio = rig.Editor.Tracks.First(t => t.IsAudio);
        rig.Editor.EditTrackProperties(lighting);
        WindowRig.Pump();
        ClickName(audio);
        Assert.Same(audio, rig.Editor.SelectedTrack);
        Assert.Same(audio, rig.Editor.TrackProperties?.Row);

        // Another timeline: its highlighted track's, and the timeline properties are that timeline's and say so.
        rig.Editor.ShowTimelineTabCommand.Execute(null);
        WindowRig.Pump();
        var name = rig.Find<TextBlock>(t => t.Name == "InspectorTitle");
        rig.Editor.OpenTimeline(other);
        WindowRig.Pump();
        Assert.NotNull(rig.Editor.SelectedTrack);
        Assert.Same(rig.Editor.SelectedTrack, rig.Editor.TrackProperties?.Row);
        Assert.Contains(rig.Editor.TrackProperties!.Track, other.Tracks);
        rig.Editor.ShowTimelineTabCommand.Execute(null);
        WindowRig.Pump();
        Assert.Equal((other.Name, 10m), (name.Text, rig.Editor.Priority));
        rig.Editor.OpenTimeline(main);
        rig.Editor.ShowTimelineTabCommand.Execute(null);
        WindowRig.Pump();
        Assert.Equal((main.Name, 0m), (name.Text, rig.Editor.Priority));
        Assert.Contains(rig.Editor.TrackProperties!.Track, main.Tracks);

        // The inspector is context aware: closing shows the Timeline tab; another track highlighted shows its own, and
        // its tab and title say which.
        var shown = rig.Editor.TrackProperties.Row;
        rig.Editor.TrackProperties.CloseCommand.Execute(null);
        Assert.True(rig.Editor.IsTimelineTab);
        var lightingRow = rig.Editor.Tracks.First(t => t != shown);
        ClickName(lightingRow);
        Assert.Same(lightingRow, rig.Editor.TrackProperties?.Row);
        Assert.False(rig.Editor.IsTimelineTab);
        Assert.Equal(("Track", lightingRow.Name), (rig.Editor.SelectionTabLabel, rig.Find<TextBlock>(t => t.Name == "InspectorTitle").Text));

        // The channel picker holds choices for its own track, so another track closes it.
        rig.Editor.EditChannels(rig.Editor.Tracks.First(t => t.IsLighting));
        WindowRig.Pump();
        ClickName(rig.Editor.Tracks.First(t => t.IsAudio));
        Assert.Null(rig.Editor.ChannelPicker);
    });

    [Fact]
    public Task The_timeline_tree_folds_while_the_lanes_are_cramped_and_comes_back_when_there_s_room() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var editor = rig.Editor;
        double Lanes() => rig.Find<Border>(b => b.Name == "LanesArea").Bounds.Width;
        Assert.True(editor.IsTreeOpen);

        // The Buttons panel out at 1400: the tree folds to its strip, and the lanes get its room.
        rig.ViewModel.Buttons.IsOpen = true;
        WindowRig.Pump(300);
        Assert.False(editor.IsTreeOpen);
        Assert.True(Lanes() >= TimelineEditorViewModel.RoomyLanes - 100, $"lanes {Lanes()}");

        // Room again: it's back.
        rig.ViewModel.Buttons.IsOpen = false;
        WindowRig.Pump(300);
        Assert.True(editor.IsTreeOpen);

        // Opened by hand, it stays however cramped it gets; folded by hand, it stays folded.
        rig.ViewModel.Buttons.IsOpen = true;
        WindowRig.Pump(300);
        rig.Click(rig.CenterOf(rig.Find<Button>(b => b.Name == "TreeStrip")));
        WindowRig.Pump(300);
        Assert.True(editor.IsTreeOpen);
        rig.Window.Width = 1300;
        WindowRig.Pump(300);
        Assert.True(editor.IsTreeOpen);
        editor.ToggleTreeCommand.Execute(null);
        rig.Window.Width = 1900;
        rig.ViewModel.Buttons.IsOpen = false;
        WindowRig.Pump(300);
        Assert.False(editor.IsTreeOpen);
    });

    [Fact]
    public Task Without_a_timeline_open_a_tree_folded_for_room_comes_back_to_open_one_from() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        rig.ViewModel.Buttons.IsOpen = true;
        WindowRig.Pump(300);
        Assert.False(rig.Editor.IsTreeOpen);

        rig.Editor.SelectedTimeline = null;
        WindowRig.Pump(300);

        Assert.True(rig.Editor.IsTreeOpen);
    });

    [Fact]
    public Task The_inspector_s_tabs_say_what_sort_of_thing_its_title_which_and_its_heading_what_kind() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var title = rig.Find<TextBlock>(t => t.Name == "InspectorTitle");
        var tab = rig.Find<Button>(b => b.Name == "SelectionTab");
        string? Heading() => rig.Find<Border>(b => b.Name == "InspectorPanel").GetVisualDescendants().OfType<TextBlock>()
                                .FirstOrDefault(t => t.Classes.Contains("heading") && t.IsEffectivelyVisible)?.Text;

        // The timeline: by name.
        rig.Editor.ShowTimelineTabCommand.Execute(null);
        WindowRig.Pump();
        Assert.Equal(("Main", "Timeline"), (title.Text, Heading()));

        // An unnamed look: a clip, called what it is.
        var look = rig.Editor.AddEffectAt(rig.TrackRow("Lighting").Track, S(1), EffectKind.Look)!;
        rig.Editor.SelectEvent(look.Id, additive: false);
        WindowRig.Pump();
        Assert.Equal(("Clip", "Look", "Look"), (tab.Content as string, title.Text, Heading()));

        // Named: the title follows the name; the heading still says what it is.
        rig.Editor.Properties!.Name = "Red wash";
        WindowRig.Pump();
        Assert.Equal(("Red wash", "Look"), (title.Text, Heading()));

        // A track: by its name, a lighting track.
        rig.Editor.EditTrackProperties(rig.Editor.Tracks[0]);
        WindowRig.Pump();
        Assert.Equal(("Track", "Lighting", "Lighting track"), (tab.Content as string, title.Text, Heading()));
    });

    [Fact]
    public Task A_timeline_ends_with_its_last_clip_until_it_s_given_a_length_of_its_own() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var editor = rig.Editor;
        editor.ShowTimelineTabCommand.Execute(null);
        WindowRig.Pump();
        var box = rig.Find<NumericUpDown>(n => n.Name == "TimelineLength");
        var ends = rig.Find<CheckBox>(c => c.Name == "EndsWithLastClip");
        var timeline = editor.SelectedTimeline!;

        // Ending with its last clip: the box shows where that is (not 0), and follows the clips.
        var clip = editor.AddEffectAt(rig.TrackRow("Lighting").Track, S(2), EffectKind.Lightning)!;   // 4 s: ends at 6
        WindowRig.Pump();
        Assert.Equal((true, false, 6m), (ends.IsChecked, box.IsEnabled, box.Value));
        editor.AddEffectAt(rig.TrackRow("Lighting").Track, S(10), EffectKind.Lightning);
        WindowRig.Pump();
        Assert.Equal(14m, box.Value);
        Assert.Null(timeline.Length);

        // Unticked: a length of its own, from where it ended, to change from there.
        editor.ShowTimelineTabCommand.Execute(null);   // adding a clip selected it
        WindowRig.Pump();
        rig.Click(rig.CenterOf(ends));
        WindowRig.Pump();
        Assert.Equal((false, true, S(14)), (ends.IsChecked, box.IsEnabled, timeline.Length));
        editor.LengthSeconds = 20;
        Assert.Equal(S(20), timeline.Length);

        // Ticked again: back to its clips; and Undo takes each step back.
        rig.Click(rig.CenterOf(ends));
        WindowRig.Pump();
        Assert.Null(timeline.Length);
        Assert.Equal(14m, box.Value);
        editor.UndoCommand.Execute(null);
        Assert.Equal(S(20), timeline.Length);
    });
}
