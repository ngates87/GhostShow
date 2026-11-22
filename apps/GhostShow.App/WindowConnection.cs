using GhostShow.App.Client;
using GhostShow.Server;

namespace GhostShow.App;

/// <summary>
/// The show computer's own window, as a device of its server (docs/plan-remote.md, step 5): connected in memory, with
/// Remote Access on or off, and the fixture library taken straight from the server. Not in the browser build.
/// </summary>
static class WindowConnection
{
    public static RemoteEndpoint To(ShowRemote remote) => new(
        "http://this-computer", () => "this computer",
        "Not connected to the show.", "Not connected to the show.",
        Connect: stop => Task.FromResult(remote.ConnectWindow(stop)),
        FetchLibrary: remote.Server.LibraryLinesAsync);
}
