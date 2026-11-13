using Microsoft.Extensions.Logging;

namespace GhostShow.Inputs;

/// <summary>
/// The show's serial input devices, each listened to on its own port (one <see cref="SerialInputService"/> each), by
/// the device's id. <see cref="Listen"/> says which devices should be listening and where; the rest are closed.
///
/// Threading: as <see cref="SerialInputService"/>: events come on the ports' reading threads.
/// </summary>
public sealed class SerialInputs : IDisposable
{
    readonly ILogger _logger;
    readonly Func<string, int, ISerialLink>? _open;
    readonly TimeSpan? _retryInterval;
    readonly Lock _lock = new();
    readonly Dictionary<Guid, SerialInputService> _services = [];

    /// <param name="open">Opens a port (tests pass a stand-in); by default a real serial port.</param>
    public SerialInputs(ILogger logger, Func<string, int, ISerialLink>? open = null, TimeSpan? retryInterval = null)
    {
        _logger = logger;
        _open = open;
        _retryInterval = retryInterval;
    }

    /// <summary>A device sent a command (a whole line, trimmed). Raised on the reading thread.</summary>
    public event Action<Guid, string>? CommandReceived;

    /// <summary>A device's port opened, failed or was lost.</summary>
    public event Action<Guid>? StatusChanged;

    /// <summary>
    /// Listens to these devices on these ports, and to no others. A device already listening on the same port at the
    /// same speed carries on undisturbed.
    /// </summary>
    public void Listen(IEnumerable<(Guid Input, string Port, int BaudRate)> wanted)
    {
        var start = new List<(SerialInputService Service, string Port, int BaudRate)>();
        var stop = new List<SerialInputService>();
        lock (_lock)
        {
            var keep = new HashSet<Guid>();
            foreach (var (input, port, baud) in wanted)
            {
                if (!keep.Add(input)) continue;
                if (!_services.TryGetValue(input, out var service))
                {
                    _services[input] = service = new SerialInputService(_logger, _open, _retryInterval);
                    service.CommandReceived += command => CommandReceived?.Invoke(input, command);
                    service.StatusChanged += () => StatusChanged?.Invoke(input);
                }
                start.Add((service, port, baud));
            }
            foreach (var input in _services.Keys.Where(i => !keep.Contains(i)).ToList())
            {
                stop.Add(_services[input]);
                _services.Remove(input);
            }
        }
        // Outside the lock: a stop raises StatusChanged, whose handler may ask for a status.
        foreach (var service in stop) service.Dispose();
        foreach (var (service, port, baud) in start) service.Start(port, baud);
    }

    /// <summary>How a device's port stands; Off for one that isn't being listened to.</summary>
    public SerialInputStatus Status(Guid input)
    {
        lock (_lock)
            return _services.TryGetValue(input, out var service) ? service.Status : new SerialInputStatus(SerialInputState.Off, null, 0);
    }

    public void Dispose() => Listen([]);
}
