using GhostShow.Core.Timeline;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using GhostShow.Core.Editing;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GhostShow.Web;

/// <summary>A device connected now.</summary>
public sealed record ConnectedDevice(Guid Id, string Name, RemoteRole Role, string Address, DateTimeOffset Since);

/// <summary>
/// The web remote: a small web server, inside the desktop app, that devices on the show's network (an iPad, a laptop,
/// a phone) open in their browser. It serves the web app, pairs devices with the PIN shown on the desktop, and keeps
/// each paired device's copy of the show live over a WebSocket (<c>/api/live</c>):
/// - <c>hello</c>: who the device is, and the whole show (<see cref="ShowDocument.Snapshot"/>), when it connects and
///   whenever another show is opened;
/// - <c>change</c>: every change to the show, as the operations that made it (the same ones the desktop applied);
/// - <c>status</c>: what's playing (and the desktop's warnings), up to 20 times a second while something plays,
///   including where each timeline a device shows ("watch") is;
/// - <c>issues</c>: a watched timeline's problems, whenever it's compiled;
/// - <c>device</c>: the device again, when its role changes on the desktop;
/// - <c>result</c>: the answer to a command the device sent (<c>{ type: "command", id, action, … }</c>): done, or why
///   not. Commands run on the show's thread (<see cref="IShowControl"/>), and a device's role is checked for each.
/// What else devices get from the show computer (the patch, sound files, levels set by hand, audio devices) is in
/// RemoteServer.ShowData.cs, and editing from a device is in RemoteServer.Edits.cs. See docs/remote.md. Nothing listens until <see cref="StartAsync"/>.
/// </summary>
public sealed partial class RemoteServer : IAsyncDisposable
{
    /// <summary>A WebSocket close code the web app reads as "pair again" (the device isn't paired, or was removed).</summary>
    public const int NotPaired = 4401;

    static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(50);

    readonly IRemoteHost _host;
    readonly ILogger _logger;
    readonly Lock _lock = new();
    readonly List<RemoteConnection> _connections = [];
    WebApplication? _app;
    CancellationTokenSource? _stopping;
    Task? _statusLoop;
    Task? _levelsLoop;
    string _status = "";
    long _ticks;
    long _observedDocumentVersion;
    bool _patchBroadcastPending;

    public RemoteServer(IRemoteHost host, RemoteSettings settings, ILogger logger, TimeProvider? clock = null)
    {
        _host = host;
        _logger = logger;
        _observedDocumentVersion = host.Session.Document.Version;
        Pairing = new DevicePairing(settings, clock);
        host.Session.Document.Changed += OnChanged;
        host.Playback.Compiled += OnCompiled;
        SubscribeToShowData();
        // A role changed on the desktop: the device hears of it (and its next command is checked against it).
        Pairing.Changed += () =>
        {
            List<RemoteConnection> connections;
            lock (_lock) connections = [.. _connections];
            foreach (var connection in connections)
                connection.Send(new JsonObject { ["type"] = "device", ["device"] = DeviceJson(connection.Device) }.ToJsonString());
        };
    }

    public DevicePairing Pairing { get; }

    /// <summary>
    /// A folder with the browser build of the app (GhostShow.Browser, published), served at /app/. Null: not served.
    /// See docs/plan-remote.md.
    /// </summary>
    public string? BrowserAppFolder { get; set; }

    public bool IsRunning => _app is not null;

    /// <summary>The port it listens on (once started).</summary>
    public int Port { get; private set; }

    /// <summary>Where devices can reach it: this computer's addresses on its networks.</summary>
    public IReadOnlyList<string> Addresses => IsRunning ? LanAddresses().Select(a => $"http://{a}:{Port}/").ToList() : [];

    /// <summary>A device connected or went.</summary>
    public event Action? ConnectionsChanged;

    public IReadOnlyList<ConnectedDevice> Connected
    {
        get
        {
            lock (_lock) return [.. _connections.Select(c => new ConnectedDevice(c.Device.Id, c.Device.Name, c.Device.Role, c.Address, c.Connected))];
        }
    }

    /// <summary>Starts listening on every network of this computer.</summary>
    /// <param name="port">The port; 0 picks a free one (tests).</param>
    public async Task StartAsync(int port)
    {
        if (_app is not null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = typeof(RemoteServer).Assembly.GetName().Name });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
        app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)PairAsync);   // typed: so its result is the response
        app.Map("/api/live", LiveAsync);
        app.MapGet("/api/library", ServeLibraryAsync);
        app.MapGet("/api/{**rest}", () => Results.NotFound());
        app.MapGet("/app/{**path}", ServeBrowserApp);
        app.MapGet("/{**path}", ServeFile);

        await app.StartAsync();
        _app = app;
        Port = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => new Uri(a.Replace("+", "localhost").Replace("[::]", "localhost")).Port).First();
        _stopping = new CancellationTokenSource();
        _statusLoop = Task.Run(() => StatusLoopAsync(_stopping.Token));
        _levelsLoop = Task.Run(() => LevelsLoopAsync(_stopping.Token));
        _logger.LogInformation("Web remote listening on port {Port}{Built}.", Port, WebAssets.IsBuilt ? "" : " (its page isn't built: Node.js wasn't found when GhostShow was built)");
    }

    public async Task StopAsync()
    {
        if (_app is not { } app) return;
        _app = null;
        // Each connection told why first: cancelling first would close them without a reason.
        List<RemoteConnection> connections;
        lock (_lock) connections = [.. _connections];
        foreach (var connection in connections) connection.Close(WebSocketCloseStatus.EndpointUnavailable, "The show computer stopped the web remote.");
        await _stopping!.CancelAsync();
        try
        {
            await _statusLoop!;
        }
        catch (OperationCanceledException)
        {
        }
        try
        {
            await _levelsLoop!;
        }
        catch (OperationCanceledException)
        {
        }
        await app.StopAsync();
        await app.DisposeAsync();
        _logger.LogInformation("Web remote stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _host.Session.Document.Changed -= OnChanged;
        _host.Playback.Compiled -= OnCompiled;
        UnsubscribeFromShowData();
    }

    /// <summary>Another show was opened (call on the show's thread): every device gets the new one.</summary>
    public void ShowReplaced()
    {
        List<RemoteConnection> connections;
        lock (_lock) connections = [.. _connections];
        foreach (var connection in connections) connection.Send(Hello(connection.Device));
    }

    /// <summary>Unpairs a device: it's disconnected and has to pair again.</summary>
    public void Remove(Guid deviceId)
    {
        Pairing.Remove(deviceId);
        List<RemoteConnection> connections;
        lock (_lock) connections = _connections.Where(c => c.Device.Id == deviceId).ToList();
        foreach (var connection in connections) connection.Close((WebSocketCloseStatus)NotPaired, "This device was removed on the show computer.");
    }

    // ---- requests ----

    async Task<IResult> PairAsync(HttpContext context)
    {
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(context.Request.Body);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest();
        }
        string pin = body?["pin"]?.GetValue<string>() ?? "", name = body?["name"]?.GetValue<string>() ?? "";
        var role = body?["role"]?.GetValue<string>() == "operator" ? RemoteRole.Operator : RemoteRole.Editor;
        var result = Pairing.Pair(pin, name, role);
        if (result.Device is not { } device)
        {
            _logger.LogWarning("A device at {Address} gave a wrong PIN.", context.Connection.RemoteIpAddress);
            return Results.Json(new JsonObject { ["error"] = result.Problem }, statusCode: result.TooManyTries ? 429 : 403);
        }
        _logger.LogInformation("Paired {Device} ({Role}) at {Address}.", device.Name, device.Role, context.Connection.RemoteIpAddress);
        return Results.Json(new JsonObject { ["token"] = result.Token, ["device"] = DeviceJson(device) });
    }

    async Task LiveAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (Pairing.Find(context.Request.Query["token"]) is not { } device || _stopping is not { } stopping)
        {
            // Accepted, then closed with a code: a browser can't see why a refused upgrade failed, but it sees this.
            await socket.CloseOutputAsync((WebSocketCloseStatus)NotPaired, "Not paired with this show computer.", context.RequestAborted);
            return;
        }
        Pairing.Seen(device);
        var ip = context.Connection.RemoteIpAddress;
        var connection = new RemoteConnection(device, socket, (ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip)?.ToString() ?? "?", ReceivedAsync);
        // On the show's thread: the show as it is now, and from then on every change, in order.
        await _host.InvokeAsync(() =>
        {
            lock (_lock) _connections.Add(connection);
            connection.Send(Hello(device));
            SendManualLevels(connection);
            return true;
        });
        if (_status.Length > 0) connection.SetStatus(_status);
        ConnectionsChanged?.Invoke();
        try
        {
            await connection.RunAsync(stopping.Token);
        }
        finally
        {
            lock (_lock) _connections.Remove(connection);
            ForgetLightingWatch(connection);
            ConnectionsChanged?.Invoke();
        }
    }

    static readonly Dictionary<string, string> BrowserTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json", [".wasm"] = "application/wasm", [".dat"] = "application/octet-stream",
        [".ico"] = "image/x-icon", [".png"] = "image/png", [".svg"] = "image/svg+xml",
    };

    /// <summary>The browser build's files, Brotli-compressed when the browser takes it and a .br is there.</summary>
    async Task ServeBrowserApp(HttpContext context, string? path)
    {
        if (BrowserAppFolder is not { } root)
        {
            context.Response.StatusCode = 404;
            return;
        }
        if (string.IsNullOrEmpty(path) && context.Request.Path.Value?.EndsWith('/') != true)
        {
            context.Response.Redirect("/app/");   // the app's files are relative to /app/
            return;
        }
        path = string.IsNullOrEmpty(path) ? "index.html" : path;
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal) || !File.Exists(full) || !BrowserTypes.TryGetValue(Path.GetExtension(full), out var type))
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = type;
        context.Response.Headers.CacheControl = path.StartsWith("_framework/", StringComparison.Ordinal) && path != "_framework/blazor.boot.json" ? "public, max-age=31536000, immutable" : "no-cache";
        // Compressed, if the publish made it and the browser takes it. On a plain http:// address (a show computer on the
        // local network) browsers only ask for gzip: Brotli is for secure connections. Uncompressed, the app is 50 MB.
        context.Response.Headers.Vary = "Accept-Encoding";
        string accepted = context.Request.Headers.AcceptEncoding.ToString();
        foreach (var (encoding, extension) in new[] { ("br", ".br"), ("gzip", ".gz") })
            if (accepted.Contains(encoding, StringComparison.Ordinal) && File.Exists(full + extension))
            {
                context.Response.Headers.ContentEncoding = encoding;
                full += extension;
                break;
            }
        await context.Response.SendFileAsync(full);
    }

    static async Task ServeFile(HttpContext context, string? path)
    {
        if (!WebAssets.IsBuilt)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(WebAssets.NotBuiltPage);
            return;
        }
        if (WebAssets.Open(path ?? "") is not { } file)
        {
            context.Response.StatusCode = 404;
            return;
        }
        await using var content = file.Content;
        context.Response.ContentType = file.Type;
        context.Response.Headers.CacheControl = file.Immutable ? "public, max-age=31536000, immutable" : "no-cache";
        await content.CopyToAsync(context.Response.Body);
    }

    // ---- commands ----

    /// <summary>What only an editor may do; an operator may run the show (play, stop, buttons, Blackout) but not change it.</summary>
    static readonly HashSet<string> EditorOnly = ["open", "edit", "undo", "redo", "save", "listShows", "newShow", "openShow", "saveShowAs", "renameShow", "saveProfile", "deleteProfile", "downloadLibrary",
                                                  "scanOutputs", "assignOutput", "setChannel", "clearUniverse", "setFixtureChannel", "setFixtureControl"];

    /// <summary>A message from a device: a command, run on the show's thread, answered with a result.</summary>
    async Task ReceivedAsync(RemoteConnection connection, string json)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        if (message?["type"]?.GetValue<string>() != "command") return;
        var device = connection.Device;
        string action = message["action"]?.GetValue<string>() ?? "";
        string? error;
        if (EditorOnly.Contains(action) && device.Role != RemoteRole.Editor)
            error = "This device is an operator: it can run the show but not change it. An editor can, or change its role on the show computer.";
        else
        {
            try
            {
                error = action switch
                {
                    "save" => await await _host.InvokeAsync(_host.Control.SaveAsync),   // saving takes a moment: the show's thread isn't held up
                    "listShows" => await ListShows(connection),
                    "newShow" => await await _host.InvokeAsync(() => _host.Control.NewShowAsync(Discard(message))),
                    "openShow" => await await _host.InvokeAsync(() => _host.Control.OpenShowAsync(ShowName(message), Discard(message))),
                    "saveShowAs" => await await _host.InvokeAsync(() => _host.Control.SaveShowAsAsync(ShowName(message))),
                    "renameShow" => await await _host.InvokeAsync(() => _host.Control.RenameShowAsync(ShowName(message))),
                    "saveProfile" or "deleteProfile" or "downloadLibrary" => await await _host.InvokeAsync(() => LibraryCommandAsync(message, action)),
                    _ when LightingActions.Contains(action) => await _host.InvokeAsync(() => LightingCommand(connection, action, message)),
                    _ => await _host.InvokeAsync(() => Execute(connection, action, message)),
                };
            }
            catch (EditException e)
            {
                error = e.Message;
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or InvalidDataException or System.Text.Json.JsonException)
            {
                error = "The show computer didn't understand that.";
            }
        }
        if (error is null && !QuietActions.Contains(action)) _logger.LogInformation("{Device}: {Action}.", device.Name, action);
        connection.Send(new JsonObject
        {
            ["type"] = "result",
            ["id"] = message["id"]?.DeepClone(),
            ["ok"] = error is null,
            ["error"] = error,
        }.ToJsonString());
    }

    static string ShowName(JsonObject message) => message["name"]?.GetValue<string>() ?? throw new FormatException("No name.");

    static bool Discard(JsonObject message) => message["discardUnsaved"]?.GetValue<bool>() == true;

    /// <summary>The "listShows" command: sends the device the shows it can open.</summary>
    async Task<string?> ListShows(RemoteConnection connection)
    {
        var names = await _host.InvokeAsync(_host.Control.SavedShows);
        connection.Send(new JsonObject
        {
            ["type"] = "shows",
            ["shows"] = new JsonArray([.. names.Select(name => JsonValue.Create(name))]),
        }.ToJsonString());
        return null;
    }

    string? Execute(RemoteConnection connection, string action, JsonObject message)
    {
        var control = _host.Control;
        Guid Id(string name) => Guid.Parse(message[name]?.GetValue<string>() ?? throw new FormatException($"No {name}."));
        var position = TimeSpan.FromSeconds(Math.Max(0, message["position"]?.GetValue<double>() ?? 0));

        // The transport, for a named timeline (the full app on a laptop shows its own); without one, for the
        // timeline open in the desktop's editor (the simple remote page).
        if (message["timeline"] is not null && action is "play" or "pause" or "stop" or "restart" or "seek" or "cue")
        {
            var playback = _host.Playback;
            var timeline = Id("timeline");
            switch (action)
            {
                case "play": playback.Play(timeline); break;
                case "pause": playback.Pause(timeline); break;
                case "stop": playback.StopPlayer(timeline); break;
                case "restart": playback.Restart(timeline); break;
                case "seek": playback.Seek(timeline, position); break;
                case "cue": playback.Cue(timeline, position); break;
            }
            return null;
        }
        return action switch
        {
            "play" => control.Transport(TransportAction.Play, null),
            "pause" => control.Transport(TransportAction.Pause, null),
            "stop" => control.Transport(TransportAction.Stop, null),
            "restart" => control.Transport(TransportAction.Restart, null),
            "seek" => control.Transport(TransportAction.Seek, position),
            "watch" => Watch(connection, Id("timeline")),
            "edit" => ApplyEdit(connection, message),
            "undo" => UndoOrRedo(connection, message, undo: true),
            "redo" => UndoOrRedo(connection, message, undo: false),
            "run" => Run(Id("timeline"), playOnceOnly: message["playOnceOnly"]?.GetValue<bool>() == true),
            "stopTimeline" => control.StopTimeline(Id("timeline")),
            "stopRunning" => control.StopRunning(Id("running")),
            "stopAll" => control.StopAll(),
            "blackout" => control.Blackout(),
            "press" => control.PressButton(Id("button")),
            "open" => control.Open(Id("timeline")),
            "audio" => SendAudioFile(connection, message["file"]?.GetValue<string>() ?? throw new FormatException("No file.")),
            "testSound" => control.TestSound(message["device"]?.GetValue<string>(), Enum.Parse<AudioSide>(message["side"]?.GetValue<string>() ?? nameof(AudioSide.Stereo))),
            _ => "This version of GhostShow doesn't know that command.",
        };
    }

    /// <summary>
    /// Runs a timeline from its start, as a show button does: another copy if it's running, or (play once only)
    /// nothing while it runs.
    /// </summary>
    string? Run(Guid timeline, bool playOnceOnly)
    {
        if (!playOnceOnly) return _host.Control.Run(timeline);
        if (_host.Session.Project.Timelines.All(t => t.Id != timeline)) return "That timeline isn't in the show any more.";
        _host.Playback.Launch(timeline, playOnceOnly: true);   // false (not started) is fine: it's already running
        return null;
    }

    /// <summary>A device shows this timeline: it's loaded (so it can play), and the device gets its problems and status.</summary>
    string? Watch(RemoteConnection connection, Guid timeline)
    {
        connection.Watching = timeline;
        _host.Playback.Load(timeline);   // raises Compiled, which sends the device its problems
        SendHistory(connection);
        return null;
    }

    /// <summary>On the show's thread: a timeline was compiled; devices showing it get its problems.</summary>
    void OnCompiled(Guid timeline)
    {
        List<RemoteConnection> watching;
        lock (_lock) watching = _connections.Where(c => c.Watching == timeline).ToList();
        if (watching.Count == 0) return;
        var message = new JsonObject
        {
            ["type"] = "issues",
            ["timeline"] = timeline.ToString(),
            ["issues"] = new JsonArray([.. _host.Playback.IssuesOf(timeline).Select(i => new JsonObject
            {
                ["severity"] = i.Severity.ToString().ToLowerInvariant(),
                ["event"] = i.EventId?.ToString(),
                ["message"] = i.Message,
            })]),
        }.ToJsonString();
        foreach (var connection in watching) connection.Send(message);
    }

    // ---- what devices are sent ----

    /// <summary>On the show's thread: a change, to every device, as the operations that made it.</summary>
    void OnChanged(ShowChange change)
    {
        _observedDocumentVersion = change.Version;
        List<RemoteConnection> connections;
        lock (_lock)
        {
            if (_connections.Count == 0)
            {
                _patchBroadcastPending = false;   // a reconnecting device gets a fresh snapshot
                return;
            }
            connections = [.. _connections];
        }
        var message = new JsonObject
        {
            ["type"] = "change",
            ["version"] = change.Version,
            ["kind"] = change.Kind.ToString().ToLowerInvariant(),
            ["description"] = change.Description,
            ["origin"] = change.Origin,
            ["ops"] = new JsonArray([.. change.Ops.Select(_host.Session.Document.Encode)]),
        }.ToJsonString();
        foreach (var connection in connections)
        {
            if (connection != _applyingEditFrom) connection.Send(message);   // (the device that made it already has it)
            SendHistory(connection);
        }
        // A patch edit can synchronously re-apply the session before this handler runs. Preserve stream order:
        // clients first apply the edit operations, then receive the recalculated patch.
        if (_patchBroadcastPending)
        {
            _patchBroadcastPending = false;
            BroadcastPatch();
        }
    }

    /// <summary>On the show's thread: who the device is, and the whole show.</summary>
    string Hello(PairedDevice device)
    {
        var snapshot = _host.Session.Document.Snapshot();
        return new JsonObject
        {
            ["type"] = "hello",
            ["device"] = DeviceJson(device),
            ["version"] = snapshot["version"]!.DeepClone(),
            ["show"] = snapshot["show"]!.DeepClone(),
            ["profiles"] = ProfilesJson(),
            ["library"] = LibraryJson(),
            ["audioDevices"] = AudioDevicesJson(),
            ["fullApp"] = BrowserAppFolder is not null,   // the full app is served at /app/
        }.ToJsonString();
    }

    static JsonObject DeviceJson(PairedDevice device) => new()
    {
        ["id"] = device.Id.ToString(),
        ["name"] = device.Name,
        ["role"] = device.Role == RemoteRole.Operator ? "operator" : "editor",
    };

    async Task StatusLoopAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(StatusInterval);
        while (await timer.WaitForNextTickAsync(stopping))
        {
            string status;
            try
            {
                status = Status().ToJsonString();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Couldn't read the playback status for the web remote.");
                continue;
            }
            if (++_ticks % 4 == 0) SendManualLevelsIfChanged();   // 5 times a second
            if (status == _status) continue;
            _status = status;
            List<RemoteConnection> connections;
            lock (_lock) connections = [.. _connections];
            foreach (var connection in connections) connection.SetStatus(status);
        }
    }

    /// <summary>What's playing: the editor's timeline (the transport's), and every running timeline and copy.</summary>
    JsonObject Status()
    {
        var playback = _host.Playback;
        JsonObject Of(PlaybackStatus s) => new()
        {
            ["state"] = s.State.ToString().ToLowerInvariant(),
            ["position"] = Math.Round(s.Position.TotalSeconds, 2),
            ["length"] = Math.Round(s.Length.TotalSeconds, 2),
            ["loop"] = s.Loop,
            ["atEnd"] = s.AtEnd,
        };
        JsonObject? focused = null;
        if (_host.FocusedTimeline is { } id)
        {
            focused = Of(playback.StatusOf(id));
            focused["timeline"] = id.ToString();
        }
        var running = new JsonArray();
        foreach (var r in playback.Running.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var node = Of(r.Status);
            node["id"] = r.Id.ToString();
            node["timeline"] = r.TimelineId.ToString();
            node["name"] = r.Name;
            node["copy"] = r.Id != r.TimelineId;
            node["priority"] = r.Priority;
            running.Add(node);
        }
        // Where each timeline a device shows is (stopped ones too: their length).
        List<Guid> watched;
        lock (_lock) watched = _connections.Select(c => c.Watching).OfType<Guid>().Distinct().ToList();
        var timelines = new JsonArray();
        foreach (var timeline in watched)
        {
            var node = Of(playback.StatusOf(timeline));
            node["timeline"] = timeline.ToString();
            timelines.Add(node);
        }
        return new JsonObject
        {
            ["type"] = "status",
            ["focused"] = focused,
            ["running"] = running,
            ["timelines"] = timelines,
            ["unsaved"] = _host.HasUnsavedChanges,
            ["needsFirstSave"] = _host.NeedsFirstSave,
            ["warnings"] = new JsonArray([.. _host.Warnings.Select(w => JsonValue.Create(w))]),
        };
    }

    /// <summary>This computer's IPv4 addresses on networks that are up (not loopback or self-assigned).</summary>
    static IEnumerable<IPAddress> LanAddresses()
    {
        IEnumerable<IPAddress> found;
        try
        {
            found = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            found = [];
        }
        return found.Distinct();
    }
}
