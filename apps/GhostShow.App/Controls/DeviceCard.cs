using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace GhostShow.App.Controls;

/// <summary>How a piece of hardware stands, for its status light. Being in the show isn't being connected: each says which.</summary>
public enum DeviceState
{
    /// <summary>Off, or not on any hardware on this computer: grey.</summary>
    Inactive,
    /// <summary>Connected and working (listening, sending): green.</summary>
    Healthy,
    /// <summary>Opening, or waiting to: amber.</summary>
    Connecting,
    /// <summary>Should be working and isn't (unplugged, in use, failed): red.</summary>
    Error,
    /// <summary>Found on this computer and free to use: an empty ring.</summary>
    Available,
    /// <summary>Found on this computer and already used by something: a grey dot, the row dimmed.</summary>
    InUse,
}

/// <summary>
/// One device on the Inputs and DMX setup screens, in one shape for both: an icon for its kind, its name, what it is and
/// where ("Serial Input · COM4 · 9600 baud"), a status light with what it's doing, buttons, and, if it has any, its
/// settings and diagnostics behind an expander (the <see cref="ContentControl.Content"/>). The <c>compact</c> class is
/// the smaller line used for hardware found on this computer. Its look is in Styles.axaml.
/// </summary>
public class DeviceCard : ContentControl
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<DeviceCard, Geometry?>(nameof(Icon));

    /// <summary>The name: a string, or a control (a TextBox, where it can be renamed in place).</summary>
    public static readonly StyledProperty<object?> TitleProperty = AvaloniaProperty.Register<DeviceCard, object?>(nameof(Title));

    /// <summary>What it is and where: "Serial Input · COM4 · 9600 baud".</summary>
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<DeviceCard, string?>(nameof(Kind));

    public static readonly StyledProperty<DeviceState> StateProperty = AvaloniaProperty.Register<DeviceCard, DeviceState>(nameof(State));

    /// <summary>What it's doing, beside the status light: "Listening on COM4 at 9600 baud."</summary>
    public static readonly StyledProperty<string?> StatusTextProperty = AvaloniaProperty.Register<DeviceCard, string?>(nameof(StatusText));

    /// <summary>A quieter line under the status: "Last: “BTN24” 4 s ago".</summary>
    public static readonly StyledProperty<string?> DetailProperty = AvaloniaProperty.Register<DeviceCard, string?>(nameof(Detail));

    /// <summary>Buttons on the right (Monitor, Remove, Add).</summary>
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<DeviceCard, object?>(nameof(Actions));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<DeviceCard, bool>(nameof(IsExpanded), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public object? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public DeviceState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string? StatusText { get => GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }
    public string? Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    Button? _expand;

    public DeviceCard() => ShowState(State);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_expand is not null) _expand.Click -= ToggleExpanded;
        _expand = e.NameScope.Find<Button>("PART_Expand");
        if (_expand is not null) _expand.Click += ToggleExpanded;
    }

    void ToggleExpanded(object? sender, RoutedEventArgs e) => SetCurrentValue(IsExpandedProperty, !IsExpanded);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty) ShowState(change.GetNewValue<DeviceState>());
        else if (change.Property == IsExpandedProperty) PseudoClasses.Set(":expanded", change.GetNewValue<bool>());
        else if (change.Property == TitleProperty || change.Property == ActionsProperty)
        {
            // Like a HeaderedContentControl's header: controls put here are this card's, so they get its DataContext.
            if (change.OldValue is ILogical old) LogicalChildren.Remove(old);
            if (change.NewValue is ILogical added && added.LogicalParent is null) LogicalChildren.Add(added);
        }
    }

    void ShowState(DeviceState state)
    {
        PseudoClasses.Set(":inactive", state == DeviceState.Inactive);
        PseudoClasses.Set(":healthy", state == DeviceState.Healthy);
        PseudoClasses.Set(":connecting", state == DeviceState.Connecting);
        PseudoClasses.Set(":error", state == DeviceState.Error);
        PseudoClasses.Set(":available", state == DeviceState.Available);
        PseudoClasses.Set(":inuse", state == DeviceState.InUse);
    }
}
