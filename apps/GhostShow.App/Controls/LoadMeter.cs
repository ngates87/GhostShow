using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// An old-school segmented level bar, like a hardware VU meter: lit segments are green at the low end, then yellow,
/// then red, so the colour of the last lit segment says how bad things are; unlit segments show faintly.
/// </summary>
public sealed class LoadMeter : Control
{
    /// <summary>Where segments turn yellow, then red (fractions of the bar).</summary>
    public const double YellowFrom = 0.6, RedFrom = 0.85;

    static readonly Color Green = Color.Parse("#3FBF4F"), Yellow = Color.Parse("#E6C229"), Red = Color.Parse("#E5483B");
    const double Gap = 2;

    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<LoadMeter, double>(nameof(Value));
    public static readonly StyledProperty<int> SegmentsProperty = AvaloniaProperty.Register<LoadMeter, int>(nameof(Segments), 16);

    static LoadMeter() => AffectsRender<LoadMeter>(ValueProperty, SegmentsProperty);

    /// <summary>0-1.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static Color ColorAt(double fraction) => fraction >= RedFrom ? Red : fraction >= YellowFrom ? Yellow : Green;

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        int segments = Math.Max(1, Segments);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), new Rect(bounds.Size), 2);
        double width = (bounds.Width - Gap * (segments + 1)) / segments;
        if (width <= 0) return;
        // A segment lights once the value reaches its middle, and at least one is lit for any load at all.
        int lit = Value <= 0 ? 0 : Math.Clamp((int)Math.Round(Math.Clamp(Value, 0, 1) * segments), 1, segments);
        for (int i = 0; i < segments; i++)
        {
            var color = ColorAt((i + 0.5) / segments);
            var brush = new SolidColorBrush(i < lit ? color : Color.FromArgb(0x30, color.R, color.G, color.B));
            context.FillRectangle(brush, new Rect(Gap + i * (width + Gap), Gap, width, Math.Max(0, bounds.Height - 2 * Gap)), 1);
        }
    }
}
