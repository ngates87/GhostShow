using Avalonia;
using Avalonia.Controls;

namespace GhostShow.App.Views;

public partial class InputMonitorView : UserControl
{
    public static readonly StyledProperty<bool> IsTimelinePanelProperty =
        AvaloniaProperty.Register<InputMonitorView, bool>(nameof(IsTimelinePanel));

    public bool IsTimelinePanel { get => GetValue(IsTimelinePanelProperty); set => SetValue(IsTimelinePanelProperty, value); }

    public InputMonitorView() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsTimelinePanelProperty && IsTimelinePanel) InputRows.Name = "TimelineInputRows";
    }
}
