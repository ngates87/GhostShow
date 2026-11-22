using System.Net.Sockets;
using GhostShow.Core.Storage;
using GhostShow.Web;
using Microsoft.Extensions.Logging;

namespace GhostShow.Server;

/// <summary>
/// Remote Access on the show computer: the web remote (<see cref="RemoteServer"/>), whether it's on and on which port,
/// and the devices paired with it (<see cref="RemoteSettings"/>, saved with this computer's settings). Off until it's
/// switched on; then it starts with the server until it's switched off. A screen (the desktop's Remote Access window)
/// shows it and works it. See docs/remote.md.
/// </summary>
public sealed class ShowRemote : IAsyncDisposable
{
    readonly RemoteServer _server;
    readonly RemoteSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly ILogger _logger;

    /// <param name="saveSettings">Saves this computer's settings (pairings and the port are in them).</param>
    /// <param name="browserApp">The browser build of the app, served at /app/ (see <see cref="FindBrowserApp"/>); null serves only the simple remote page.</param>
    public ShowRemote(IRemoteHost host, RemoteSettings settings, Func<Task> saveSettings, IShowThread thread, ILogger logger, string? browserApp)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _logger = logger;
        _server = new RemoteServer(host, settings, logger) { BrowserAppFolder = browserApp };
        // Both come from the server's threads.
        _server.Pairing.Changed += () => thread.Post(() =>
        {
            _ = _saveSettings();
            Changed?.Invoke();
        });
        _server.ConnectionsChanged += () => thread.Post(() => Changed?.Invoke());
    }

    public RemoteServer Server => _server;

    /// <summary>Devices paired, connected or gone, or it was switched on or off. On the show's thread.</summary>
    public event Action? Changed;

    /// <summary>Whether devices can connect (the web remote is running).</summary>
    public bool IsOn { get; private set; }

    /// <summary>The port it's on, or starts on.</summary>
    public int Port => _settings.Port;

    /// <summary>At startup: back on if it was left on. Null, or why it couldn't start.</summary>
    public Task<string?> StartIfEnabledAsync() => _settings.Enabled ? SetEnabledAsync(true, _settings.Port) : Task.FromResult<string?>(null);

    /// <summary>
    /// On starts the web remote on this port (1024-65535) and remembers it; off stops it, and disconnects every device.
    /// Null, or why it couldn't start (it stays off).
    /// </summary>
    public async Task<string?> SetEnabledAsync(bool on, int port)
    {
        if (on)
        {
            port = Math.Clamp(port, 1024, 65535);
            try
            {
                await _server.StartAsync(port);
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
            {
                _logger.LogWarning(e, "The web remote couldn't start on port {Port}.", port);
                IsOn = false;
                return $"Couldn't start on port {port}: {Reason(e)} Pick another port and switch it on again.";
            }
            _settings.Port = port;
        }
        else
        {
            await _server.StopAsync();
        }
        _settings.Enabled = on;
        IsOn = on;
        Changed?.Invoke();
        await _saveSettings();
        return null;
    }

    /// <summary>Why it couldn't start, in words. The web server wraps a port in use two deep (IOException, AddressInUseException, SocketException).</summary>
    static string Reason(Exception e)
    {
        for (var cause = e; cause is not null; cause = cause.InnerException)
            if (cause is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } || cause.GetType().Name == "AddressInUseException")
                return "something else is using it.";
        return e.Message;
    }

    /// <summary>A new PIN: devices not paired yet need it; paired ones keep working.</summary>
    public void NewPin() => _server.Pairing.NewPin();

    /// <summary>Unpairs a device, and disconnects it if it's connected.</summary>
    public void Remove(Guid device) => _server.Remove(device);

    public void SetRole(Guid device, RemoteRole role) => _server.Pairing.SetRole(device, role);

    /// <summary>Another show was opened, or the show was renamed (on the show's thread): every device gets it.</summary>
    public void ShowReplaced() => _server.ShowReplaced();

    /// <summary>
    /// The browser build of the app (GhostShow.Browser, published), served at /app/: the folder in
    /// GHOSTSHOW_BROWSER_APP if that's set, the browser-app folder next to the program in an installed copy (see
    /// installer/build.cs), or else the one published in this source tree
    /// (apps/GhostShow.Browser/bin/Release/net10.0-browser/publish/wwwroot), found by looking up from where the program
    /// runs. Null if there's none: then only the simple remote page is served.
    /// </summary>
    public static string? FindBrowserApp()
    {
        if (Environment.GetEnvironmentVariable("GHOSTSHOW_BROWSER_APP") is { Length: > 0 } set) return set;
        var installed = Path.Combine(AppContext.BaseDirectory, "browser-app");
        if (File.Exists(Path.Combine(installed, "index.html"))) return installed;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var published = Path.Combine(folder.FullName, "GhostShow.Browser", "bin", "Release", "net10.0-browser", "publish", "wwwroot");
            if (File.Exists(Path.Combine(published, "index.html"))) return published;
        }
        return null;
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
