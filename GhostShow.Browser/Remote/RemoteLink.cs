using System.Net.WebSockets;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json.Nodes;
using GhostShow.Core.Editing;

namespace GhostShow.Browser.Remote;

/// <summary>
/// The connection to the show computer (the web remote's <c>/api/live</c>, see GhostShow.Web/RemoteServer.cs), with
/// the token the web remote's page saved when this browser paired (same address, so same storage). Reconnects by
/// itself.
/// </summary>
sealed partial class RemoteLink
{
    ClientWebSocket? _socket;
    int _nextId = 1;

    // One event per kind of message from the show computer (see docs/remote.md for what each holds). They're raised on
    // the connection's thread: handlers post to the UI thread.

    /// <summary>"hello": who this device is, the whole show, the fixture profiles it uses, the audio devices.</summary>
    public event Action<JsonObject>? Hello;

    /// <summary>A change: its description and operations.</summary>
    public event Action<string, IReadOnlyList<EditOp>>? Changed;

    public event Action<JsonObject>? Status;

    /// <summary>"issues": a watched timeline's problems.</summary>
    public event Action<JsonObject>? Issues;

    /// <summary>"patch": the fixtures and universes changed on the show computer (with the profiles they use).</summary>
    public event Action<JsonObject>? Patch;

    /// <summary>"manual": a universe's levels set by hand on the show computer.</summary>
    public event Action<JsonObject>? Manual;

    /// <summary>"audio": a sound file's length and waveform.</summary>
    public event Action<JsonObject>? Audio;

    /// <summary>"audioDevices": the show computer's audio devices changed.</summary>
    public event Action<JsonObject>? AudioDevices;

    /// <summary>Why it isn't connected (shown until the show arrives).</summary>
    public event Action<string>? Problem;

    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);

    static string Host => JSHost.GlobalThis.GetPropertyAsJSObject("location")!.GetPropertyAsString("host")!;
    static bool Secure => JSHost.GlobalThis.GetPropertyAsJSObject("location")!.GetPropertyAsString("protocol") == "https:";

    public async Task RunAsync()
    {
        int retry = 0;
        while (true)
        {
            var token = GetItem("ghostshow.token");
            if (string.IsNullOrEmpty(token))
            {
                Problem?.Invoke("This browser isn't paired yet. Open the address without /app/ first and pair with the PIN, then come back.");
                await Task.Delay(3000);
                continue;
            }
            var socket = _socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri($"{(Secure ? "wss" : "ws")}://{Host}/api/live?token={Uri.EscapeDataString(token)}"), CancellationToken.None);
                retry = 0;
                await ReceiveAsync(socket);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                Problem?.Invoke($"Can't reach the show computer ({e.Message}). Trying again…");
            }
            if (socket.CloseStatus is { } status && (int)status == 4401)
            {
                Problem?.Invoke("This browser isn't paired any more. Open the address without /app/ and pair again.");
            }
            await Task.Delay(Math.Min(5000, 500 << Math.Min(retry++, 4)));
        }
    }

    async Task ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length))!.AsObject();
            message.SetLength(0);
            switch (json["type"]?.GetValue<string>())
            {
                case "hello":
                    Hello?.Invoke(json);
                    break;
                case "change":
                    Changed?.Invoke(json["description"]!.GetValue<string>(), [.. json["ops"]!.AsArray().Select(o => ShowDocument.Decode(o!))]);
                    break;
                case "status":
                    Status?.Invoke(json);
                    break;
                case "issues":
                    Issues?.Invoke(json);
                    break;
                case "patch":
                    Patch?.Invoke(json);
                    break;
                case "manual":
                    Manual?.Invoke(json);
                    break;
                case "audio":
                    Audio?.Invoke(json);
                    break;
                case "audioDevices":
                    AudioDevices?.Invoke(json);
                    break;
            }
        }
    }

    /// <summary>
    /// Sends a command (see docs/remote.md: play, seek, watch, run, …), e.g. <c>Send("seek", ("timeline", id), ("position", 12.5))</c>.
    /// Not waited for: what it did shows in the next status.
    /// </summary>
    public void Send(string action, params (string Name, JsonNode? Value)[] fields)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        var json = new JsonObject { ["type"] = "command", ["id"] = _nextId++, ["action"] = action };
        foreach (var (name, value) in fields) json[name] = value;
        _ = socket.SendAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Out of step: drops the connection, which reconnects and gets the whole show again.</summary>
    public void Resync() => _socket?.Abort();
}
