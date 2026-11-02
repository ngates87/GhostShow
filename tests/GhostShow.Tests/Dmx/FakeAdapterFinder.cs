using GhostShow.Dmx.Discovery;

namespace GhostShow.Tests.Dmx;

/// <summary>Adapters that tests plug in and pull out; no real port is ever opened.</summary>
sealed class FakeAdapterFinder : IDmxAdapterFinder
{
    readonly Lock _lock = new();
    readonly Dictionary<string, DmxAdapterInfo?> _ports = [];
    readonly HashSet<string> _busy = [];

    public int Listings;
    public readonly List<string> Probed = [];

    public void Plug(string port, DmxAdapterInfo? adapter) { lock (_lock) _ports[port] = adapter; }
    public void Unplug(string port) { lock (_lock) _ports.Remove(port); }
    public void SetBusy(string port, bool busy) { lock (_lock) { if (busy) _busy.Add(port); else _busy.Remove(port); } }

    public IReadOnlyList<string> CandidatePorts()
    {
        lock (_lock)
        {
            Listings++;
            return [.. _ports.Keys];
        }
    }

    public DmxAdapterInfo? Probe(string port, int timeoutMs, out bool busy)
    {
        lock (_lock)
        {
            Probed.Add(port);
            busy = _busy.Contains(port);
            return busy ? null : _ports.GetValueOrDefault(port);
        }
    }
}
