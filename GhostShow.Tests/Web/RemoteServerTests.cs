using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Tests.Playback;
using GhostShow.Web;
using Microsoft.Extensions.Logging.Abstractions;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Web;

/// <summary>The web remote's server, for real: listening on a free port, with an HTTP client and WebSockets as a device's browser.</summary>
public sealed class RemoteServerTests : IAsyncLifetime
{
    /// <summary>The desktop app, for the server: the show's thread is a lock here (tests change the show inside it too).</summary>
    /// <summary>The desktop's controls, for the server: records what it was asked to do.</summary>
    sealed class Control : IShowControl
    {
        public readonly List<string> Calls = [];
        string? Did(string call)
        {
            Calls.Add(call);
            return call.StartsWith("press", StringComparison.Ordinal) ? "That button isn't in the show any more." : null;
        }

        public string? Transport(TransportAction action, TimeSpan? position) => Did($"{action} {position?.TotalSeconds}".Trim());
        public string? Run(Guid timelineId) => Did($"run {timelineId}");
        public string? StopTimeline(Guid timelineId) => Did($"stopTimeline {timelineId}");
        public string? StopRunning(Guid runningId) => Did($"stopRunning {runningId}");
        public string? StopAll() => Did("stopAll");
        public string? Blackout() => Did("blackout");
        public string? PressButton(Guid buttonId) => Did($"press {buttonId}");
        public string? Open(Guid timelineId) => Did($"open {timelineId}");
        public string? TestSound(string? device) => Did($"testSound {device}");
    }

    /// <summary>Sound files, measured when the test says so.</summary>
    sealed class FakeAudioFiles : IAudioFiles
    {
        public readonly Dictionary<string, TimeSpan> Measured = [];
        public TimeSpan? GetLength(string path) => Measured.TryGetValue(path, out var length) ? length : null;
        public Waveform? GetWaveform(string path) => Measured.ContainsKey(path) ? new Waveform([10, 200, 30]) : null;
    }

    sealed class Host(ShowSession session, ShowPlayback playback) : IRemoteHost
    {
        public readonly Lock Gate = new();

        public Task<T> InvokeAsync<T>(Func<T> function)
        {
            lock (Gate) return Task.FromResult(function());
        }

        public ShowSession Session { get; } = session;
        public ShowPlayback Playback { get; } = playback;
        public Guid? FocusedTimeline { get; set; }
        public Control Commands { get; } = new();
        public IShowControl Control => Commands;
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public IReadOnlyList<string> AudioDevices { get; set; } = ["Speakers", "USB Interface"];
        public event Action? AudioDevicesChanged;
        public void ChangeAudioDevices(params string[] names)
        {
            AudioDevices = names;
            AudioDevicesChanged?.Invoke();
        }
    }

    readonly ShowTimeline _main;
    readonly ShowDocument _document;
    readonly ShowSession _session = new(new ShowEngine(), new FixtureProfileLibrary([]), NullLogger<ShowSession>.Instance);
    readonly PlaybackManager _manager = new(new ShowEngine(), new FakeAudioOutput(), new ManualClock());
    readonly FakeAudioFiles _audioFiles = new();
    readonly Host _host;
    readonly RemoteSettings _settings = new();
    readonly RemoteServer _server;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public RemoteServerTests()
    {
        _main = new ShowTimeline { Name = "Main", Length = S(30), Tracks = [new TimelineTrack { Name = "Lights", Kind = TrackKind.Lighting }] };
        _session.Use(new Project { Name = "Haunt 2026", Timelines = [_main] });
        _document = _session.Document;
        _host = new Host(_session, new ShowPlayback(_session, _manager, _audioFiles));
        _server = new RemoteServer(_host, _settings, NullLogger.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        await _server.StartAsync(0);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/");
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _http.Dispose();
    }

    async Task<string> PairAsync(string name = "iPad", string role = "editor")
    {
        var response = await _http.PostAsJsonAsync("api/pair", new { pin = _server.Pairing.Pin, name, role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
    }

    async Task<ClientWebSocket> ConnectAsync(string? token)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/api/live?token={token}"), TestContext.Current.CancellationToken);
        return socket;
    }

    /// <summary>The next message of a type (skipping others, e.g. status while waiting for a change).</summary>
    static async Task<JsonObject> NextAsync(ClientWebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[1 << 16];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException($"Closed: {(int?)result.CloseStatus} {result.CloseStatusDescription}");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var json = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!.AsObject();
            if (json["type"]!.GetValue<string>() == type) return json;
        }
    }

    [Fact]
    public async Task Pairing_takes_the_PIN_and_gives_a_token_that_connects()
    {
        var wrong = await _http.PostAsJsonAsync("api/pair", new { pin = "000000" == _server.Pairing.Pin ? "111111" : "000000", name = "Phone" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        var token = await PairAsync("Nate's iPad", "operator");

        var device = Assert.Single(_settings.Devices);
        Assert.Equal(("Nate's iPad", RemoteRole.Operator), (device.Name, device.Role));
        Assert.DoesNotContain(token, device.TokenHash);   // only its hash is kept
        using var socket = await ConnectAsync(token);
        var hello = await NextAsync(socket, "hello");
        Assert.Equal(("Nate's iPad", "operator"), (hello["device"]!["name"]!.GetValue<string>(), hello["device"]!["role"]!.GetValue<string>()));
        Assert.Equal("Haunt 2026", hello["show"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_device_that_isnt_paired_is_told_so_by_the_close_code()
    {
        using var socket = await ConnectAsync("not-a-token");
        var result = await socket.ReceiveAsync(new byte[256], TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(RemoteServer.NotPaired, (int)result.CloseStatus!);
    }

    [Fact]
    public async Task A_device_s_copy_of_the_show_follows_every_change_including_undo()
    {
        using var socket = await ConnectAsync(await PairAsync());
        var hello = await NextAsync(socket, "hello");
        var copy = new ShowDocument(ShowDocument.FromSnapshot(hello["show"]!));
        var lights = ShowPath.Timeline(_main.Id).Track(_main.Tracks[0].Id);

        lock (_host.Gate)
        {
            _document.Apply(new Transaction("Rename track", new SetOp(lights.Then("name"), "Spots")));
            _document.Apply(new Transaction("Add clip", new InsertOp(lights.Then("events"), -1, new LightingClip { Start = S(2) })));
            _document.Undo(_main.Id);
        }
        long version = hello["version"]!.GetValue<long>();
        for (int i = 0; i < 3; i++)
        {
            var change = await NextAsync(socket, "change");
            Assert.Equal(++version, change["version"]!.GetValue<long>());
            copy.Apply(new Transaction(change["description"]!.GetValue<string>(), [.. change["ops"]!.AsArray().Select(o => ShowDocument.Decode(o!))]));
        }

        Assert.Equal("Spots", copy.Project.Timelines[0].Tracks[0].Name);
        Assert.Empty(copy.Project.Timelines[0].Tracks[0].Events);
    }

    [Fact]
    public async Task Another_show_opened_reaches_devices_as_a_new_hello()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");
        lock (_host.Gate)
        {
            _document.Open(new Project { Name = "Haunt 2027", Timelines = [] });
            _server.ShowReplaced();
        }
        var hello = await NextAsync(socket, "hello");
        Assert.Equal("Haunt 2027", hello["show"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Status_says_what_the_editor_s_timeline_is_doing_and_what_runs()
    {
        _host.Playback.Load(_main.Id);
        _host.FocusedTimeline = _main.Id;
        using var socket = await ConnectAsync(await PairAsync());

        var status = await NextAsync(socket, "status");
        Assert.Equal((_main.Id.ToString(), "stopped"), (status["focused"]!["timeline"]!.GetValue<string>(), status["focused"]!["state"]!.GetValue<string>()));

        _host.Playback.Play(_main.Id);
        JsonObject playing;
        do playing = await NextAsync(socket, "status");
        while (playing["focused"]!["state"]!.GetValue<string>() != "playing");
        var running = Assert.Single(playing["running"]!.AsArray());
        Assert.Equal(("Main", false), (running!["name"]!.GetValue<string>(), running["copy"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_removed_device_is_disconnected_and_can_t_come_back_without_pairing()
    {
        var token = await PairAsync();
        using var socket = await ConnectAsync(token);
        await NextAsync(socket, "hello");
        Assert.Single(_server.Connected);

        _server.Remove(_settings.Devices[0].Id);

        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => NextAsync(socket, "never"));
        Assert.Contains($"{RemoteServer.NotPaired}", closed.Message);
        using var again = await ConnectAsync(token);
        var result = await again.ReceiveAsync(new byte[256], TestContext.Current.CancellationToken);
        Assert.Equal(RemoteServer.NotPaired, (int)result.CloseStatus!);
    }

    [Fact]
    public async Task Stopping_the_server_tells_devices_why()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");

        await _server.StopAsync();

        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => NextAsync(socket, "never"));
        Assert.Contains("1001", closed.Message);
        Assert.Contains("stopped", closed.Message);
    }

    static int _commands;

    static async Task<JsonObject> CommandAsync(ClientWebSocket socket, object command)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(command)!.AsObject();
        int id = Interlocked.Increment(ref _commands);
        json["type"] = "command";
        json["id"] = id;
        await socket.SendAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        while (true)
        {
            var result = await NextAsync(socket, "result");
            if (result["id"]!.GetValue<int>() == id) return result;
        }
    }

    [Fact]
    public async Task Commands_reach_the_show_computer_and_each_gets_an_answer()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");
        var (scare, slot, button) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        foreach (var command in new object[]
        {
            new { action = "play" }, new { action = "seek", position = 12.5 }, new { action = "run", timeline = scare.ToString() },
            new { action = "stopRunning", running = slot.ToString() }, new { action = "stopAll" }, new { action = "blackout" },
            new { action = "open", timeline = scare.ToString() },
        })
        {
            var answer = await CommandAsync(socket, command);
            Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        }

        Assert.Equal(["Play", "Seek 12.5", $"run {scare}", $"stopRunning {slot}", "stopAll", "blackout", $"open {scare}"], _host.Commands.Calls);
        var refused = await CommandAsync(socket, new { action = "press", button = button.ToString() });
        Assert.Equal((false, "That button isn't in the show any more."), (refused["ok"]!.GetValue<bool>(), refused["error"]!.GetValue<string>()));
        Assert.Contains("doesn't know", (await CommandAsync(socket, new { action = "fly" }))["error"]!.GetValue<string>());
        Assert.Contains("didn't understand", (await CommandAsync(socket, new { action = "run", timeline = "nonsense" }))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_operator_runs_the_show_but_can_t_change_it_until_made_an_editor()
    {
        using var socket = await ConnectAsync(await PairAsync("Door phone", "operator"));
        await NextAsync(socket, "hello");

        Assert.True((await CommandAsync(socket, new { action = "blackout" }))["ok"]!.GetValue<bool>());
        var open = await CommandAsync(socket, new { action = "open", timeline = _main.Id.ToString() });
        Assert.False(open["ok"]!.GetValue<bool>());
        Assert.Contains("operator", open["error"]!.GetValue<string>());
        Assert.Equal(["blackout"], _host.Commands.Calls);   // the open never reached the show computer

        _server.Pairing.SetRole(_settings.Devices[0].Id, RemoteRole.Editor);   // on the desktop
        var device = await NextAsync(socket, "device");
        Assert.Equal("editor", device["device"]!["role"]!.GetValue<string>());
        Assert.True((await CommandAsync(socket, new { action = "open", timeline = _main.Id.ToString() }))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_device_watching_a_timeline_gets_its_problems_and_where_it_is_even_stopped()
    {
        lock (_host.Gate)
            _main.Tracks.Add(new TimelineTrack { Kind = TrackKind.Event, Events = [new TimelineCommandEvent { Start = S(1), TimelineId = Guid.NewGuid() }] });
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");

        // (Not CommandAsync: it would skip the problems, which come before the command's answer.)
        var watch = $$"""{"type":"command","id":1,"action":"watch","timeline":"{{_main.Id}}"}""";
        await socket.SendAsync(Encoding.UTF8.GetBytes(watch), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        var issues = await NextAsync(socket, "issues");
        Assert.Equal(_main.Id.ToString(), issues["timeline"]!.GetValue<string>());
        Assert.Contains("isn't in the show", Assert.Single(issues["issues"]!.AsArray())!["message"]!.GetValue<string>());
        JsonObject status;
        do status = await NextAsync(socket, "status");
        while (status["timelines"]!.AsArray().Count == 0);
        var watched = Assert.Single(status["timelines"]!.AsArray())!;
        Assert.Equal(("stopped", 30.0), (watched["state"]!.GetValue<string>(), watched["length"]!.GetValue<double>()));
    }

    [Fact]
    public async Task The_transport_can_work_a_named_timeline_rather_than_the_desktop_s()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");
        var main = _main.Id.ToString();

        Assert.True((await CommandAsync(socket, new { action = "play", timeline = main }))["ok"]!.GetValue<bool>());
        Assert.Equal(TransportState.Playing, _host.Playback.StatusOf(_main.Id).State);
        await CommandAsync(socket, new { action = "seek", timeline = main, position = 12.0 });
        Assert.Equal(S(12), _host.Playback.StatusOf(_main.Id).Position);
        await CommandAsync(socket, new { action = "pause", timeline = main });
        Assert.Equal(TransportState.Paused, _host.Playback.StatusOf(_main.Id).State);
        await CommandAsync(socket, new { action = "stop", timeline = main });
        Assert.Equal(TransportState.Stopped, _host.Playback.StatusOf(_main.Id).State);
        Assert.Empty(_host.Commands.Calls);   // none of it went to the desktop's own transport

        await CommandAsync(socket, new { action = "run", timeline = main, playOnceOnly = true });
        await CommandAsync(socket, new { action = "run", timeline = main, playOnceOnly = true });
        Assert.Single(_host.Playback.Running);   // once only
    }

    [Fact]
    public async Task Devices_get_the_patch_with_its_profiles_and_its_changes()
    {
        var fixture = new Core.Fixtures.FixtureInstance { Name = "Entry LED", ProfileId = TestProfiles.Rgbw.Id, ModeName = TestProfiles.Rgbw.Modes[0].Name, StartAddress = 20 };
        lock (_host.Gate)
        {
            _session.UseProfiles(new FixtureProfileLibrary([TestProfiles.Rgbw]));
            _session.Project.Fixtures.Add(fixture);
            _session.Apply();
        }
        using var socket = await ConnectAsync(await PairAsync());
        var hello = await NextAsync(socket, "hello");
        Assert.Equal(TestProfiles.Rgbw.Id, Assert.Single(hello["profiles"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal(["Speakers", "USB Interface"], hello["audioDevices"]!.AsArray().Select(d => d!.GetValue<string>()));
        var copy = ShowDocument.FromSnapshot(hello["show"]!);
        Assert.Equal("Entry LED", Assert.Single(copy.Fixtures).Name);

        lock (_host.Gate)
        {
            fixture.StartAddress = 30;   // the Fixtures screen changes the patch, then applies it
            _session.Apply();
        }
        var patch = await NextAsync(socket, "patch");
        Assert.Equal(30, patch["fixtures"]![0]!["startAddress"]!.GetValue<int>());
        Assert.Single(patch["profiles"]!.AsArray());
    }

    [Fact]
    public async Task Levels_set_by_hand_reach_devices_as_they_change()
    {
        lock (_host.Gate) _session.Apply();   // universe 1 exists
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");
        var first = await NextAsync(socket, "manual");   // all of them, on connecting
        Assert.Equal(0, Convert.FromBase64String(first["levels"]!.GetValue<string>())[9]);

        _session.Engine.SetChannel(1, 10, 200);   // a fader on the show computer

        JsonObject manual;
        do manual = await NextAsync(socket, "manual");
        while (Convert.FromBase64String(manual["levels"]!.GetValue<string>())[9] != 200);
        Assert.Equal(1, manual["universe"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_sound_file_s_length_and_waveform_come_once_the_show_computer_has_measured_it()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");
        Assert.True((await CommandAsync(socket, new { action = "audio", file = "audio/thunder.wav" }))["ok"]!.GetValue<bool>());

        var path = _host.Playback.ResolveAudio("audio/thunder.wav");
        _audioFiles.Measured[path] = S(3.5);
        lock (_host.Gate) _host.Playback.OnAudioMeasured(path);

        var audio = await NextAsync(socket, "audio");
        Assert.Equal(("audio/thunder.wav", 3.5), (audio["file"]!.GetValue<string>(), audio["length"]!.GetValue<double>()));
        Assert.Equal([10, 200, 30], Convert.FromBase64String(audio["waveform"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Audio_device_changes_reach_devices_and_a_test_tone_can_be_asked_for()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");

        lock (_host.Gate) _host.ChangeAudioDevices("Speakers", "USB Interface", "Fog machine speaker");
        var devices = await NextAsync(socket, "audioDevices");
        Assert.Equal(3, devices["devices"]!.AsArray().Count);

        await CommandAsync(socket, new { action = "testSound", device = "USB Interface" });
        Assert.Equal(["testSound USB Interface"], _host.Commands.Calls);
    }

    [Fact]
    public async Task Status_carries_the_desktop_s_warnings()
    {
        _host.Warnings = ["Universe 1: the adapter was unplugged. Retrying."];
        using var socket = await ConnectAsync(await PairAsync());
        var status = await NextAsync(socket, "status");
        Assert.Equal("Universe 1: the adapter was unplugged. Retrying.", Assert.Single(status["warnings"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public async Task Pages_are_served_and_unknown_api_paths_are_not_found()
    {
        var page = await _http.GetAsync("", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("GhostShow", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("api/nothing", TestContext.Current.CancellationToken)).StatusCode);
    }
}

public class DevicePairingTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Five_wrong_PINs_pause_pairing_for_a_minute_even_for_the_right_one()
    {
        var clock = new Clock();
        var pairing = new DevicePairing(new RemoteSettings(), clock);
        string wrong = pairing.Pin == "123456" ? "654321" : "123456";
        for (int i = 0; i < 5; i++) Assert.Null(pairing.Pair(wrong, "x", RemoteRole.Editor).Device);

        var locked = pairing.Pair(pairing.Pin, "iPad", RemoteRole.Editor);
        Assert.True(locked.TooManyTries);

        clock.Now += TimeSpan.FromMinutes(1.1);
        Assert.NotNull(pairing.Pair(pairing.Pin, "iPad", RemoteRole.Editor).Device);
    }

    [Fact]
    public void A_new_PIN_replaces_the_old_and_tokens_find_their_device()
    {
        var settings = new RemoteSettings();
        var pairing = new DevicePairing(settings);
        var old = pairing.Pin;
        var paired = pairing.Pair(old, "  Laptop  ", RemoteRole.Editor);
        Assert.Equal("Laptop", paired.Device!.Name);
        Assert.Same(paired.Device, pairing.Find(paired.Token));
        Assert.Null(pairing.Find("someone-else"));

        do pairing.NewPin(); while (pairing.Pin == old);
        Assert.Null(pairing.Pair(old, "Phone", RemoteRole.Editor).Device);
        Assert.Single(settings.Devices);
    }
}
