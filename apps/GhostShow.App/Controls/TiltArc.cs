using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// A tilt move's angles on an arc, as the moving head sees them: 0° straight ahead (up), negative to one side, positive
/// to the other, as far as the fixture tilts (<see cref="Limit"/> each way, so a 230° bar's arc reaches past level).
/// The start handle is a ring, the end handle filled; the part of the arc the move covers glows, and a beam shows
/// where the selected handle points. Press on a handle to pick it; press or drag anywhere to move the picked one
/// there (Shift: 5° steps); the wheel nudges it a degree.
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

    /// <summary>How far the fixture tilts each way from straight ahead, in degrees.</summary>
    public double Limit { get => GetValue(LimitProperty); set => SetValue(LimitProperty, value); }
    public double StartDegrees { get => GetValue(StartDegreesProperty); set => SetValue(StartDegreesProperty, value); }
    public double EndDegrees { get => GetValue(EndDegreesProperty); set => SetValue(EndDegreesProperty, value); }
    public bool IsEndSelected { get => GetValue(IsEndSelectedProperty); set => SetValue(IsEndSelectedProperty, value); }

    static TiltArc() => AffectsRender<TiltArc>(LimitProperty, StartDegreesProperty, EndDegreesProperty, IsEndSelectedProperty);

    static readonly Color Accent = Color.Parse("#B04BFF");
    static readonly IPen TrackPen = new Pen(new SolidColorBrush(Color.Parse("#3B2F52")), 6, lineCap: PenLineCap.Round);
    static readonly IPen GlowPen = new Pen(new SolidColorBrush(Accent, 0.25), 14, lineCap: PenLineCap.Round);
    static readonly IPen MovePen = new Pen(new SolidColorBrush(Accent), 6, lineCap: PenLineCap.Round);
    static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#E6DDF5")), 2);
    static readonly IPen OutlinePen = new Pen(new SolidColorBrush(Color.Parse("#E6DDF5")), 1.6);
    static readonly IBrush Label = new SolidColorBrush(Color.Parse("#E6DDF5"));
    static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#1A1424"));
    static readonly IBrush AccentFill = new SolidColorBrush(Accent);

    bool _dragging;

    /// <summary>The arc's centre (where the fixture sits) and radius, fitted to the control.</summary>
    (Point Pivot, double Radius) Geometry()
    {
        double below = Math.Max(0, -Math.Cos(Radians(Limit)));   // how far past level the arc goes (0 for ±90°)
        double radius = Math.Max(10, Math.Min((Bounds.Width - 90) / 2, (Bounds.Height - 50) / (1 + below)));
        return (new Point(Bounds.Width / 2, 36 + radius), radius);
    }

    static double Radians(double degrees) => degrees * Math.PI / 180;

    static Point At(Point pivot, double radius, double degrees) =>
        new(pivot.X + radius * Math.Sin(Radians(degrees)), pivot.Y - radius * Math.Cos(Radians(degrees)));

    StreamGeometry Arc(Point pivot, double radius, double from, double to)
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

        context.DrawGeometry(null, TrackPen, Arc(pivot, radius, -limit, limit));
        double low = Math.Min(StartDegrees, EndDegrees), high = Math.Max(StartDegrees, EndDegrees);
        if (high - low > 0.5)
        {
            var move = Arc(pivot, radius, low, high);
            context.DrawGeometry(null, GlowPen, move);
            context.DrawGeometry(null, MovePen, move);
        }

        // Marks: straight ahead, every 45° within reach, and the ends.
        var marks = new SortedSet<double> { 0, -limit, limit };
        for (double a = 45; a < limit; a += 45) { marks.Add(a); marks.Add(-a); }
        foreach (double mark in marks)
        {
            context.DrawLine(TickPen, At(pivot, radius - 9, mark), At(pivot, radius + 9, mark));
            var text = new FormattedText(Degrees(mark), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                         Typeface.Default, 12, Label);
            var at = At(pivot, radius + 26, mark);
            context.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
        }

        // The beam, from the fixture to where the selected handle points.
        double selected = IsEndSelected ? EndDegrees : StartDegrees;
        var beam = new StreamGeometry();
        using (var g = beam.Open())
        {
            g.BeginFigure(At(pivot, 14, selected), true);
            g.LineTo(At(pivot, radius - 12, selected - 5));
            g.LineTo(At(pivot, radius - 12, selected + 5));
            g.EndFigure(true);
        }
        var beamFill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(At(pivot, 14, selected), RelativeUnit.Absolute),
            EndPoint = new RelativePoint(At(pivot, radius, selected), RelativeUnit.Absolute),
            GradientStops = [new GradientStop(Color.FromArgb(170, Accent.R, Accent.G, Accent.B), 0), new GradientStop(Color.FromArgb(20, Accent.R, Accent.G, Accent.B), 1)],
        };
        context.DrawGeometry(beamFill, null, beam);

        DrawFixture(context, pivot, selected);

        // The handles: start a ring, end filled; the selected one larger, with a glow.
        DrawHandle(context, At(pivot, radius, StartDegrees), filled: false, selected: !IsEndSelected);
        DrawHandle(context, At(pivot, radius, EndDegrees), filled: true, selected: IsEndSelected);
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

    static void DrawHandle(DrawingContext context, Point at, bool filled, bool selected)
    {
        double size = selected ? 10 : 7;
        if (selected) context.DrawEllipse(new SolidColorBrush(Accent, 0.3), null, at, size + 6, size + 6);
        context.DrawEllipse(filled ? AccentFill : Dark, new Pen(Brushes.White, selected ? 2.5 : 1.5), at, size, size);
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
        // Pressed on a handle: that one is picked (and dragged); elsewhere, the picked one goes there.
        if (Distance(point, At(pivot, radius, EndDegrees)) < 16) SetCurrentValue(IsEndSelectedProperty, true);
        else if (Distance(point, At(pivot, radius, StartDegrees)) < 16) SetCurrentValue(IsEndSelectedProperty, false);
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
