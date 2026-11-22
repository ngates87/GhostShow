using System.Text.Json.Nodes;
using GhostShow.App.Client;
using GhostShow.Core.Playback;

namespace GhostShow.App.Tests;

/// <summary>Playback as a device sees it: between two status messages, a playing timeline's position moves on smoothly.</summary>
public class RemotePlaybackTests
{
    sealed class ManualTime : TimeProvider
    {
        long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    readonly ManualTime _time = new();
    readonly RemotePlayback _playback;
    readonly Guid _timeline = Guid.NewGuid(), _copy = Guid.NewGuid();

    public RemotePlaybackTests() =>
        _playback = new RemotePlayback(new RemoteLink(new RemoteEndpoint("http://127.0.0.1:1", () => null, "", "")), _time);

    void Status(string state, double position, bool loop = false)
    {
        JsonObject Of() => new() { ["state"] = state, ["position"] = position, ["length"] = 30.0, ["loop"] = loop };
        var running = Of();
        (running["id"], running["name"], running["timeline"]) = (_copy.ToString(), "Main", _timeline.ToString());
        var watched = Of();
        watched["timeline"] = _timeline.ToString();
        _playback.OnStatus(new JsonObject { ["type"] = "status", ["running"] = new JsonArray(running), ["timelines"] = new JsonArray(watched) });
    }

    double Position() => _playback.StatusOf(_timeline).Position.TotalSeconds;

    [Fact]
    public void A_playing_timeline_moves_on_between_statuses_and_a_paused_one_stays()
    {
        Status("playing", 10.0);
        _time.Advance(30);
        Assert.Equal(10.03, Position(), 3);
        Assert.Equal(10.03, _playback.Running.Single().Status.Position.TotalSeconds, 3);   // running copies too

        Status("paused", 10.05);
        _time.Advance(30);
        Assert.Equal(10.05, Position(), 3);
    }

    [Fact]
    public void Without_statuses_it_stops_a_little_ahead_until_the_next_says_where_it_is()
    {
        Status("playing", 10.0);
        _time.Advance(2000);   // the connection stalled

        Assert.Equal(10.0 + RemotePlayback.MaxAhead.TotalSeconds, Position(), 3);
        Status("playing", 12.0);
        Assert.Equal(12.0, Position(), 3);
    }

    [Fact]
    public void At_the_end_it_stops_there_or_goes_round_if_it_loops()
    {
        Status("playing", 29.95);
        _time.Advance(100);
        Assert.Equal(30.0, Position(), 3);

        Status("playing", 29.95, loop: true);
        _time.Advance(100);
        Assert.Equal(0.05, Position(), 3);
    }
}
