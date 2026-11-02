using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using GhostShow.Core.Storage;

namespace GhostShow.Web;

/// <summary>
/// One connected device: its WebSocket, with messages queued in order (the show, then its changes) and the latest
/// playback status kept apart, so a slow device gets every change but only the newest status, never a backlog of them.
/// </summary>
/// <param name="received">Handles a message from the device (a command), one at a time, in order.</param>
sealed class RemoteConnection(PairedDevice device, WebSocket socket, string address, Func<RemoteConnection, string, Task> received)
{
    /// <summary>The longest message a device may send (an edit can hold many clips); a longer one closes the connection.</summary>
    const int MaxMessage = 16 * 1024 * 1024;

    /// <summary>In the queue, stands for "send the newest levels" (see <see cref="SetLevels"/>).</summary>
    const string SendLevels = "levels";

    readonly Channel<string> _messages = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _closing = new();
    string? _status;
    string? _levels;
    (WebSocketCloseStatus Status, string Reason)? _close;

    public PairedDevice Device { get; } = device;

    /// <summary>Where it connected from (its IP address).</summary>
    public string Address { get; } = address;

    public DateTimeOffset Connected { get; } = DateTimeOffset.UtcNow;

    /// <summary>The timeline the device shows (it said so with a "watch" command): it gets that timeline's status and problems.</summary>
    public Guid? Watching { get; set; }

    /// <summary>Sound files the device asked about that the show computer hadn't measured yet (show's thread only).</summary>
    public HashSet<string> WaitingForAudio { get; } = [];

    /// <summary>A DMX setup screen is open on the device: it gets the hardware's state (show's thread only).</summary>
    public bool WatchingOutputs { get; set; }

    /// <summary>The universe whose levels a screen on the device shows; 0 for none (see <see cref="RemoteServer"/>'s levels loop).</summary>
    public int WatchingLevels { get; set; }

    /// <summary>The levels last sent: only changes are sent again.</summary>
    public string? LevelsSent { get; set; }

    /// <summary>Queues a message; sent in order after those before it.</summary>
    public void Send(string json) => _messages.Writer.TryWrite(json);

    /// <summary>The newest playback status: replaces one not sent yet.</summary>
    public void SetStatus(string json)
    {
        Interlocked.Exchange(ref _status, json);
        _messages.Writer.TryWrite("");   // wakes the sender; an empty message means "send the status"
    }

    /// <summary>The newest levels: replace those not sent yet, like the status.</summary>
    public void SetLevels(string json)
    {
        Interlocked.Exchange(ref _levels, json);
        _messages.Writer.TryWrite(SendLevels);
    }

    /// <summary>Closes it (the device was removed, or the server stops), with a code the web app understands.</summary>
    public void Close(WebSocketCloseStatus status, string reason)
    {
        _close = (status, reason);
        _closing.Cancel();
    }

    /// <summary>Sends and receives until the device goes, it's closed, or the server stops.</summary>
    /// <remarks>
    /// Nothing pending on the socket is cancelled: cancelling a WebSocket's send or receive aborts the connection, and
    /// the device would never see the close code (e.g. "pair again"). Instead the sender is stopped by completing its
    /// queue, the close is sent while the receive waits, and the device gets a moment to answer it.
    /// </remarks>
    public async Task RunAsync(CancellationToken stopping)
    {
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(stopping, _closing.Token);
        var sending = SendAsync();
        var receiving = ReceiveAsync();
        var closed = Task.Delay(Timeout.Infinite, closing.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        await Task.WhenAny(sending, receiving, closed);

        _messages.Writer.TryComplete();
        if (!await Within(sending, TimeSpan.FromSeconds(2))) socket.Abort();   // stuck sending to a device that's gone quiet
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var (status, reason) = _close ?? (WebSocketCloseStatus.NormalClosure, "Bye");
                await socket.CloseOutputAsync(status, reason, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception e) when (e is WebSocketException or TimeoutException or ObjectDisposedException)
        {
            socket.Abort();
        }
        if (!await Within(receiving, TimeSpan.FromSeconds(2))) socket.Abort();
    }

    static async Task<bool> Within(Task task, TimeSpan time) => await Task.WhenAny(task, Task.Delay(time)) == task;

    async Task SendAsync()
    {
        try
        {
            await foreach (var message in _messages.Reader.ReadAllAsync())
            {
                if (_closing.IsCancellationRequested) return;
                var json = message.Length == 0 ? Interlocked.Exchange(ref _status, null)
                         : message == SendLevels ? Interlocked.Exchange(ref _levels, null)
                         : message;
                if (json is null) continue;
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Reads what the device sends, handing each whole message on, until it closes or goes.</summary>
    async Task ReceiveAsync()
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessage)
                {
                    Close(WebSocketCloseStatus.MessageTooBig, "That message is too long.");
                    return;
                }
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Text) await received(this, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
        }
    }
}
