using Microsoft.Extensions.Logging;

namespace GhostShow.Inputs;

public enum SerialInputState
{
    /// <summary>Not listening: no port picked, or turned off.</summary>
    Off,
    /// <summary>Opening the port (it takes a moment).</summary>
    Opening,
    /// <summary>The port is open and commands are being read.</summary>
    Listening,
    /// <summary>The port couldn't be opened or was lost (unplugged, in use); it's tried again every few seconds.</summary>
    Retrying,
}

/// <param name="Port">The port picked; null when off.</param>
/// <param name="Problem">Why it's retrying, in words for the operator; null otherwise.</param>
public sealed record SerialInputStatus(SerialInputState State, string? Port, int BaudRate, string? Problem = null);

/// <summary>
/// Listens on one serial port and reports each command (one line) a device sends, e.g. a prop controller or sensor
/// box. Only the port picked is opened, never probed or scanned. Like the DMX outputs, it never gives up: if the port is
/// missing, in use or unplugged, it says so and tries again every few seconds, so plugging the device back in mid-show
/// just works.
///
/// Threading: reading happens on a thread of its own. <see cref="CommandReceived"/> and <see cref="StatusChanged"/>
/// are raised on that thread, so the UI passes them to its own thread.
/// </summary>
public sealed class SerialInputService : IDisposable
{
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(3);

    readonly ILogger _logger;
    readonly Func<string, int, ISerialLink> _open;
    readonly TimeSpan _retryInterval;
    readonly Lock _lock = new();
    Listener? _listener;
    SerialInputStatus _status = new(SerialInputState.Off, null, 0);

    /// <param name="open">Opens a port (tests pass a stand-in); by default a real serial port.</param>
    public SerialInputService(ILogger logger, Func<string, int, ISerialLink>? open = null, TimeSpan? retryInterval = null)
    {
        _logger = logger;
        _open = open ?? ((port, baud) => new SerialPortLink(port, baud));
        _retryInterval = retryInterval ?? DefaultRetryInterval;
    }

    /// <summary>A command arrived (a whole line, trimmed). Raised on the reading thread.</summary>
    public event Action<string>? CommandReceived;

    /// <summary>The port opened, failed or was lost. Raised on the reading thread (or the caller's, for Start and Stop).</summary>
    public event Action? StatusChanged;

    public SerialInputStatus Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>
    /// Listens on this port, closing whatever port it listened on before. Returns at once; the port opens in the
    /// background (see <see cref="Status"/>).
    /// </summary>
    public void Start(string port, int baudRate)
    {
        Listener listener;
        lock (_lock)
        {
            if (_listener is { } current && current.Port == port && current.BaudRate == baudRate) return;
            _listener?.Stop();
            _listener = listener = new Listener(this, port, baudRate);
        }
        listener.Begin();
    }

    /// <summary>Stops listening and closes the port.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_listener is null) return;
            _listener.Stop();
            _listener = null;
        }
        SetStatus(null, new SerialInputStatus(SerialInputState.Off, null, 0));
    }

    public void Dispose() => Stop();

    /// <summary>Sets the status, unless it comes from a listener that has since been replaced or stopped.</summary>
    void SetStatus(Listener? from, SerialInputStatus status)
    {
        lock (_lock)
        {
            if (from is not null && from != _listener) return;
            if (status == _status) return;
            _status = status;
        }
        StatusChanged?.Invoke();
    }

    /// <summary>One port, read on its own thread until stopped: open, read commands, and if that fails, wait and open again.</summary>
    sealed class Listener(SerialInputService owner, string port, int baudRate)
    {
        public string Port { get; } = port;
        public int BaudRate { get; } = baudRate;

        readonly CancellationTokenSource _stop = new();
        readonly Lock _linkLock = new();
        ISerialLink? _link;

        public void Begin()
        {
            owner.SetStatus(this, new SerialInputStatus(SerialInputState.Opening, Port, BaudRate));
            new Thread(Run) { IsBackground = true, Name = $"Serial input {Port}" }.Start();
        }

        public void Stop()
        {
            _stop.Cancel();
            // Closing the port also ends a read that's waiting on it.
            lock (_linkLock) _link?.Dispose();
        }

        void Run()
        {
            string? lastProblem = null;
            while (!_stop.IsCancellationRequested)
            {
                string problem = ReadUntilLost();
                if (_stop.IsCancellationRequested) return;
                owner.SetStatus(this, new SerialInputStatus(SerialInputState.Retrying, Port, BaudRate, problem));
                // Logged once per problem, not every few seconds while a device stays unplugged.
                if (problem != lastProblem) owner._logger.LogWarning("Serial input {Port}: {Problem} Retrying every {Seconds} s.", Port, problem, owner._retryInterval.TotalSeconds);
                lastProblem = problem;
                _stop.Token.WaitHandle.WaitOne(owner._retryInterval);
            }
        }

        /// <summary>Opens the port and reads commands until it fails or is stopped. Returns what went wrong.</summary>
        string ReadUntilLost()
        {
            ISerialLink link;
            try
            {
                link = owner._open(Port, BaudRate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return ex switch
                {
                    FileNotFoundException => $"{Port} isn't connected.",
                    UnauthorizedAccessException => $"{Port} is in use by another program (or this user may not open it).",
                    _ => $"{Port} couldn't be opened: {ex.Message}",
                };
            }
            lock (_linkLock)
            {
                if (_stop.IsCancellationRequested)
                {
                    link.Dispose();
                    return "Stopped.";
                }
                _link = link;
            }
            owner.SetStatus(this, new SerialInputStatus(SerialInputState.Listening, Port, BaudRate));
            owner._logger.LogInformation("Serial input: listening on {Port} at {Baud} baud.", Port, BaudRate);

            var splitter = new SerialLineSplitter();
            var buffer = new byte[256];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    int read = link.Read(buffer);
                    if (read > 0)
                        foreach (var command in splitter.Add(buffer.AsSpan(0, read))) owner.CommandReceived?.Invoke(command);
                    else if (splitter.Flush() is { } unended)
                        owner.CommandReceived?.Invoke(unended);   // the line went quiet without a CR or LF
                }
                return "Stopped.";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                return $"{Port} stopped answering (unplugged?).";
            }
            finally
            {
                lock (_linkLock) _link = null;
                link.Dispose();
            }
        }
    }
}
