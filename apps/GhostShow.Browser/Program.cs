using Avalonia;
using Avalonia.Browser;
using GhostShow.Browser;

internal sealed partial class Program
{
    static Task Main(string[] args) => BuildAvaloniaApp().WithInterFont().StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<BrowserApp>();
}
