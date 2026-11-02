using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShow.App.ViewModels;

namespace GhostShow.App.Views;

/// <summary>
/// Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z for a setup screen. They listen on the window (or browser page) around it, because a
/// key pressed while nothing has focus never reaches anything inside the screen. In a text field they stay the field's.
/// </summary>
static class UndoKeys
{
    /// <param name="screen">Only while this is showing.</param>
    /// <param name="history">The screen's history, once it has its view model.</param>
    public static void Listen(Control screen, Func<ShowHistoryViewModel?> history)
    {
        void OnKey(object? sender, KeyEventArgs e)
        {
            if (!screen.IsEffectivelyVisible || history() is not { } current) return;
            if (TopLevel.GetTopLevel(screen)?.FocusManager?.GetFocusedElement() is TextBox) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (e.Key == Key.Z && !shift) current.UndoCommand.Execute(null);
            else if (e.Key == Key.Y || (e.Key == Key.Z && shift)) current.RedoCommand.Execute(null);
            else return;
            e.Handled = true;
        }

        TopLevel? listeningOn = null;
        screen.AttachedToVisualTree += (_, _) =>
        {
            listeningOn = TopLevel.GetTopLevel(screen);
            listeningOn?.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        };
        screen.DetachedFromVisualTree += (_, _) =>
        {
            listeningOn?.RemoveHandler(InputElement.KeyDownEvent, OnKey);
            listeningOn = null;
        };
    }
}
