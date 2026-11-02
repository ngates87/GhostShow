using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShow.App.ViewModels;

namespace GhostShow.App.Views;

/// <summary>
/// The Envelope Editor's screen (see <see cref="EnvelopeEditorViewModel"/>): its keyboard shortcuts. Everything else
/// is bound. Keys typed into a field go to the field.
/// </summary>
public partial class EnvelopeEditorView : UserControl
{
    public EnvelopeEditorView() => InitializeComponent();

    // The shortcuts listen on the window (or browser page) around this screen, because a key pressed while nothing
    // has focus never reaches anything inside it. They come before TimelineView's own, which sits around this one
    // in the browser.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnShortcut);
    }

    void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible) return;
        if (DataContext is not EnvelopeEditorViewModel { Editor: var editor } envelopes) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Action? action = (e.Key, ctrl, shift) switch
        {
            (Key.Left, false, _) when editor.SelectedPoints.Count > 0 => () => editor.Nudge(-1, 0, fine: shift),
            (Key.Right, false, _) when editor.SelectedPoints.Count > 0 => () => editor.Nudge(+1, 0, fine: shift),
            (Key.Up, false, _) when editor.SelectedPoints.Count > 0 => () => editor.Nudge(0, +1, fine: shift),
            (Key.Down, false, _) when editor.SelectedPoints.Count > 0 => () => editor.Nudge(0, -1, fine: shift),
            (Key.Z, true, false) => () => editor.UndoCommand.Execute(null),
            (Key.Z, true, true) or (Key.Y, true, _) => () => editor.RedoCommand.Execute(null),
            (Key.A, true, _) => () => envelopes.SelectAllHandlesCommand.Execute(null),
            (Key.Delete or Key.Back, false, _) => () => envelopes.DeleteHandlesCommand.Execute(null),
            (Key.Escape, false, _) => () => editor.SelectPoints([], additive: false),
            (Key.Space, false, _) => () => editor.Playback.PlayPauseCommand.Execute(null),
            _ => null,
        };
        if (action is null) return;
        action();
        e.Handled = true;
    }
}
