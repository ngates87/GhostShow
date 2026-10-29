using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GhostShow.Core.Timeline;

namespace GhostShow.App.Controls;

/// <summary>The shapes an event track's events are drawn with (<see cref="EventSymbol"/>), centred on a point.</summary>
public static class EventSymbols
{
    /// <param name="r">Half its size.</param>
    public static Geometry Of(EventSymbol symbol, Point c, double r)
    {
        double x = c.X, y = c.Y;
        if (symbol == EventSymbol.Circle) return new EllipseGeometry(new Rect(x - r, y - r, 2 * r, 2 * r));
        if (symbol == EventSymbol.Square) return new RectangleGeometry(new Rect(x - r * 0.85, y - r * 0.85, r * 1.7, r * 1.7));
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        void Polygon(params (double X, double Y)[] points)
        {
            g.BeginFigure(new Point(x + points[0].X * r, y + points[0].Y * r), true);
            foreach (var p in points.Skip(1)) g.LineTo(new Point(x + p.X * r, y + p.Y * r));
            g.EndFigure(true);
        }
        void Hole(double dx, double dy, double radius)   // a round hole (an eye), cut out by the even-odd fill
        {
            g.BeginFigure(new Point(x + (dx - radius) * r, y + dy * r), true);
            g.ArcTo(new Point(x + (dx + radius) * r, y + dy * r), new Size(radius * r, radius * r), 0, false, SweepDirection.Clockwise);
            g.ArcTo(new Point(x + (dx - radius) * r, y + dy * r), new Size(radius * r, radius * r), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }
        switch (symbol)
        {
            case EventSymbol.Triangle:
                Polygon((-0.8, -1), (1, 0), (-0.8, 1));
                break;
            case EventSymbol.Star:
                var star = new (double, double)[10];
                for (int i = 0; i < 10; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 5, radius = i % 2 == 0 ? 1.1 : 0.45;
                    star[i] = (Math.Cos(angle) * radius, Math.Sin(angle) * radius + 0.1);
                }
                Polygon(star);
                break;
            case EventSymbol.Flag:
                Polygon((-0.75, 1), (-0.75, -1), (1, -0.55), (-0.45, -0.05), (-0.45, 1));
                break;
            case EventSymbol.Bolt:
                Polygon((0.3, -1.1), (-0.65, 0.15), (-0.05, 0.15), (-0.3, 1.1), (0.65, -0.2), (0.05, -0.2));
                break;
            case EventSymbol.Skull:
                g.SetFillRule(FillRule.EvenOdd);
                g.BeginFigure(new Point(x - 0.6 * r, y + 0.45 * r), true);
                g.ArcTo(new Point(x + 0.6 * r, y + 0.45 * r), new Size(0.85 * r, 0.85 * r), 0, true, SweepDirection.Clockwise);
                g.LineTo(new Point(x + 0.45 * r, y + r));
                g.LineTo(new Point(x - 0.45 * r, y + r));
                g.EndFigure(true);
                Hole(-0.33, -0.1, 0.24);
                Hole(0.33, -0.1, 0.24);
                Polygon((0, 0.22), (0.12, 0.45), (-0.12, 0.45));
                break;
            case EventSymbol.Ghost:
                g.SetFillRule(FillRule.EvenOdd);
                g.BeginFigure(new Point(x - 0.8 * r, y + r), true);
                g.LineTo(new Point(x - 0.8 * r, y - 0.2 * r));
                g.ArcTo(new Point(x + 0.8 * r, y - 0.2 * r), new Size(0.8 * r, 0.8 * r), 0, false, SweepDirection.Clockwise);
                g.LineTo(new Point(x + 0.8 * r, y + r));
                g.LineTo(new Point(x + 0.4 * r, y + 0.65 * r));
                g.LineTo(new Point(x, y + r));
                g.LineTo(new Point(x - 0.4 * r, y + 0.65 * r));
                g.EndFigure(true);
                Hole(-0.3, -0.25, 0.17);
                Hole(0.3, -0.25, 0.17);
                break;
            default:
                Polygon((0, -1), (1, 0), (0, 1), (-1, 0));
                break;
        }
        return geometry;
    }
}

/// <summary>An event symbol on its own (in the event's properties, to choose one).</summary>
public sealed class EventSymbolIcon : Control
{
    public static readonly StyledProperty<EventSymbol> SymbolProperty = AvaloniaProperty.Register<EventSymbolIcon, EventSymbol>(nameof(Symbol));

    static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#43A047"));

    static EventSymbolIcon() => AffectsRender<EventSymbolIcon>(SymbolProperty);

    public EventSymbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context) =>
        context.DrawGeometry(Fill, new Pen(Brushes.Black, 1), EventSymbols.Of(Symbol, new Point(Bounds.Width / 2, Bounds.Height / 2), 7));
}
