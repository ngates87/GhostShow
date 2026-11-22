using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using GhostShow.App.Client;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Tests;

/// <summary>
/// The browser's connection code, run natively (docs/plan-remote.md, step 5): a copy of the show kept in step with the
/// show computer's over the web remote, as the window will be.
/// </summary>
public class NativeClientTests
{
    static void Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting until {what}.");
            WindowRig.Pump(20);
        }
    }

    static T Await<T>(Task<T> task)
    {
        Until(() => task.IsCompleted, "the task finished");
        return task.GetAwaiter().GetResult();
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public Task A_native_client_gets_the_show_and_edits_go_both_ways() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var remote = rig.ViewModel.Remote;
        remote.Port = FreePort();
        Await(remote.SetEnabledAsync(true).ContinueWith(_ => true));
        Assert.True(remote.IsOn, remote.Status);
        string origin = $"http://127.0.0.1:{remote.Server.Port}";
        using var http = new HttpClient();
        var paired = Await(http.PostAsync($"{origin}/api/pair", new StringContent($$"""{"pin":"{{remote.Pin}}","name":"This computer"}""", Encoding.UTF8, "application/json")));
        string token = JsonNode.Parse(Await(paired.Content.ReadAsStringAsync()))!["token"]!.GetValue<string>();

        var show = new RemoteShow(new RemoteEndpoint(origin, () => token, "Not paired.", "Not paired any more."), FixtureProfileLoader.LoadBuiltIn());
        var copy = show.Session;
        int arrived = 0, statuses = 0;
        show.Arrived += _ => arrived++;
        show.StatusArrived += _ => statuses++;
        using var stop = new CancellationTokenSource();
        var running = show.RunAsync(stop.Token);

        Until(() => arrived == 1, "the show arrived");
        Assert.Equal(rig.Session.Project.Timelines.Select(t => t.Id), copy.Project.Timelines.Select(t => t.Id));
        Assert.Equal(rig.Session.Project.Fixtures.Select(f => f.Name), copy.Project.Fixtures.Select(f => f.Name));

        // An edit here reaches the show computer's show...
        var timeline = copy.Project.Timelines[0];
        copy.Document.Record("Rename timeline", () => timeline.Name = "Renamed in the window", timeline);
        Until(() => rig.Session.Project.Timelines[0].Name == "Renamed in the window", "the show computer has the edit");

        // ...and one there reaches this copy.
        var track = rig.Session.Project.Timelines[0].Tracks[0];
        rig.Session.Document.Record("Rename track", () => track.Name = "Renamed on the show computer", track);
        Until(() => copy.Project.Timelines[0].Tracks[0].Name == "Renamed on the show computer", "the copy has the show computer's edit");
        Assert.Equal(1, arrived);   // in step all along: no resync

        // Playing on the show computer shows here.
        var main = rig.Session.Project.Timelines[0].Id;
        rig.ViewModel.ShowPlayback.Play(main);
        Until(() => show.Playback.StatusOf(main).State == Core.Playback.TransportState.Playing, "the copy hears it's playing");
        Assert.True(statuses > 0);

        stop.Cancel();
        Until(() => running.IsCompleted, "the connection stopped");
    });

    [Fact]
    public Task The_window_s_own_connection_works_with_Remote_Access_off_and_isn_t_a_device() => Ui.Run(() =>
    {
        using var rig = new WindowRig();
        var remote = rig.ViewModel.Remote;
        Assert.False(remote.IsOn);
        var show = new RemoteShow(WindowConnection.To(remote.Show), FixtureProfileLoader.LoadBuiltIn());
        int arrived = 0;
        show.Arrived += _ => arrived++;
        using var stop = new CancellationTokenSource();
        var running = show.RunAsync(stop.Token);

        Until(() => arrived == 1, "the show arrived");
        Assert.Equal(rig.Session.Project.Timelines.Select(t => t.Id), show.Session.Project.Timelines.Select(t => t.Id));
        Assert.Equal(0, remote.ConnectedCount);   // the window isn't a device

        var timeline = show.Session.Project.Timelines[0];
        show.Session.Document.Record("Rename timeline", () => timeline.Name = "Renamed in the window", timeline);
        Until(() => rig.Session.Project.Timelines[0].Name == "Renamed in the window", "the show computer has the edit");
        var track = rig.Session.Project.Timelines[0].Tracks[0];
        rig.Session.Document.Record("Rename track", () => track.Name = "Renamed on the show computer", track);
        Until(() => show.Session.Project.Timelines[0].Tracks[0].Name == "Renamed on the show computer", "the window has the show computer's edit");

        var loading = show.Library.LoadAsync();
        Until(() => loading.IsCompleted, "the library loaded");
        Assert.Equal(rig.Session.Profiles.Profiles.Count, show.Session.Profiles.Profiles.Count);

        stop.Cancel();
        Until(() => running.IsCompleted, "the connection stopped");
    });
}
