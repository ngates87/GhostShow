using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GhostShow.App.Dialogs;
using GhostShow.App.ViewModels;

namespace GhostShow.App;

public partial class MainWindow : Window
{
    bool _outputStopped;
    bool _closing;   // the save prompt is open or shutdown is under way

    /// <summary>Keys held down. A held key repeats, but one press is one press: it mustn't start a scare over and over.</summary>
    readonly HashSet<Key> _heldKeys = [];

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        // Tunnel, so a focused button doesn't take Space as a click first.
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => _heldKeys.Remove(e.Key), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Bubble, after the field itself (handled too: a number box takes Enter to commit its value).
        AddHandler(KeyDownEvent, OnKeyDownAfterControls, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        Deactivated += (_, _) => _heldKeys.Clear();   // a key let go in another window never reports it here
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) viewModel.ExitRequested += Close;
        };
    }

    /// <summary>
    /// Space plays and pauses on every screen (as on show consoles), and a show button's key presses it. While typing
    /// in a field, keys that type (letters, digits) go to the field, but F keys and the like still press their
    /// buttons: a field left with the cursor in it mustn't silently stop the show's keys. While a button is waiting
    /// for a key, the key goes to that instead. Esc finishes editing the buttons.
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
        if (e.KeyModifiers != KeyModifiers.None) return;
        bool typing = FocusManager?.GetFocusedElement() is TextBox;
        string key = e.Key.ToString();
        if (e.Key == Key.Space && !typing)
        {
            if (!isRepeat) viewModel.Playback.PlayPauseCommand.Execute(null);
        }
        else if (buttons.HasKey(key) && !(typing && TriggerKeys.TypesText(key)))
        {
            if (!isRepeat) buttons.PressKey(key);
        }
        else if (e.Key == Key.Escape && buttons.IsEditing && !typing)
        {
            buttons.IsEditing = false;   // (while typing, Esc first leaves the field: see OnKeyDownAfterControls)
        }
        else return;   // not ours, so an unused key that repeats (the arrows) still repeats
        e.Handled = true;
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
        if (inField || clicked.FindAncestorOfType<Views.ButtonsPanel>() is null) viewModel.Buttons.CancelLearningKey();
    }

    /// <summary>
    /// A click anywhere but the field with the cursor (or the control it belongs to, such as a number box's arrows)
    /// takes the cursor out of it. Otherwise a click on the lanes or a blank area would leave it there, and keys that
    /// type would keep going to the field instead of pressing buttons.
    /// </summary>
    void LeaveFieldUnlessClickedIn(Visual clicked)
    {
        if (FocusManager?.GetFocusedElement() is not TextBox field) return;
        var owner = field.TemplatedParent as Visual ?? field;
        if (clicked == owner || owner.IsVisualAncestorOf(clicked)) return;
        LeaveField();
    }

    /// <summary>Takes the cursor out of whatever field has it: nothing has keyboard focus, so keys reach the window.</summary>
    void LeaveField() => FocusManager?.Focus(null);

    /// <summary>Enter or Esc in a field: done with it, so the cursor leaves it (rename boxes do this themselves).</summary>
    void OnKeyDownAfterControls(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || FocusManager?.GetFocusedElement() is not TextBox) return;
        LeaveField();
        e.Handled = true;
    }

    async void OnAbout(object? sender, RoutedEventArgs e) => await new AboutDialog().ShowDialog(this);

    Views.RemoteWindow? _remote;

    /// <summary>View → Remote Access… (or the menu bar's indicator): one window, brought to the front if it's open.</summary>
    void OnRemote(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (_remote is { } open)
        {
            open.Activate();
            return;
        }
        _remote = new Views.RemoteWindow { DataContext = viewModel.Remote };
        _remote.Closed += (_, _) => _remote = null;
        _remote.Show(this);
    }

    /// <summary>
    /// Closing first offers to save unsaved changes (Cancel keeps the window open), then is held until every output
    /// has sent its blackout and released its device, so fixtures don't keep the last look (or a fog machine keep
    /// fogging) after the app is gone.
    /// </summary>
    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_outputStopped || DataContext is not MainViewModel viewModel) return;
        e.Cancel = true;   // decided asynchronously below; Close() is called again once it's safe
        if (_closing) return;   // already asking, or already shutting down
        _closing = true;
        if (!await viewModel.ConfirmCloseAsync())
        {
            _closing = false;
            return;
        }
        IsEnabled = false;
        await viewModel.ShutdownAsync();
        _outputStopped = true;
        Close();
    }
}
