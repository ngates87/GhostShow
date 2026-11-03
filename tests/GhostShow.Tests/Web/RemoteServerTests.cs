using GhostShow.Tests.Dmx;
using GhostShow.Dmx.Discovery;
using GhostShow.Dmx;
using GhostShow.Core.Dmx;
using System.IO.Compression;
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
        public Task<string?> SaveAsync() => Task.FromResult(Did("save"));
        public IReadOnlyList<string> SavedShows() => ["Haunt 2026", "Test show"];
        public Task<string?> NewShowAsync(bool discardUnsaved) => Task.FromResult(Did($"new {discardUnsaved}"));
        public Task<string?> OpenShowAsync(string name, bool discardUnsaved) => Task.FromResult(Did($"openShow {name} {discardUnsaved}"));
        public Task<string?> SaveShowAsAsync(string name) => Task.FromResult(Did($"saveAs {name}"));
        public Task<string?> RenameShowAsync(string name) => Task.FromResult(Did($"rename {name}"));
    }

    /// <summary>Sound files, measured when the test says so.</summary>
    sealed class FakeAudioFiles : IAudioFiles
    {
        public readonly Dictionary<string, TimeSpan> Measured = [];
        public TimeSpan? GetLength(string path) => Measured.TryGetValue(path, out var length) ? length : null;
        public Waveform? GetWaveform(string path) => Measured.ContainsKey(path) ? new Waveform([10, 200, 30]) : null;
    }

    sealed class Host(ShowSession session, ShowPlayback playback, IFixtureLibrary library, IOutputSetup outputs, ILiveLevels levels) : IRemoteHost
    {
        public IFixtureLibrary Library => library;
        public IOutputSetup Outputs => outputs;
        public ILiveLevels Levels => levels;
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
        public bool HasUnsavedChanges { get; set; }
        public bool NeedsFirstSave { get; set; }
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
    readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ghostshow-remote-tests", Guid.NewGuid().ToString("N")));
    readonly LocalFixtureLibrary _library;
    readonly MachineSettings _machine = new();
    readonly FakeAdapterFinder _finder = new();
    readonly OutputManager _outputManager;
    readonly LocalOutputSetup _outputSetup;

    public RemoteServerTests()
    {
        _main = new ShowTimeline { Name = "Main", Length = S(30), Tracks = [new TimelineTrack { Name = "Lights", Kind = TrackKind.Lighting }] };
        _session.Use(new Project { Name = "Haunt 2026", Timelines = [_main] });
        _document = _session.Document;
        _library = new LocalFixtureLibrary(_session, _paths, new MachineSettings(), NullLogger.Instance);
        _outputManager = new OutputManager(_session.Engine, new ListLogger(), new DmxOutputOptions { BlackoutOnStop = TimeSpan.Zero }, _ => new FakeDevice());
        _outputSetup = new LocalOutputSetup(_session, _outputManager, _machine, () => Task.CompletedTask, _finder);
        _host = new Host(_session, new ShowPlayback(_session, _manager, _audioFiles), _library, _outputSetup, new LocalLiveLevels(_session.Engine, _outputManager.Find));
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
        await _outputManager.DisposeAsync();
        _http.Dispose();
        if (Directory.Exists(_paths.ConfigDirectory)) Directory.Delete(_paths.ConfigDirectory, recursive: true);
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

    /// <summary>Sends a command without waiting for its answer (for reading what comes before the answer).</summary>
    static async Task SendAsync(ClientWebSocket socket, object command)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(command)!.AsObject();
        json["type"] = "command";
        json["id"] = Interlocked.Increment(ref _commands);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
    }

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

    // ---- editing from a device ----

    /// <summary>An "edit" command, as the full app sends one, for a track's name.</summary>
    object RenameTrack(string name, int? merge = null)
    {
        var path = ShowPath.Timeline(_main.Id).Track(_main.Tracks[0].Id).Then("name").Value;
        return new { action = "edit", description = "Rename track", merge, ops = new[] { new { op = "set", path, value = name } } };
    }

    [Fact]
    public async Task An_edit_from_a_device_changes_the_show_and_reaches_the_others_but_not_itself()
    {
        using var editor = await ConnectAsync(await PairAsync("Laptop"));
        await NextAsync(editor, "hello");
        using var phone = await ConnectAsync(await PairAsync("Phone", "operator"));
        await NextAsync(phone, "hello");
        var changes = new List<ShowChange>();
        _document.Changed += changes.Add;

        var answer = await CommandAsync(editor, RenameTrack("Spots"));

        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.Equal("Spots", _main.Tracks[0].Name);
        Assert.Equal(("Rename track", "Laptop"), (changes.Single().Description, changes.Single().Origin));
        Assert.Equal("Rename track", _document.NextUndo(_main.Id));   // in the one history, for the desktop's Undo too

        var change = await NextAsync(phone, "change");
        Assert.Equal("Spots", change["ops"]![0]!["value"]!.GetValue<string>());
        // The editor heard nothing of its own edit: the next change it gets is the next one made.
        lock (_host.Gate) _document.Apply(new Transaction("Loop", new SetOp(ShowPath.Timeline(_main.Id).Then("loop"), true)));
        Assert.Equal("Loop", (await NextAsync(editor, "change"))["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_operator_can_t_edit_undo_or_save()
    {
        using var phone = await ConnectAsync(await PairAsync("Phone", "operator"));
        await NextAsync(phone, "hello");

        foreach (var command in new[] { RenameTrack("Hacked"), new { action = "undo" }, new { action = "save" } })
            Assert.Contains("operator", (await CommandAsync(phone, command))["error"]!.GetValue<string>());

        Assert.Equal("Lights", _main.Tracks[0].Name);
        Assert.Empty(_host.Commands.Calls);
    }

    [Fact]
    public async Task An_edit_that_doesn_t_fit_changes_nothing_and_says_why()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");
        var gone = ShowPath.Timeline(_main.Id).Track(Guid.NewGuid()).Then("name").Value;
        var fits = ShowPath.Timeline(_main.Id).Then("loop").Value;

        var answer = await CommandAsync(socket, new
        {
            action = "edit", description = "Two things",
            ops = new object[] { new { op = "set", path = fits, value = true }, new { op = "set", path = gone, value = "x" } },
        });

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.StartsWith("Can't edit", answer["error"]!.GetValue<string>());
        Assert.False(_main.Loop);   // all or nothing
    }

    [Fact]
    public async Task Undo_and_redo_are_done_on_the_show_computer_and_come_back_as_changes_with_the_history()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");
        await SendAsync(socket, new { action = "watch", timeline = _main.Id.ToString() });
        await NextAsync(socket, "history");   // on watching

        await SendAsync(socket, RenameTrack("Spots"));
        Assert.Equal("Rename track", (await NextAsync(socket, "history"))["undo"]!.GetValue<string>());

        await SendAsync(socket, new { action = "undo" });
        var undone = await NextAsync(socket, "change");   // to the device that asked too: it hasn't changed its copy
        Assert.Equal(("undo", "Lights"), (undone["kind"]!.GetValue<string>(), undone["ops"]![0]!["value"]!.GetValue<string>()));
        var history = await NextAsync(socket, "history");
        Assert.Equal(("Rename track", true), (history["redo"]!.GetValue<string>(), history["undo"] is null));
        Assert.Equal("Lights", _main.Tracks[0].Name);

        await SendAsync(socket, new { action = "redo" });
        await NextAsync(socket, "change");
        Assert.Equal("Spots", _main.Tracks[0].Name);
    }

    [Fact]
    public async Task Edits_with_one_merge_number_are_one_undo_step()
    {
        using var socket = await ConnectAsync(await PairAsync());
        await NextAsync(socket, "hello");

        await CommandAsync(socket, RenameTrack("A", merge: 1));
        await CommandAsync(socket, RenameTrack("AB", merge: 1));
        await CommandAsync(socket, RenameTrack("ABC", merge: 2));

        await CommandAsync(socket, new { action = "watch", timeline = _main.Id.ToString() });
        await CommandAsync(socket, new { action = "undo" });
        Assert.Equal("AB", _main.Tracks[0].Name);
        await CommandAsync(socket, new { action = "undo" });
        Assert.Equal("Lights", _main.Tracks[0].Name);
    }

    [Fact]
    public async Task A_device_can_save_and_status_says_whether_the_show_is_unsaved()
    {
        _host.HasUnsavedChanges = true;
        _host.NeedsFirstSave = true;
        using var socket = await ConnectAsync(await PairAsync());
        var status = await NextAsync(socket, "status");
        Assert.True(status["unsaved"]!.GetValue<bool>());
        Assert.True(status["needsFirstSave"]!.GetValue<bool>());

        Assert.True((await CommandAsync(socket, new { action = "save" }))["ok"]!.GetValue<bool>());

        Assert.Equal(["save"], _host.Commands.Calls);
    }

    [Fact]
    public async Task A_device_can_list_start_open_name_and_rename_shows()
    {
        using var socket = await ConnectAsync(await PairAsync());

        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"command","id":99,"action":"listShows"}"""), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        var shows = await NextAsync(socket, "shows");
        Assert.Equal(["Haunt 2026", "Test show"], shows["shows"]!.AsArray().Select(n => n!.GetValue<string>()));

        await CommandAsync(socket, new { action = "newShow", discardUnsaved = true });
        await CommandAsync(socket, new { action = "openShow", name = "Test show" });
        await CommandAsync(socket, new { action = "saveShowAs", name = "Scares" });
        await CommandAsync(socket, new { action = "renameShow", name = "Scares 2" });

        Assert.Equal(["new True", "openShow Test show False", "saveAs Scares", "rename Scares 2"], _host.Commands.Calls);
    }

    [Theory]
    [InlineData("listShows")]
    [InlineData("newShow")]
    [InlineData("openShow")]
    [InlineData("saveShowAs")]
    [InlineData("renameShow")]
    public async Task An_operator_cant_use_the_file_menu(string action)
    {
        using var socket = await ConnectAsync(await PairAsync(role: "operator"));

        var result = await CommandAsync(socket, new { action, name = "Scares" });

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Empty(_host.Commands.Calls);
    }

    [Theory]
    [InlineData("gzip, deflate, br", "br", "from brotli")]
    [InlineData("gzip, deflate", "gzip", "from gzip")]   // a browser on a plain http:// address
    [InlineData("", null, "plain")]
    public async Task The_full_app_s_files_come_compressed_the_best_way_the_browser_takes(string accepts, string? encoding, string body)
    {
        var folder = Path.Combine(_paths.ConfigDirectory, "app");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "app.js"), "plain", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "app.js.br"), "from brotli", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "app.js.gz"), "from gzip", TestContext.Current.CancellationToken);
        _server.BrowserAppFolder = folder;

        // A client that doesn't unpack anything, so this reads which file was sent.
        using var request = new HttpRequestMessage(HttpMethod.Get, "app/app.js");
        if (accepts.Length > 0) request.Headers.TryAddWithoutValidation("Accept-Encoding", accepts);
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(encoding, response.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Equal(body, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // ---- the fixture library ----

    /// <summary>A custom type's JSON, as a device sends it.</summary>
    static JsonNode CustomType(string id)
    {
        var json = JsonNode.Parse(FixtureProfileLoader.ToJson(TestProfiles.Rgbw))!;
        json["id"] = id;
        return json;
    }

    [Fact]
    public async Task A_paired_device_can_fetch_the_whole_library_and_others_can_t()
    {
        _session.UseProfiles(new FixtureProfileLibrary([TestProfiles.Rgbw, TestProfiles.Spot]));
        string token = await PairAsync();
        var cancel = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("api/library?token=wrong", cancel)).StatusCode);

        var response = await _http.GetAsync($"api/library?token={Uri.EscapeDataString(token)}", cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.True(long.Parse(response.Headers.GetValues("X-Library-Version").Single()) > 0);

        using var unpacked = new StreamReader(new GZipStream(await response.Content.ReadAsStreamAsync(cancel), CompressionMode.Decompress));
        var lines = (await unpacked.ReadToEndAsync(cancel)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([TestProfiles.Rgbw.Id, TestProfiles.Spot.Id], lines.Select(l => FixtureProfileLoader.ParseJson(l).Id));
    }

    [Fact]
    public async Task A_device_saves_a_custom_type_on_the_show_computer_and_every_device_hears_the_library_changed()
    {
        using var socket = await ConnectAsync(await PairAsync());
        using var other = await ConnectAsync(await PairAsync("Laptop"));
        long before = (await NextAsync(other, "hello"))["library"]!["version"]!.GetValue<long>();

        var result = await CommandAsync(socket, new { action = "saveProfile", profile = CustomType("custom/acme/fogger") });

        Assert.True(result["ok"]!.GetValue<bool>(), result["error"]?.ToString());
        Assert.NotNull(_session.Profiles.Find("custom/acme/fogger"));
        Assert.True(File.Exists(CustomProfiles.PathOf(_paths, "custom/acme/fogger")));
        Assert.True((await NextAsync(other, "library"))["version"]!.GetValue<long>() > before);
    }

    [Theory]
    [InlineData("custom/../../evil")]
    [InlineData("custom/a/../../../evil")]
    [InlineData("generic/rgbw")]
    [InlineData("custom/a\\b")]
    public async Task A_custom_type_s_id_can_t_leave_the_profile_folder_or_replace_a_library_type(string id)
    {
        using var socket = await ConnectAsync(await PairAsync());

        var result = await CommandAsync(socket, new { action = "saveProfile", profile = CustomType(id) });

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Null(_session.Profiles.Find(id));
    }

    [Fact]
    public async Task A_device_deletes_a_custom_type_unless_the_show_uses_it()
    {
        using var socket = await ConnectAsync(await PairAsync());
        Assert.True((await CommandAsync(socket, new { action = "saveProfile", profile = CustomType("custom/acme/fogger") }))["ok"]!.GetValue<bool>());
        var fixture = new FixtureInstance { Name = "Fog", ProfileId = "custom/acme/fogger", ModeName = TestProfiles.Rgbw.Modes[0].Name };
        lock (_host.Gate) _session.Project.Fixtures.Add(fixture);

        Assert.False((await CommandAsync(socket, new { action = "deleteProfile", profile = "custom/acme/fogger" }))["ok"]!.GetValue<bool>());
        Assert.False((await CommandAsync(socket, new { action = "deleteProfile", profile = TestProfiles.Rgbw.Id }))["ok"]!.GetValue<bool>());

        lock (_host.Gate) _session.Project.Fixtures.Remove(fixture);
        Assert.True((await CommandAsync(socket, new { action = "deleteProfile", profile = "custom/acme/fogger" }))["ok"]!.GetValue<bool>());
        Assert.Null(_session.Profiles.Find("custom/acme/fogger"));
    }

    [Fact]
    public async Task An_operator_can_t_change_the_library()
    {
        using var socket = await ConnectAsync(await PairAsync(role: "operator"));

        Assert.False((await CommandAsync(socket, new { action = "saveProfile", profile = CustomType("custom/acme/fogger") }))["ok"]!.GetValue<bool>());
        Assert.False((await CommandAsync(socket, new { action = "downloadLibrary" }))["ok"]!.GetValue<bool>());
        Assert.Null(_session.Profiles.Find("custom/acme/fogger"));
    }

    [Fact]
    public async Task Undo_can_ask_for_the_show_s_edits_only_leaving_the_timeline_s_alone()
    {
        using var socket = await ConnectAsync(await PairAsync());
        lock (_host.Gate) _document.Record("Rename the show", () => _session.Project.Name = "Other", ShowDocument.Shallow(_session.Project));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        lock (_host.Gate) _document.Record("Change length", () => _main.Length = S(99), ShowDocument.Shallow(_main));
        Assert.True((await CommandAsync(socket, new { action = "watch", timeline = _main.Id.ToString() }))["ok"]!.GetValue<bool>());

        Assert.True((await CommandAsync(socket, new { action = "undo", show = true }))["ok"]!.GetValue<bool>());

        Assert.Equal("Haunt 2026", _session.Project.Name);
        Assert.Equal(S(99), _main.Length);
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

    // ---- the Lighting screens ----

    static async Task<OutputSetupState> NextOutputsAsync(ClientWebSocket socket, Func<OutputSetupState, bool> until)
    {
        while (true)
        {
            var state = ShowJson.FromNode<OutputSetupState>((await NextAsync(socket, "outputs"))["state"]!)!;
            if (until(state)) return state;
        }
    }

    [Fact]
    public async Task A_DMX_setup_screen_on_a_device_sees_the_show_computer_s_adapters_and_picks_one()
    {
        _finder.Plug("/dev/ttyFAKE0", new DmxAdapterInfo("/dev/ttyFAKE0", DmxAdapterKind.OpenDmx, "SN1"));
        await _outputSetup.ApplyAsync();
        using var laptop = await ConnectAsync(await PairAsync("Laptop"));
        await NextAsync(laptop, "hello");

        Assert.True((await CommandAsync(laptop, new { action = "watchOutputs", on = true }))["ok"]!.GetValue<bool>());
        var state = await NextOutputsAsync(laptop, s => s.Adapters.Count > 0);
        var universe = Assert.Single(state.Universes);
        string adapter = universe.Choices.Single(c => c.Contains("/dev/ttyFAKE0"));
        Assert.Equal("Simulator (no hardware)", universe.Selected);

        Assert.True((await CommandAsync(laptop, new { action = "assignOutput", universe = universe.Universe, choice = adapter }))["ok"]!.GetValue<bool>());

        state = await NextOutputsAsync(laptop, s => s.Universes[0].Selected == adapter);
        Assert.Equal(new OpenDmxDeviceConfig("/dev/ttyFAKE0", "SN1"), _machine.FindOutput(universe.Universe));
    }

    [Fact]
    public async Task A_device_can_only_pick_a_device_the_show_computer_offered()
    {
        using var laptop = await ConnectAsync(await PairAsync("Laptop"));
        await NextAsync(laptop, "hello");

        var refused = await CommandAsync(laptop, new { action = "assignOutput", universe = 1, choice = "/etc/passwd" });

        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Contains("isn't available", refused["error"]!.GetValue<string>());
        Assert.Null(_machine.FindOutput(1));
    }

    [Fact]
    public async Task Adapters_are_only_looked_for_while_a_device_has_the_setup_screen_open()
    {
        using var laptop = await ConnectAsync(await PairAsync("Laptop"));
        await NextAsync(laptop, "hello");
        Assert.Equal(0, _finder.Listings);

        await CommandAsync(laptop, new { action = "watchOutputs", on = true });
        await Eventually.True(() => _finder.Listings == 1, "the show computer has looked");

        laptop.Abort();   // the laptop goes without closing its screen
        await Eventually.True(() => _server.Connected.Count == 0, "the connection has gone");
        _finder.Plug("/dev/ttyFAKE1", new DmxAdapterInfo("/dev/ttyFAKE1", DmxAdapterKind.OpenDmx, "SN2"));
        _outputSetup.Tick();
        _outputSetup.Tick();
        Assert.Equal(1, _finder.Listings);
    }

    [Fact]
    public async Task Faders_on_a_device_set_levels_on_the_show_computer_and_it_sends_them_back()
    {
        using var laptop = await ConnectAsync(await PairAsync("Laptop"));
        await NextAsync(laptop, "hello");
        await CommandAsync(laptop, new { action = "watchLevels", universe = 1 });
        var first = await NextAsync(laptop, "levels");
        Assert.Contains("universe 1", first["source"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        Assert.True((await CommandAsync(laptop, new { action = "setChannel", universe = 1, address = 5, value = 200 }))["ok"]!.GetValue<bool>());

        var frame = new byte[512];
        Assert.True(_session.Engine.TryCopyUniverse(1, frame, out _));
        Assert.Equal(200, frame[4]);
        while (Convert.FromBase64String((await NextAsync(laptop, "levels"))["levels"]!.GetValue<string>())[4] != 200) { }

        Assert.True((await CommandAsync(laptop, new { action = "clearUniverse", universe = 1 }))["ok"]!.GetValue<bool>());
        _session.Engine.TryCopyUniverse(1, frame, out _);
        Assert.Equal(0, frame[4]);
    }

    [Fact]
    public async Task A_fader_out_of_range_says_why()
    {
        using var laptop = await ConnectAsync(await PairAsync("Laptop"));
        await NextAsync(laptop, "hello");

        var refused = await CommandAsync(laptop, new { action = "setChannel", universe = 1, address = 600, value = 1 });

        Assert.Contains("out of range", refused["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("scanOutputs")]
    [InlineData("assignOutput")]
    [InlineData("setChannel")]
    [InlineData("clearUniverse")]
    [InlineData("setFixtureChannel")]
    [InlineData("setFixtureControl")]
    public async Task An_operator_can_t_change_outputs_or_levels(string action)
    {
        using var phone = await ConnectAsync(await PairAsync("Phone", "operator"));
        await NextAsync(phone, "hello");

        var refused = await CommandAsync(phone, new { action, universe = 1, address = 5, value = 200, choice = "x" });

        Assert.Contains("operator", refused["error"]!.GetValue<string>());
        var frame = new byte[512];
        _session.Engine.TryCopyUniverse(1, frame, out _);
        Assert.Equal(0, frame[4]);
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
