using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// A tilt move's angles on an arc, as the moving head sees them: 0° straight ahead (up), negative to one side, positive
/// to the other, as far as the fixture tilts (<see cref="Limit"/> each way, so a 230° bar's arc reaches past level).
/// The start handle (S, <see cref="StartBrush"/>) and end handle (E, <see cref="EndBrush"/>) each have their own
/// colour; the part of the arc the move covers shades from one to the other, with an arrow the way it goes, and a beam
/// in the picked handle's colour shows where it points. Press on a handle to pick it; press or drag anywhere to move
/// the picked one there (Shift: 5° steps); the wheel nudges it a degree.
/// </summary>
public sealed class TiltArc : Control
{
    public static readonly StyledProperty<double> LimitProperty = AvaloniaProperty.Register<TiltArc, double>(nameof(Limit), 90);

    public static readonly StyledProperty<double> StartDegreesProperty =
        AvaloniaProperty.Register<TiltArc, double>(nameof(StartDegrees), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> EndDegreesProperty =
        AvaloniaProperty.Register<TiltArc, double>(nameof(EndDegrees), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsEndSelectedProperty =
        AvaloniaProperty.Register<TiltArc, bool>(nameof(IsEndSelected), true, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> StartBrushProperty = AvaloniaProperty.Register<TiltArc, IBrush?>(nameof(StartBrush));

    public static readonly StyledProperty<IBrush?> EndBrushProperty = AvaloniaProperty.Register<TiltArc, IBrush?>(nameof(EndBrush));

    /// <summary>How far the fixture tilts each way from straight ahead, in degrees.</summary>
    public double Limit { get => GetValue(LimitProperty); set => SetValue(LimitProperty, value); }
    public double StartDegrees { get => GetValue(StartDegreesProperty); set => SetValue(StartDegreesProperty, value); }
    public double EndDegrees { get => GetValue(EndDegreesProperty); set => SetValue(EndDegreesProperty, value); }
    public bool IsEndSelected { get => GetValue(IsEndSelectedProperty); set => SetValue(IsEndSelectedProperty, value); }

    /// <summary>The start handle's colour (as its card's, under the arc).</summary>
    public IBrush? StartBrush { get => GetValue(StartBrushProperty); set => SetValue(StartBrushProperty, value); }

    /// <summary>The end handle's colour: the app's accent.</summary>
    public IBrush? EndBrush { get => GetValue(EndBrushProperty); set => SetValue(EndBrushProperty, value); }

    static TiltArc()
    {
        AffectsRender<TiltArc>(LimitProperty, StartDegreesProperty, EndDegreesProperty, IsEndSelectedProperty, StartBrushProperty, EndBrushProperty);
        AffectsMeasure<TiltArc>(LimitProperty);
    }

    static readonly IPen TrackPen = new Pen(new SolidColorBrush(Color.Parse("#3A4048")), 6, lineCap: PenLineCap.Round);
    static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#6E757E")), 1.5);
    static readonly IPen OutlinePen = new Pen(new SolidColorBrush(Color.Parse("#B8BEC6")), 1.4);
    static readonly IPen ArrowPen = new Pen(Brushes.White, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    static readonly IBrush Label = new SolidColorBrush(Color.Parse("#9AA0A6"));
    static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#1E2024"));
    static readonly Color DefaultStart = Color.Parse("#66BB6A"), DefaultEnd = Color.Parse("#4CA3E0");

    Color StartColor => (StartBrush as ISolidColorBrush)?.Color ?? DefaultStart;
    Color EndColor => (EndBrush as ISolidColorBrush)?.Color ?? DefaultEnd;

    bool _dragging;

    /// <summary>The arc's centre (where the fixture sits) and radius, fitted to the control.</summary>
    (Point Pivot, double Radius) Geometry()
    {
        double below = Math.Max(0, -Math.Cos(Radians(Limit)));   // how far past level the arc goes (0 for ±90°)
        double radius = Math.Max(10, Math.Min((Bounds.Width - 90) / 2, (Bounds.Height - 50) / (1 + below)));
        return (new Point(Bounds.Width / 2, 36 + radius), radius);
    }

    /// <summary>As tall as the arc needs at the width it's given (the fixture under its pivot included), no taller.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width;
        double below = Math.Max(0, -Math.Cos(Radians(Limit)));
        double radius = Math.Clamp((width - 90) / 2, 10, 150);
        return new Size(Math.Min(width, 2 * radius + 90), 36 + radius * (1 + below) + Math.Max(36, 26 - radius * below + 14));
    }

    static double Radians(double degrees) => degrees * Math.PI / 180;

    static Point At(Point pivot, double radius, double degrees) =>
        new(pivot.X + radius * Math.Sin(Radians(degrees)), pivot.Y - radius * Math.Cos(Radians(degrees)));

    static StreamGeometry Arc(Point pivot, double radius, double from, double to)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(At(pivot, radius, from), false);
        context.ArcTo(At(pivot, radius, to), new Size(radius, radius), 0, Math.Abs(to - from) > 180,
                      to > from ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
        context.EndFigure(false);
        return geometry;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var (pivot, radius) = Geometry();
        double limit = Math.Max(1, Limit);
        Color startColor = StartColor, endColor = EndColor;

        context.DrawGeometry(null, TrackPen, Arc(pivot, radius, -limit, limit));

        // Marks: straight ahead, every 45° within reach, and the ends.
        var marks = new SortedSet<double> { 0, -limit, limit };
        for (double a = 45; a < limit; a += 45) { marks.Add(a); marks.Add(-a); }
        foreach (double mark in marks)
        {
            context.DrawLine(TickPen, At(pivot, radius - 8, mark), At(pivot, radius + 8, mark));
            var text = new FormattedText(Degrees(mark), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                         Typeface.Default, 11, Label);
            var at = At(pivot, radius + 26, mark);
            context.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
        }

        // The move: shaded from the start's colour to the end's, with an arrow halfway along, the way it goes.
        if (Math.Abs(EndDegrees - StartDegrees) > 0.5)
        {
            var shade = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(At(pivot, radius, StartDegrees), RelativeUnit.Absolute),
                EndPoint = new RelativePoint(At(pivot, radius, EndDegrees), RelativeUnit.Absolute),
                GradientStops = [new GradientStop(startColor, 0), new GradientStop(endColor, 1)],
            };
            var move = Arc(pivot, radius, Math.Min(StartDegrees, EndDegrees), Math.Max(StartDegrees, EndDegrees));
            context.DrawGeometry(null, new Pen(shade, 6, lineCap: PenLineCap.Round), move);
            if (Math.Abs(EndDegrees - StartDegrees) > 20) DrawArrow(context, pivot, radius, StartDegrees, EndDegrees);
        }

        // The beam, from the fixture to where the picked handle points, in its colour.
        double selected = IsEndSelected ? EndDegrees : StartDegrees;
        Color beamColor = IsEndSelected ? endColor : startColor;
        var beam = new StreamGeometry();
        using (var g = beam.Open())
        {
            g.BeginFigure(At(pivot, 14, selected), true);
            g.LineTo(At(pivot, radius - 14, selected - 5));
            g.LineTo(At(pivot, radius - 14, selected + 5));
            g.EndFigure(true);
        }
        var beamFill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(At(pivot, 14, selected), RelativeUnit.Absolute),
            EndPoint = new RelativePoint(At(pivot, radius, selected), RelativeUnit.Absolute),
            GradientStops = [new GradientStop(Color.FromArgb(150, beamColor.R, beamColor.G, beamColor.B), 0),
                             new GradientStop(Color.FromArgb(10, beamColor.R, beamColor.G, beamColor.B), 1)],
        };
        context.DrawGeometry(beamFill, null, beam);

        DrawFixture(context, pivot, selected);

        // The handles, lettered; the picked one larger, ringed, and drawn on top.
        var startAt = At(pivot, radius, StartDegrees);
        var endAt = At(pivot, radius, EndDegrees);
        if (IsEndSelected)
        {
            DrawHandle(context, startAt, "S", startColor, selected: false);
            DrawHandle(context, endAt, "E", endColor, selected: true);
        }
        else
        {
            DrawHandle(context, endAt, "E", endColor, selected: false);
            DrawHandle(context, startAt, "S", startColor, selected: true);
        }
    }

    /// <summary>A chevron halfway along the move, pointing from its start towards its end.</summary>
    static void DrawArrow(DrawingContext context, Point pivot, double radius, double from, double to)
    {
        double mid = (from + to) / 2, sign = to > from ? 1 : -1;
        var tip = At(pivot, radius, mid + sign * 2);
        var along = new Vector(Math.Cos(Radians(mid)), Math.Sin(Radians(mid))) * sign;   // the arc's direction there
        var across = new Vector(-along.Y, along.X);
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(tip - along * 6 + across * 6, false);
            g.LineTo(tip);
            g.LineTo(tip - along * 6 - across * 6);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, ArrowPen, arrow);
    }

    /// <summary>A small moving head: its base below the pivot, its head tilted to the angle.</summary>
    static void DrawFixture(DrawingContext context, Point pivot, double degrees)
    {
        context.DrawRectangle(Dark, OutlinePen, new Rect(pivot.X - 26, pivot.Y + 16, 52, 12), 3, 3);
        context.DrawLine(OutlinePen, new Point(pivot.X - 14, pivot.Y + 16), new Point(pivot.X - 14, pivot.Y));
        context.DrawLine(OutlinePen, new Point(pivot.X + 14, pivot.Y + 16), new Point(pivot.X + 14, pivot.Y));
        using (context.PushTransform(Matrix.CreateTranslation(-pivot.X, -pivot.Y) * Matrix.CreateRotation(Radians(degrees)) * Matrix.CreateTranslation(pivot.X, pivot.Y)))
            context.DrawRectangle(Dark, OutlinePen, new Rect(pivot.X - 9, pivot.Y - 16, 18, 24), 4, 4);
        context.DrawEllipse(Dark, OutlinePen, pivot, 3, 3);
    }

    static void DrawHandle(DrawingContext context, Point at, string letter, Color color, bool selected)
    {
        double size = selected ? 11 : 8.5;
        if (selected) context.DrawEllipse(new SolidColorBrush(color, 0.28), null, at, size + 6, size + 6);
        context.DrawEllipse(new SolidColorBrush(color), new Pen(selected ? Brushes.White : Dark, selected ? 2 : 1.5), at, size, size);
        var text = new FormattedText(letter, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                     new Typeface(FontFamily.Default, weight: FontWeight.Bold), selected ? 12 : 10, Dark);
        context.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
    }

    static string Degrees(double degrees) => degrees switch { 0 => "0°", > 0 => $"+{degrees:0}°", _ => $"−{-degrees:0}°" };

    double AngleAt(Point point)
    {
        var (pivot, _) = Geometry();
        return Math.Atan2(point.X - pivot.X, pivot.Y - point.Y) * 180 / Math.PI;
    }

    void SetSelected(double degrees, KeyModifiers modifiers)
    {
        double step = modifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
        double value = Math.Clamp(Math.Round(degrees / step) * step, -Limit, Limit);
        if (IsEndSelected) SetCurrentValue(EndDegreesProperty, value);
        else SetCurrentValue(StartDegreesProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        var (pivot, radius) = Geometry();
        // Pressed on a handle: that one is picked (and dragged); elsewhere, the picked one goes there. The picked one
        // is drawn on top, so it's the one found first where they overlap.
        bool onEnd = Distance(point, At(pivot, radius, EndDegrees)) < 16, onStart = Distance(point, At(pivot, radius, StartDegrees)) < 16;
        if (onEnd && onStart) { }
        else if (onEnd) SetCurrentValue(IsEndSelectedProperty, true);
        else if (onStart) SetCurrentValue(IsEndSelectedProperty, false);
        else SetSelected(AngleAt(point), e.KeyModifiers);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SetSelected(AngleAt(e.GetPosition(this)), e.KeyModifiers);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetSelected((IsEndSelected ? EndDegrees : StartDegrees) + Math.Sign(e.Delta.Y), KeyModifiers.None);
        e.Handled = true;
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
