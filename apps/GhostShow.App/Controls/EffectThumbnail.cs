using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GhostShow.Core.Timeline;

namespace GhostShow.App.Controls;

/// <summary>
/// A tiny picture of an effect, for the Effects menu: how bright it goes over its usual length, in its colours, as
/// it would play on a light already glowing a little (so dimming effects show too). Drawn from the effect itself (a
/// fixed pattern of it), so it always matches what it does. No kind: a Level Control clip, drawn as a hand-shaped envelope.
/// </summary>
public sealed class EffectThumbnail : Control
{
    public static readonly StyledProperty<EffectKind?> KindProperty = AvaloniaProperty.Register<EffectThumbnail, EffectKind?>(nameof(Kind));

    public EffectKind? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    static EffectThumbnail() => AffectsRender<EffectThumbnail>(KindProperty);

    const int Columns = 64;          // samples across, whatever the width (it's drawn scaled)
    const int SamplesPerColumn = 8;  // the brightest of these (the darkest, for a dimming effect): a flash or a drop-out can be shorter than a column
    const double Ambient = 0.55;     // the light's level before the effect, as a share of full: room to show both flashes and dips
    static readonly Color AmbientColour = Color.Parse("#FFE2B0");
    static readonly IBrush Back = new SolidColorBrush(Color.Parse("#1A1A1A"));
    static readonly IPen Frame = new Pen(new SolidColorBrush(Color.Parse("#40FFFFFF")), 1);
    static readonly Dictionary<EffectKind, (double Level, Color Colour)[]> Pictures = [];

    /// <summary>The effects that dim the light already there rather than flash their own: their drop-outs are what to see.</summary>
    static readonly HashSet<EffectKind> Dimming = [EffectKind.BrownOut, EffectKind.Flicker];

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Back, bounds, 3);
        var inner = bounds.Deflate(2);
        if (inner.Width <= 0 || inner.Height <= 0) return;

        var columns = Kind is { } kind ? PictureOf(kind) : LevelControl();
        double width = inner.Width / columns.Length;
        for (int i = 0; i < columns.Length; i++)
        {
            var (level, colour) = columns[i];
            double height = Math.Max(1, level * inner.Height);
            context.FillRectangle(new SolidColorBrush(colour, 0.85),
                                  new Rect(inner.X + i * width, inner.Bottom - height, Math.Ceiling(width), height));
        }
        context.DrawRectangle(Frame, bounds.Deflate(0.5), 3);
    }

    /// <summary>Each column's brightness (0-1) and colour, worked out once per kind.</summary>
    static (double Level, Color Colour)[] PictureOf(EffectKind kind)
    {
        if (Pictures.TryGetValue(kind, out var picture)) return picture;
        var info = EffectInfo.Of(kind);
        var length = info.DefaultLength;
        var keys = LightingEffects.Keys(LightingEffect.Create(kind, seed: 7), length);
        var envelope = LightingEffects.EnvelopeFor(keys, EffectRole.Brightness, null, (byte)(Ambient * 255), length);

        bool dims = Dimming.Contains(kind);
        picture = new (double, Color)[Columns];
        for (int column = 0; column < Columns; column++)
        {
            double best = dims ? 1 : 0;
            LightingEffects.Key? strongest = null;
            for (int s = 0; s < SamplesPerColumn; s++)
            {
                var time = TimeSpan.FromTicks(length.Ticks * (column * SamplesPerColumn + s) / (Columns * SamplesPerColumn));
                double level = Envelope.ValueAt(envelope, time) / 255;
                if (strongest is not null && (dims ? level >= best : level <= best)) continue;
                best = level;
                strongest = KeyAt(keys, time.TotalSeconds);
            }
            // The effect's colour where it has taken over; the light's own warm glow where it's only dimming it.
            var colour = strongest is { Mix: > 0.5 } key
                ? Color.FromRgb(Byte(key.Colour.R), Byte(key.Colour.G), Byte(key.Colour.B))
                : AmbientColour;
            picture[column] = (best, colour);
        }
        Pictures[kind] = picture;
        return picture;
    }

    /// <summary>The key in force at a time: the last one at or before it.</summary>
    static LightingEffects.Key? KeyAt(IReadOnlyList<LightingEffects.Key> keys, double seconds)
    {
        LightingEffects.Key? found = null;
        foreach (var key in keys)
        {
            if (key.Time > seconds) break;
            found = key;
        }
        return found ?? (keys.Count > 0 ? keys[0] : null);
    }

    static byte Byte(double share) => (byte)Math.Round(Math.Clamp(share, 0, 1) * 255);

    static (double, Color)[]? _levelControl;

    /// <summary>A rise, a hold, a dip and a fade: handles set by hand.</summary>
    static (double, Color)[] LevelControl()
    {
        if (_levelControl is not null) return _levelControl;
        (double T, double Level)[] handles = [(0, 0.15), (0.2, 0.9), (0.5, 0.9), (0.6, 0.45), (0.75, 0.7), (1, 0.15)];
        _levelControl = new (double, Color)[Columns];
        for (int column = 0; column < Columns; column++)
        {
            double t = (column + 0.5) / Columns;
            int next = Array.FindIndex(handles, h => h.T >= t);
            var (a, b) = (handles[Math.Max(0, next - 1)], handles[next]);
            double f = b.T > a.T ? (t - a.T) / (b.T - a.T) : 0;
            _levelControl[column] = (a.Level + (b.Level - a.Level) * f, AmbientColour);
        }
        return _levelControl;
    }
}
