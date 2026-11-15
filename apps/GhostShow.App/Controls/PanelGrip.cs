using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GhostShow.App.Controls;

/// <summary>
/// The grip on a side panel's inner edge, as in VS Code and Visual Studio: drag it to make the panel wider or narrower
/// (within <see cref="MinPanelWidth"/> and <see cref="MaxPanelWidth"/>), drag it well past the narrowest to fold the
/// panel away, double-click it for the panel's usual width. A thin accent line shows where it is while it's pointed at
/// or dragged. It sets <see cref="PanelWidth"/> and <see cref="IsPanelOpen"/>, which the panel binds to.
/// </summary>
public sealed class PanelGrip : Control
{
    public static readonly StyledProperty<double> PanelWidthProperty =
        AvaloniaProperty.Register<PanelGrip, double>(nameof(PanelWidth), 300, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsPanelOpenProperty =
        AvaloniaProperty.Register<PanelGrip, bool>(nameof(IsPanelOpen), true, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinPanelWidthProperty = AvaloniaProperty.Register<PanelGrip, double>(nameof(MinPanelWidth), 180);
    public static readonly StyledProperty<double> MaxPanelWidthProperty = AvaloniaProperty.Register<PanelGrip, double>(nameof(MaxPanelWidth), 720);
    public static readonly StyledProperty<double> DefaultPanelWidthProperty = AvaloniaProperty.Register<PanelGrip, double>(nameof(DefaultPanelWidth), 300);

    /// <summary>
    /// The name of what the panel takes its room from (the timeline's lanes): the panel won't be widened past the point
    /// where that would be narrower than <see cref="KeepRoom"/>, as VS Code keeps room for its editor.
    /// </summary>
    public static readonly StyledProperty<string?> KeepRoomForProperty = AvaloniaProperty.Register<PanelGrip, string?>(nameof(KeepRoomFor));

    public static readonly StyledProperty<double> KeepRoomProperty = AvaloniaProperty.Register<PanelGrip, double>(nameof(KeepRoom), 420);

    /// <summary>The panel is on the right, the grip on its left edge: dragging left makes it wider.</summary>
    public static readonly StyledProperty<bool> GrowsLeftwardProperty = AvaloniaProperty.Register<PanelGrip, bool>(nameof(GrowsLeftward));

    public static readonly DirectProperty<PanelGrip, bool> IsDraggingProperty =
        AvaloniaProperty.RegisterDirect<PanelGrip, bool>(nameof(IsDragging), g => g.IsDragging);

    public double PanelWidth { get => GetValue(PanelWidthProperty); set => SetValue(PanelWidthProperty, value); }
    public bool IsPanelOpen { get => GetValue(IsPanelOpenProperty); set => SetValue(IsPanelOpenProperty, value); }
    public double MinPanelWidth { get => GetValue(MinPanelWidthProperty); set => SetValue(MinPanelWidthProperty, value); }
    public double MaxPanelWidth { get => GetValue(MaxPanelWidthProperty); set => SetValue(MaxPanelWidthProperty, value); }
    public double DefaultPanelWidth { get => GetValue(DefaultPanelWidthProperty); set => SetValue(DefaultPanelWidthProperty, value); }
    public bool GrowsLeftward { get => GetValue(GrowsLeftwardProperty); set => SetValue(GrowsLeftwardProperty, value); }
    public string? KeepRoomFor { get => GetValue(KeepRoomForProperty); set => SetValue(KeepRoomForProperty, value); }
    public double KeepRoom { get => GetValue(KeepRoomProperty); set => SetValue(KeepRoomProperty, value); }

    /// <summary>Being dragged: the panel follows at once (its slide-out animation is for opening and folding).</summary>
    public bool IsDragging
    {
        get => _isDragging;
        private set => SetAndRaise(IsDraggingProperty, ref _isDragging, value);
    }

    bool _isDragging, _isPointedAt;
    double _startX, _startWidth, _widest;

    static readonly IBrush Line = new SolidColorBrush(Color.Parse("#3B8EEA"));

    static PanelGrip() => AffectsRender<PanelGrip>(IsDraggingProperty);

    public PanelGrip()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Width = 8;
        ToolTip.SetTip(this, "Drag to resize · double-click for the usual width");
        ToolTip.SetShowDelay(this, 800);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // the whole strip takes the pointer, not only the line
        if (_isPointedAt || IsDragging)
            context.FillRectangle(Line, new Rect((Bounds.Width - 2) / 2, 0, 2, Bounds.Height));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointedAt = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointedAt = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            SetCurrentValue(PanelWidthProperty, DefaultPanelWidth);
            return;
        }
        // Measured against the window: the grip moves with the edge it's dragging.
        _startX = e.GetPosition(TopLevel.GetTopLevel(this)).X;
        _startWidth = PanelWidth;
        _widest = MaxPanelWidth;
        if (KeepRoomFor is { } name && TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name) is { IsEffectivelyVisible: true } room)
            _widest = Math.Max(_startWidth, Math.Min(MaxPanelWidth, _startWidth + room.Bounds.Width - KeepRoom));
        IsDragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsDragging) return;
        double moved = e.GetPosition(TopLevel.GetTopLevel(this)).X - _startX;
        double wanted = _startWidth + (GrowsLeftward ? -moved : moved);
        if (wanted < MinPanelWidth / 2)
        {
            // Dragged well past the narrowest: it folds away, keeping the width it had for when it's opened again.
            IsDragging = false;
            e.Pointer.Capture(null);
            SetCurrentValue(PanelWidthProperty, _startWidth);
            SetCurrentValue(IsPanelOpenProperty, false);
            return;
        }
        SetCurrentValue(PanelWidthProperty, Math.Round(Math.Clamp(wanted, MinPanelWidth, Math.Max(MinPanelWidth, _widest))));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsDragging) return;
        IsDragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        IsDragging = false;
    }
}
