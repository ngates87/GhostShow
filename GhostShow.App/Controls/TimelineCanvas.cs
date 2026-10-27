using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GhostShow.App.ViewModels;
using GhostShow.Core.Timeline;

namespace GhostShow.App.Controls;

/// <summary>
/// The timeline's ruler and rows (laid out by <see cref="TimelineEditorViewModel.Rows"/>, the same rows as the track
/// headers):
/// - Lighting track: its blocks, each showing its channels' lines in miniature. Expanded, each channel also gets its
///   own row with its line through every block (blocks shaded) and its points, where points are edited.
/// - Audio: a block over the time it plays, with its fade-in and fade-out drawn as a volume outline; dashed when the
///   file's length isn't known yet.
/// - Marker: a flag with its name.
/// Selected events and points are gold, things with problems dashed red, disabled events and muted tracks dimmed.
///
/// Mouse, blocks and events: click selects (Ctrl adds or removes), drag moves (snapped; one edit on release); drag a
/// block's edges: a lighting block stretches, an audio block's right edge sets its length and its left edge trims its
/// start; double-click an empty part of a row to add an event (lighting: a block), or a lighting block to expand.
/// Mouse, points (expanded channel rows): double-click adds a point; click selects (Ctrl adds); drag moves the
/// selected points in time (snapped, within their block) and level; right-click for fade/step and delete. Clicking or
/// dragging on the ruler moves the playhead.
///
/// Drawn directly (only the part scrolled into view) rather than built from controls, so long shows stay cheap.
/// </summary>
public sealed class TimelineCanvas : Control
{
    public const double RulerHeight = 26;
    const double EdgeGrab = 6, MinBlockWidth = 6, DragThreshold = 3, PointRadius = 4.5, PointGrab = 7, LinePad = 6;
    static readonly TimeSpan TailRoom = TimeSpan.FromSeconds(10);
    static readonly double[] RulerSteps = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];

    static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
    static readonly IBrush LaneAlt = new SolidColorBrush(Color.Parse("#242424"));
    static readonly IBrush ChannelRowFill = new SolidColorBrush(Color.Parse("#181A1D"));
    static readonly IBrush SelectedLane = new SolidColorBrush(Color.FromArgb(28, 255, 215, 0));
    static readonly IBrush RulerFill = new SolidColorBrush(Color.Parse("#2B2B2B"));
    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
    static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1);
    static readonly IPen LanePen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    static readonly IPen ChannelRowPen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
    static readonly IBrush RulerText = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
    static readonly IBrush HintText = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
    static readonly IBrush EndFill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
    static readonly IPen EndPen = new Pen(new SolidColorBrush(Color.Parse("#E57373")), 1, new DashStyle([4, 3], 0));

    static readonly Color AudioColor = Color.Parse("#2F84C0"), MarkerColor = Color.Parse("#9A5BD0"), LightingColor = Color.Parse("#B8862B");
    static readonly IBrush ClipSpanFill = new SolidColorBrush(Color.FromArgb(34, 0xB8, 0x86, 0x2B));
    static readonly IPen SelectedPen = new Pen(Brushes.Gold, 2);
    static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 2, new DashStyle([4, 3], 0));
    static readonly IPen RampPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5);
    static readonly IPen UnknownLengthPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1, new DashStyle([3, 3], 0));
    static readonly IPen PointOutline = new Pen(Brushes.Black, 1);
    static readonly Typeface Font = new("Inter");

    public static readonly StyledProperty<TimelineEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<TimelineCanvas, TimelineEditorViewModel?>(nameof(Editor));

    public TimelineEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    ScrollViewer? _scroller;

    // Drag state. Moves and resizes are previewed by drawing with an offset; the edit is made once, on release.
    enum DragKind { None, Pending, Move, Resize, ResizeStart, Scrub, PendingPoints, MovePoints }
    enum Edge { None, Left, Right }
    DragKind _drag;
    Point _dragStart;
    Guid _dragEvent;
    TimeSpan _dragOriginalLength;
    TimeSpan _dragDelta;
    TimeSpan _resizeLength;
    TimeSpan _resizeStart;
    TimeSpan _dragOriginalStart;
    double _dragRowHeight;
    int _dragValueDelta;

    public TimelineCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (change.OldValue is TimelineEditorViewModel old) old.PropertyChanged -= OnEditorChanged;
        if (change.NewValue is TimelineEditorViewModel editor) editor.PropertyChanged += OnEditorChanged;
        InvalidateMeasure();
        InvalidateVisual();
    }

    void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TimelineEditorViewModel.Revision):
            case nameof(TimelineEditorViewModel.PixelsPerSecond):
            case nameof(TimelineEditorViewModel.SelectedTimeline):
            case nameof(TimelineEditorViewModel.Rows):
                InvalidateMeasure();
                InvalidateVisual();
                break;
            case nameof(TimelineEditorViewModel.Selection):
            case nameof(TimelineEditorViewModel.SelectedPoints):
            case nameof(TimelineEditorViewModel.ErrorEvents):
            case nameof(TimelineEditorViewModel.SelectedTrack):
                InvalidateVisual();
                break;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null) _scroller.ScrollChanged += OnScrolled;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null) _scroller.ScrollChanged -= OnScrolled;
        _scroller = null;
    }

    void OnScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    double Pps => Editor?.PixelsPerSecond ?? 60;

    double X(TimeSpan time) => time.TotalSeconds * Pps;

    TimeSpan TimeAt(double x) => TimeSpan.FromSeconds(Math.Max(0, x) / Pps);

    /// <summary>Where a level (0-255) is drawn in a row, and back.</summary>
    static double Y(TimelineRow row, double value) => RulerHeight + row.Top + LinePad + (1 - value / 255) * (row.Height - 2 * LinePad);

    static byte ValueAt(TimelineRow row, double y) =>
        (byte)Math.Clamp(Math.Round((1 - (y - RulerHeight - row.Top - LinePad) / (row.Height - 2 * LinePad)) * 255), 0, 255);

    /// <summary>Wide enough for every event and point plus some room to add more; at least the visible width.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Editor is not { } editor) return default;
        var end = ContentEnd(editor) + TailRoom;
        double width = Math.Max(X(end), double.IsFinite(availableSize.Width) ? availableSize.Width : 0);
        return new Size(width, RulerHeight + editor.ContentHeight + 1);
    }

    static TimeSpan ContentEnd(TimelineEditorViewModel editor)
    {
        var end = editor.SelectedTimeline?.Length ?? TimeSpan.Zero;
        foreach (var row in editor.Tracks)
        {
            foreach (var e in row.Track.Events)
            {
                var eventEnd = e.Start + editor.LengthOf(e);
                if (eventEnd > end) end = eventEnd;
            }
            foreach (var lane in row.Track.Channels)
                if (lane.Points.Count > 0 && lane.Points[^1].Time > end) end = lane.Points[^1].Time;
        }
        return end;
    }

    // ---- drawing ----

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        context.FillRectangle(Background, new Rect(bounds.Size));
        if (Editor is not { } editor) return;

        // Only draw what's scrolled into view (plus a margin for labels that start just off-screen).
        double left = (_scroller?.Offset.X ?? 0) - 200;
        double right = (_scroller?.Offset.X ?? 0) + (_scroller?.Viewport.Width ?? bounds.Width) + 50;
        var step = RulerStep();

        foreach (var row in editor.Rows)
        {
            var rect = RowRect(row, bounds.Width);
            if (row.Lane is not null) context.FillRectangle(ChannelRowFill, rect);
            else if (row.Track == editor.SelectedTrack) context.FillRectangle(SelectedLane, rect);
            else if (row.TrackIndex % 2 == 1) context.FillRectangle(LaneAlt, rect);
            context.DrawLine(row.Lane is null ? LanePen : ChannelRowPen, rect.BottomLeft, rect.BottomRight);
        }
        DrawGrid(context, step, left, right, bounds.Height);

        if (editor.SelectedTimeline?.Length is { } length)
        {
            // Past an explicit length nothing plays: shade it.
            double x = X(length);
            context.FillRectangle(EndFill, new Rect(x, RulerHeight, Math.Max(0, bounds.Width - x), bounds.Height - RulerHeight));
            context.DrawLine(EndPen, new Point(x, RulerHeight), new Point(x, bounds.Height));
        }

        foreach (var row in editor.Rows)
        {
            var track = row.Track.Track;
            using (context.PushOpacity(track.Enabled ? 1 : 0.35))
            {
                if (row.Lane is { } lane) DrawChannelRow(context, editor, row, lane, left, right);
                else if (row.Track.IsLighting && row.Track.Track.Channels.Count == 0)
                    DrawText(context, "Right-click the track name → Channels… to choose what this track controls", new Point(Math.Max(left, 0) + 8, RulerHeight + row.Top + 15), 600, HintText, 12);
                else DrawEvents(context, editor, row, left, right);
            }
        }

        DrawRuler(context, step, left, right, bounds.Width);
    }

    static Rect RowRect(TimelineRow row, double width) => new(0, RulerHeight + row.Top, width, row.Height);

    void DrawEvents(DrawingContext context, TimelineEditorViewModel editor, TimelineRow row, double left, double right)
    {
        var events = row.Track.Track.Events;
        double top = RulerHeight + row.Top;
        foreach (var e in events)
        {
            var (start, eventLength) = Displayed(editor, e);
            double x = X(start), width = Math.Max(MinBlockWidth, X(eventLength));
            if (x > right || x + Math.Max(width, 120) < left) continue;
            // Labels stop at the next event in the row so they don't run over it.
            double labelRoom = NextStartAfter(editor, events, e, start) is { } next ? X(next) - x - 6 : 400;
            using (context.PushOpacity(e.Enabled ? 1 : 0.35))
                DrawEvent(context, editor, e, new Rect(x, top + 4, width, row.Height - 8), labelRoom, eventLength);
        }
    }

    /// <summary>
    /// An expanded channel's row: where the track's blocks are (shaded: double-click there adds a point to that block;
    /// elsewhere starts a new block), the channel's line through them, and its points.
    /// </summary>
    void DrawChannelRow(DrawingContext context, TimelineEditorViewModel editor, TimelineRow row, ChannelLane lane, double left, double right)
    {
        var rect = RowRect(row, Bounds.Width);
        foreach (var clip in row.Track.Track.Events.OfType<LightingClip>())
        {
            var (start, length) = Displayed(editor, clip);
            context.FillRectangle(ClipSpanFill, new Rect(X(start), rect.Y + 1, Math.Max(1, X(length)), rect.Height - 2));
        }
        var color = Color.Parse(TimelineEditorViewModel.LaneColor(lane));
        DrawLine(context, row, ChannelPoints(editor, row.Track.Track, lane.Target), new Pen(new SolidColorBrush(color), 2), right);
        var fill = new SolidColorBrush(color);
        foreach (var point in PointsOf(row.Track.Track, lane.Target))
        {
            var (time, value) = DisplayedPoint(editor, point);
            double x = X(time);
            if (x < left || x > right) continue;
            bool selected = editor.SelectedPoints.Contains(point);
            var center = new Point(x, Y(row, value));
            // Selected: bigger, white with a gold ring, so it stands out on any line colour (including yellow).
            double r = selected ? PointRadius + 1.5 : PointRadius;
            IBrush brush = selected ? Brushes.White : fill;
            IPen outline = selected ? SelectedPen : PointOutline;
            if (point.Step) context.DrawRectangle(brush, outline, new Rect(center.X - r, center.Y - r, r * 2, r * 2));
            else context.DrawEllipse(brush, outline, center, r, r);
        }
        if (editor.ErrorEvents.Contains(lane.Id)) context.DrawRectangle(ErrorPen, RowRect(row, Bounds.Width).Deflate(2));
    }

    /// <summary>A channel's points in every block of a track (the block lanes for its target).</summary>
    static IEnumerable<CurvePoint> PointsOf(TimelineTrack track, LaneTarget target) =>
        track.Events.OfType<LightingClip>().SelectMany(c => c.LaneFor(target)?.Points ?? []);

    /// <summary>
    /// A channel's line as it plays, on the timeline: each block's points (up to its end) at block start + time, the
    /// first point of a block jumping there. Points being dragged are drawn where they would land.
    /// </summary>
    List<(TimeSpan Time, byte Value, bool Jump)> ChannelPoints(TimelineEditorViewModel editor, TimelineTrack track, LaneTarget target)
    {
        var result = new List<(TimeSpan, byte, bool)>();
        foreach (var clip in track.Events.OfType<LightingClip>().Where(c => c.Enabled).OrderBy(c => c.Start))
        {
            if (clip.LaneFor(target) is not { } lane) continue;
            var (clipStart, clipLength) = Displayed(editor, clip);
            double scale = clip.Length > TimeSpan.Zero ? (double)clipLength.Ticks / clip.Length.Ticks : 1;
            bool first = true;
            foreach (var p in lane.Points.Where(p => p.Time <= clip.Length).OrderBy(p => p.Time))
            {
                var (time, value) = _drag == DragKind.MovePoints && editor.SelectedPoints.Contains(p)
                    ? DisplayedPoint(editor, p)
                    : (clipStart + TimeSpan.FromTicks((long)(p.Time.Ticks * scale)), p.Value);
                result.Add((time, value, first || p.Step));
                first = false;
            }
        }
        return result.OrderBy(p => p.Item1).ToList();
    }

    /// <summary>The level over time: nothing before the first point, straight lines for fades, hold-then-jump for jumps, flat after the last.</summary>
    void DrawLine(DrawingContext context, TimelineRow row, List<(TimeSpan Time, byte Value, bool Jump)> points, IPen pen, double right)
    {
        if (points.Count == 0) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(X(points[0].Time), Y(row, points[0].Value)), false);
            double lastValue = points[0].Value;
            for (int i = 1; i < points.Count; i++)
            {
                var (time, value, jump) = points[i];
                double x = X(time);
                if (jump) g.LineTo(new Point(x, Y(row, lastValue)));
                g.LineTo(new Point(x, Y(row, value)));
                lastValue = value;
            }
            g.LineTo(new Point(Math.Max(right, X(points[^1].Time)), Y(row, lastValue)));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>A lighting block's lines in miniature inside it (time scaled to the block, level to its height).</summary>
    void DrawClipContents(DrawingContext context, LightingClip clip, Rect rect)
    {
        if (clip.Length <= TimeSpan.Zero) return;
        var inner = new Rect(rect.X + 1, rect.Y + 15, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 18));
        foreach (var lane in clip.Lanes.Where(l => l.Points.Count > 0))
        {
            var pen = new Pen(new SolidColorBrush(Color.Parse(TimelineEditorViewModel.LaneColor(lane))), 1.5);
            var points = lane.Points.Where(p => p.Time <= clip.Length).OrderBy(p => p.Time).ToList();
            double Px(TimeSpan t) => inner.X + inner.Width * t.Ticks / clip.Length.Ticks;
            double Py(byte v) => inner.Bottom - inner.Height * v / 255.0;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(Px(points[0].Time), Py(points[0].Value)), false);
                byte last = points[0].Value;
                foreach (var p in points.Skip(1))
                {
                    if (p.Step) g.LineTo(new Point(Px(p.Time), Py(last)));
                    g.LineTo(new Point(Px(p.Time), Py(p.Value)));
                    last = p.Value;
                }
                g.LineTo(new Point(inner.Right, Py(last)));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    void DrawEvent(DrawingContext context, TimelineEditorViewModel editor, TimelineEvent e, Rect rect, double labelRoom, TimeSpan length)
    {
        bool selected = editor.Selection.Contains(e.Id), error = editor.ErrorEvents.Contains(e.Id);
        switch (e)
        {
            case LightingClip clip:
            {
                context.FillRectangle(new SolidColorBrush(LightingColor, 0.35), rect, 3);
                context.DrawRectangle(new Pen(new SolidColorBrush(LightingColor), 1), rect.Deflate(0.5), 3);
                using (context.PushClip(rect)) DrawClipContents(context, clip, rect);
                DrawText(context, clip.Name ?? "Lighting", rect.TopLeft + new Point(4, 1), Math.Min(labelRoom, rect.Width - 6));
                break;
            }
            case AudioEvent audio:
            {
                context.FillRectangle(new SolidColorBrush(AudioColor, 0.55), rect, 3);
                if (editor.LengthOf(audio) <= TimeSpan.Zero && audio.Length is null)
                    context.DrawRectangle(UnknownLengthPen, rect.Deflate(1), 3);   // file length not known (yet)
                DrawVolumeOutline(context, audio, rect, length);
                DrawText(context, TimelineCompiler.Describe(audio), rect.TopLeft + new Point(4, 2), labelRoom);
                break;
            }
            case MarkerEvent marker:
            {
                var brush = new SolidColorBrush(MarkerColor);
                context.DrawLine(new Pen(brush, 2), new Point(rect.X, rect.Y - 4), new Point(rect.X, rect.Bottom + 4));
                var flag = new Rect(rect.X, rect.Y, Math.Min(labelRoom, MeasureText(marker.Name ?? "Marker") + 10), 18);
                context.FillRectangle(brush, flag, 2);
                DrawText(context, marker.Name ?? "Marker", flag.TopLeft + new Point(4, 1), labelRoom);
                rect = new Rect(rect.X - 2, rect.Y, flag.Width + 2, rect.Height);
                break;
            }
            default:
                return;
        }
        if (error) context.DrawRectangle(ErrorPen, rect.Inflate(1), 3);
        if (selected) context.DrawRectangle(SelectedPen, rect.Inflate(1), 3);
    }

    /// <summary>The volume envelope across the block: rises over the fade-in, falls over the fade-out.</summary>
    void DrawVolumeOutline(DrawingContext context, AudioEvent audio, Rect rect, TimeSpan length)
    {
        if (audio.FadeIn <= TimeSpan.Zero && audio.FadeOut <= TimeSpan.Zero) return;
        double top = rect.Y + 16 + (1 - Math.Clamp(audio.Volume, 0, 1)) * (rect.Height - 18), bottom = rect.Bottom - 1;
        double fadeIn = Math.Min(X(audio.FadeIn), rect.Width), fadeOut = length > TimeSpan.Zero ? Math.Min(X(audio.FadeOut), rect.Width) : 0;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.X, bottom), false);
            g.LineTo(new Point(rect.X + fadeIn, top));
            g.LineTo(new Point(rect.Right - fadeOut, top));
            if (fadeOut > 0) g.LineTo(new Point(rect.Right, bottom));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, RampPen, geometry);
    }

    void DrawGrid(DrawingContext context, double step, double left, double right, double height)
    {
        double first = Math.Max(0, Math.Floor(left / Pps / step) * step);
        for (double t = first; t * Pps <= right; t += step)
        {
            double x = Math.Round(t * Pps) + 0.5;
            context.DrawLine(GridPen, new Point(x, RulerHeight), new Point(x, height));
        }
    }

    void DrawRuler(DrawingContext context, double step, double left, double right, double width)
    {
        context.FillRectangle(RulerFill, new Rect(0, 0, width, RulerHeight));
        double first = Math.Max(0, Math.Floor(left / Pps / step) * step);
        for (double t = first; t * Pps <= right; t += step)
        {
            double x = Math.Round(t * Pps) + 0.5;
            context.DrawLine(TickPen, new Point(x, RulerHeight - 8), new Point(x, RulerHeight));
            var text = new FormattedText(RulerLabel(t, step), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, RulerText);
            context.DrawText(text, new Point(x + 3, 3));
            // Minor ticks at a fifth of the step.
            for (int i = 1; i < 5; i++)
            {
                double mx = Math.Round((t + step * i / 5) * Pps) + 0.5;
                context.DrawLine(TickPen, new Point(mx, RulerHeight - 4), new Point(mx, RulerHeight));
            }
        }
        context.DrawLine(LanePen, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
    }

    /// <summary>The smallest round step that leaves room for a label (about 70 px apart).</summary>
    double RulerStep()
    {
        foreach (var step in RulerSteps) if (step * Pps >= 70) return step;
        return RulerSteps[^1];
    }

    static string RulerLabel(double seconds, double step)
    {
        var time = TimeSpan.FromSeconds(Math.Round(seconds, 3));
        string format = step < 1 ? @"m\:ss\.ff" : @"m\:ss";
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(format);
    }

    static void DrawText(DrawingContext context, string text, Point origin, double room, IBrush? brush = null, double size = 12)
    {
        if (room < 12) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush ?? Brushes.White)
        {
            MaxTextWidth = room, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(formatted, origin);
    }

    static double MeasureText(string text) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 12, Brushes.White).Width;

    /// <summary>Where an event is drawn: its model values, or the dragged preview while moving or resizing.</summary>
    (TimeSpan Start, TimeSpan Length) Displayed(TimelineEditorViewModel editor, TimelineEvent e)
    {
        var start = e.Start;
        var length = editor.LengthOf(e);
        if (_drag == DragKind.Move && editor.Selection.Contains(e.Id))
            start = editor.SnapTime(e.Start + _dragDelta);
        else if (_drag == DragKind.Resize && e.Id == _dragEvent)
            length = _resizeLength;
        else if (_drag == DragKind.ResizeStart && e.Id == _dragEvent)
            (start, length) = (_resizeStart, _dragOriginalStart + _dragOriginalLength - _resizeStart);
        return (start, length);
    }

    /// <summary>Where a point is drawn on the timeline: as it is, or where it would land while selected points are dragged.</summary>
    (TimeSpan Time, byte Value) DisplayedPoint(TimelineEditorViewModel editor, CurvePoint point)
    {
        var at = editor.AbsoluteTime(point);
        if (_drag != DragKind.MovePoints || !editor.SelectedPoints.Contains(point)) return (at, point.Value);
        var time = editor.SnapTime(at + _dragDelta < TimeSpan.Zero ? TimeSpan.Zero : at + _dragDelta);
        if (editor.LaneOf(point) is { } lane && editor.ClipOf(lane) is { } clip)
            time = time < clip.Start ? clip.Start : time > clip.Start + clip.Length ? clip.Start + clip.Length : time;
        return (time, (byte)Math.Clamp(point.Value + _dragValueDelta, 0, 255));
    }

    TimeSpan? NextStartAfter(TimelineEditorViewModel editor, IList<TimelineEvent> events, TimelineEvent e, TimeSpan start)
    {
        TimeSpan? next = null;
        foreach (var other in events)
        {
            if (other == e) continue;
            var otherStart = Displayed(editor, other).Start;
            if (otherStart > start && (next is null || otherStart < next)) next = otherStart;
        }
        return next;
    }

    // ---- hit testing ----

    TimelineRow? RowAt(double y)
    {
        if (Editor is not { } editor || y < RulerHeight) return null;
        double rowY = y - RulerHeight;
        foreach (var row in editor.Rows) if (rowY >= row.Top && rowY < row.Bottom) return row;
        return null;
    }

    /// <summary>
    /// The event under a point in a row, and which of its edges the point is on (blocks only: lighting and audio).
    /// Edges come first, so a block's edge stays grabbable where another block overlaps it; otherwise the topmost
    /// (last drawn) wins.
    /// </summary>
    (TimelineEvent? Event, Edge Edge) HitEvent(TimelineEditorViewModel editor, TimelineRow row, Point point)
    {
        var events = row.Track.Track.Events;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is not (AudioEvent or LightingClip)) continue;
            var e = events[i];
            double x = X(e.Start), width = Math.Max(MinBlockWidth, X(editor.LengthOf(e)));
            if (width <= EdgeGrab * 2) continue;
            if (point.X >= x + width - EdgeGrab && point.X <= x + width + 2) return (e, Edge.Right);
            if (point.X >= x - 2 && point.X <= x + EdgeGrab) return (e, Edge.Left);
        }
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            double x = X(e.Start);
            double width = e is MarkerEvent marker ? MeasureText(marker.Name ?? "Marker") + 10 : Math.Max(MinBlockWidth, X(editor.LengthOf(e)));
            if (point.X >= x - 2 && point.X <= x + width + 2) return (e, Edge.None);
        }
        return (null, Edge.None);
    }

    /// <summary>The nearest point of a channel row (in any block) within grabbing distance.</summary>
    CurvePoint? HitPoint(TimelineRow row, ChannelLane lane, Point at)
    {
        if (Editor is not { } editor) return null;
        CurvePoint? best = null;
        double bestDistance = PointGrab;
        foreach (var p in PointsOf(row.Track.Track, lane.Target))
        {
            double dx = X(editor.AbsoluteTime(p)) - at.X, dy = Y(row, p.Value) - at.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= bestDistance) (best, bestDistance) = (p, d);
        }
        return best;
    }

    // ---- mouse ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is not { } editor) return;
        var properties = e.GetCurrentPoint(this).Properties;
        var point = e.GetPosition(this);
        bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        Focus();

        if (properties.IsRightButtonPressed)
        {
            if (RowAt(point.Y) is { Lane: { } rightLane } rightRow && HitPoint(rightRow, rightLane, point) is { } rightPoint)
            {
                if (!editor.SelectedPoints.Contains(rightPoint)) editor.SelectPoint(rightPoint, additive: false);
                ShowPointMenu(editor, rightPoint);
                e.Handled = true;
            }
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        e.Handled = true;

        if (point.Y < RulerHeight)
        {
            _drag = DragKind.Scrub;
            e.Pointer.Capture(this);
            editor.SeekTo(TimeAt(point.X));
            return;
        }

        if (RowAt(point.Y) is not { } row)
        {
            if (!additive) editor.ClearSelection();
            return;
        }
        editor.SelectedTrack = row.Track;

        if (row.Lane is { } lane)
        {
            PressChannelRow(e, editor, row, lane, point, additive);
            return;
        }
        var (hit, edge) = HitEvent(editor, row, point);
        if (hit is null)
        {
            if (e.ClickCount == 2) _ = editor.AddEventAtAsync(row.TrackIndex, TimeAt(point.X));
            else if (!additive) editor.ClearSelection();
            return;
        }
        if (e.ClickCount == 2 && hit is LightingClip)
        {
            row.Track.IsExpanded = true;   // to edit its lines
            return;
        }
        if (additive)
        {
            editor.SelectEvent(hit.Id, additive: true);
            return;
        }
        if (!editor.Selection.Contains(hit.Id)) editor.SelectEvent(hit.Id, additive: false);

        _dragStart = point;
        _dragEvent = hit.Id;
        _dragDelta = TimeSpan.Zero;
        _dragOriginalLength = editor.LengthOf(hit);
        _dragOriginalStart = hit.Start;
        _resizeLength = _dragOriginalLength;
        _resizeStart = hit.Start;
        _drag = edge switch { Edge.Right => DragKind.Resize, Edge.Left => DragKind.ResizeStart, _ => DragKind.Pending };
        e.Pointer.Capture(this);
    }

    void PressChannelRow(PointerPressedEventArgs e, TimelineEditorViewModel editor, TimelineRow row, ChannelLane lane, Point point, bool additive)
    {
        var hit = HitPoint(row, lane, point);
        if (hit is null)
        {
            if (e.ClickCount == 2) editor.AddPoint(lane, TimeAt(point.X), ValueAt(row, point.Y));
            else if (!additive) editor.ClearSelection();
            return;
        }
        if (additive)
        {
            editor.SelectPoint(hit, additive: true);
            return;
        }
        if (!editor.SelectedPoints.Contains(hit)) editor.SelectPoint(hit, additive: false);
        _dragStart = point;
        _dragDelta = TimeSpan.Zero;
        _dragValueDelta = 0;
        _dragRowHeight = row.Height;
        _drag = DragKind.PendingPoints;
        e.Pointer.Capture(this);
    }

    void ShowPointMenu(TimelineEditorViewModel editor, CurvePoint point)
    {
        var fade = new MenuItem { Header = "Fade into this point", IsEnabled = point.Step };
        fade.Click += (_, _) => editor.SetStep(point, false);
        var step = new MenuItem { Header = "Step: hold, then jump here", IsEnabled = !point.Step };
        step.Click += (_, _) => editor.SetStep(point, true);
        var delete = new MenuItem { Header = editor.SelectedPoints.Count > 1 ? $"Delete {editor.SelectedPoints.Count} points" : "Delete point" };
        delete.Click += (_, _) => editor.DeleteCommand.Execute(null);
        new ContextMenu { ItemsSource = new object[] { fade, step, new Separator(), delete } }.Open(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Editor is not { } editor) return;
        var point = e.GetPosition(this);
        switch (_drag)
        {
            case DragKind.None:
                var row = RowAt(point.Y);
                Cursor = row is { Lane: { } lane } && HitPoint(row, lane, point) is not null ? new Cursor(StandardCursorType.Hand)
                       : row is { Lane: null } && HitEvent(editor, row, point).Edge != Edge.None ? new Cursor(StandardCursorType.SizeWestEast)
                       : Cursor.Default;
                return;
            case DragKind.Scrub:
                editor.SeekTo(TimeAt(point.X));
                return;
            case DragKind.Pending or DragKind.PendingPoints when Math.Abs(point.X - _dragStart.X) < DragThreshold && Math.Abs(point.Y - _dragStart.Y) < DragThreshold:
                return;   // a click, not a drag (yet)
            case DragKind.Pending:
                _drag = DragKind.Move;
                break;
            case DragKind.PendingPoints:
                _drag = DragKind.MovePoints;
                break;
        }
        var delta = TimeSpan.FromSeconds((point.X - _dragStart.X) / Pps);
        switch (_drag)
        {
            case DragKind.Move:
                _dragDelta = delta;
                break;
            case DragKind.MovePoints:
                _dragDelta = delta;
                _dragValueDelta = (int)Math.Round(-(point.Y - _dragStart.Y) / (_dragRowHeight - 2 * LinePad) * 255);
                break;
            case DragKind.Resize:
                _resizeLength = editor.SnapTime(_dragOriginalStart + _dragOriginalLength + delta) - _dragOriginalStart;
                if (_resizeLength < TimeSpan.FromMilliseconds(50)) _resizeLength = TimeSpan.FromMilliseconds(50);
                break;
            case DragKind.ResizeStart:
                var end = _dragOriginalStart + _dragOriginalLength;
                _resizeStart = editor.SnapTime(_dragOriginalStart + delta < TimeSpan.Zero ? TimeSpan.Zero : _dragOriginalStart + delta);
                if (_resizeStart > end - TimeSpan.FromMilliseconds(50)) _resizeStart = end - TimeSpan.FromMilliseconds(50);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Editor is not { } editor) return;
        var drag = _drag;
        _drag = DragKind.None;
        e.Pointer.Capture(null);
        switch (drag)
        {
            case DragKind.Pending:
                // A plain click on an event that was part of a multi-selection selects just that one.
                if (editor.Selection.Count > 1) editor.SelectEvent(_dragEvent, additive: false);
                break;
            case DragKind.Move:
                editor.MoveSelection(_dragDelta);
                break;
            case DragKind.Resize:
                editor.ResizeEvent(_dragEvent, _resizeLength);
                break;
            case DragKind.ResizeStart:
                editor.ResizeEventStart(_dragEvent, _resizeStart);
                break;
            case DragKind.MovePoints:
                editor.MoveSelectedPoints(_dragDelta, _dragValueDelta);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is DragKind.None) return;
        _drag = DragKind.None;   // e.g. the window lost focus mid-drag: drop the preview, change nothing
        InvalidateVisual();
    }
}
