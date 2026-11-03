using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace GhostShow.App.Views;

public partial class RemoteWindow : Window
{
    public RemoteWindow() => InitializeComponent();

    /// <summary>
    /// A link can't have its text selected, so its menu copies it instead: the address is usually wanted on another
    /// device, not in this computer's browser.
    /// </summary>
    async void CopyAddress(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string address } && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(address);
    }
}
