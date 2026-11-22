using System.Runtime.InteropServices.JavaScript;
using GhostShow.App.Client;

namespace GhostShow.Browser;

/// <summary>What the page knows: where it was loaded from (the show computer), and the token the web remote's page saved when this browser paired (same address, so same storage).</summary>
static partial class BrowserPage
{
    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);

    /// <summary>The show computer's address, as the page was loaded from it.</summary>
    public static string Origin => JSHost.GlobalThis.GetPropertyAsJSObject("location")!.GetPropertyAsString("origin")!;

    public static RemoteEndpoint Endpoint() => new(Origin, () => GetItem("ghostshow.token"),
        "This browser isn't paired yet. Open the address without /app/ first and pair with the PIN, then come back.",
        "This browser isn't paired any more. Open the address without /app/ and pair again.");
}
