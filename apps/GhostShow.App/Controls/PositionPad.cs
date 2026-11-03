using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// A square to point a moving light with (a position handle's pan and tilt): pan across, tilt up and down, both
/// 0-255 (DMX). Click or drag to set them.
/// </summary>
public sealed class PositionPad : Control
{
    public static readonly StyledProperty<double> PanProperty =
        AvaloniaProperty.Register<PositionPad, double>(nameof(Pan), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> TiltProperty =
        AvaloniaProperty.Register<PositionPad, double>(nameof(Tilt), defaultBindingMode: BindingMode.TwoWay);

    static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#1B1B1B"));
    static readonly IPen Border = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    static readonly IPen Grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    static readonly IPen Cross = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 215, 0)), 1);

    bool _dragging;

    static PositionPad() => AffectsRender<PositionPad>(PanProperty, TiltProperty);

    public PositionPad()
    {
        Width = 180;
        Height = 180;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public double Pan
    {
        get => GetValue(PanProperty);
        set => SetValue(PanProperty, value);
    }

    public double Tilt
    {
        get => GetValue(TiltProperty);
        set => SetValue(TiltProperty, value);
    }

    Rect Area => new Rect(Bounds.Size).Deflate(6);

    public override void Render(DrawingContext context)
    {
        var area = Area;
        context.FillRectangle(Fill, area, 4);
        for (int i = 1; i < 4; i++)
        {
            double x = area.Left + area.Width * i / 4, y = area.Top + area.Height * i / 4;
            context.DrawLine(Grid, new Point(x, area.Top), new Point(x, area.Bottom));
            context.DrawLine(Grid, new Point(area.Left, y), new Point(area.Right, y));
        }
        context.DrawRectangle(Border, area, 4);
        var at = PointOf(Pan, Tilt);
        context.DrawLine(Cross, new Point(at.X, area.Top), new Point(at.X, area.Bottom));
        context.DrawLine(Cross, new Point(area.Left, at.Y), new Point(area.Right, at.Y));
        context.DrawEllipse(Brushes.Gold, new Pen(Brushes.Black, 1), at, 6, 6);
    }

    /// <summary>Where pan and tilt are on the pad (tilt 255 at the top).</summary>
    public Point PointOf(double pan, double tilt)
    {
        var area = Area;
        return new Point(area.Left + area.Width * pan / 255, area.Bottom - area.Height * tilt / 255);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        SetFrom(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SetFrom(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    void SetFrom(Point point)
    {
        var area = Area;
        Pan = Math.Round(Math.Clamp((point.X - area.Left) / area.Width, 0, 1) * 255);
        Tilt = Math.Round(Math.Clamp((area.Bottom - point.Y) / area.Height, 0, 1) * 255);
    }
}
