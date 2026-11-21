using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>
/// The 512 channel values of a universe as a grid: address, value, a fill proportional to the value, and a gold
/// border for a moment after a value changes. With <see cref="Fixtures"/> set, the patch is drawn over it: a band in
/// each fixture's colour (the same as the Fixtures screen's address map) with its name above its channels, each
/// channel tinted that colour and labelled with what it does, so it's clear whose values are moving. Hovering a
/// channel says its address, fixture, channel and value.
///
/// Drawn directly rather than as 512 templated controls: building those took ~200 ms every time the Monitor screen
/// was shown, and drawing the grid takes a few milliseconds. The view model owns the arrays and bumps
/// <see cref="Revision"/> when they change; the grid only redraws then.
/// </summary>
public sealed class DmxMonitorGrid : Control
{
    const int Addresses = 512;
    const double MinCellWidth = 34, CellHeight = 38;
    /// <summary>With fixtures shown: a band for their names above each row, and room for a channel label.</summary>
    const double BandHeight = 14, PatchedCellHeight = 46;
    static readonly int[] ColumnChoices = [32, 24, 16, 12, 8];
    public const double HighlightMs = 600;

    static readonly IBrush CellBackground = new SolidColorBrush(Color.Parse("#232323"));
    static readonly IBrush AddressBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
    static readonly IPen ChangedPen = new Pen(Brushes.Gold, 1.5);
    static readonly Typeface Font = new("Inter");
    static readonly Typeface BoldFont = new("Inter", FontStyle.Normal, FontWeight.SemiBold);

    // Text layouts never change, so they're built once: 512 address labels and 256 value labels.
    readonly FormattedText[] _addressTexts = Enumerable.Range(1, Addresses).Select(a => Text(a.ToString(), Font, 9, AddressBrush)).ToArray();
    readonly FormattedText[] _valueTexts = Enumerable.Range(0, 256).Select(v => Text(v.ToString(), BoldFont, 12, Brushes.White)).ToArray();

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

    /// <summary>The fixtures patched on this universe, drawn over the grid; null draws the bare values.</summary>
    public static readonly StyledProperty<IReadOnlyList<PatchMapBlock>?> FixturesProperty =
        AvaloniaProperty.Register<DmxMonitorGrid, IReadOnlyList<PatchMapBlock>?>(nameof(Fixtures));

    static DmxMonitorGrid()
    {
        AffectsRender<DmxMonitorGrid>(ValuesProperty, ChangedAtProperty, RevisionProperty, FillProperty, FixturesProperty);
        AffectsMeasure<DmxMonitorGrid>(FixturesProperty);
    }

    public byte[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public long[]? ChangedAt { get => GetValue(ChangedAtProperty); set => SetValue(ChangedAtProperty, value); }
    public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IReadOnlyList<PatchMapBlock>? Fixtures { get => GetValue(FixturesProperty); set => SetValue(FixturesProperty, value); }

    int _columns = 32;

    // With fixtures shown: which fixture (and which of its channels) each address is, and its label, made as needed.
    readonly PatchMapBlock?[] _owner = new PatchMapBlock?[Addresses];
    readonly int[] _channel = new int[Addresses];
    readonly FormattedText?[] _labels = new FormattedText?[Addresses];
    double _labelWidth;
    int? _hoverIndex;

    bool ShowsFixtures => Fixtures is { Count: > 0 };
    double RowHeight => ShowsFixtures ? BandHeight + PatchedCellHeight : CellHeight;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FixturesProperty) MapFixtures();
        else if (change.Property == RevisionProperty && _hoverIndex is { } hover) ShowTip(hover);   // its value changed under the pointer
    }

    void MapFixtures()
    {
        Array.Clear(_owner);
        Array.Clear(_labels);
        foreach (var block in Fixtures ?? [])
            for (int i = 0; i < block.Count; i++)
            {
                int index = block.Start - 1 + i;
                if (index is < 0 or >= Addresses) continue;
                _owner[index] = block;
                _channel[index] = i;
            }
        if (_hoverIndex is { } hover) ShowTip(hover);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 32 * MinCellWidth : availableSize.Width;
        _columns = ColumnChoices.FirstOrDefault(c => width / c >= MinCellWidth, ColumnChoices[^1]);
        return new Size(width, (Addresses + _columns - 1) / _columns * RowHeight);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var changedAt = ChangedAt;
        var fill = Fill ?? Brushes.SteelBlue;
        double cellWidth = Bounds.Width / _columns;
        long now = Stopwatch.GetTimestamp();
        bool patched = ShowsFixtures;
        double rowHeight = RowHeight, top = patched ? BandHeight : 0;
        if (patched && cellWidth != _labelWidth)
        {
            Array.Clear(_labels);   // trimmed to the old width
            _labelWidth = cellWidth;
        }

        for (int i = 0; i < Addresses; i++)
        {
            var cell = new Rect(i % _columns * cellWidth, i / _columns * rowHeight + top, cellWidth, rowHeight - top).Deflate(1);
            byte value = values is null ? (byte)0 : values[i];
            bool recentlyChanged = changedAt is not null && changedAt[i] != 0
                                   && Stopwatch.GetElapsedTime(changedAt[i], now).TotalMilliseconds < HighlightMs;
            var owner = patched ? _owner[i] : null;

            context.DrawRectangle(CellBackground, null, cell, 3, 3);
            if (owner is not null)
                context.DrawRectangle(new SolidColorBrush(PatchMap.ColorFor(owner.ColorIndex), 0.45), null, cell, 3, 3);
            if (value > 0)
            {
                // Fill rises from the bottom in proportion to the value, like a fader.
                double height = cell.Height * value / 255.0;
                using (context.PushOpacity(0.85))
                    context.DrawRectangle(fill, null, new Rect(cell.X, cell.Bottom - height, cell.Width, height), 3, 3);
            }
            if (recentlyChanged) context.DrawRectangle(null, ChangedPen, cell, 3, 3);

            var address = _addressTexts[i];
            context.DrawText(address, new Point(cell.X + (cell.Width - address.Width) / 2, cell.Y + 2));
            if (owner is not null && LabelOf(i, owner, cell.Width) is { } label)
                context.DrawText(label, new Point(cell.X + (cell.Width - label.Width) / 2, cell.Y + 14));
            var text = _valueTexts[value];
            context.DrawText(text, new Point(cell.X + (cell.Width - text.Width) / 2, cell.Bottom - text.Height - 3));
        }

        if (patched) DrawNames(context, cellWidth, rowHeight);
    }

    /// <summary>Each fixture's name in a band of its colour above its channels, on each row it's on.</summary>
    void DrawNames(DrawingContext context, double cellWidth, double rowHeight)
    {
        foreach (var block in Fixtures!)
        {
            int first = Math.Max(block.Start, 1) - 1, last = Math.Min(block.End, Addresses) - 1;
            var brush = new SolidColorBrush(PatchMap.ColorFor(block.ColorIndex));
            for (int start = first; start <= last;)
            {
                int row = start / _columns, end = Math.Min(last, (row + 1) * _columns - 1);
                var band = new Rect(start % _columns * cellWidth + 1, row * rowHeight + 1, (end - start + 1) * cellWidth - 2, BandHeight - 2);
                context.DrawRectangle(brush, null, band, 3, 3);
                using (context.PushClip(band))
                {
                    var name = new FormattedText(start == first ? block.Name : $"… {block.Name}", CultureInfo.CurrentCulture,
                                                 FlowDirection.LeftToRight, BoldFont, 9, Brushes.White)
                    {
                        MaxTextWidth = Math.Max(1, band.Width - 6),
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                    };
                    context.DrawText(name, new Point(band.X + 3, band.Y + (band.Height - name.Height) / 2));
                }
                start = end + 1;
            }
        }
    }

    FormattedText? LabelOf(int index, PatchMapBlock owner, double width)
    {
        if (_labels[index] is { } made) return made;
        string name = owner.ChannelLabels[_channel[index]];
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _labels[index] = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 9, LabelBrush)
        {
            MaxTextWidth = Math.Max(1, width - 4),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
    }

    // ---- hovering: what the channel is ----

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        double cellWidth = Bounds.Width / _columns;
        int column = (int)(point.X / cellWidth), row = (int)(point.Y / RowHeight);
        int index = row * _columns + column;
        int? hover = column >= 0 && column < _columns && row >= 0 && index < Addresses ? index : null;
        if (hover == _hoverIndex) return;
        _hoverIndex = hover;
        if (hover is { } i) ShowTip(i);
        else ToolTip.SetTip(this, null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverIndex = null;
        ToolTip.SetTip(this, null);
    }

    /// <summary>"Address 12 · Beam bar 1 · Red: 255", or without the fixture where none is shown.</summary>
    void ShowTip(int index)
    {
        int value = Values is { } values ? values[index] : 0;
        string tip = ShowsFixtures && _owner[index] is { } owner
            ? $"Address {index + 1} · {owner.Name} · {owner.ChannelLabels[_channel[index]]} (ch {_channel[index] + 1}): {value}"
            : $"Address {index + 1}: {value}";
        ToolTip.SetTip(this, tip);
    }

    static FormattedText Text(string s, Typeface typeface, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
