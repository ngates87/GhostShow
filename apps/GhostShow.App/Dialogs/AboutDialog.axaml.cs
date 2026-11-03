using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GhostShow.App.Dialogs;

/// <summary>The logo, name and version of the app.</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        // Drop the "+commit" build metadata the SDK appends to the informational version.
        var version = typeof(AboutDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = $"Version {version?.Split('+')[0] ?? "unknown"}";
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
