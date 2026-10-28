using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using GhostShow.App.ViewModels;
using GhostShow.Core.Timeline;
using Page = GhostShow.App.ViewModels.Page;

namespace GhostShow.App.Tests;

/// <summary>
/// The real window driven by mouse and keyboard on the headless platform: the interactions view-model tests can't
/// see (hit testing, drags, focus, shortcuts, context menus).
/// </summary>
public class UiTests
{
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

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

        var summary = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "1 channel");   // not the name box (it has its own menu)
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
    public Task Right_clicking_an_empty_part_of_a_lighting_row_adds_an_effect_there() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var row = rig.TrackRow("Lighting");

        rig.Click(rig.At(row, 6), MouseButton.Right);
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
        rig.Click(rig.CenterOf(rig.Find<Button>(b => b.Content as string == "＋ New fixture type…")));
        var mode = setup.ProfileEditor!.SelectedMode!;
        Button Quick(string label) => rig.Find<Button>(b => b.Content as string == $"＋ {label}" && b.IsEffectivelyVisible);

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

        var effects = rig.Find<Button>(b => b.Flyout is MenuFlyout f && f.Items.OfType<MenuItem>().Any(i => i.Header as string == "Level Control"));
        var flyout = (MenuFlyout)effects.Flyout!;
        rig.Click(rig.CenterOf(effects));   // open it: its items' commands bind once shown
        Assert.True(flyout.IsOpen);
        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Equal("Level Control", items[0].Header);
        Assert.Equal(EffectInfo.All.Select(i => i.Name), items.Skip(1).Select(i => (string)i.Header!));
        items[0].Command!.Execute(items[0].CommandParameter);
        rig.Editor.Playback.Seek(S(10));
        items[1].Command!.Execute(items[1].CommandParameter);

        flyout.Hide();

        Assert.Equal(["Level Control", "Lightning"], rig.Lighting.Events.Select(e => e.Name));
        Assert.Equal([null, EffectKind.Lightning], rig.Lighting.Events.Cast<LightingClip>().Select(c => c.Effect?.Kind));
    });
}
