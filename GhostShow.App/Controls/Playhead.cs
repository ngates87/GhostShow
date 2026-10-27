using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// The playhead: a line across the timeline at the playback position. A separate layer over the
/// <see cref="TimelineCanvas"/>, so moving it 30 times a second redraws one line rather than every event.
/// </summary>
public sealed class Playhead : Control
{
    static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 1.5);
    static readonly IBrush HeadFill = new SolidColorBrush(Color.Parse("#FF5252"));

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<Playhead, TimeSpan>(nameof(Position));

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<Playhead, double>(nameof(PixelsPerSecond), 60);

    static Playhead() => AffectsRender<Playhead>(PositionProperty, PixelsPerSecondProperty);

    public Playhead()
    {
        IsHitTestVisible = false;
    }

    public TimeSpan Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double PixelsPerSecond
    {
        get => GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    /// <summary>Where the line is, in this control's coordinates (the view scrolls to keep it visible while playing).</summary>
    public double X => Math.Round(Position.TotalSeconds * PixelsPerSecond) + 0.5;

    public override void Render(DrawingContext context)
    {
        double x = X;
        context.DrawLine(LinePen, new Point(x, 0), new Point(x, Bounds.Height));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(x - 6, 0), true);
            g.LineTo(new Point(x + 6, 0));
            g.LineTo(new Point(x, 9));
            g.EndFigure(true);
        }
        context.DrawGeometry(HeadFill, null, head);
    }
}
