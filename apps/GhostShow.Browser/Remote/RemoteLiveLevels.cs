using System.Text.Json.Nodes;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;

namespace GhostShow.Browser.Remote;

/// <summary>
/// Live levels for the Control, Channels and Monitor screens here. The levels are on the show computer: it sends those
/// of the universe on show ("levels" messages, see <see cref="Watch"/>), and faders moved here are sent to it.
/// </summary>
sealed class RemoteLiveLevels(ShowSession session, RemoteLink link) : ILiveLevels
{
    /// <summary>How long a fader moved here ignores what the show computer says, so older levels on their way don't pull it back.</summary>
    const int HoldMilliseconds = 500;

    /// <summary>A fader's moves are gathered for this long, and only the latest sent: a slow network mustn't build up a backlog of them.</summary>
    static readonly TimeSpan SendDelay = TimeSpan.FromMilliseconds(33);

    readonly byte[] _levels = new byte[512];
    readonly byte[] _sent = new byte[512];
    readonly bool[] _controlled = new bool[512];
    readonly Dictionary<int, (byte Value, long Until)> _held = [];   // by address
    readonly Dictionary<string, (string Action, (string, JsonNode?)[] Fields)> _waitingToSend = [];   // by what they set
    int? _watching;
    int? _received;   // the universe the arrays hold
    bool _known;
    bool _sendScheduled;
    string _source = "Asking the show computer…";

    public CommandResult SetChannel(int universe, int address, int value)
    {
        Hold(universe, address, value);
        Queue($"channel {universe} {address}", "setChannel", ("universe", universe), ("address", address), ("value", value));
        return CommandResult.Ok;
    }

    public CommandResult ClearUniverse(int universe)
    {
        if (universe == _watching) _held.Clear();
        Queue($"clear {universe}", "clearUniverse", ("universe", universe));
        return CommandResult.Ok;
    }

    public CommandResult SetFixtureChannel(Guid fixtureId, int channelNumber, int value)
    {
        if (Fixture(fixtureId) is { } fixture) Hold(fixture.Universe, fixture.StartAddress + channelNumber - 1, value);
        Queue($"fixture {fixtureId} {channelNumber}", "setFixtureChannel", ("fixture", fixtureId.ToString()), ("channel", channelNumber), ("value", value));
        return CommandResult.Ok;
    }

    public CommandResult SetFixtureControl(Guid fixtureId, ChannelFunction function, int value)
    {
        if (Fixture(fixtureId) is { } fixture && fixture.TryGetAddress(function, out int address)) Hold(fixture.Universe, address, value);
        Queue($"control {fixtureId} {function}", "setFixtureControl", ("fixture", fixtureId.ToString()), ("function", function.ToString()), ("value", value));
        return CommandResult.Ok;
    }

    public bool TryCopyLevels(int universe, Span<byte> destination)
    {
        if (universe != _received || !_known) return false;
        _levels.AsSpan().CopyTo(destination);
        long now = Environment.TickCount64;
        foreach (var (address, held) in _held.ToList())
        {
            if (now > held.Until) _held.Remove(address);
            else destination[address - 1] = held.Value;
        }
        return true;
    }

    public void CopyTimelineControlled(int universe, Span<bool> destination)
    {
        if (universe == _received) _controlled.AsSpan().CopyTo(destination);
        else destination.Clear();
    }

    public string CopyAsSent(int universe, Span<byte> destination)
    {
        if (universe != _received)
        {
            destination.Clear();
            return "Asking the show computer…";
        }
        _sent.AsSpan().CopyTo(destination);
        return _source;
    }

    public void Watch(int? universe)
    {
        if (universe == _watching) return;
        _watching = universe;
        _held.Clear();
        SendWatch();
    }

    /// <summary>The whole show arrived (it may have reconnected): the show computer doesn't remember what this device was watching.</summary>
    public void OnHello()
    {
        if (_watching is not null) SendWatch();
    }

    /// <summary>A "levels" message: a universe's levels now.</summary>
    public void OnLevels(JsonObject message)
    {
        int universe = message["universe"]!.GetValue<int>();
        if (universe != _watching) return;   // on its way when the screen moved on
        _received = universe;
        _known = message["levels"] is not null;
        if (_known) Convert.FromBase64String(message["levels"]!.GetValue<string>()).CopyTo(_levels, 0);
        Convert.FromBase64String(message["sent"]!.GetValue<string>()).CopyTo(_sent, 0);
        var controlled = Convert.FromBase64String(message["controlled"]!.GetValue<string>());
        for (int i = 0; i < _controlled.Length; i++) _controlled[i] = controlled[i] != 0;
        _source = message["source"]!.GetValue<string>();
    }

    void SendWatch() => link.Send("watchLevels", ("universe", _watching));

    PatchedFixture? Fixture(Guid id) => session.Engine.Fixtures.FirstOrDefault(f => f.Id == id);

    void Hold(int universe, int address, int value)
    {
        if (universe == _watching) _held[address] = ((byte)value, Environment.TickCount64 + HoldMilliseconds);
    }

    void Queue(string key, string action, params (string, JsonNode?)[] fields)
    {
        _waitingToSend[key] = (action, fields);
        if (_sendScheduled) return;
        _sendScheduled = true;
        _ = SendSoonAsync();
    }

    async Task SendSoonAsync()
    {
        await Task.Delay(SendDelay);
        _sendScheduled = false;
        foreach (var (action, fields) in _waitingToSend.Values) link.Send(action, fields);
        _waitingToSend.Clear();
    }
}
