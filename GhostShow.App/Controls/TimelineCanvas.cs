using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GhostShow.App.ViewModels;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;

namespace GhostShow.App.Controls;

/// <summary>
/// The timeline's ruler and track rows (laid out by <see cref="TimelineEditorViewModel.Rows"/>, the same rows as the
/// track headers), drawn the way VenueMagic draws them:
/// - Lighting track: its Level Control clips, each drawing its envelopes inside itself, every channel in its colour:
///   collapsed, as a preview; expanded (taller), with their handles. Between clips, each channel's held level.
/// - Event: a diamond on each event's time (green: runs a timeline, red: stops one), with what it runs or stops.
/// - Audio: a clip over the time it plays, showing the file's waveform at the loudness it will play (the clip's volume
///   and fades, and the track's volume), with its fade-in and fade-out drawn as a volume outline; dashed when the
///   file's length isn't known yet (its waveform appears once it has been measured).
/// Selected clips and handles are gold, things with problems dashed red, disabled clips and muted tracks dimmed,
/// locked clips carry a padlock and linked ones a chain.
///
/// Mouse, clips: click selects (Ctrl adds or removes), drag moves (with the clips linked to it; snapped; one edit on
/// release); drag an edge to resize (a lighting clip's envelopes scale or crop; an audio clip's left edge trims into
/// its file); double-click an empty part of a row to add a clip, or a lighting clip to open the Envelope Editor (on an
/// expanded clip's envelope line: to add a handle there).
/// Right-click a clip: Add Envelope Handle ▸ (per channel), split, lock, link, copy, delete; an effect clip (violet):
/// New random pattern and Convert to Level Control. Right-click empty space: Add here, Add effect here ▸, Paste here.
/// Mouse, handles (expanded lighting tracks): click selects (Ctrl adds); drag moves the selected handles in time (not
/// past their neighbours; end handles only up and down) and level; right-click for the curve type and Delete. The
/// first clip's start handle sets the channel's start level, when the channel starts at its first clip. Clicking or
/// dragging on the ruler moves the playhead; dragging on empty space draws a selection box.
///
/// Drag a timeline from the tree onto the lanes: a Run timeline event for it where it's dropped (on an event track;
/// elsewhere, on the first event track, made if needed).
///
/// Drawn directly (only the part scrolled into view) rather than built from controls, so long shows stay cheap.
/// </summary>
public sealed class TimelineCanvas : Control
{
    public const double RulerHeight = 26;
    const double EventMark = 15;   // half an event's symbol
    const double EdgeGrab = 6, MinBlockWidth = 6, DragThreshold = 3, HandleSize = 3.5, HandleGrab = 7, ClipPad = 3, ClipTitle = 15;
    static readonly TimeSpan TailRoom = TimeSpan.FromSeconds(10);
    static readonly double[] RulerSteps = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];

    static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
    static readonly IBrush LaneAlt = new SolidColorBrush(Color.Parse("#242424"));
    static readonly IBrush SelectedLane = new SolidColorBrush(Color.FromArgb(28, 255, 215, 0));
    static readonly IBrush RulerFill = new SolidColorBrush(Color.Parse("#2B2B2B"));
    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
    static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1);
    static readonly IPen LanePen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    static readonly IBrush RulerText = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
    static readonly IBrush HintText = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
    static readonly IBrush EndFill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
    static readonly IPen EndPen = new Pen(new SolidColorBrush(Color.Parse("#E57373")), 1, new DashStyle([4, 3], 0));

    static readonly Color AudioColor = Color.Parse("#2F84C0"), LightingColor = Color.Parse("#B8862B");
    static readonly IBrush ClipFill = new SolidColorBrush(Color.FromArgb(235, 0x2A, 0x26, 0x1E));
    static readonly IPen ClipBorder = new Pen(new SolidColorBrush(LightingColor), 1);
    static readonly IBrush TitleFill = new SolidColorBrush(LightingColor, 0.55);
    static readonly Color EffectColor = Color.Parse("#8E6BD8");
    static readonly IPen EffectBorder = new Pen(new SolidColorBrush(EffectColor), 1);
    static readonly IBrush EffectTitleFill = new SolidColorBrush(EffectColor, 0.6);
    static readonly IPen SelectedPen = new Pen(Brushes.Gold, 2);
    static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 2, new DashStyle([4, 3], 0));
    static readonly IBrush WaveFill = new SolidColorBrush(Color.FromArgb(210, 0xC8, 0xE6, 0xFF));
    static readonly IPen RampPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5);
    static readonly IPen UnknownLengthPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1, new DashStyle([3, 3], 0));
    static readonly IPen HandleOutline = new Pen(Brushes.Black, 1);
    static readonly IPen IconPen = new Pen(Brushes.White, 1.2);
    static readonly Typeface Font = new("Inter");

    public static readonly StyledProperty<TimelineEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<TimelineCanvas, TimelineEditorViewModel?>(nameof(Editor));

    public TimelineEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>The right-click menu opened last (it isn't attached to the canvas; tests pick its items through this).</summary>
    public ContextMenu? LastMenu { get; private set; }

    ScrollViewer? _scroller;           // sideways (the lanes)
    double _viewLeft, _viewRight;      // what's scrolled into view, while rendering
    ScrollViewer? _verticalScroller;   // up and down (headers and lanes together)

    // Drag state. Moves and resizes are previewed by drawing with an offset; the edit is made once, on release.
    enum DragKind { None, Pending, Move, Resize, ResizeStart, Scrub, PendingPoints, MovePoints, PendingBox, Box, StartLevel }
    enum Edge { None, Left, Right }
    DragKind _drag;
    Point _dragStart;
    Guid _dragEvent;
    TimeSpan _dragOriginalLength;
    TimeSpan _dragDelta;
    TimeSpan _resizeLength;
    TimeSpan _resizeStart;
    TimeSpan _dragOriginalStart;
    TimeSpan? _snapGuide;        // where a drag snapped to an edge (drawn as a guide line); null when on the grid
    IReadOnlySet<Guid> _moving = new HashSet<Guid>();   // what a move drags: the selection and what's linked to it
    bool _additiveBox;
    Point _boxEnd;
    const double EdgeSnapPixels = 8;
    static readonly IPen SnapGuidePen = new Pen(new SolidColorBrush(Color.Parse("#4DD0E1")), 1, new DashStyle([3, 2], 0));
    static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(40, 0x4D, 0xD0, 0xE1));
    static readonly IPen BoxPen = new Pen(new SolidColorBrush(Color.Parse("#4DD0E1")), 1);
    TimelineRow? _dragRow;
    CurvePoint? _dragAnchor;
    IReadOnlyDictionary<CurvePoint, (TimeSpan Time, byte Value)> _pointPreview = new Dictionary<CurvePoint, (TimeSpan, byte)>();
    TimeSpan _pointDelta;
    int _valueDelta;
    ChannelLane? _startChannel;   // the channel whose start level is being dragged (applied as it moves: one undo step)
    int _startDrags;

    /// <summary>A node of the timeline tree being dragged (the tree's own drag; here, a timeline dropped on the lanes).</summary>
    public static readonly DataFormat<TimelineTreeNode> TreeNodeFormat = DataFormat.CreateInProcessFormat<TimelineTreeNode>("ghostshow/timeline-tree-node");

    /// <summary>
    /// The tree node being dragged, while the tree drags one. On Linux a drag can go through the desktop's drag
    /// protocol (XDND) even within the app, which carries no in-process data: this finds the node then.
    /// </summary>
    public static TimelineTreeNode? DraggedNode { get; set; }

    /// <summary>The tree node a drag carries, if it's one.</summary>
    public static TimelineTreeNode? NodeIn(IDataTransfer data) => data.TryGetValue(TreeNodeFormat) ?? DraggedNode;

    /// <summary>Logs what a drag over the lanes carries (set by the app; where a drag is refused, this says why).</summary>
    public static ILogger? Log { get; set; }

    (TimelineRow? Row, TimeSpan At, string? Refused)? _dropPreview;   // where a timeline being dragged over would land (a ghost diamond), or why it can't

    public TimelineCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropPreview());
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>A timeline from the tree, over the lanes: shows where its Run timeline event would go (VenueMagic's drag onto an event track).</summary>
    void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_dropPreview is null)
            Log?.LogInformation("Drag over the lanes: formats [{Formats}], in-process node {InProcess}, tree dragging {Dragged}, allowed {Effects}",
                string.Join(", ", e.DataTransfer.Formats), e.DataTransfer.TryGetValue(TreeNodeFormat)?.Name ?? "none", DraggedNode?.Name ?? "none", e.DragEffects);
        if (Editor is not { } editor || NodeIn(e.DataTransfer) is not TimelineNode { Timeline: var timeline })
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropPreview();
            return;
        }
        var point = e.GetPosition(this);
        var row = RowAt(point.Y);
        var target = row is { Track.Kind: TrackKind.Event } ? row : editor.Rows.FirstOrDefault(r => r.Track.Kind == TrackKind.Event);
        // The open timeline itself can't go on its own lanes (each run would start another copy of itself): say so
        // where it would have gone, rather than just refusing the drop.
        bool itself = timeline == editor.SelectedTimeline;
        _dropPreview = (target, editor.SnapTime(TimeAt(point.X)), itself ? $"{timeline.Name} is the timeline open here: it can't run itself" : null);
        e.DragEffects = itself ? DragDropEffects.None : DragDropEffects.Link;
        e.Handled = true;
        InvalidateVisual();
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        var preview = _dropPreview;
        ClearDropPreview();
        if (Editor is not { } editor || NodeIn(e.DataTransfer) is not TimelineNode { Timeline: var timeline } || preview?.Refused is not null) return;
        var point = e.GetPosition(this);
        editor.DropTimeline(timeline, RowAt(point.Y)?.Track, preview?.At ?? TimeAt(point.X));
        e.Handled = true;
    }

    void ClearDropPreview()
    {
        if (_dropPreview is null) return;
        _dropPreview = null;
        InvalidateVisual();
    }

    static readonly IBrush DropFill = new SolidColorBrush(Color.FromArgb(150, 0x43, 0xA0, 0x47));

    /// <summary>The ghost of a timeline being dragged over: a diamond where its event would go (on a new event track's row when there's none: below the tracks).</summary>
    void DrawDropPreview(DrawingContext context, TimelineEditorViewModel editor)
    {
        if (_dropPreview is not { } preview) return;
        double y = preview.Row is { } row ? RulerHeight + row.Top + row.Height / 2 : RulerHeight + editor.ContentHeight + EventTrackGhost;
        double x = X(preview.At);
        context.DrawGeometry(preview.Refused is null ? DropFill : DropRefusedFill, SnapGuidePen, EventSymbols.Of(EventSymbol.Diamond, new Point(x, y), EventMark));
        context.DrawLine(SnapGuidePen, new Point(x, RulerHeight), new Point(x, Bounds.Height));
        if (preview.Refused is { } why)
        {
            // On a dark tag above the lane, so it reads over the events already there.
            var text = new FormattedText(why, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 12, ErrorText);
            var tag = new Rect(x + EventMark + 4, y - EventMark - 26, text.Width + 12, text.Height + 6);
            context.DrawRectangle(RefusedTag, null, tag, 3);
            context.DrawText(text, tag.TopLeft + new Vector(6, 3));
        }
    }

    static readonly IBrush DropRefusedFill = new SolidColorBrush(Color.FromArgb(150, 0xE5, 0x39, 0x35));
    static readonly IBrush RefusedTag = new SolidColorBrush(Color.FromArgb(235, 0x1A, 0x1A, 0x1A));

    const double EventTrackGhost = 20;

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
        _verticalScroller = _scroller?.FindAncestorOfType<ScrollViewer>();
        if (_verticalScroller is not null) _verticalScroller.ScrollChanged += OnScrolled;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null) _scroller.ScrollChanged -= OnScrolled;
        if (_verticalScroller is not null) _verticalScroller.ScrollChanged -= OnScrolled;
        _scroller = _verticalScroller = null;
    }

    void OnScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    double Pps => Editor?.PixelsPerSecond ?? 60;

    double X(TimeSpan time) => time.TotalSeconds * Pps;

    TimeSpan TimeAt(double x) => TimeSpan.FromSeconds(Math.Max(0, x) / Pps);

    /// <summary>Where an expanded lighting row draws envelopes: below the clips' titles, to just above the row's bottom.</summary>
    public static (double Top, double Bottom) EnvelopeBand(TimelineRow row) =>
        (RulerHeight + row.Top + ClipPad + ClipTitle + 4, RulerHeight + row.Bottom - ClipPad - 5);

    /// <summary>Where a level (0-255) is drawn in an expanded lighting row, and back.</summary>
    public static double LevelY(TimelineRow row, double value)
    {
        var (top, bottom) = EnvelopeBand(row);
        return top + (1 - value / 255) * (bottom - top);
    }

    static bool ShowsEnvelopes(TimelineRow row) => row.Track.IsLighting && row.Track.IsExpanded;

    /// <summary>Wide enough for every clip plus some room to add more; at least the visible width.</summary>
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
            foreach (var e in row.Track.Events)
            {
                var eventEnd = e.Start + editor.LengthOf(e);
                if (eventEnd > end) end = eventEnd;
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
        double viewTop = (_verticalScroller?.Offset.Y ?? 0) - RulerHeight - 50;
        double viewBottom = (_verticalScroller?.Offset.Y ?? 0) + (_verticalScroller?.Viewport.Height ?? bounds.Height) + 50;
        (_viewLeft, _viewRight) = (left, right);
        var visibleRows = editor.Rows.Where(r => r.Bottom >= viewTop && r.Top <= viewBottom).ToList();
        var step = RulerStep();

        foreach (var row in visibleRows)
        {
            var rect = RowRect(row, bounds.Width);
            if (row.Track == editor.SelectedTrack) context.FillRectangle(SelectedLane, rect);
            else if (row.TrackIndex % 2 == 1) context.FillRectangle(LaneAlt, rect);
            context.DrawLine(LanePen, rect.BottomLeft, rect.BottomRight);
        }
        DrawGrid(context, step, left, right, bounds.Height);

        if (editor.SelectedTimeline?.Length is { } length)
        {
            // Past an explicit length nothing plays: shade it.
            double x = X(length);
            context.FillRectangle(EndFill, new Rect(x, RulerHeight, Math.Max(0, bounds.Width - x), bounds.Height - RulerHeight));
            context.DrawLine(EndPen, new Point(x, RulerHeight), new Point(x, bounds.Height));
        }

        foreach (var row in visibleRows)
        {
            using (context.PushOpacity(row.Track.Track.Enabled ? 1 : 0.35))
            {
                if (row.Track.IsLighting && row.Track.Track.Channels.Count == 0)
                    DrawText(context, "Right-click the track name → Channels… to choose what this track controls",
                             new Point(Math.Max(left, 0) + 8, RulerHeight + row.Top + (row.Height - 16) / 2), 600, HintText, 12);
                else DrawEvents(context, editor, row, left, right);
            }
        }

        if (_snapGuide is { } guide) context.DrawLine(SnapGuidePen, new Point(X(guide), RulerHeight), new Point(X(guide), bounds.Height));
        if (_drag == DragKind.Box) context.DrawRectangle(BoxFill, BoxPen, new Rect(_dragStart, _boxEnd).Normalize());
        DrawDropPreview(context, editor);
        DrawRuler(context, step, left, right, bounds.Width);
    }

    static Rect RowRect(TimelineRow row, double width) => new(0, RulerHeight + row.Top, width, row.Height);

    void DrawEvents(DrawingContext context, TimelineEditorViewModel editor, TimelineRow row, double left, double right)
    {
        var events = row.Track.Track.Events;
        double top = RulerHeight + row.Top;
        var levels = row.Track.IsLighting ? editor.LevelsOf(row.Track.Track) : [];
        if (row.Track.IsLighting && row.Track.Track.DrawLevelsBetweenClips) DrawHolds(context, editor, row, levels, left, right);
        // Labels stop at the next clip in the row so they don't run over it: every start, sorted once per row
        // (finding "the next one" by searching per clip made a row of 500 clips cost 250,000 comparisons).
        var starts = events.Select(e => Displayed(editor, e).Start).Order().ToArray();
        foreach (var e in events)
        {
            var (start, eventLength) = Displayed(editor, e);
            double x = X(start), width = Math.Max(MinBlockWidth, X(eventLength));
            if (e is TimelineCommandEvent) (x, width) = (x - EventMark, EventMark * 2);   // a diamond on its time
            if (x > right || x + Math.Max(width, 120) < left) continue;
            int next = Array.BinarySearch(starts, start + TimeSpan.FromTicks(1));
            if (next < 0) next = ~next;
            double labelRoom = next < starts.Length ? X(starts[next]) - x - 6 : 400;
            var rect = new Rect(x, top + ClipPad, width, row.Height - 2 * ClipPad);
            using (context.PushOpacity(e.Enabled ? 1 : 0.4))
                DrawEvent(context, editor, row, e, rect, labelRoom, eventLength, levels);
        }
    }

    /// <summary>
    /// Between a lighting track's clips (and before the first, for a channel held at a fixed level from the start;
    /// and after the last) each channel holds a level: drawn as a line in its colour, so a light that stays on can be
    /// seen to stay on (VenueMagic's "Draw lamp levels between clips").
    /// </summary>
    void DrawHolds(DrawingContext context, TimelineEditorViewModel editor, TimelineRow row, IReadOnlyList<TrackLevels> levels, double left, double right)
    {
        foreach (var channel in levels)
        {
            var paint = PaintOf(channel.Channel);
            var pen = paint.Hold;
            void Hold(double from, double to, byte level)
            {
                if (to <= from || to < left || from > right) return;
                double y = LevelY(row, level);
                context.DrawLine(pen, new Point(Math.Max(from, left), y), new Point(Math.Min(to, right), y));
            }
            var clips = channel.Clips;
            // Before the first clip: the start level, held (as VenueMagic draws it); or, for a channel left alone
            // until then, faint and dashed: where the first clip will start from.
            double firstStart = clips.Count > 0 ? X(Displayed(editor, clips[0].Clip).Start) : right;
            if (channel.HoldsBeforeFirstClip) Hold(0, firstStart, channel.Channel.StartLevel);
            else if (clips.Count > 0)
            {
                pen = paint.Waiting;
                Hold(0, firstStart, clips[0].StartLevel);
                pen = paint.Hold;
            }
            for (int i = 0; i < clips.Count; i++)
            {
                var (start, length) = Displayed(editor, clips[i].Clip);
                var playsFor = clips[i].PlaysFor < clips[i].Clip.Length ? clips[i].PlaysFor : length;
                double to = i + 1 < clips.Count ? X(Displayed(editor, clips[i + 1].Clip).Start) : right;
                Hold(X(start + playsFor), to, clips[i].EndLevel);
            }
        }
    }

    void DrawEvent(DrawingContext context, TimelineEditorViewModel editor, TimelineRow row, TimelineEvent e, Rect rect, double labelRoom,
                   TimeSpan length, IReadOnlyList<TrackLevels> levels)
    {
        bool selected = editor.Selection.Contains(e.Id), error = editor.ErrorEvents.Contains(e.Id);
        switch (e)
        {
            case LightingClip clip:
            {
                // Collapsed, the envelopes are a preview (lines only); expanded, they have their handles to edit.
                bool expanded = ShowsEnvelopes(row);
                context.FillRectangle(ClipFill, rect, 4);
                // Built-in effects (Lightning, Fire, …) have a violet title; their envelopes are made, not edited.
                bool effect = clip.Effect is not null;
                context.FillRectangle(effect ? EffectTitleFill : TitleFill, new Rect(rect.X, rect.Y, rect.Width, ClipTitle), 4);
                using (context.PushClip(rect.Deflate(1))) DrawEnvelopes(context, editor, row, clip, rect, levels, editable: expanded && !effect, startDots: expanded);
                context.DrawRectangle(effect ? EffectBorder : ClipBorder, rect.Deflate(0.5), 4);
                double titleY = rect.Y + 0.5;
                double icons = DrawClipIcons(context, clip, rect, titleY);
                string title = effect ? $"⚡ {clip.Name ?? clip.KindName}" : clip.Name ?? clip.KindName;
                DrawText(context, title, new Point(rect.X + 4, titleY), Math.Min(labelRoom, rect.Width - 8 - icons), size: 11);
                break;
            }
            case AudioEvent audio:
            {
                context.FillRectangle(new SolidColorBrush(AudioColor, 0.55), rect, 4);
                using (context.PushClip(rect.Deflate(1))) DrawWaveform(context, editor, row.Track.Track, audio, rect, length);
                if (editor.LengthOf(audio) <= TimeSpan.Zero && audio.Length is null)
                    context.DrawRectangle(UnknownLengthPen, rect.Deflate(1), 4);   // file length not known (yet)
                DrawVolumeOutline(context, audio, row.Track.Track, rect, length);
                double icons = DrawClipIcons(context, audio, rect, rect.Y + 1);
                DrawText(context, TimelineCompiler.Describe(audio), rect.TopLeft + new Point(4, 1), Math.Min(labelRoom, rect.Width - 8 - icons));
                break;
            }
            case TimelineCommandEvent command:
                DrawCommand(context, command, rect, selected, error);
                return;
            default:
                return;
        }
        if (error) context.DrawRectangle(ErrorPen, rect.Inflate(1), 4);
        if (selected) context.DrawRectangle(SelectedPen, rect.Inflate(1), 4);
    }

    static readonly IBrush RunFill = new SolidColorBrush(Color.Parse("#43A047")), StopFill = new SolidColorBrush(Color.Parse("#E53935"));

    /// <summary>
    /// An event track's event: its symbol on its time (green: run, red: stop). What it runs or stops is its tooltip
    /// (and in its properties), so close events stay readable.
    /// </summary>
    void DrawCommand(DrawingContext context, TimelineCommandEvent command, Rect rect, bool selected, bool error)
    {
        double cx = rect.X + EventMark, cy = rect.Y + rect.Height / 2;
        context.DrawGeometry(command.Command == TimelineCommand.Run ? RunFill : StopFill, selected ? SelectedPen : error ? ErrorPen : HandleOutline,
            EventSymbols.Of(command.Symbol, new Point(cx, cy), EventMark));
    }

    /// <summary>What an event does, for its tooltip: its name, or the timeline it runs ("Stop …" for a stop).</summary>
    static string CommandLabel(TimelineEditorViewModel editor, TimelineCommandEvent command)
    {
        string target = editor.TimelinePath(command.TimelineId) ?? "(choose a timeline)";
        return command.Name ?? (command.Command == TimelineCommand.Run ? $"Run {target}" : $"Stop {target}");
    }

    static readonly IBrush ErrorText = new SolidColorBrush(Color.Parse("#FF8A80"));

    /// <summary>A padlock (locked) and a chain (linked) at a clip's top right; returns the width they take.</summary>
    static double DrawClipIcons(DrawingContext context, TimelineEvent e, Rect rect, double y)
    {
        double x = rect.Right - 4, used = 0;
        if (rect.Width < 40) return 0;
        if (e.Locked)
        {
            x -= 9;
            context.DrawRectangle(Brushes.White, null, new Rect(x, y + 7, 9, 7), 1.5);
            var shackle = new StreamGeometry();
            using (var g = shackle.Open())
            {
                g.BeginFigure(new Point(x + 2, y + 7), false);
                g.ArcTo(new Point(x + 7, y + 7), new Size(2.5, 3), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, IconPen, shackle);
            used += 13;
            x -= 4;
        }
        if (e.LinkGroup is not null)
        {
            x -= 12;
            context.DrawRectangle(null, IconPen, new Rect(x, y + 6, 7, 5), 2.5);
            context.DrawRectangle(null, IconPen, new Rect(x + 5, y + 9, 7, 5), 2.5);
            used += 16;
        }
        return used;
    }

    /// <summary>A channel colour's brushes and pens, made once (a redraw can paint hundreds of envelopes).</summary>
    /// <param name="Waiting">Before the first clip of a channel left alone: the level that clip starts from, faint and dashed (not played).</param>
    sealed record ChannelPaint(IBrush Fill, IPen Line, IPen Hold, IPen Ring, IPen Waiting);

    static readonly Dictionary<string, ChannelPaint> Paints = new();

    static ChannelPaint PaintOf(ChannelLane channel)
    {
        var key = TimelineEditorViewModel.LaneColor(channel);
        if (Paints.TryGetValue(key, out var paint)) return paint;
        var color = Color.Parse(key);
        var fill = new SolidColorBrush(color);
        return Paints[key] = new ChannelPaint(fill, new Pen(fill, 1.6), new Pen(new SolidColorBrush(color, 0.6), 1.5), new Pen(fill, 1.2),
                                              new Pen(new SolidColorBrush(color, 0.35), 1, new DashStyle([3, 4], 0)));
    }

    /// <summary>
    /// An expanded lighting clip's envelopes, one per track channel in its colour: from the start handle (where the
    /// previous clip left the channel) through its handles to its end handle. Handles being dragged are drawn where
    /// they'd land; a clip being resized is drawn scaled (or cropped). Handles show where there's room for them
    /// (selected ones always do); only the part in view is drawn.
    /// </summary>
    /// <param name="editable">Full lines and the handles; otherwise a thin preview.</param>
    /// <param name="startDots">The start handles (an effect has no handles of its own, but its first clip's start dots set the track's starting levels).</param>
    void DrawEnvelopes(DrawingContext context, TimelineEditorViewModel editor, TimelineRow row, LightingClip clip, Rect rect, IReadOnlyList<TrackLevels> levels,
                       bool editable, bool startDots)
    {
        var (start, length) = Displayed(editor, clip);
        double scale = clip.ScaleOnResize && clip.Length > TimeSpan.Zero ? (double)length.Ticks / clip.Length.Ticks : 1;
        // Cropping from the left keeps the handles where they are on the timeline.
        var origin = !clip.ScaleOnResize && _drag == DragKind.ResizeStart && _dragEvent == clip.Id ? clip.Start : start;
        double Px(TimeSpan t) => X(origin + TimeSpan.FromTicks((long)(t.Ticks * scale)));
        double left = Math.Max(rect.Left, (_scroller?.Offset.X ?? 0) - 10), right = Math.Min(rect.Right, (_scroller?.Offset.X ?? 0) + (_scroller?.Viewport.Width ?? Bounds.Width) + 10);
        if (right <= left) return;

        foreach (var channel in levels)
        {
            var paint = PaintOf(channel.Channel);
            var found = channel.Find(clip);
            var lane = found is null ? clip.LaneFor(channel.Channel.Target) : found.Lane;
            var envelope = found?.Handles ?? clip.EnvelopeFor(channel.Channel.Target, channel.StartLevelOf(clip));
            if (_pointPreview.Count > 0 && lane is not null && lane.Points.Any(_pointPreview.ContainsKey))
                envelope = envelope.Select(p => _pointPreview.TryGetValue(p, out var to) ? new CurvePoint { Time = to.Time, Value = to.Value, Curve = p.Curve } : p)
                                   .OrderBy(p => p.Time).ToList();

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(Px(envelope[0].Time), LevelY(row, envelope[0].Value)), false);
                for (int i = 1; i < envelope.Count; i++)
                {
                    var (a, b) = (envelope[i - 1], envelope[i]);
                    double xa = Px(a.Time), xb = Px(b.Time);
                    if (xb < left || xa > right)   // out of view: keep the line continuous, skip the detail
                    {
                        g.LineTo(new Point(xb, LevelY(row, b.Value)));
                        continue;
                    }
                    if (b.Curve == CurveKind.Step || b.Time == a.Time)
                    {
                        g.LineTo(new Point(xb, LevelY(row, a.Value)));
                        g.LineTo(new Point(xb, LevelY(row, b.Value)));
                    }
                    else if (b.Curve == CurveKind.Linear) g.LineTo(new Point(xb, LevelY(row, b.Value)));
                    else foreach (var (t, v) in Envelope.SampleSegment(envelope, i, (int)Math.Clamp((xb - xa) / 4, 2, 32)).Skip(1)) g.LineTo(new Point(Px(t), LevelY(row, v)));
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, editable ? paint.Line : paint.Ring, geometry);   // a preview: thinner, no handles
            if (!editable && !startDots) continue;

            // The start handle: locked (a ring), unless it's the channel's own start level (a filled dot, draggable).
            var startAt = new Point(Px(TimeSpan.Zero), LevelY(row, envelope[0].Value));
            if (startAt.X >= left - 4) context.DrawEllipse(editor.FirstClipOf(channel) == clip ? paint.Fill : null, paint.Ring, startAt, 3, 3);
            if (!editable) continue;

            if (lane is null || lane.Points.Count == 0) continue;
            // Unselected handles as one shape; selected ones on top: bigger, white with a gold ring, so they stand out
            // on any line colour (including yellow).
            bool roomForHandles = (Px(clip.Length) - Px(TimeSpan.Zero)) / lane.Points.Count >= 5;
            var handles = new StreamGeometry();
            var selected = new List<(Point Center, bool End)>();
            using (var g = handles.Open())
            {
                foreach (var point in lane.Points)
                {
                    var (time, value) = _pointPreview.TryGetValue(point, out var moved) ? moved : (point.Time, point.Value);
                    var center = new Point(Px(time), LevelY(row, value));
                    if (center.X < left - HandleSize || center.X > right + HandleSize) continue;
                    bool end = point == lane.Points[^1];
                    if (editor.SelectedPoints.Contains(point)) selected.Add((center, end));
                    else if (roomForHandles || end) AddHandle(g, center, HandleSize, end);
                }
            }
            context.DrawGeometry(paint.Fill, HandleOutline, handles);
            if (selected.Count == 0) continue;
            var picked = new StreamGeometry();
            using (var g = picked.Open())
                foreach (var (center, end) in selected) AddHandle(g, center, HandleSize + 1.5, end);
            context.DrawGeometry(Brushes.White, SelectedPen, picked);
        }
    }

    /// <summary>A handle's shape: a square, or for an end handle a diamond.</summary>
    static void AddHandle(StreamGeometryContext g, Point center, double r, bool end)
    {
        if (end)
        {
            g.BeginFigure(new Point(center.X, center.Y - r - 1), true);
            g.LineTo(new Point(center.X + r + 1, center.Y));
            g.LineTo(new Point(center.X, center.Y + r + 1));
            g.LineTo(new Point(center.X - r - 1, center.Y));
        }
        else
        {
            g.BeginFigure(new Point(center.X - r, center.Y - r), true);
            g.LineTo(new Point(center.X + r, center.Y - r));
            g.LineTo(new Point(center.X + r, center.Y + r));
            g.LineTo(new Point(center.X - r, center.Y + r));
        }
        g.EndFigure(true);
    }

    /// <summary>
    /// The file's waveform across the clip (only the part in view), mirrored about the middle of the space under the
    /// title, at the loudness it will play: the clip's volume and fades times the track's volume. A looping clip
    /// repeats the file from its start point. Nothing until the file has been measured.
    /// </summary>
    void DrawWaveform(DrawingContext context, TimelineEditorViewModel editor, TimelineTrack track, AudioEvent audio, Rect rect, TimeSpan length)
    {
        if (editor.WaveformOf(audio) is not { Count: > 0 } waveform) return;
        // While its left edge is dragged, the clip trims into its file by as much as its start moves.
        var offset = audio.Offset;
        if (_drag == DragKind.ResizeStart && audio.Id == _dragEvent) offset += _resizeStart - audio.Start;
        var repeat = waveform.Length - offset;
        double volume = Math.Clamp(audio.Volume, 0, 1) * Math.Clamp(track.Volume, 0, 1);
        var gain = new AudioCue(audio.Id, "", "", 0, length > TimeSpan.Zero ? length.Ticks : null, 0, volume,
                                audio.FadeIn.Ticks, audio.FadeOut.Ticks, audio.Loop);

        TimeSpan FileTime(TimeSpan t) => audio.Loop && repeat > TimeSpan.Zero && offset + t >= waveform.Length
            ? offset + TimeSpan.FromTicks(t.Ticks % repeat.Ticks)
            : offset + t;

        double top = rect.Y + ClipTitle, bottom = rect.Bottom - 2, middle = (top + bottom) / 2, half = (bottom - top) / 2;
        double from = Math.Max(rect.X, _viewLeft), to = Math.Min(rect.Right, _viewRight);
        if (to - from < 1 || half < 2) return;
        int columns = (int)Math.Ceiling(to - from);
        var heights = new double[columns + 1];
        for (int i = 0; i <= columns; i++)
        {
            var t = TimeSpan.FromSeconds((from + i - rect.X) / Pps);
            var file = FileTime(t);
            heights[i] = waveform.PeakBetween(file, file + TimeSpan.FromSeconds(1 / Pps)) * gain.VolumeAt(t.Ticks) * half;
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(from, middle - heights[0]), true);
            for (int i = 1; i <= columns; i++) g.LineTo(new Point(from + i, middle - heights[i]));
            for (int i = columns; i >= 0; i--) g.LineTo(new Point(from + i, middle + heights[i] + 0.5));
            g.EndFigure(true);
        }
        context.DrawGeometry(WaveFill, null, geometry);
    }

    /// <summary>The volume envelope across the clip (clip times track volume): rises over the fade-in, falls over the fade-out.</summary>
    void DrawVolumeOutline(DrawingContext context, AudioEvent audio, TimelineTrack track, Rect rect, TimeSpan length)
    {
        if (audio.FadeIn <= TimeSpan.Zero && audio.FadeOut <= TimeSpan.Zero) return;
        double volume = Math.Clamp(audio.Volume, 0, 1) * Math.Clamp(track.Volume, 0, 1);
        double top = rect.Y + 16 + (1 - volume) * (rect.Height - 18), bottom = rect.Bottom - 1;
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

    /// <summary>Where a clip is drawn: its model values, or the dragged preview while moving or resizing.</summary>
    (TimeSpan Start, TimeSpan Length) Displayed(TimelineEditorViewModel editor, TimelineEvent e)
    {
        var start = e.Start;
        var length = editor.LengthOf(e);
        if (_drag == DragKind.Move && _moving.Contains(e.Id))
            start = _snapGuide is null ? editor.SnapTime(e.Start + _dragDelta) : e.Start + _dragDelta;
        else if (_drag == DragKind.Resize && e.Id == _dragEvent)
            length = _resizeLength;
        else if (_drag == DragKind.ResizeStart && e.Id == _dragEvent)
            (start, length) = (_resizeStart, _dragOriginalStart + _dragOriginalLength - _resizeStart);
        return (start, length);
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
    /// The clip under a point in a row, and which of its edges the point is on (audio and lighting clips, unless
    /// locked). Edges come first, so a clip's edge stays grabbable where another overlaps it; otherwise the topmost
    /// (last drawn) wins.
    /// </summary>
    (TimelineEvent? Event, Edge Edge) HitEvent(TimelineEditorViewModel editor, TimelineRow row, Point point)
    {
        var events = row.Track.Track.Events;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is not (AudioEvent or LightingClip) || events[i].Locked) continue;
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
            double width = Math.Max(MinBlockWidth, X(editor.LengthOf(e)));
            if (e is TimelineCommandEvent) (x, width) = (x - EventMark, EventMark * 2);
            if (point.X >= x - 2 && point.X <= x + width + 2) return (e, Edge.None);
        }
        return (null, Edge.None);
    }

    /// <summary>
    /// The nearest envelope handle within grabbing distance in an expanded lighting row: a handle (Point), or a
    /// channel's own start level (the first clip's start handle, when the channel starts at its first clip).
    /// </summary>
    (CurvePoint? Point, ChannelLane? StartOf) HitHandle(TimelineEditorViewModel editor, TimelineRow row, Point at)
    {
        if (!ShowsEnvelopes(row)) return (null, null);
        (CurvePoint?, ChannelLane?) best = (null, null);
        double bestDistance = HandleGrab;
        void Consider(double x, double y, CurvePoint? point, ChannelLane? startOf)
        {
            double dx = x - at.X, dy = y - at.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= bestDistance) (best, bestDistance) = ((point, startOf), d);
        }
        var levels = editor.LevelsOf(row.Track.Track);
        foreach (var clip in row.Track.Track.Events.OfType<LightingClip>())
        {
            if (at.X < X(clip.Start) - HandleGrab || at.X > X(clip.Start + clip.Length) + HandleGrab) continue;
            foreach (var channel in levels)
            {
                if (editor.FirstClipOf(channel) == clip) Consider(X(clip.Start), LevelY(row, channel.Channel.StartLevel), null, channel.Channel);
                foreach (var p in clip.LaneFor(channel.Channel.Target)?.Points ?? []) Consider(X(clip.Start + p.Time), LevelY(row, p.Value), p, null);
            }
        }
        return best;
    }

    /// <summary>The channel whose envelope line passes within a few pixels of a point in an expanded clip, or null.</summary>
    TrackLevels? HitCurve(TimelineEditorViewModel editor, TimelineRow row, LightingClip clip, Point at)
    {
        if (!ShowsEnvelopes(row)) return null;
        var time = TimeAt(at.X) - clip.Start;
        if (time <= TimeSpan.Zero || time >= clip.Length) return null;
        TrackLevels? best = null;
        double bestDistance = HandleGrab;
        foreach (var levels in editor.LevelsOf(row.Track.Track))
        {
            var envelope = levels.Find(clip)?.Handles ?? clip.EnvelopeFor(levels.Channel.Target, levels.StartLevelOf(clip));
            double d = Math.Abs(LevelY(row, Envelope.ValueAt(envelope, time)) - at.Y);
            if (d <= bestDistance) (best, bestDistance) = (levels, d);
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
            if (RowAt(point.Y) is not { } rightRow) return;
            if (HitHandle(editor, rightRow, point) is { Point: { } rightPoint })
            {
                if (!editor.SelectedPoints.Contains(rightPoint)) editor.SelectPoint(rightPoint, additive: false);
                ShowHandleMenu(editor);
            }
            else if ((HitEvent(editor, rightRow, point).Event ?? LockedEventAt(editor, rightRow, point)) is { } rightEvent)
            {
                if (!editor.Selection.Contains(rightEvent.Id)) editor.SelectEvent(rightEvent.Id, additive: false);
                ShowEventMenu(editor, rightRow, rightEvent, TimeAt(point.X));
            }
            else
            {
                editor.SelectedTrack = rightRow.Track;
                ShowEmptyMenu(editor, rightRow, TimeAt(point.X));
            }
            e.Handled = true;
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
            StartBox(e, editor, point, additive);
            return;
        }
        editor.SelectedTrack = row.Track;

        var handle = HitHandle(editor, row, point);
        if (handle.StartOf is { } startOf && !additive)
        {
            _drag = DragKind.StartLevel;
            _startChannel = startOf;
            _startDrags++;
            _dragRow = row;
            e.Pointer.Capture(this);
            return;
        }
        if (handle.Point is { } hitPoint)
        {
            PressHandle(e, editor, row, hitPoint, point, additive);
            return;
        }

        var (hit, edge) = HitEvent(editor, row, point);
        hit ??= LockedEventAt(editor, row, point);
        if (hit is null)
        {
            if (e.ClickCount == 2) _ = editor.AddEventAtAsync(row.TrackIndex, TimeAt(point.X));
            else StartBox(e, editor, point, additive);
            return;
        }
        if (e.ClickCount == 2 && hit is LightingClip { Effect: not null } effectClip)
        {
            editor.SelectEvent(effectClip.Id, additive: false);   // its settings are in the properties panel
            return;
        }
        if (e.ClickCount == 2 && hit is LightingClip doubleClicked)
        {
            // On an envelope's line (expanded): a handle there. Elsewhere: the Envelope Editor (as in VenueMagic).
            if (HitCurve(editor, row, doubleClicked, point) is { } curve) editor.AddHandle(doubleClicked, curve.Channel.Target, TimeAt(point.X));
            else editor.OpenEnvelopeEditor(doubleClicked);
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

    /// <summary>A locked clip under the point (HitEvent skips their edges, not their bodies; this finds them too).</summary>
    TimelineEvent? LockedEventAt(TimelineEditorViewModel editor, TimelineRow row, Point point) =>
        row.Track.Track.Events.LastOrDefault(e => e.Locked && point.X >= X(e.Start) - 2 && point.X <= X(e.Start + editor.LengthOf(e)) + 2);

    void PressHandle(PointerPressedEventArgs e, TimelineEditorViewModel editor, TimelineRow row, CurvePoint hit, Point point, bool additive)
    {
        if (additive)
        {
            editor.SelectPoint(hit, additive: true);
            return;
        }
        if (!editor.SelectedPoints.Contains(hit)) editor.SelectPoint(hit, additive: false);
        _dragStart = point;
        _dragAnchor = hit;
        _dragRow = row;
        _pointDelta = TimeSpan.Zero;
        _valueDelta = 0;
        _drag = DragKind.PendingPoints;
        e.Pointer.Capture(this);
    }

    /// <summary>A press on empty space: clears the selection (unless Ctrl), and a drag from here draws a selection box.</summary>
    void StartBox(PointerPressedEventArgs e, TimelineEditorViewModel editor, Point point, bool additive)
    {
        if (!additive) editor.ClearSelection();
        _additiveBox = additive;
        _dragStart = _boxEnd = point;
        _drag = DragKind.PendingBox;
        e.Pointer.Capture(this);
    }

    /// <summary>Selects what the box covers: handles, where it covers expanded clips' handles; otherwise the clips it touches.</summary>
    void FinishBox(TimelineEditorViewModel editor)
    {
        var box = new Rect(_dragStart, _boxEnd).Normalize();
        TimeSpan from = TimeAt(box.Left), to = TimeAt(box.Right);
        var rows = editor.Rows.Where(r => RulerHeight + r.Bottom > box.Top && RulerHeight + r.Top < box.Bottom).ToList();
        var points = rows.Where(ShowsEnvelopes)
            .SelectMany(r => r.Track.Track.Events.OfType<LightingClip>().SelectMany(c => c.Lanes.SelectMany(l => l.Points)
                .Where(p => box.Contains(new Point(X(c.Start + p.Time), LevelY(r, p.Value))))))
            .ToList();
        if (points.Count > 0)
        {
            editor.SelectPoints(points, _additiveBox);
            return;
        }
        var events = rows.SelectMany(r => r.Track.Track.Events)
            .Where(ev => ev.Start <= to && ev.Start + editor.LengthOf(ev) >= from)
            .Select(ev => ev.Id).ToList();
        if (events.Count > 0) editor.SelectEvents(events, _additiveBox);
    }

    void ShowEventMenu(TimelineEditorViewModel editor, TimelineRow row, TimelineEvent clicked, TimeSpan at)
    {
        var items = new List<object>();
        if (clicked is LightingClip { Effect: not null } effectClip)
        {
            var pattern = new MenuItem { Header = "New random pattern", IsEnabled = !effectClip.Locked };
            pattern.Click += (_, _) => editor.NewEffectPattern(effectClip);
            var convert = new MenuItem { Header = "Convert to Level Control (editable handles)", IsEnabled = !effectClip.Locked };
            convert.Click += (_, _) => editor.ConvertToLevelControl(effectClip);
            items.AddRange([pattern, convert, new Separator()]);
        }
        else if (clicked is LightingClip clip)
        {
            var envelopes = new MenuItem { Header = "Envelope Editor…" };
            envelopes.Click += (_, _) => editor.OpenEnvelopeEditor(clip);
            items.Add(envelopes);
            // VenueMagic: right-click a clip → Add Envelope Handle ▸ the channel's envelope, at the clicked time.
            var add = new MenuItem { Header = "Add Envelope Handle", IsEnabled = !clip.Locked && row.Track.Track.Channels.Count > 0 };
            add.ItemsSource = row.Track.Track.Channels.Select(channel =>
            {
                var item = new MenuItem
                {
                    Header = editor.LaneLabel(channel.Target),
                    Icon = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.Parse(TimelineEditorViewModel.LaneColor(channel))) },
                };
                item.Click += (_, _) =>
                {
                    row.Track.IsExpanded = true;   // so the new handle shows
                    editor.AddHandle(clip, channel.Target, at);
                };
                return item;
            }).ToList();
            items.Add(add);
            var expand = new MenuItem { Header = row.Track.IsExpanded ? "Collapse track" : "Expand track (show envelopes)" };
            expand.Click += (_, _) => row.Track.IsExpanded = !row.Track.IsExpanded;
            items.Add(expand);
            items.Add(new Separator());
        }
        int count = editor.Selection.Count;
        string what = count > 1 ? $"{count} clips" : "clip";
        var position = editor.Playback.Position;
        var split = new MenuItem { Header = "Split at playhead", IsEnabled = !clicked.Locked && clicked is not LightingClip { Effect: not null } && clicked.Start < position && position < clicked.Start + editor.LengthOf(clicked) };
        split.Click += (_, _) => editor.SplitCommand.Execute(null);
        items.Add(split);
        var lockItem = new MenuItem { Header = clicked.Locked ? $"Unlock {what}" : $"Lock {what}" };
        lockItem.Click += (_, _) => editor.LockCommand.Execute(null);
        items.Add(lockItem);
        if (editor.CanLink)
        {
            var link = new MenuItem { Header = $"Link {what} (move together)" };
            link.Click += (_, _) => editor.LinkClipsCommand.Execute(null);
            items.Add(link);
        }
        if (editor.CanUnlink)
        {
            var unlink = new MenuItem { Header = "Unlink" };
            unlink.Click += (_, _) => editor.UnlinkClipsCommand.Execute(null);
            items.Add(unlink);
        }
        items.Add(new Separator());
        var duplicate = new MenuItem { Header = $"Duplicate {what}", InputGesture = new KeyGesture(Key.D, KeyModifiers.Control) };
        duplicate.Click += (_, _) => editor.DuplicateCommand.Execute(null);
        var copy = new MenuItem { Header = $"Copy {what}", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => editor.CopyCommand.Execute(null);
        var delete = new MenuItem { Header = $"Delete {what}", InputGesture = new KeyGesture(Key.Delete), IsEnabled = !clicked.Locked };
        delete.Click += (_, _) => editor.DeleteCommand.Execute(null);
        items.AddRange([duplicate, copy, delete]);
        Open(new ContextMenu { ItemsSource = items });
    }

    void ShowEmptyMenu(TimelineEditorViewModel editor, TimelineRow row, TimeSpan at)
    {
        string what = row.Track.Kind switch { TrackKind.Lighting => "Level Control clip", TrackKind.Event => "Run timeline event", _ => "sound…" };
        var add = new MenuItem { Header = $"Add {what} here" };
        add.Click += (_, _) => _ = editor.AddEventAtAsync(row.TrackIndex, at);
        var paste = new MenuItem { Header = "Paste here", IsEnabled = editor.CanPaste };
        paste.Click += (_, _) => editor.PasteAt(at);
        var items = new List<object> { add };
        if (row.Track.IsLighting)
        {
            var effects = new MenuItem { Header = "Add effect here" };
            effects.ItemsSource = EffectInfo.All.Select(info =>
            {
                var item = new MenuItem { Header = info.Name };
                ToolTip.SetTip(item, info.Description);
                item.Click += (_, _) => editor.AddEffectAt(row.Track, editor.SnapTime(at), info.Kind);
                return item;
            }).ToList();
            items.Add(effects);
        }
        items.Add(paste);
        Open(new ContextMenu { ItemsSource = items });
    }

    /// <summary>Right-click a handle: its curve type (how the level gets to it from the previous handle), and Delete.</summary>
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

    void Open(ContextMenu menu)
    {
        LastMenu = menu;
        menu.Open(this);
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
                var handle = row is null ? default : HitHandle(editor, row, point);
                Cursor = handle.StartOf is not null ? new Cursor(StandardCursorType.SizeNorthSouth)
                       : handle.Point is not null ? new Cursor(StandardCursorType.Hand)
                       : row is not null && HitEvent(editor, row, point).Edge != Edge.None ? new Cursor(StandardCursorType.SizeWestEast)
                       : Cursor.Default;
                var hovered = row is null ? null : HitEvent(editor, row, point).Event ?? LockedEventAt(editor, row, point);
                ToolTip.SetTip(this, hovered is TimelineCommandEvent command ? CommandLabel(editor, command) : null);
                return;
            case DragKind.Scrub:
                editor.SeekTo(TimeAt(point.X));
                return;
            case DragKind.StartLevel when _startChannel is not null && _dragRow is not null:
                editor.SetStartLevel(_startChannel, LevelAt(_dragRow, point.Y), field: $"drag {_startDrags}");
                return;
            case DragKind.Pending or DragKind.PendingPoints or DragKind.PendingBox when Math.Abs(point.X - _dragStart.X) < DragThreshold && Math.Abs(point.Y - _dragStart.Y) < DragThreshold:
                return;   // a click, not a drag (yet)
            case DragKind.PendingBox or DragKind.Box:
                _drag = DragKind.Box;
                _boxEnd = point;
                InvalidateVisual();
                return;
            case DragKind.Pending:
                _moving = editor.MovingWith(editor.Selection);
                if (_moving.Count == 0) return;   // locked (or linked to a locked clip): it stays put
                _drag = DragKind.Move;
                break;
            case DragKind.PendingPoints:
                _drag = DragKind.MovePoints;
                break;
        }
        var delta = TimeSpan.FromSeconds((point.X - _dragStart.X) / Pps);
        var tolerance = TimeSpan.FromSeconds(EdgeSnapPixels / Pps);
        _snapGuide = null;
        switch (_drag)
        {
            case DragKind.Move:
                // The grabbed clip's start or end snaps to the playhead or another clip's edge, if near one.
                var start = _dragOriginalStart + delta;
                var snappedStart = editor.SnapToEdges(start, tolerance, _moving);
                var snappedEnd = _dragOriginalLength > TimeSpan.Zero ? editor.SnapToEdges(start + _dragOriginalLength, tolerance, _moving) : null;
                if (snappedStart is { } s1 && (snappedEnd is null || (s1 - start).Duration() <= (snappedEnd.Value - start - _dragOriginalLength).Duration()))
                    (delta, _snapGuide) = (s1 - _dragOriginalStart, s1);
                else if (snappedEnd is { } s2)
                    (delta, _snapGuide) = (s2 - _dragOriginalLength - _dragOriginalStart, s2);
                _dragDelta = delta;
                break;
            case DragKind.MovePoints when _dragRow is not null:
                var (top, bottom) = EnvelopeBand(_dragRow);
                _pointDelta = delta;
                _valueDelta = (int)Math.Round(-(point.Y - _dragStart.Y) / (bottom - top) * 255);
                _pointPreview = editor.PreviewPointMove(_pointDelta, _valueDelta, _dragAnchor);
                break;
            case DragKind.Resize:
                var dragged = new HashSet<Guid> { _dragEvent };
                var newEnd = _dragOriginalStart + _dragOriginalLength + delta;
                _snapGuide = editor.SnapToEdges(newEnd, tolerance, dragged);
                _resizeLength = (_snapGuide ?? editor.SnapTime(newEnd)) - _dragOriginalStart;
                if (_resizeLength < TimeSpan.FromMilliseconds(50)) _resizeLength = TimeSpan.FromMilliseconds(50);
                break;
            case DragKind.ResizeStart:
                var end = _dragOriginalStart + _dragOriginalLength;
                var newStart = _dragOriginalStart + delta < TimeSpan.Zero ? TimeSpan.Zero : _dragOriginalStart + delta;
                _snapGuide = editor.SnapToEdges(newStart, tolerance, new HashSet<Guid> { _dragEvent });
                _resizeStart = _snapGuide ?? editor.SnapTime(newStart);
                if (_resizeStart > end - TimeSpan.FromMilliseconds(50)) _resizeStart = end - TimeSpan.FromMilliseconds(50);
                break;
        }
        InvalidateVisual();
    }

    static byte LevelAt(TimelineRow row, double y)
    {
        var (top, bottom) = EnvelopeBand(row);
        return (byte)Math.Clamp(Math.Round((1 - (y - top) / (bottom - top)) * 255), 0, 255);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Editor is not { } editor) return;
        var drag = _drag;
        bool edgeSnapped = _snapGuide is not null;
        EndDrag();
        e.Pointer.Capture(null);
        switch (drag)
        {
            case DragKind.Box:
                FinishBox(editor);
                break;
            case DragKind.Pending:
                // A plain click on a clip that was part of a multi-selection selects just that one.
                if (editor.Selection.Count > 1) editor.SelectEvent(_dragEvent, additive: false);
                break;
            case DragKind.Move:
                editor.MoveSelection(_dragDelta, snapToGrid: !edgeSnapped);
                break;
            case DragKind.Resize:
                editor.ResizeEvent(_dragEvent, _resizeLength, snapToGrid: !edgeSnapped);
                break;
            case DragKind.ResizeStart:
                editor.ResizeEventStart(_dragEvent, _resizeStart, snapToGrid: !edgeSnapped);
                break;
            case DragKind.MovePoints:
                editor.MoveSelectedPoints(_pointDelta, _valueDelta, _dragAnchor);
                break;
        }
        InvalidateVisual();
    }

    void EndDrag()
    {
        _drag = DragKind.None;
        _snapGuide = null;
        _pointPreview = new Dictionary<CurvePoint, (TimeSpan, byte)>();
        _moving = new HashSet<Guid>();
        _startChannel = null;
        _dragRow = null;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is DragKind.None) return;
        EndDrag();   // e.g. the window lost focus mid-drag: drop the preview, change nothing
        InvalidateVisual();
    }
}
