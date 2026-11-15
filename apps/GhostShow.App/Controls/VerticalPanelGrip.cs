using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GhostShow.App.Controls;

/// <summary>Horizontal divider dragged vertically to resize a panel below it.</summary>
public sealed class VerticalPanelGrip : Control
{
    public static readonly StyledProperty<double> PanelHeightProperty = AvaloniaProperty.Register<VerticalPanelGrip, double>(nameof(PanelHeight), 180, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinPanelHeightProperty = AvaloniaProperty.Register<VerticalPanelGrip, double>(nameof(MinPanelHeight), 100);
    public static readonly StyledProperty<double> MaxPanelHeightProperty = AvaloniaProperty.Register<VerticalPanelGrip, double>(nameof(MaxPanelHeight), 480);
    public static readonly StyledProperty<double> DefaultPanelHeightProperty = AvaloniaProperty.Register<VerticalPanelGrip, double>(nameof(DefaultPanelHeight), 180);
    public static readonly StyledProperty<double> KeepRoomProperty = AvaloniaProperty.Register<VerticalPanelGrip, double>(nameof(KeepRoom), 220);
    public static readonly DirectProperty<VerticalPanelGrip, bool> IsDraggingProperty = AvaloniaProperty.RegisterDirect<VerticalPanelGrip, bool>(nameof(IsDragging), g => g._dragging);

    static readonly IBrush Line = new SolidColorBrush(Color.Parse("#3B8EEA"));
    bool _dragging, _pointed;
    double _startY, _startHeight;

    public double PanelHeight { get => GetValue(PanelHeightProperty); set => SetValue(PanelHeightProperty, value); }
    public double MinPanelHeight { get => GetValue(MinPanelHeightProperty); set => SetValue(MinPanelHeightProperty, value); }
    public double MaxPanelHeight { get => GetValue(MaxPanelHeightProperty); set => SetValue(MaxPanelHeightProperty, value); }
    public double DefaultPanelHeight { get => GetValue(DefaultPanelHeightProperty); set => SetValue(DefaultPanelHeightProperty, value); }
    public double KeepRoom { get => GetValue(KeepRoomProperty); set => SetValue(KeepRoomProperty, value); }
    public bool IsDragging { get => _dragging; private set => SetAndRaise(IsDraggingProperty, ref _dragging, value); }

    static VerticalPanelGrip() => AffectsRender<VerticalPanelGrip>(IsDraggingProperty);

    public VerticalPanelGrip()
    {
        Height = 8;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        ToolTip.SetTip(this, "Drag to resize · double-click for the usual height");
        ToolTip.SetShowDelay(this, 800);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_pointed || IsDragging) context.FillRectangle(Line, new Rect(0, (Bounds.Height - 2) / 2, Bounds.Width, 2));
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); _pointed = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _pointed = false; InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { SetCurrentValue(PanelHeightProperty, DefaultPanelHeight); return; }
        var top = TopLevel.GetTopLevel(this);
        _startY = e.GetPosition(top).Y;
        _startHeight = PanelHeight;
        IsDragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsDragging) return;
        double movedUp = _startY - e.GetPosition(TopLevel.GetTopLevel(this)).Y;
        double available = (this.GetVisualParent() as Control)?.Bounds.Height ?? double.MaxValue;
        double max = Math.Min(MaxPanelHeight, Math.Max(MinPanelHeight, available - KeepRoom));
        SetCurrentValue(PanelHeightProperty, Math.Round(Math.Clamp(_startHeight + movedUp, MinPanelHeight, max)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsDragging) return;
        IsDragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); IsDragging = false; }
}
