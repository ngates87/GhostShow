using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using GhostShow.Core.Dmx;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;
using GhostShow.Tests.Playback;
using GhostShow.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Tests.Web;

/// <summary>
/// What the native window would pay if it worked the show over the connection, as a device does (docs/plan-remote.md,
/// step 5): the whole show arriving, this copy loading it (as on a resync), and edits going there and back. Over
/// loopback with the real web server; the show's thread is the fake host's lock. Measures only when GHOSTSHOW_MEASURE
/// names a file to write the report to.
/// </summary>
public sealed partial class RemoteServerTests
{
    static readonly IReadOnlyList<FixtureProfile> BuiltIn = FixtureProfileLoader.LoadBuiltIn();

    [Fact]
    public async Task Measure_working_the_show_over_the_connection()
    {
        string? report = Environment.GetEnvironmentVariable("GHOSTSHOW_MEASURE");
        Assert.SkipWhen(string.IsNullOrEmpty(report), "Set GHOSTSHOW_MEASURE to a file to measure (docs/plan-remote.md, step 5).");
        var lines = new List<string>
        {
            $"Measured {DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.ProcessorCount} logical processors, .NET {Environment.Version}, loopback.",
            "",
            "| Show | Snapshot | Snapshot arrives (p50 / max) | Load it here (p50 / max) | Edit, 1 handle (p50 / max) | Edit, 200 handles (p50 / max) | Edit reaches another device (p50 / max) |",
            "|---|---|---|---|---|---|---|",
        };
        // The first run pays for loading and compiling the code (as the window's first connection would); the rest are warm.
        lines.Add(await MeasureAsync("Haunt-sized, first after starting", MakeShow(fixtures: 40, timelines: 20, events: 100)));
        lines.Add(await MeasureAsync("Haunt-sized: 20 timelines × 100 events, 40 fixtures", MakeShow(fixtures: 40, timelines: 20, events: 100)));
        lines.Add(await MeasureAsync("Stress: 1 timeline × 10,000 events, 100 fixtures", MakeShow(fixtures: 100, timelines: 1, events: 10_000)));
        lines.Add("");
        lines.Add("Load it here = parse + read the show + use it; the screens' own rebuild isn't included.");
        lines.Add("");
        lines.AddRange(Breakdown);
        File.WriteAllLines(report!, lines);
    }

    /// <summary>Lighting as the editor stores it (clips of channel envelopes), from random level events like the benchmarks'.</summary>
    static Project MakeShow(int fixtures, int timelines, int events)
    {
        var patch = Enumerable.Range(0, fixtures)
            .Select(i => new FixtureInstance { Name = $"Par {i + 1}", ProfileId = "generic/rgbw", ModeName = "5 channel (dimmer first)", StartAddress = 1 + i * 5 })
            .ToList();
        var random = new Random(42);
        ChannelFunction[] controls = [ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue, ChannelFunction.White];
        var project = new Project { Name = "Measure", Fixtures = patch, Timelines = [] };
        for (int t = 0; t < timelines; t++)
        {
            var tracks = Enumerable.Range(0, 4).Select(i => new TimelineTrack { Name = $"Lighting {i + 1}", Kind = TrackKind.Lighting }).ToList();
            var length = TimeSpan.FromSeconds(events >= 1000 ? 600 : 120);
            for (int i = 0; i < events; i++)
                tracks[i % tracks.Count].Events.Add(new LevelsEvent
                {
                    FixtureId = patch[random.Next(patch.Count)].Id,
                    Start = TimeSpan.FromSeconds(random.NextDouble() * (length.TotalSeconds - 6)),
                    Fade = TimeSpan.FromSeconds(random.Next(6)),
                    Levels = controls.Take(random.Next(1, 6)).Select(c => new Level((byte)random.Next(256), Control: c)).ToList(),
                });
            var timeline = new ShowTimeline { Name = $"Room {t + 1}", Length = length, Tracks = [.. tracks] };
            LevelEventConversion.Upgrade(timeline);
            project.Timelines.Add(timeline);
        }
        return project;
    }

    async Task<string> MeasureAsync(string name, Project project)
    {
        var cancel = TestContext.Current.CancellationToken;
        var session = new ShowSession(new ShowEngine(), new FixtureProfileLibrary(BuiltIn), NullLogger<ShowSession>.Instance);
        session.Use(project);
        var playback = new ShowPlayback(session, new PlaybackManager(session.Engine, new FakeAudioOutput(), new ManualClock()), _audioFiles);
        var host = new Host(session, playback, _library, _outputSetup, new LocalLiveLevels(session.Engine, _outputManager.Find));
        await using var server = new RemoteServer(host, new RemoteSettings(), NullLogger.Instance);
        await server.StartAsync(0);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        async Task<ClientWebSocket> Connect(string device)
        {
            var paired = await http.PostAsJsonAsync("api/pair", new { pin = server.Pairing.Pin, name = device, role = "editor" }, cancel);
            var token = (await paired.Content.ReadFromJsonAsync<JsonObject>(cancel))!["token"]!.GetValue<string>();
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/api/live?token={token}"), cancel);
            return socket;
        }

        // The whole show arriving, and loading it into a copy here (what a resync costs), five times each.
        var arrive = new List<double>();
        var load = new List<double>();
        List<double> parse = [], fromSnapshot = [], use = [], serverSnapshot = [];
        int bytes = 0;
        ShowSession? copy = null;
        for (int i = 0; i < 5; i++)
        {
            var clock = Stopwatch.StartNew();
            using var socket = await Connect($"Window {i}");
            var raw = await NextRawAsync(socket, "hello");
            arrive.Add(clock.Elapsed.TotalMilliseconds);
            bytes = raw.Length;
            clock.Restart();
            var hello = JsonNode.Parse(raw)!.AsObject();
            parse.Add(clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            var shown = ShowDocument.FromSnapshot(hello["show"]!);
            fromSnapshot.Add(clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            copy = new ShowSession(new ShowEngine(), new FixtureProfileLibrary(BuiltIn), NullLogger<ShowSession>.Instance);
            copy.Use(shown);
            use.Add(clock.Elapsed.TotalMilliseconds);
            load.Add(parse[^1] + fromSnapshot[^1] + use[^1]);
            clock.Restart();
            lock (host.Gate) host.Session.Document.Snapshot().ToJsonString();
            serverSnapshot.Add(clock.Elapsed.TotalMilliseconds);
        }
        Breakdown.Add($"- {name}: here, parse {Spread(parse)}, read the show {Spread(fromSnapshot)}, use it {Spread(use)}; "
                      + $"on the show computer, the show as JSON {Spread(serverSnapshot)}.");

        // Edits made in this copy, sent as the browser sends them (RemoteEdits), answered by the show computer.
        using var window = await Connect("Window");
        using var other = await Connect("Laptop");
        await NextRawAsync(window, "hello");
        await NextRawAsync(other, "hello");
        var document = copy!.Document;
        var lanes = copy.Project.Timelines[0].Tracks.SelectMany(t => t.Events).OfType<LightingClip>().SelectMany(c => c.Lanes).ToList();
        var points = lanes.SelectMany(l => l.Points.Take(Math.Max(0, l.Points.Count - 1)).Select(p => (Lane: l, Point: p))).ToList();
        async Task<(List<double> There, List<double> Other)> Edits(int handles)
        {
            var there = new List<double>();
            var toOther = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                var moving = points.Skip(i * handles % Math.Max(1, points.Count - handles)).Take(handles).ToList();
                var change = document.Record(handles == 1 ? "Move envelope handle" : $"Move {handles} envelope handles",
                    () => { foreach (var (_, p) in moving) p.Value = (byte)(p.Value ^ 1); }, moving.Select(m => m.Lane).Distinct().ToArray<object>())!;
                int id = 1000 + Interlocked.Increment(ref _commands);
                var command = new JsonObject
                {
                    ["type"] = "command", ["id"] = id, ["action"] = "edit", ["description"] = change.Description,
                    ["ops"] = new JsonArray([.. change.Ops.Select(document.Encode)]),
                };
                var clock = Stopwatch.StartNew();
                await window.SendAsync(Encoding.UTF8.GetBytes(command.ToJsonString()), WebSocketMessageType.Text, true, cancel);
                var answered = Task.Run(async () =>
                {
                    while (JsonNode.Parse(await NextRawAsync(window, "result"))!["id"]!.GetValue<int>() != id) { }
                    return clock.Elapsed.TotalMilliseconds;
                }, cancel);
                var reached = Task.Run(async () =>
                {
                    await NextRawAsync(other, "change");
                    return clock.Elapsed.TotalMilliseconds;
                }, cancel);
                there.Add(await answered);
                toOther.Add(await reached);
            }
            return (there, toOther);
        }
        var (one, _) = await Edits(1);
        var (many, reachedOther) = await Edits(Math.Min(200, points.Count / 2));

        return $"| {name} | {bytes / 1024.0 / 1024.0:0.0} MB | {Spread(arrive)} | {Spread(load)} | {Spread(one)} | {Spread(many)} | {Spread(reachedOther)} |";
    }

    static string Spread(List<double> ms) => $"{Percentile(ms, 0.5):0.0} / {ms.Max():0.0} ms";

    static readonly List<string> Breakdown = [];

    static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Round(p * (sorted.Count - 1)))];
    }

    /// <summary>The next message of a type, as received (skipping others).</summary>
    static async Task<string> NextRawAsync(ClientWebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var buffer = new byte[1 << 16];
        var marker = $"\"type\":\"{type}\"";
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
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            if (text.Contains(marker, StringComparison.Ordinal)) return text;
        }
    }
}
