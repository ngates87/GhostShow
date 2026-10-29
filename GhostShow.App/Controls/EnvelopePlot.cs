using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using GhostShow.App.ViewModels;
using GhostShow.Core.Timeline;

namespace GhostShow.App.Controls;

/// <summary>
/// The Envelope Editor's plot: one lighting clip's envelopes, large. Time runs left to right (the ruler shows timeline
/// time), level bottom (0%) to top (100%). The ticked channels' envelopes are drawn in their colours with their
/// handles; outside the clip, the levels the channels play before and after it are drawn dimmed, for context. The
/// playhead shows where it is in the clip.
///
/// The active channel is drawn on top and thicker; the others a little dimmed.
///
/// Mouse: click a handle to select it (Ctrl adds or removes); drag to move the selected handles (Shift keeps to the
/// main direction; the window's locks keep time or level); drag on empty space to box-select. Double-click a curve
/// to add a handle on it; double-click anywhere else in the clip to add one to the active channel, right there.
/// The first clip's start handle, when the channel starts there, sets its start level. Right-click a handle for its
/// curve type and Delete; right-click elsewhere to add a handle. Ctrl+wheel zooms around the pointer; the wheel pans.
/// </summary>
public sealed class EnvelopePlot : Control
{
    const double AxisWidth = 46, RulerHeight = 22, TopPad = 10, BottomPad = 12, RightPad = 14;
    const double HandleSize = 4.5, HandleGrab = 8, CurveGrab = 10, DragThreshold = 3;

    static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1A1A1A"));
    static readonly IBrush ClipArea = new SolidColorBrush(Color.Parse("#24211B"));
    static readonly IBrush OutsideArea = new SolidColorBrush(Color.Parse("#161616"));
    static readonly IBrush RulerFill = new SolidColorBrush(Color.Parse("#2B2B2B"));
    static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
    static readonly IBrush HintBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);
    static readonly IPen MajorGridPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), 1);
    static readonly IPen ClipEdgePen = new Pen(new SolidColorBrush(Color.Parse("#B8862B")), 1.5);
    static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 1.5);
    static readonly IPen HandleOutline = new Pen(Brushes.Black, 1);
    static readonly IPen SelectedPen = new Pen(Brushes.Gold, 2);
    static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(40, 0x4D, 0xD0, 0xE1));
    static readonly IPen BoxPen = new Pen(new SolidColorBrush(Color.Parse("#4DD0E1")), 1);
    static readonly Typeface Font = new("Inter");
    static readonly double[] TimeSteps = [0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];

    public static readonly StyledProperty<EnvelopeEditorViewModel?> EnvelopesProperty =
        AvaloniaProperty.Register<EnvelopePlot, EnvelopeEditorViewModel?>(nameof(Envelopes));

    public EnvelopeEditorViewModel? Envelopes
    {
        get => GetValue(EnvelopesProperty);
        set => SetValue(EnvelopesProperty, value);
    }

    /// <summary>The right-click menu opened last (tests pick its items through this).</summary>
    public ContextMenu? LastMenu { get; private set; }

    enum DragKind { None, PendingPoints, MovePoints, PendingBox, Box, StartLevel }
    DragKind _drag;
    Point _dragStart, _boxEnd;
    bool _additive;
    CurvePoint? _anchor;
    TimeSpan _deltaTime;
    int _deltaValue;
    IReadOnlyDictionary<CurvePoint, (TimeSpan Time, byte Value)> _preview = new Dictionary<CurvePoint, (TimeSpan, byte)>();
    ChannelLane? _startChannel;
    int _startDrags;

    public EnvelopePlot()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // ---- following the view-models ----

    TimelineEditorViewModel? _editor;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EnvelopesProperty) return;
        if (change.OldValue is EnvelopeEditorViewModel old)
        {
            old.PropertyChanged -= OnChanged;
            old.Editor.PropertyChanged -= OnChanged;
            old.Editor.Playback.PropertyChanged -= OnPlaybackChanged;
        }
        _editor = (change.NewValue as EnvelopeEditorViewModel)?.Editor;
        if (change.NewValue is EnvelopeEditorViewModel envelopes)
        {
            envelopes.PropertyChanged += OnChanged;
            envelopes.Editor.PropertyChanged += OnChanged;
            envelopes.Editor.Playback.PropertyChanged += OnPlaybackChanged;
        }
        InvalidateVisual();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EnvelopeEditorViewModel.Revision) or nameof(EnvelopeEditorViewModel.Clip)
            or nameof(TimelineEditorViewModel.Revision) or nameof(TimelineEditorViewModel.SelectedPoints))
            InvalidateVisual();
    }

    void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.Position)) InvalidateVisual();
    }

    // ---- geometry ----

    Rect Plot => new(AxisWidth, RulerHeight + TopPad, Math.Max(1, Bounds.Width - AxisWidth - RightPad), Math.Max(1, Bounds.Height - RulerHeight - TopPad - BottomPad));

    /// <summary>Where a time (relative to the clip's start) is drawn, and back.</summary>
    double X(TimeSpan relative)
    {
        var view = Envelopes!;
        return Plot.X + (double)(relative - view.ViewStart).Ticks / view.ViewLength.Ticks * Plot.Width;
    }

    TimeSpan TimeAt(double x)
    {
        var view = Envelopes!;
        return view.ViewStart + TimeSpan.FromTicks((long)((x - Plot.X) / Plot.Width * view.ViewLength.Ticks));
    }

    double Y(double level) => Plot.Y + (1 - level / 255) * Plot.Height;

    byte LevelAt(double y) => (byte)Math.Clamp(Math.Round((1 - (y - Plot.Y) / Plot.Height) * 255), 0, 255);

    /// <summary>Where a handle (relative time and level) is on screen; public for tests.</summary>
    public Point PointOf(TimeSpan relative, double level) => new(X(relative), Y(level));

    // ---- drawing ----

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (Envelopes is not { Clip: { } clip } view || _editor is not { } editor)
        {
            DrawText(context, "Double-click a Level Control clip on the timeline to edit its envelopes here.", new Point(AxisWidth, RulerHeight + 20), 600, HintBrush, 13);
            return;
        }
        var plot = Plot;
        context.FillRectangle(OutsideArea, plot);
        double clipLeft = Math.Max(plot.Left, X(TimeSpan.Zero)), clipRight = Math.Min(plot.Right, X(clip.Length));
        if (clipRight > clipLeft) context.FillRectangle(ClipArea, new Rect(clipLeft, plot.Top, clipRight - clipLeft, plot.Height));

        // Levels: a line every 25%, labelled.
        for (int percent = 0; percent <= 100; percent += 25)
        {
            double y = Math.Round(Y(percent * 2.55)) + 0.5;
            context.DrawLine(percent % 50 == 0 ? MajorGridPen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, $"{percent}%", new Point(4, y - 8), AxisWidth - 8, LabelBrush, 11);
        }

        // Time: grid and ruler in timeline time.
        context.FillRectangle(RulerFill, new Rect(0, 0, Bounds.Width, RulerHeight));
        double step = TimeSteps.FirstOrDefault(s => s / view.ViewLength.TotalSeconds * plot.Width >= 80, TimeSteps[^1]);
        double first = Math.Ceiling((clip.Start + view.ViewStart).TotalSeconds / step) * step;
        for (double t = first; ; t += step)
        {
            double x = X(TimeSpan.FromSeconds(t) - clip.Start);
            if (x > plot.Right) break;
            x = Math.Round(x) + 0.5;
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(MajorGridPen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            DrawText(context, TimeLabel(TimeSpan.FromSeconds(t), step), new Point(x + 3, 3), 90, LabelBrush, 11);
        }
        foreach (var edge in new[] { TimeSpan.Zero, clip.Length })
        {
            double x = X(edge);
            if (x >= plot.Left && x <= plot.Right) context.DrawLine(ClipEdgePen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        List<(double X, Color Colour)> chips;
        using (context.PushClip(plot.Inflate(new Thickness(HandleSize + 2, HandleSize + 2, HandleSize + 2, HandleSize + 2))))
        {
            chips = DrawGroups(context, editor, view, clip, plot);
            // The active channel last, so it's on top.
            foreach (var levels in view.ShownLevels.OrderBy(l => l.Channel.Target == view.ActiveTarget))
                DrawChannel(context, editor, view, clip, levels, plot, active: levels.Channel.Target == view.ActiveTarget);
        }

        // Colour handles' colours, in the ruler above them (clear of handles at 100%).
        foreach (var (x, colour) in chips) context.DrawRectangle(new SolidColorBrush(colour), HandleOutline, new Rect(x - 6, RulerHeight - 12, 12, 10), 2);

        var playhead = editor.Playback.Position - clip.Start;
        double px = X(playhead);
        if (px >= plot.Left && px <= plot.Right) context.DrawLine(PlayheadPen, new Point(px, RulerHeight), new Point(px, plot.Bottom));
        if (_drag == DragKind.Box) context.DrawRectangle(BoxFill, BoxPen, new Rect(_dragStart, _boxEnd).Normalize());
    }

    static readonly IPen GroupPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1, new DashStyle([3, 3], 0));

    /// <summary>
    /// Grouped handles (colour, position, snapshot): a dashed line joining them. Returns where colour handles are and
    /// their colours, for chips in the ruler.
    /// </summary>
    List<(double X, Color Colour)> DrawGroups(DrawingContext context, TimelineEditorViewModel editor, EnvelopeEditorViewModel view, LightingClip clip, Rect plot)
    {
        var chips = new List<(double, Color)>();
        var shown = clip.Lanes.Where(l => view.IsShown(l.Target)).ToList();
        foreach (var group in shown.SelectMany(l => l.Points).Where(p => p.Group is not null).GroupBy(p => p.Group))
        {
            var points = group.ToList();
            var (time, _) = _preview.TryGetValue(points[0], out var moved) ? moved : (points[0].Time, points[0].Value);
            double x = X(time);
            if (x < plot.Left - 6 || x > plot.Right + 6) continue;
            var ys = points.Select(p => Y(_preview.TryGetValue(p, out var m) ? m.Value : p.Value)).ToList();
            context.DrawLine(GroupPen, new Point(x, Math.Min(plot.Top + 14, ys.Min())), new Point(x, ys.Max()));
            var all = editor.GroupOf(points[0]);
            if (all.Count > 1 && new HandleGroupViewModel(all, group.Key!.Value, editor) is { IsColour: true } colour) chips.Add((x, colour.Colour));
        }
        return chips;
    }

    void DrawChannel(DrawingContext context, TimelineEditorViewModel editor, EnvelopeEditorViewModel view, LightingClip clip, TrackLevels levels, Rect plot, bool active)
    {
        var color = Color.Parse(TimelineEditorViewModel.LaneColor(levels.Channel));
        var fill = new SolidColorBrush(color, active ? 1 : 0.7);

        // What plays either side of the clip, dimmed: a sample every few pixels.
        var dim = new Pen(new SolidColorBrush(color, 0.35), 1.5);
        DrawPlayed(context, levels, clip, view.ViewStart, TimeSpan.Zero, dim);
        DrawPlayed(context, levels, clip, clip.Length, view.ViewStart + view.ViewLength, dim);

        var found = levels.Find(clip);
        var lane = found is null ? clip.LaneFor(levels.Channel.Target) : found.Lane;
        var envelope = found?.Handles ?? clip.EnvelopeFor(levels.Channel.Target, levels.StartLevelOf(clip));
        if (_preview.Count > 0 && lane is not null && lane.Points.Any(_preview.ContainsKey))
            envelope = envelope.Select(p => _preview.TryGetValue(p, out var to) ? new CurvePoint { Time = to.Time, Value = to.Value, Curve = p.Curve } : p).OrderBy(p => p.Time).ToList();

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(X(envelope[0].Time), Y(envelope[0].Value)), false);
            for (int i = 1; i < envelope.Count; i++)
            {
                var (a, b) = (envelope[i - 1], envelope[i]);
                if (b.Curve == CurveKind.Step || b.Time == a.Time)
                {
                    g.LineTo(new Point(X(b.Time), Y(a.Value)));
                    g.LineTo(new Point(X(b.Time), Y(b.Value)));
                }
                else if (b.Curve == CurveKind.Linear) g.LineTo(new Point(X(b.Time), Y(b.Value)));
                else
                {
                    int pieces = (int)Math.Clamp((X(b.Time) - X(a.Time)) / 3, 2, 200);
                    foreach (var (t, v) in Envelope.SampleSegment(envelope, i, pieces).Skip(1)) g.LineTo(new Point(X(t), Y(v)));
                }
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(fill, active ? 3 : 1.5), geometry);

        // The start handle: a ring (where the previous clip left the channel), or a dot that sets the start level.
        bool ownStart = editor.FirstClipOf(levels) == clip;
        context.DrawEllipse(ownStart ? fill : null, new Pen(fill, 1.5), new Point(X(TimeSpan.Zero), Y(envelope[0].Value)), 4, 4);

        if (lane is null) return;
        foreach (var point in lane.Points)
        {
            var (time, value) = _preview.TryGetValue(point, out var moved) ? moved : (point.Time, point.Value);
            var center = new Point(X(time), Y(value));
            bool selected = editor.SelectedPoints.Contains(point);
            double r = selected ? HandleSize + 1.5 : HandleSize;
            IBrush brush = selected ? Brushes.White : fill;
            IPen outline = selected ? SelectedPen : HandleOutline;
            if (point == lane.Points[^1])
            {
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(center.X, center.Y - r - 1), true);
                    g.LineTo(new Point(center.X + r + 1, center.Y));
                    g.LineTo(new Point(center.X, center.Y + r + 1));
                    g.LineTo(new Point(center.X - r - 1, center.Y));
                    g.EndFigure(true);
                }
                context.DrawGeometry(brush, outline, diamond);
            }
            else context.DrawRectangle(brush, outline, new Rect(center.X - r, center.Y - r, r * 2, r * 2));
        }
    }

    /// <summary>The level a channel plays between two times (relative to the clip), as a line; gaps where the timeline leaves it alone.</summary>
    void DrawPlayed(DrawingContext context, TrackLevels levels, LightingClip clip, TimeSpan from, TimeSpan to, IPen pen)
    {
        if (to <= from) return;
        double x0 = Math.Max(Plot.Left, X(from)), x1 = Math.Min(Plot.Right, X(to));
        Point? last = null;
        for (double x = x0; x <= x1 + 0.1; x += 3)
        {
            var at = TimeAt(Math.Min(x, x1));
            // Inside the clip's own span, the clip is drawn; outside, the other clips (or the hold) play.
            byte? level = levels.ValueAt(clip.Start + at);
            if (level is null) { last = null; continue; }
            var p = new Point(Math.Min(x, x1), Y(level.Value));
            if (last is { } l) context.DrawLine(pen, l, p);
            last = p;
        }
    }

    static string TimeLabel(TimeSpan time, double step) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(step < 1 ? @"m\:ss\.ff" : @"m\:ss");

    static void DrawText(DrawingContext context, string text, Point origin, double room, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush)
        {
            MaxTextWidth = Math.Max(1, room), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(formatted, origin);
    }

    // ---- hit testing ----

    /// <summary>The nearest handle within grabbing distance: a handle, or a channel's own start level (first clip).</summary>
    (CurvePoint? Point, ChannelLane? StartOf) HitHandle(Point at)
    {
        if (Envelopes is not { Clip: { } clip } view || _editor is not { } editor) return (null, null);
        (CurvePoint?, ChannelLane?) best = (null, null);
        double bestDistance = HandleGrab;
        void Consider(Point p, CurvePoint? point, ChannelLane? startOf)
        {
            double d = Math.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y));
            if (d <= bestDistance) (best, bestDistance) = ((point, startOf), d);
        }
        foreach (var levels in view.ShownLevels)
        {
            if (editor.FirstClipOf(levels) == clip) Consider(PointOf(TimeSpan.Zero, levels.Channel.StartLevel), null, levels.Channel);
            foreach (var p in clip.LaneFor(levels.Channel.Target)?.Points ?? []) Consider(PointOf(p.Time, p.Value), p, null);
        }
        return best;
    }

    /// <summary>The shown channel whose curve passes nearest a point (within a few pixels, inside the clip), or null.</summary>
    TrackLevels? HitCurve(Point at)
    {
        if (Envelopes is not { Clip: { } clip } view) return null;
        var time = TimeAt(at.X);
        if (time <= TimeSpan.Zero || time >= clip.Length) return null;
        TrackLevels? best = null;
        double bestDistance = CurveGrab;
        foreach (var levels in view.ShownLevels)
        {
            var envelope = levels.Find(clip)?.Handles ?? clip.EnvelopeFor(levels.Channel.Target, levels.StartLevelOf(clip));
            double d = Math.Abs(Y(Envelope.ValueAt(envelope, time)) - at.Y);
            if (d <= bestDistance) (best, bestDistance) = (levels, d);
        }
        return best;
    }

    // ---- mouse ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Envelopes is not { Clip: { } clip } view || _editor is not { } editor) return;
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        Focus();
        e.Handled = true;

        var handle = HitHandle(point);
        if (properties.IsRightButtonPressed)
        {
            if (handle.Point is { } rightPoint)
            {
                if (!editor.SelectedPoints.Contains(rightPoint)) editor.SelectPoint(rightPoint, additive: false);
                ShowHandleMenu(editor);
            }
            else ShowAddMenu(view, TimeAt(point.X), LevelAt(point.Y), clip);
            return;
        }
        if (!properties.IsLeftButtonPressed) return;

        if (handle.StartOf is { } startOf && !additive)
        {
            _drag = DragKind.StartLevel;
            _startChannel = startOf;
            _startDrags++;
            e.Pointer.Capture(this);
            return;
        }
        if (handle.Point is { } hit)
        {
            if (additive)
            {
                editor.SelectPoint(hit, additive: true);
                return;
            }
            if (!editor.SelectedPoints.Contains(hit)) editor.SelectPoint(hit, additive: false);
            (_dragStart, _anchor, _deltaTime, _deltaValue, _drag) = (point, hit, TimeSpan.Zero, 0, DragKind.PendingPoints);
            e.Pointer.Capture(this);
            return;
        }
        if (e.ClickCount == 2)
        {
            // On a curve: a handle there, on its level. Anywhere else: on the active channel, where clicked.
            var time = TimeAt(point.X);
            if (HitCurve(point) is { } curve) view.AddHandle(curve.Channel.Target, time);
            else if (view.ActiveLevels is { } activeLevels) view.AddHandle(activeLevels.Channel.Target, time, LevelAt(point.Y));
            return;
        }
        if (!additive) editor.SelectPoints([], additive: false);
        (_additive, _dragStart, _boxEnd, _drag) = (additive, point, point, DragKind.PendingBox);
        e.Pointer.Capture(this);
    }

    void ShowHandleMenu(TimelineEditorViewModel editor)
    {
        var points = editor.SelectedPoints.ToList();
        var items = new List<object>();
        foreach (var (curve, label) in new[] { (CurveKind.Step, "Step"), (CurveKind.Linear, "Linear"), (CurveKind.Spline, "Spline") })
        {
            var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = points.All(p => p.Curve == curve) };
            item.Click += (_, _) => editor.SetCurve(editor.SelectedPoints, curve);
            items.Add(item);
        }
        items.Add(new Separator());
        bool deletable = points.Any(p => editor.ClipOf(p) is { } clip && !clip.IsEndHandle(p));
        var delete = new MenuItem { Header = points.Count > 1 ? $"Delete {points.Count} handles" : "Delete handle", IsEnabled = deletable, InputGesture = new KeyGesture(Key.Delete) };
        delete.Click += (_, _) => editor.DeleteCommand.Execute(null);
        items.Add(delete);
        Open(new ContextMenu { ItemsSource = items });
    }

    /// <summary>
    /// Right-click elsewhere: add a handle to the active channel right there (the same as a double-click), or to
    /// another channel's curve at that time.
    /// </summary>
    void ShowAddMenu(EnvelopeEditorViewModel view, TimeSpan time, byte level, LightingClip clip)
    {
        bool inside = !clip.Locked && time > TimeSpan.Zero && time < clip.Length;
        var items = new List<object>();
        if (view.ActiveLevels is { } active)
        {
            var here = new MenuItem
            {
                Header = $"Add {view.Editor.LaneLabel(active.Channel.Target)} handle here", IsEnabled = inside,
                Icon = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.Parse(TimelineEditorViewModel.LaneColor(active.Channel))) },
            };
            here.Click += (_, _) => view.AddHandle(active.Channel.Target, time, level);
            items.Add(here);
        }
        // Colour and position handles for each fixture that has the channels for one.
        foreach (var (fixtureId, name) in view.GroupFixtures)
            foreach (var (kind, label) in new[] { (HandleGroupKind.Colour, "colour"), (HandleGroupKind.Position, "position") })
            {
                if (view.Track is not { } track || view.Editor.GroupTargets(track, fixtureId, kind).Count < 2) continue;
                var item = new MenuItem { Header = $"Add {name} {label} handle here", IsEnabled = inside };
                item.Click += (_, _) => view.AddGroupHandle(fixtureId, kind, time);
                items.Add(item);
            }
        var others = view.ShownLevels.Where(l => l.Channel.Target != view.ActiveTarget).ToList();
        if (others.Count == 0)
        {
            Open(new ContextMenu { ItemsSource = items });
            return;
        }
        var add = new MenuItem { Header = "Add handle on another channel", IsEnabled = inside };
        items.Add(add);
        add.ItemsSource = others.Select(levels =>
        {
            var item = new MenuItem
            {
                Header = view.Editor.LaneLabel(levels.Channel.Target),
                Icon = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.Parse(TimelineEditorViewModel.LaneColor(levels.Channel))) },
            };
            item.Click += (_, _) => view.AddHandle(levels.Channel.Target, time);
            return item;
        }).ToList();
        Open(new ContextMenu { ItemsSource = items });
    }

    void Open(ContextMenu menu)
    {
        LastMenu = menu;
        menu.Open(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Envelopes is not { } view || _editor is not { } editor) return;
        var point = e.GetPosition(this);
        switch (_drag)
        {
            case DragKind.None:
                var handle = HitHandle(point);
                Cursor = handle.StartOf is not null ? new Cursor(StandardCursorType.SizeNorthSouth)
                       : handle.Point is not null ? new Cursor(StandardCursorType.Hand)
                       : Cursor.Default;
                return;
            case DragKind.StartLevel when _startChannel is not null:
                editor.SetStartLevel(_startChannel, LevelAt(point.Y), field: $"envelope drag {_startDrags}");
                return;
            case DragKind.PendingPoints or DragKind.PendingBox when Math.Abs(point.X - _dragStart.X) < DragThreshold && Math.Abs(point.Y - _dragStart.Y) < DragThreshold:
                return;
            case DragKind.PendingBox or DragKind.Box:
                _drag = DragKind.Box;
                _boxEnd = point;
                InvalidateVisual();
                return;
            case DragKind.PendingPoints or DragKind.MovePoints:
                _drag = DragKind.MovePoints;
                double dx = point.X - _dragStart.X, dy = point.Y - _dragStart.Y;
                bool constrain = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                bool keepTime = view.LockTime || (constrain && Math.Abs(dy) > Math.Abs(dx));
                bool keepLevel = view.LockLevel || (constrain && Math.Abs(dx) >= Math.Abs(dy));
                _deltaTime = keepTime ? TimeSpan.Zero : TimeSpan.FromTicks((long)(dx / Plot.Width * view.ViewLength.Ticks));
                _deltaValue = keepLevel ? 0 : (int)Math.Round(-dy / Plot.Height * 255);
                _preview = editor.PreviewPointMove(_deltaTime, _deltaValue, _anchor);
                InvalidateVisual();
                return;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Envelopes is not { Clip: { } clip } view || _editor is not { } editor) return;
        var drag = _drag;
        EndDrag();
        e.Pointer.Capture(null);
        switch (drag)
        {
            case DragKind.MovePoints:
                editor.MoveSelectedPoints(_deltaTime, _deltaValue, _anchor);
                break;
            case DragKind.Box:
                var box = new Rect(_dragStart, _boxEnd).Normalize();
                var inside = view.ShownLevels.SelectMany(l => clip.LaneFor(l.Channel.Target)?.Points ?? []).Where(p => box.Contains(PointOf(p.Time, p.Value))).ToList();
                editor.SelectPoints(inside, _additive);
                break;
        }
        InvalidateVisual();
    }

    void EndDrag()
    {
        _drag = DragKind.None;
        _preview = new Dictionary<CurvePoint, (TimeSpan, byte)>();
        _startChannel = null;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is DragKind.None) return;
        EndDrag();
        InvalidateVisual();
    }

    /// <summary>Ctrl+wheel zooms around the pointer; the wheel (either direction) pans.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Envelopes is not { Clip: not null } view) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y != 0) view.ZoomAround(TimeAt(e.GetPosition(this).X), e.Delta.Y > 0 ? 1 / 1.25 : 1.25);
        }
        else
        {
            double amount = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            view.Pan(-view.ViewLength * 0.1 * amount);
        }
        e.Handled = true;
    }
}
