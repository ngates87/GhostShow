using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace GhostShow.Browser;

/// <summary>The browser build: one view (no windows), connected to the show computer.</summary>
public partial class BrowserApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            var shell = new ShellView();
            single.MainView = shell;
            _ = shell.StartAsync();   // connects, and keeps reconnecting, for as long as the page is open
        }
        base.OnFrameworkInitializationCompleted();
    }
}
