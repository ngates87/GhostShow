using Avalonia.Threading;
using GhostShow.Server;

namespace GhostShow.App;

/// <summary>The show's thread in the desktop app: its UI thread, while it hosts the server in-process (docs/plan-remote.md, step 4).</summary>
public sealed class UiShowThread : IShowThread
{
    public static readonly UiShowThread Instance = new();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
