using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GhostShow.App.Dialogs;

/// <summary>Save / Don't save / Cancel. Closing the dialog any other way counts as Cancel.</summary>
public partial class SaveChangesDialog : Window
{
    public SaveChangesDialog() => InitializeComponent();

    public SaveChangesDialog(string showName, string action) : this()
    {
        Question.Text = $"Save changes to '{showName}' before {action}?";
    }

    void OnSave(object? sender, RoutedEventArgs e) => Close(SaveChoice.Save);
    void OnDontSave(object? sender, RoutedEventArgs e) => Close(SaveChoice.DontSave);
    void OnCancel(object? sender, RoutedEventArgs e) => Close(SaveChoice.Cancel);
}

/// <summary>Shows <see cref="SaveChangesDialog"/> over the main window.</summary>
public sealed class SaveChangesDialogPrompt(Window owner) : ISaveChangesPrompt
{
    public async Task<SaveChoice> AskAsync(string showName, string action) =>
        await new SaveChangesDialog(showName, action).ShowDialog<SaveChoice?>(owner) ?? SaveChoice.Cancel;
}
