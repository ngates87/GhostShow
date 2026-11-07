using Avalonia;
using GhostShow.App;

// Only one copy runs at a time (see SingleInstance): a second start brings the running window to the front instead.
using var instance = SingleInstance.TryClaim(SingleInstance.DefaultName);
if (instance is null)
{
    if (!SingleInstance.AskRunningToShow(SingleInstance.DefaultName, TimeSpan.FromSeconds(3)))
        Console.Error.WriteLine("GhostShow is already running, but didn't answer (it may still be starting).");
    return 0;
}

return AppBuilder.Configure(() => new App { SingleInstance = instance })
    .UsePlatformDetect()
    .WithInterFont()
    .LogToTrace()
    .StartWithClassicDesktopLifetime(args);
