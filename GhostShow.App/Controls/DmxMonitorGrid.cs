using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// The 512 channel values of a universe as a grid: address, value, a fill proportional to the value, and a gold
/// border for a moment after a value changes.
///
/// Drawn directly rather than as 512 templated controls: building those took ~200 ms every time the Monitor screen
/// was shown, and drawing the grid takes a few milliseconds. The view model owns the arrays and bumps
/// <see cref="Revision"/> when they change; the grid only redraws then.
/// </summary>
public sealed class DmxMonitorGrid : Control
{
    const int Addresses = 512;
    const double MinCellWidth = 34, CellHeight = 38;
    static readonly int[] ColumnChoices = [32, 24, 16, 12, 8];
    public const double HighlightMs = 600;

    static readonly IBrush CellBackground = new SolidColorBrush(Color.Parse("#232323"));
    static readonly IBrush AddressBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    static readonly IPen ChangedPen = new Pen(Brushes.Gold, 1.5);
    static readonly Typeface Font = new("Inter");
    static readonly Typeface BoldFont = new("Inter", FontStyle.Normal, FontWeight.SemiBold);

    // Text layouts never change, so they're built once: 512 address labels and 256 value labels.
    static readonly FormattedText[] AddressTexts = Enumerable.Range(1, Addresses).Select(a => Text(a.ToString(), Font, 9, AddressBrush)).ToArray();
    static readonly FormattedText[] ValueTexts = Enumerable.Range(0, 256).Select(v => Text(v.ToString(), BoldFont, 12, Brushes.White)).ToArray();

    public static readonly StyledProperty<byte[]?> ValuesProperty =
        AvaloniaProperty.Register<DmxMonitorGrid, byte[]?>(nameof(Values));

    /// <summary>Stopwatch timestamp of each channel's last change (index = address - 1); 0 = not recently.</summary>
    public static readonly StyledProperty<long[]?> ChangedAtProperty =
        AvaloniaProperty.Register<DmxMonitorGrid, long[]?>(nameof(ChangedAt));

    /// <summary>Bumped by the view model whenever Values or ChangedAt change, since the arrays themselves are reused.</summary>
    public static readonly StyledProperty<long> RevisionProperty =
        AvaloniaProperty.Register<DmxMonitorGrid, long>(nameof(Revision));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<DmxMonitorGrid, IBrush?>(nameof(Fill));

    static DmxMonitorGrid() => AffectsRender<DmxMonitorGrid>(ValuesProperty, ChangedAtProperty, RevisionProperty, FillProperty);

    public byte[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public long[]? ChangedAt { get => GetValue(ChangedAtProperty); set => SetValue(ChangedAtProperty, value); }
    public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    int _columns = 32;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 32 * MinCellWidth : availableSize.Width;
        _columns = ColumnChoices.FirstOrDefault(c => width / c >= MinCellWidth, ColumnChoices[^1]);
        return new Size(width, (Addresses + _columns - 1) / _columns * CellHeight);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var changedAt = ChangedAt;
        var fill = Fill ?? Brushes.SteelBlue;
        double cellWidth = Bounds.Width / _columns;
        long now = Stopwatch.GetTimestamp();

        for (int i = 0; i < Addresses; i++)
        {
            var cell = new Rect(i % _columns * cellWidth, i / _columns * CellHeight, cellWidth, CellHeight).Deflate(1);
            byte value = values is null ? (byte)0 : values[i];
            bool recentlyChanged = changedAt is not null && changedAt[i] != 0
                                   && Stopwatch.GetElapsedTime(changedAt[i], now).TotalMilliseconds < HighlightMs;

            context.DrawRectangle(CellBackground, recentlyChanged ? ChangedPen : null, cell, 3, 3);
            if (value > 0)
            {
                // Fill rises from the bottom in proportion to the value, like a fader.
                double height = cell.Height * value / 255.0;
                using (context.PushOpacity(0.85))
                    context.DrawRectangle(fill, null, new Rect(cell.X, cell.Bottom - height, cell.Width, height), 3, 3);
            }

            var address = AddressTexts[i];
            context.DrawText(address, new Point(cell.X + (cell.Width - address.Width) / 2, cell.Y + 2));
            var text = ValueTexts[value];
            context.DrawText(text, new Point(cell.X + (cell.Width - text.Width) / 2, cell.Bottom - text.Height - 3));
        }
    }

    static FormattedText Text(string s, Typeface typeface, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
