using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace GhostShow.App.Dialogs;

/// <summary>
/// Before the app closes: what's still live (closing stops it), and, with unsaved changes, Save and close / Close
/// without saving / Cancel. While the show is live, Enter means Cancel, so a stray key press doesn't end it. Closing the
/// dialog any other way counts as Cancel.
/// </summary>
public partial class CloseDialog : Window
{
    static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E6C229"));

    public CloseDialog() => InitializeComponent();

    public CloseDialog(CloseWarning warning) : this()
    {
        Question.Text = warning.IsLive ? "The show is still live. Close GhostShow anyway?" : "Close GhostShow?";
        foreach (var line in warning.Lines)
            Live.Children.Add(new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = "⚠", Foreground = WarningBrush, Margin = new(0, 0, 8, 0), [DockPanel.DockProperty] = Dock.Left },
                    new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
                },
            });
        Live.IsVisible = warning.IsLive;
        Consequence.Text = warning.Consequence;
        Unsaved.Text = $"Changes to '{warning.ShowName}' aren't saved.";
        Unsaved.IsVisible = SaveButton.IsVisible = warning.HasUnsavedChanges;
        CloseButton.Content = warning.HasUnsavedChanges ? "Close without saving" : warning.IsLive ? "Close anyway" : "Close";

        var safe = warning.IsLive ? CancelButton : warning.HasUnsavedChanges ? SaveButton : CloseButton;
        safe.IsDefault = true;
        safe.Classes.Add("accent");
    }

    void OnSave(object? sender, RoutedEventArgs e) => Close(SaveChoice.Save);
    void OnClose(object? sender, RoutedEventArgs e) => Close(SaveChoice.DontSave);
    void OnCancel(object? sender, RoutedEventArgs e) => Close(SaveChoice.Cancel);
}
