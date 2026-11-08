using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GhostShow.App.Dialogs;
using GhostShow.App.ViewModels;
using GhostShow.Core.Model;

namespace GhostShow.App.Views;

/// <summary>
/// Everything inside the main window: menus, sections and pages, the Buttons panel and the status bar, with the keys
/// that work everywhere (Space, trigger keys, learning a key, leaving a field). The desktop's window hosts it; the plan
/// is for the browser page to host it too (docs/plan-shared-view.md).
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>Keys held down. A held key repeats, but one press is one press: it mustn't start a scare over and over.</summary>
    readonly HashSet<Key> _heldKeys = [];

    /// <summary>Keys held down whose press was an input, so their release is one too.</summary>
    readonly HashSet<Key> _inputKeys = [];

    /// <summary>The window (or page) this view is in. Key presses go to it when no control has focus, so the keys are
    /// listened for there rather than on this view, or a key pressed with nothing focused would never be seen.</summary>
    TopLevel? Host { get; set; }

    Window? Window => Host as Window;

    public MainView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Host = TopLevel.GetTopLevel(this);
        if (Host is null) return;
        // Tunnel, so a focused button doesn't take Space as a click first.
        Host.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Host.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        Host.AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel);
        // Bubble, after the field itself (handled too: a number box takes Enter to commit its value).
        Host.AddHandler(KeyDownEvent, OnKeyDownAfterControls, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Host is { } host)
        {
            host.RemoveHandler(KeyDownEvent, OnKeyDown);
            host.RemoveHandler(KeyUpEvent, OnKeyUp);
            host.RemoveHandler(PointerPressedEvent, OnPointerPressedAnywhere);
            host.RemoveHandler(KeyDownEvent, OnKeyDownAfterControls);
        }
        Host = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The host lost the keyboard (another window is in front): keys let go meanwhile are never reported here.</summary>
    public void HostLostFocus()
    {
        _heldKeys.Clear();
        _inputKeys.Clear();
        if (DataContext is MainViewModel viewModel) viewModel.Buttons.IsAppFocused = false;
    }

    /// <summary>The host has the keyboard again.</summary>
    public void HostGotFocus()
    {
        if (DataContext is MainViewModel viewModel) viewModel.Buttons.IsAppFocused = true;
    }

    /// <summary>
    /// Space plays and pauses on every screen (as on show consoles). Every other key on its own is an input: the input
    /// monitor lists it, and it presses the show button with that key. While typing in a field, keys that type (letters,
    /// digits) go to the field instead, but F keys and the like still press their buttons: a field left with the cursor in
    /// it mustn't silently stop the show's keys. While a button is waiting for a key, the key goes to that instead. Esc
    /// finishes editing the buttons.
    /// </summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool isRepeat = !_heldKeys.Add(e.Key);
        if (DataContext is not MainViewModel { IsShowOpen: true } viewModel) return;
        var buttons = viewModel.Buttons;
        if (buttons.IsLearningKey)
        {
            if (!isRepeat) buttons.LearnKey(e.Key.ToString(), e.KeyModifiers != KeyModifiers.None);
            e.Handled = true;
            return;
        }
        string key = e.Key.ToString();
        if (e.KeyModifiers != KeyModifiers.None || TriggerKeys.IsModifier(key)) return;   // shortcuts aren't inputs
        bool typing = Host?.FocusManager?.GetFocusedElement() is TextBox;
        if (e.Key == Key.Space && !typing)
        {
            if (!isRepeat) viewModel.Playback.PlayPauseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (typing && TriggerKeys.TypesText(key)) return;   // it goes to the field

        if (!isRepeat)
        {
            _inputKeys.Add(e.Key);
            buttons.Input(ButtonTrigger.Keyboard, key, isPressed: true);
        }
        if (buttons.HasKey(key)) e.Handled = true;   // the button's, even while it re-arms
        else if (e.Key == Key.Escape && buttons.IsEditing && !typing)
        {
            buttons.IsEditing = false;   // (while typing, Esc first leaves the field: see OnKeyDownAfterControls)
            e.Handled = true;
        }
        // Otherwise it isn't ours, so an unused key that repeats (the arrows) still repeats.
    }

    /// <summary>The release of a key whose press was an input: an input too (sensors report both).</summary>
    void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _heldKeys.Remove(e.Key);
        if (_inputKeys.Remove(e.Key) && DataContext is MainViewModel viewModel)
            viewModel.Buttons.Input(ButtonTrigger.Keyboard, e.Key.ToString(), isPressed: false);
    }

    /// <summary>
    /// A click into a field, or outside the buttons panel, stops a button waiting for a key: they've moved on to
    /// something else. Clicks on the rest of the panel don't, or Add key couldn't be clicked to stop it itself.
    /// </summary>
    void OnPointerPressedAnywhere(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual clicked) return;
        LeaveFieldUnlessClickedIn(clicked);
        if (DataContext is not MainViewModel { Buttons.IsLearningKey: true } viewModel) return;
        bool inField = clicked is TextBox || clicked.FindAncestorOfType<TextBox>() is not null;
        if (inField || clicked.FindAncestorOfType<ButtonsPanel>() is null) viewModel.Buttons.CancelLearningKey();
    }

    /// <summary>
    /// A click anywhere but the field with the cursor (or the control it belongs to, such as a number box's arrows)
    /// takes the cursor out of it. Otherwise a click on the lanes or a blank area would leave it there, and keys that
    /// type would keep going to the field instead of pressing buttons.
    /// </summary>
    void LeaveFieldUnlessClickedIn(Visual clicked)
    {
        if (Host?.FocusManager?.GetFocusedElement() is not TextBox field) return;
        var owner = field.TemplatedParent as Visual ?? field;
        if (clicked == owner || owner.IsVisualAncestorOf(clicked)) return;
        LeaveField();
    }

    /// <summary>Takes the cursor out of whatever field has it: nothing has keyboard focus, so keys reach the window.</summary>
    void LeaveField() => Host?.FocusManager?.Focus(null);

    /// <summary>Enter or Esc in a field: done with it, so the cursor leaves it (rename boxes do this themselves).</summary>
    void OnKeyDownAfterControls(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || Host?.FocusManager?.GetFocusedElement() is not TextBox) return;
        LeaveField();
        e.Handled = true;
    }

    async void OnAbout(object? sender, RoutedEventArgs e)
    {
        if (Window is { } window) await new AboutDialog().ShowDialog(window);
    }

    RemoteWindow? _remote;

    /// <summary>View → Remote Access… (or the menu bar's indicator): one window, brought to the front if it's open.</summary>
    void OnRemote(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (Window is null) return;   // only a window has other windows
        if (_remote is { } open)
        {
            open.Activate();
            return;
        }
        _remote = new RemoteWindow { DataContext = viewModel.Remote };
        _remote.Closed += (_, _) => _remote = null;
        _remote.Show(Window);
    }
}
