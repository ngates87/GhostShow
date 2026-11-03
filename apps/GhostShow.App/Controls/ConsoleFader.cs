using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// A vertical fader drawn to look like a lighting console's: a recessed slot, a ridged black cap with a white index
/// line, a printed 0-FL scale beside the slot, and a column of level LEDs.
///
/// Input works like a physical fader: drag the cap (hold Shift for fine control), click the slot to jump there,
/// mouse wheel to nudge, and arrow keys / Page Up / Page Down / Home / End when focused. Value is 0-255 (DMX).
/// </summary>
public sealed class ConsoleFader : Control
{
    const double Max = 255;
    const double CapWidth = 36, CapHeight = 22, SlotWidth = 6, TrackPadding = CapHeight / 2 + 2;

    static readonly IBrush Faceplate = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1C1D20"), 0), new GradientStop(Color.Parse("#26272B"), 0.5), new GradientStop(Color.Parse("#1C1D20"), 1) },
    };
    static readonly IBrush SlotBrush = new SolidColorBrush(Color.Parse("#050506"));
    static readonly IPen SlotShadowPen = new Pen(new SolidColorBrush(Color.Parse("#000000")), 1);
    static readonly IPen SlotHighlightPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    static readonly IPen MajorTickPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1);
    static readonly IPen MinorTickPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
    static readonly IBrush ScaleText = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    static readonly IBrush LedOff = new SolidColorBrush(Color.Parse("#1A2A33"));
    static readonly IBrush LedOn = new SolidColorBrush(Color.Parse("#3FB6FF"));
    static readonly IBrush CapBody = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#4A4C52"), 0), new GradientStop(Color.Parse("#2A2B2F"), 0.45), new GradientStop(Color.Parse("#16171A"), 1) },
    };
    static readonly IPen CapEdge = new Pen(new SolidColorBrush(Color.Parse("#0A0A0B")), 1);
    static readonly IPen RidgeDark = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1);
    static readonly IPen RidgeLight = new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), 1);
    static readonly IPen IndexLine = new Pen(Brushes.White, 2);
    static readonly IPen FocusPen = new Pen(new SolidColorBrush(Color.Parse("#3FB6FF")), 1.5);
    static readonly IBrush CapShadow = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    static readonly Typeface Font = new("Inter");

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ConsoleFader, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay,
                                                        coerce: (_, v) => Math.Clamp(Math.Round(v), 0, Max));

    static ConsoleFader()
    {
        AffectsRender<ConsoleFader>(ValueProperty);
        FocusableProperty.OverrideDefaultValue<ConsoleFader>(true);
    }

    public ConsoleFader()
    {
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        MinWidth = 64;
        MinHeight = 120;
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    bool _dragging;
    double _dragStartY, _dragStartValue;

    double TrackTop => TrackPadding;
    double TrackBottom => Bounds.Height - TrackPadding;
    double TrackLength => Math.Max(1, TrackBottom - TrackTop);
    double SlotX => Bounds.Width / 2 + 6;   // slot sits right of centre, leaving room for the scale on the left

    double YFor(double value) => TrackBottom - value / Max * TrackLength;
    double ValueAt(double y) => (TrackBottom - y) / TrackLength * Max;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Faceplate, null, bounds, 4, 4);

        DrawScale(context);
        DrawLeds(context);

        // The slot: a dark recess, shadowed on the left, catching light on the right.
        var slot = new Rect(SlotX - SlotWidth / 2, TrackTop - 4, SlotWidth, TrackLength + 8);
        context.DrawRectangle(SlotBrush, null, slot, 3, 3);
        context.DrawLine(SlotShadowPen, slot.TopLeft, slot.BottomLeft);
        context.DrawLine(SlotHighlightPen, slot.TopRight + new Vector(0.5, 0), slot.BottomRight + new Vector(0.5, 0));

        DrawCap(context, YFor(Value));
        if (IsFocused) context.DrawRectangle(null, FocusPen, bounds.Deflate(1), 4, 4);
    }

    /// <summary>Printed legend: a tick every 5%, longer every 10%, labelled 0, 5 and FL like a console's scale.</summary>
    void DrawScale(DrawingContext context)
    {
        double right = SlotX - CapWidth / 2 - 3;
        for (int percent = 0; percent <= 100; percent += 5)
        {
            double y = YFor(percent / 100.0 * Max);
            bool major = percent % 10 == 0;
            context.DrawLine(major ? MajorTickPen : MinorTickPen, new Point(right - (major ? 8 : 4), y), new Point(right, y));

            string? label = percent switch { 100 => "FL", 50 => "5", 0 => "0", _ => null };
            if (label is null) continue;
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9, ScaleText);
            context.DrawText(text, new Point(right - 10 - text.Width, y - text.Height / 2));
        }
    }

    /// <summary>A column of level LEDs on the right, lit up to the current level.</summary>
    void DrawLeds(DrawingContext context)
    {
        const int count = 20;
        double x = SlotX + CapWidth / 2 + 5, height = TrackLength / count;
        int lit = (int)Math.Ceiling(Value / Max * count);
        for (int i = 0; i < count; i++)
        {
            var led = new Rect(x, TrackBottom - (i + 1) * height + 1, 4, Math.Max(1, height - 2));
            context.DrawRectangle(i < lit ? LedOn : LedOff, null, led, 1, 1);
        }
    }

    void DrawCap(DrawingContext context, double centreY)
    {
        var cap = new Rect(SlotX - CapWidth / 2, centreY - CapHeight / 2, CapWidth, CapHeight);
        context.DrawRectangle(CapShadow, null, cap.Translate(new Vector(0, 3)), 4, 4);
        context.DrawRectangle(CapBody, CapEdge, cap, 4, 4);

        // Grip ridges above and below the index line: a dark groove with a highlight under it.
        foreach (double offset in new[] { -7.0, -4.0, 4.0, 7.0 })
        {
            double y = Math.Round(centreY + offset) + 0.5;
            context.DrawLine(RidgeDark, new Point(cap.X + 4, y), new Point(cap.Right - 4, y));
            context.DrawLine(RidgeLight, new Point(cap.X + 4, y + 1), new Point(cap.Right - 4, y + 1));
        }
        context.DrawLine(IndexLine, new Point(cap.X + 3, centreY), new Point(cap.Right - 3, centreY));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        double y = e.GetPosition(this).Y;

        // Grabbing the cap moves it relative to where it was grabbed; clicking the slot elsewhere jumps there first.
        if (Math.Abs(y - YFor(Value)) > CapHeight / 2) Value = ValueAt(y);
        _dragging = true;
        _dragStartY = y;
        _dragStartValue = Value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        double fineness = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1.0;   // Shift: fine control
        double delta = (_dragStartY - e.GetPosition(this).Y) / TrackLength * Max * fineness;
        Value = _dragStartValue + delta;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Value += Math.Sign(e.Delta.Y) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 5);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double? value = e.Key switch
        {
            Key.Up => Value + 1,
            Key.Down => Value - 1,
            Key.PageUp => Value + Max / 10,
            Key.PageDown => Value - Max / 10,
            Key.Home => Max,   // top of the fader
            Key.End => 0,
            _ => null,
        };
        if (value is null) return;
        Value = value.Value;
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }
}
