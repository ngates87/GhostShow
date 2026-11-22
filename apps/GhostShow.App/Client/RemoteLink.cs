using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GhostShow.Core.Editing;

namespace GhostShow.App.Client;

/// <summary>Where the show computer is, and how this device proves it's paired with it.</summary>
/// <param name="Origin">The web remote's address, e.g. http://192.168.1.20:7700 (no trailing slash).</param>
/// <param name="Token">The token this device got when it paired; null if it hasn't paired.</param>
/// <param name="NotPaired">What to say while it isn't paired.</param>
/// <param name="NoLongerPaired">What to say when the show computer says it isn't paired any more.</param>
sealed record RemoteEndpoint(string Origin, Func<string?> Token, string NotPaired, string NoLongerPaired);

/// <summary>
/// The connection to the show computer (the web remote's <c>/api/live</c>, see libs/GhostShow.Web/RemoteServer.cs), as
/// a paired device (<see cref="RemoteEndpoint"/>: in a browser, the token the web remote's page saved when it paired).
/// Reconnects by itself.
/// </summary>
sealed class RemoteLink(RemoteEndpoint endpoint)
{
    ClientWebSocket? _socket;
    Channel<string>? _outgoing;   // what's waiting to be sent; there's one per connection
    int _nextId;
    bool _resyncing;   // the connection is being dropped on purpose (see Resync): not worth telling the user

    // One event per kind of message from the show computer (see docs/remote.md for what each holds). They're raised on
    // the connection's thread: handlers post to the UI thread.

    /// <summary>"hello": who this device is, the whole show, the fixture profiles it uses, the audio devices.</summary>
    public event Action<JsonObject>? Hello;

    /// <summary>A change: its description and operations.</summary>
    public event Action<string, IReadOnlyList<EditOp>>? Changed;

    /// <summary>Commands someone is waiting on (see <see cref="RequestAsync"/>), by number. Locked: answers arrive on the connection's thread.</summary>
    readonly Dictionary<int, TaskCompletionSource<string?>> _waiting = [];

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

    /// <summary>"shows": the shows that can be opened, answering "listShows".</summary>
    public event Action<JsonObject>? Shows;

    /// <summary>"result": the answer to a command.</summary>
    public event Action<JsonObject>? Result;

    /// <summary>"history": what Undo and Redo would do next.</summary>
    public event Action<JsonObject>? History;

    /// <summary>"library": what the fixture library says about itself, and its version.</summary>
    public event Action<JsonObject>? Library;

    /// <summary>"outputs": what the show computer's DMX hardware looks like now.</summary>
    public event Action<JsonObject>? Outputs;

    /// <summary>"levels": the levels of the universe this device asked for.</summary>
    public event Action<JsonObject>? Levels;

    /// <summary>"device": this device again, when its role was changed on the show computer.</summary>
    public event Action<JsonObject>? Device;

    /// <summary>"triggers": how the show computer's triggers stand (they run only there).</summary>
    public event Action<JsonObject>? Triggers;

    /// <summary>"diagnostics": how the show computer is running, for the Diagnostics screen.</summary>
    public event Action<JsonObject>? Diagnostics;

    /// <summary>"audioSetup": the show computer's sound, for the Audio screen.</summary>
    public event Action<JsonObject>? AudioSetup;

    /// <summary>"inputs": the show computer's serial ports, and which one each input device is on.</summary>
    public event Action<JsonObject>? Inputs;

    /// <summary>"input": an input that arrived on the show computer (for the input monitor, or a trigger being learned).</summary>
    public event Action<JsonObject>? Input;

    /// <summary>The connection dropped (it's being tried again).</summary>
    public event Action? Disconnected;

    /// <summary>Why it isn't connected (shown until the show arrives).</summary>
    public event Action<string>? Problem;

    /// <summary>The token this device got when it paired; null if it hasn't.</summary>
    public string? Token => endpoint.Token();

    /// <summary>The show computer's address (the web remote's), e.g. http://192.168.1.20:7700.</summary>
    public string Origin => endpoint.Origin;

    /// <summary>The connection's address: the origin's, as a WebSocket.</summary>
    string LiveAddress(string token) =>
        (Origin.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "wss:" + Origin[6..] : "ws:" + Origin[(Origin.IndexOf(':') + 1)..])
        + $"/api/live?token={Uri.EscapeDataString(token)}";

    /// <summary>Connects, and connects again whenever the connection drops, until <paramref name="stop"/> (the page or window closed).</summary>
    public async Task RunAsync(CancellationToken stop = default)
    {
        using var stopping = stop.Register(() => _socket?.Abort());
        int retry = 0;
        while (!stop.IsCancellationRequested)
        {
            var token = Token;
            if (string.IsNullOrEmpty(token))
            {
                Problem?.Invoke(endpoint.NotPaired);
                if (!await Wait(TimeSpan.FromSeconds(3), stop)) return;
                continue;
            }
            var socket = _socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri(LiveAddress(token)), stop);
                retry = 0;
                var outgoing = _outgoing = Channel.CreateUnbounded<string>();
                _ = SendAsync(socket, outgoing);
                await ReceiveAsync(socket);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!stop.IsCancellationRequested) Problem?.Invoke($"Can't reach the show computer ({e.Message}). Trying again…");
            }
            _outgoing?.Writer.TryComplete();
            _outgoing = null;
            List<TaskCompletionSource<string?>> unanswered;
            lock (_waiting)
            {
                unanswered = [.. _waiting.Values];
                _waiting.Clear();
            }
            foreach (var answer in unanswered) answer.SetResult("Lost the connection to the show computer.");
            if (stop.IsCancellationRequested) return;
            if (!_resyncing) Disconnected?.Invoke();
            _resyncing = false;
            if (socket.CloseStatus is { } status && (int)status == 4401)
            {
                Problem?.Invoke(endpoint.NoLongerPaired);
            }
            if (!await Wait(TimeSpan.FromMilliseconds(Math.Min(5000, 500 << Math.Min(retry++, 4))), stop)) return;
        }
    }

    /// <summary>Waits; false if stopped meanwhile.</summary>
    static async Task<bool> Wait(TimeSpan time, CancellationToken stop)
    {
        try
        {
            await Task.Delay(time, stop);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
                case "shows":
                    Shows?.Invoke(json);
                    break;
                case "result":
                    TaskCompletionSource<string?>? answer = null;
                    if (json["id"]?.GetValue<int>() is { } id)
                        lock (_waiting) _waiting.Remove(id, out answer);
                    if (answer is not null)
                        answer.SetResult(json["ok"]?.GetValue<bool>() == true ? null : json["error"]?.GetValue<string>() ?? "The show computer refused that.");
                    else Result?.Invoke(json);
                    break;
                case "history":
                    History?.Invoke(json);
                    break;
                case "library":
                    Library?.Invoke(json);
                    break;
                case "outputs":
                    Outputs?.Invoke(json);
                    break;
                case "levels":
                    Levels?.Invoke(json);
                    break;
                case "device":
                    Device?.Invoke(json);
                    break;
                case "triggers":
                    Triggers?.Invoke(json);
                    break;
                case "diagnostics":
                    Diagnostics?.Invoke(json);
                    break;
                case "audioSetup":
                    AudioSetup?.Invoke(json);
                    break;
                case "inputs":
                    Inputs?.Invoke(json);
                    break;
                case "input":
                    Input?.Invoke(json);
                    break;
            }
        }
    }

    /// <summary>
    /// Sends a command (see docs/remote.md: play, seek, watch, run, edit, …), e.g.
    /// <c>Send("seek", ("timeline", id), ("position", 12.5))</c>. Returns its number, which its "result" will carry;
    /// null if there's no connection right now (then it isn't sent).
    /// </summary>
    public int? Send(string action, params (string Name, JsonNode? Value)[] fields)
    {
        int id = Interlocked.Increment(ref _nextId);
        return Send(id, action, fields) ? id : null;
    }

    bool Send(int id, string action, (string Name, JsonNode? Value)[] fields)
    {
        if (_outgoing is not { } outgoing) return false;
        var json = new JsonObject { ["type"] = "command", ["id"] = id, ["action"] = action };
        foreach (var (name, value) in fields) json[name] = value;
        return outgoing.Writer.TryWrite(json.ToJsonString());
    }

    /// <summary>Sends a command and waits for its answer. Returns why it couldn't be done, or null if it was.</summary>
    public Task<string?> RequestAsync(string action, params (string Name, JsonNode? Value)[] fields)
    {
        // Registered before it's sent, so an answer can't arrive first.
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id = Interlocked.Increment(ref _nextId);
        lock (_waiting) _waiting[id] = answer;
        if (!Send(id, action, fields))
        {
            lock (_waiting) _waiting.Remove(id);
            return Task.FromResult<string?>("Not connected to the show computer.");
        }
        return answer.Task;
    }

    /// <summary>Sends what's queued, one message at a time (a WebSocket can't send two at once).</summary>
    static async Task SendAsync(ClientWebSocket socket, Channel<string> outgoing)
    {
        try
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync())
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection dropped: the receiving side sees that too, and connects again.
        }
    }

    /// <summary>Out of step: drops the connection on purpose; it reconnects at once and gets the whole show again.</summary>
    public void Resync()
    {
        _resyncing = true;
        _socket?.Abort();
    }
}
