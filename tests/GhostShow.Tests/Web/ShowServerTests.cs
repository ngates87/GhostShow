using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GhostShow.Audio;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;
using GhostShow.Server;
using GhostShow.Tests.Dmx;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Tests.Web;

/// <summary>The server on its own (docs/plan-remote.md, steps 5 and 7): no window, its own show thread.</summary>
public sealed class ShowServerTests : IDisposable
{
    readonly TempDirectory _config = new();
    readonly DedicatedShowThread _thread = new();
    readonly List<Exception> _unhandled = [];

    public ShowServerTests() => _thread.Unhandled += e => { lock (_unhandled) _unhandled.Add(e); };

    public void Dispose()
    {
        _thread.Dispose();
        _config.Dispose();
    }

    [Fact]
    public async Task Awaits_on_the_show_thread_come_back_to_it()
    {
        bool before = false, after = false;
        await await _thread.InvokeAsync(async () =>
        {
            before = _thread.IsCurrent;
            await Task.Delay(10);
            after = _thread.IsCurrent;
        });

        Assert.True(before && after);
    }

    [Fact]
    public async Task The_server_runs_with_no_window()
    {
        var session = new ShowSession(new ShowEngine(), new FixtureProfileLibrary(FixtureProfileLoader.LoadBuiltIn()), NullLogger<ShowSession>.Instance);
        session.Use(new Project { Name = "Haunt 2026", Timelines = [new ShowTimeline { Name = "Main", Length = TimeSpan.FromSeconds(30) }] });
        var audio = new AudioService(NullLogger.Instance, useNullDevice: true);
        var manager = new PlaybackManager(session.Engine, audio, new StopwatchClock());
        var settings = new MachineSettings { Remote = new RemoteSettings { Enabled = true, Port = FreePort() } };
        var services = new ShowServices(session, new OutputManager(session.Engine, NullLoggerFactory.Instance), manager,
                                        new PlaybackLoop(manager, NullLogger.Instance), audio, settings, new AppPaths(_config.Path),
                                        new RecentEventsLog(), new ShowThreadProbe(_thread), _thread, new FakeAdapterFinder());

        var server = await _thread.InvokeAsync(() => new ShowServer(services, NullLogger.Instance));
        await await _thread.InvokeAsync(() => server.StartAsync(server.Tick.Start));

        Assert.True(server.Remote.IsOn, server.Remote.Problem);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.Remote.Port}/") };
        var paired = await http.PostAsJsonAsync("api/pair", new { pin = server.Remote.Server.Pairing.Pin, name = "iPad" }, TestContext.Current.CancellationToken);
        var token = (await paired.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["token"]!.GetValue<string>();
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.Remote.Port}/api/live?token={token}"), TestContext.Current.CancellationToken);
        var hello = JsonNode.Parse(await NextRawAsync(socket, "hello"))!;
        Assert.Equal("Haunt 2026", hello["show"]!["name"]!.GetValue<string>());
        await Eventually.True(() => server.Tick.Count > 3, "the server ticks itself");

        Assert.True(await await _thread.InvokeAsync(server.ShutdownAsync));
        Assert.Empty(_unhandled);
    }

    static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static Task<string> NextRawAsync(System.Net.WebSockets.ClientWebSocket socket, string type) => RemoteServerTests.NextRawAsync(socket, type);
}
