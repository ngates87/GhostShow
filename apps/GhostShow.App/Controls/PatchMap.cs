using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.Controls;

/// <summary>One fixture on the map. ChannelLabels[i] describes address Start + i.</summary>
public sealed record PatchMapBlock(Guid Id, string Name, int Start, IReadOnlyList<string> ChannelLabels, int ColorIndex, bool Enabled)
{
    public int Count => ChannelLabels.Count;
    public int End => Start + Count - 1;
}

/// <summary>
/// The 512 addresses of a universe as a grid, with each fixture drawn as a coloured block over its channels, the way
/// lighting consoles show a patch: gaps, groups and collisions are visible at a glance.
///
/// Placing: when <see cref="Footprint"/> is set (the channel count of the fixture being placed), every opening big
/// enough for it is outlined in green, counting the selected fixture's own addresses as free since that's what is
/// being moved. Hovering a free address previews the fixture there (green if it fits, red if not); clicking places
/// it. Clicking a block selects its fixture, and dragging a block re-addresses it.
///
/// Drawn directly rather than built from 512 controls, so redrawing on every pointer move stays cheap.
/// </summary>
public sealed class PatchMap : Control
{
    const int Addresses = 512;
    const double MinCellWidth = 32, RowHeight = 28;
    const int CompactColumns = 32;
    static readonly int[] ColumnChoices = [32, 24, 20, 16, 12, 10, 8];

    // Muted hues that stay readable with white text on a dark background, distinct for neighbouring fixtures.
    static readonly Color[] Palette =
    [
        Color.Parse("#2E7D9A"), Color.Parse("#7A5BA6"), Color.Parse("#3F8F5B"), Color.Parse("#A0662E"),
        Color.Parse("#8A3F6E"), Color.Parse("#4F6FB0"), Color.Parse("#6E8A2E"), Color.Parse("#A04A3F"),
        Color.Parse("#2E8A82"), Color.Parse("#8A6E2E"),
    ];

    static readonly IBrush EmptyCell = new SolidColorBrush(Color.Parse("#232323"));
    static readonly IBrush AddressText = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    static readonly IBrush NameText = Brushes.White;
    static readonly IBrush OverlapFill = new SolidColorBrush(Color.FromArgb(120, 0xE5, 0x39, 0x35));
    static readonly IPen OverlapPen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 2);
    static readonly IPen SelectedPen = new Pen(Brushes.Gold, 2.5);
    static readonly IPen CompactSelectedPen = new Pen(Brushes.Gold, 1.5);
    static readonly IPen DisabledPen = new Pen(new SolidColorBrush(Color.Parse("#9E9E9E")), 1.5, new DashStyle([3, 3], 0));
    static readonly IBrush OpeningFill = new SolidColorBrush(Color.FromArgb(40, 0x66, 0xBB, 0x6A));
    static readonly IPen OpeningPen = new Pen(new SolidColorBrush(Color.Parse("#66BB6A")), 1.5, new DashStyle([4, 3], 0));
    static readonly IBrush PreviewFill = new SolidColorBrush(Color.FromArgb(110, 0x66, 0xBB, 0x6A));
    static readonly IPen PreviewPen = new Pen(new SolidColorBrush(Color.Parse("#A5D6A7")), 2.5);
    static readonly IPen GhostPen = new Pen(Brushes.White, 2, new DashStyle([4, 3], 0));
    static readonly IPen BadPen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 2.5, new DashStyle([4, 3], 0));
    static readonly Typeface Font = new("Inter");

    public static readonly StyledProperty<IReadOnlyList<PatchMapBlock>?> BlocksProperty =
        AvaloniaProperty.Register<PatchMap, IReadOnlyList<PatchMapBlock>?>(nameof(Blocks));

    public static readonly StyledProperty<Guid?> SelectedIdProperty =
        AvaloniaProperty.Register<PatchMap, Guid?>(nameof(SelectedId));

    /// <summary>
    /// Heat-map style: 32 square cells per row with colour only (no numbers or names), sized to fit a narrow column.
    /// Clicking selects; no dragging or placing.
    /// </summary>
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<PatchMap, bool>(nameof(Compact));

    /// <summary>
    /// Addresses to point out (the channels a lighting track picks). While any are set, the compact map dims every
    /// other address, outlines these in gold, and shows ones no fixture covers (raw channels) in teal.
    /// </summary>
    public static readonly StyledProperty<IReadOnlySet<int>?> HighlightedAddressesProperty =
        AvaloniaProperty.Register<PatchMap, IReadOnlySet<int>?>(nameof(HighlightedAddresses));

    /// <summary>Run with the fixture's ID when a block is clicked: for maps inside templates, where events can't be wired.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> FixtureClickCommandProperty =
        AvaloniaProperty.Register<PatchMap, System.Windows.Input.ICommand?>(nameof(FixtureClickCommand));

    /// <summary>Channel count of the fixture being placed; 0 turns placement off.</summary>
    public static readonly StyledProperty<int> FootprintProperty =
        AvaloniaProperty.Register<PatchMap, int>(nameof(Footprint));

    static PatchMap()
    {
        AffectsRender<PatchMap>(BlocksProperty, SelectedIdProperty, FootprintProperty, HighlightedAddressesProperty);
        AffectsMeasure<PatchMap>(BlocksProperty, CompactProperty);
    }

    public PatchMap()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public IReadOnlyList<PatchMapBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public Guid? SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public IReadOnlySet<int>? HighlightedAddresses
    {
        get => GetValue(HighlightedAddressesProperty);
        set => SetValue(HighlightedAddressesProperty, value);
    }

    public System.Windows.Input.ICommand? FixtureClickCommand
    {
        get => GetValue(FixtureClickCommandProperty);
        set => SetValue(FixtureClickCommandProperty, value);
    }

    public int Footprint
    {
        get => GetValue(FootprintProperty);
        set => SetValue(FootprintProperty, value);
    }

    /// <summary>A block was clicked (not dragged).</summary>
    public event Action<Guid>? FixtureClicked;

    /// <summary>A block was dragged to a new start address that fits within 1-512.</summary>
    public event Action<Guid, int>? FixtureMoved;

    /// <summary>A free address was clicked where the footprint fits: place the fixture starting there.</summary>
    public event Action<int>? PlaceRequested;

    /// <summary>Describes the address under the pointer ("12 · Fixture 1 · ch 2 Green"), or null when it leaves.</summary>
    public event Action<string?>? HoverChanged;

    int _columns = 32;
    int? _hoverAddress;
    int _pressedAddress;
    PatchMapBlock? _pressedBlock;
    Point _pressedAt;
    int _grabOffset;
    int? _dragStart;   // non-null while dragging: where the block would start if dropped now

    double CellWidth => Bounds.Width / _columns;

    double CellHeight => Compact ? CellWidth : RowHeight;

    /// <summary>Where the footprint fits, given every enabled fixture except the selected one.</summary>
    IReadOnlyList<AddressRange> Openings() => Footprint <= 0 ? [] :
        new UniverseUsage((Blocks ?? []).Where(b => b.Enabled && b.Id != SelectedId).Select(b => new AddressRange(b.Start, b.End)))
            .OpeningsFor(Footprint);

    bool FitsAt(int start) =>
        Footprint > 0 && Openings().Any(o => o.Start <= start && start + Footprint - 1 <= o.End);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // What's under the pointer may have changed (e.g. a fixture was just placed there); keep the readout honest.
        if ((change.Property == BlocksProperty || change.Property == FootprintProperty) && _hoverAddress is { } hover)
            HoverChanged?.Invoke(Describe(hover));
        // Scroll the newly selected fixture into view, e.g. after picking it in the list.
        if (change.Property == SelectedIdProperty && (Blocks ?? []).FirstOrDefault(b => b.Id == SelectedId) is { } block)
            RaiseEvent(new RequestBringIntoViewEventArgs
            {
                RoutedEvent = RequestBringIntoViewEvent,
                TargetObject = this,
                TargetRect = CellRect(block.Start).Union(CellRect(Math.Min(block.End, Addresses))),
            });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 32 * MinCellWidth : availableSize.Width;
        _columns = ColumnsFor(width);
        int rows = (Addresses + _columns - 1) / _columns;
        return new Size(width, rows * (Compact ? width / _columns : RowHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _columns = ColumnsFor(finalSize.Width);
        return finalSize;
    }

    int ColumnsFor(double width) =>
        Compact ? CompactColumns : ColumnChoices.FirstOrDefault(c => width / c >= MinCellWidth, ColumnChoices[^1]);

    public override void Render(DrawingContext context)
    {
        var blocks = Blocks ?? [];
        var enabled = blocks.Where(b => b.Enabled).ToList();
        if (Compact)
        {
            RenderCompact(context, enabled);
            return;
        }

        for (int address = 1; address <= Addresses; address++)
            context.DrawRectangle(EmptyCell, null, CellRect(address).Deflate(1), 3, 3);

        foreach (var opening in Openings())
            foreach (var segment in Segments(opening.Start, opening.End)) context.DrawRectangle(OpeningFill, OpeningPen, segment.Deflate(2), 4, 4);

        // Disabled fixtures as outlines only: they're in the show but don't occupy addresses.
        foreach (var block in blocks.Where(b => !b.Enabled)) DrawOutline(context, block.Start, block.Count, DisabledPen);
        foreach (var block in enabled.Where(b => b.Id != SelectedId)) DrawBlock(context, block, selected: false);
        foreach (var block in enabled.Where(b => b.Id == SelectedId)) DrawBlock(context, block, selected: true);

        DrawOverlaps(context, enabled);
        for (int address = 1; address <= Addresses; address++) DrawAddressNumber(context, address);

        if (_dragStart is { } start && _pressedBlock is { } dragged)
        {
            bool bad = start + dragged.Count - 1 > Addresses
                       || enabled.Any(b => b.Id != dragged.Id && b.Start <= start + dragged.Count - 1 && start <= b.End);
            DrawOutline(context, start, dragged.Count, bad ? BadPen : GhostPen);
        }
        else if (_hoverAddress is { } hover && Footprint > 0 && OtherBlockAt(hover) is null)
        {
            DrawPlacementPreview(context, hover);
        }
    }

    /// <summary>The heat map: a coloured square per used address, red where fixtures collide, the selected fixture outlined.</summary>
    void RenderCompact(DrawingContext context, List<PatchMapBlock> enabled)
    {
        for (int address = 1; address <= Addresses; address++)
            context.DrawRectangle(EmptyCell, null, CellRect(address).Deflate(0.5));
        foreach (var block in enabled)
        {
            var fill = new SolidColorBrush(Palette[block.ColorIndex % Palette.Length]);
            for (int address = block.Start; address <= Math.Min(block.End, Addresses); address++)
                context.DrawRectangle(fill, null, CellRect(address).Deflate(0.5));
        }
        foreach (var overlap in new UniverseUsage(enabled.Select(b => new AddressRange(b.Start, b.End))).OverlapRanges)
            for (int address = overlap.Start; address <= overlap.End; address++)
                context.DrawRectangle(OverlapPen.Brush, null, CellRect(address).Deflate(0.5));
        foreach (var block in enabled.Where(b => b.Id == SelectedId))
            foreach (var segment in Segments(block.Start, Math.Min(block.End, Addresses)))
                context.DrawRectangle(null, CompactSelectedPen, segment, 1, 1);

        if (HighlightedAddresses is not { Count: > 0 } highlighted) return;
        for (int address = 1; address <= Addresses; address++)
        {
            var cell = CellRect(address);
            if (!highlighted.Contains(address)) context.DrawRectangle(DimFill, null, cell);
            else if (!enabled.Any(b => b.Start <= address && address <= b.End)) context.DrawRectangle(RawHighlightFill, HighlightPen, cell.Deflate(1));
            else context.DrawRectangle(null, HighlightPen, cell.Deflate(1));
        }
    }

    static readonly IBrush DimFill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    static readonly IBrush RawHighlightFill = new SolidColorBrush(Color.Parse("#26A69A"));
    static readonly IPen HighlightPen = new Pen(Brushes.Gold, 1.5);

    void DrawPlacementPreview(DrawingContext context, int start)
    {
        if (FitsAt(start))
        {
            foreach (var segment in Segments(start, start + Footprint - 1)) context.DrawRectangle(PreviewFill, PreviewPen, segment.Deflate(1.5), 4, 4);
        }
        else
        {
            DrawOutline(context, start, Footprint, BadPen);
        }
    }

    void DrawBlock(DrawingContext context, PatchMapBlock block, bool selected)
    {
        var colour = Palette[block.ColorIndex % Palette.Length];
        var fill = new SolidColorBrush(selected ? Brighten(colour) : colour);
        var segments = Segments(block.Start, Math.Min(block.End, Addresses)).ToList();
        foreach (var segment in segments) context.DrawRectangle(fill, selected ? SelectedPen : null, segment.Deflate(1.5), 4, 4);

        // The name goes in the first segment, under the address numbers, clipped to the segment.
        var first = segments[0];
        using (context.PushClip(first.Deflate(3)))
        {
            var name = new FormattedText(block.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, NameText)
            {
                MaxTextWidth = Math.Max(1, first.Width - 8),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(name, new Point(first.X + 5, first.Bottom - name.Height - 3));
        }
    }

    void DrawOverlaps(DrawingContext context, List<PatchMapBlock> enabled)
    {
        foreach (var overlap in new UniverseUsage(enabled.Select(b => new AddressRange(b.Start, b.End))).OverlapRanges)
            for (int address = overlap.Start; address <= overlap.End; address++)
                context.DrawRectangle(OverlapFill, OverlapPen, CellRect(address).Deflate(2), 3, 3);
    }

    /// <summary>An outline over a run of addresses, clipped at 512 so a fixture that wouldn't fit still shows where it runs out.</summary>
    void DrawOutline(DrawingContext context, int start, int count, IPen pen)
    {
        foreach (var segment in Segments(start, Math.Min(start + count - 1, Addresses))) context.DrawRectangle(null, pen, segment.Deflate(2), 4, 4);
    }

    void DrawAddressNumber(DrawingContext context, int address)
    {
        var text = new FormattedText(address.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 9, AddressText);
        var cell = CellRect(address);
        context.DrawText(text, new Point(cell.X + 4, cell.Y + 3));
    }

    /// <summary>The rectangles a run of addresses covers: one per grid row it spans.</summary>
    IEnumerable<Rect> Segments(int start, int end)
    {
        int address = start;
        while (address <= end)
        {
            int row = (address - 1) / _columns;
            int rowEnd = Math.Min(end, (row + 1) * _columns);
            yield return new Rect(CellRect(address).TopLeft, CellRect(rowEnd).BottomRight);
            address = rowEnd + 1;
        }
    }

    Rect CellRect(int address)
    {
        int index = address - 1;
        return new Rect(index % _columns * CellWidth, index / _columns * CellHeight, CellWidth, CellHeight);
    }

    int? AddressAt(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= Bounds.Width) return null;
        int address = (int)(point.Y / CellHeight) * _columns + (int)(point.X / CellWidth) + 1;
        return address is >= 1 and <= Addresses ? address : null;
    }

    /// <summary>
    /// An enabled block other than the selected fixture's. While placing, the selected fixture's own addresses count as
    /// free: a click there places it (a drag still moves it).
    /// </summary>
    PatchMapBlock? OtherBlockAt(int address) =>
        (Blocks ?? []).FirstOrDefault(b => b.Enabled && b.Id != SelectedId && b.Start <= address && address <= b.End);

    /// <summary>The enabled block at an address, preferring the selected fixture where blocks overlap.</summary>
    PatchMapBlock? BlockAt(int address) =>
        (Blocks ?? []).Where(b => b.Enabled && b.Start <= address && address <= b.End)
                      .OrderByDescending(b => b.Id == SelectedId)
                      .FirstOrDefault();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || AddressAt(e.GetPosition(this)) is not { } address) return;

        var block = BlockAt(address);
        if (Compact)
        {
            if (block is not null)
            {
                FixtureClicked?.Invoke(block.Id);
                if (FixtureClickCommand?.CanExecute(block.Id) == true) FixtureClickCommand.Execute(block.Id);
            }
            e.Handled = true;
            return;
        }
        if (block is null)
        {
            // Empty address: place the fixture here if it fits.
            if (Footprint > 0 && FitsAt(address)) PlaceRequested?.Invoke(address);
            e.Handled = true;
            return;
        }

        _pressedBlock = block;
        _pressedAddress = address;
        _pressedAt = e.GetPosition(this);
        _grabOffset = address - _pressedBlock.Start;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var address = AddressAt(point);
        if (address != _hoverAddress)
        {
            _hoverAddress = address;
            HoverChanged?.Invoke(address is { } a ? Describe(a) : null);
            if (Footprint > 0) InvalidateVisual();
        }

        if (_pressedBlock is null || address is null) return;
        var moved = point - _pressedAt;
        if (_dragStart is null && Math.Abs(moved.X) + Math.Abs(moved.Y) < 6) return;   // still a click, not a drag
        int start = Math.Max(1, address.Value - _grabOffset);
        if (start != _dragStart)
        {
            _dragStart = start;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedBlock is not { } block) return;
        if (_dragStart is { } start)
        {
            if (start != block.Start && start + block.Count - 1 <= Addresses) FixtureMoved?.Invoke(block.Id, start);
        }
        else if (block.Id == SelectedId && Footprint > 0 && OtherBlockAt(_pressedAddress) is null && FitsAt(_pressedAddress))
        {
            PlaceRequested?.Invoke(_pressedAddress);   // a click inside an opening, over the fixture's own cells
        }
        else
        {
            FixtureClicked?.Invoke(block.Id);
        }
        _pressedBlock = null;
        _dragStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverAddress = null;
        HoverChanged?.Invoke(null);
        InvalidateVisual();
    }

    string Describe(int address)
    {
        var here = (Blocks ?? []).Where(b => b.Enabled && b.Start <= address && address <= b.End).ToList();
        if (here.Count == 0 || (Footprint > 0 && here.All(b => b.Id == SelectedId)))
        {
            if (Footprint <= 0) return $"{address} · free";
            return FitsAt(address)
                ? $"{address} · free · click to place here ({address}-{address + Footprint - 1})"
                : $"{address} · free, but {Footprint} channels from here would run into another fixture or past 512";
        }
        return string.Join("   and   ", here.Select(b => $"{address} · {b.Name} · ch {address - b.Start + 1} {b.ChannelLabels[address - b.Start]}"))
               + (here.Count > 1 ? "   (overlap)" : "");
    }

    /// <summary>A little brighter (a fifth of the way to white), so the selected block stands out but keeps white text readable.</summary>
    static Color Brighten(Color c) => Color.FromRgb((byte)(c.R + (255 - c.R) / 5), (byte)(c.G + (255 - c.G) / 5), (byte)(c.B + (255 - c.B) / 5));
}
