using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShow.Core.Storage;
using GhostShow.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Tests.Web;

/// <summary>A show's thread for tests: actions wait until the test runs them.</summary>
sealed class ManualShowThread : IShowThread
{
    readonly ConcurrentQueue<Action> _waiting = new();

    public int Waiting => _waiting.Count;

    public void Post(Action action) => _waiting.Enqueue(action);

    public void RunAll()
    {
        while (_waiting.TryDequeue(out var action)) action();
    }
}

/// <summary>Remote Access in the server (<see cref="ShowRemote"/>), over the same fake host as the server's own tests.</summary>
public sealed partial class RemoteServerTests
{
    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    (ShowRemote Remote, RemoteSettings Settings, List<string> Saved, ManualShowThread Thread) NewRemote(RemoteSettings? settings = null)
    {
        settings ??= new RemoteSettings();
        var saved = new List<string>();
        var thread = new ManualShowThread();
        var remote = new ShowRemote(_host, settings, () =>
        {
            lock (saved) saved.Add($"on={settings.Enabled} port={settings.Port} devices={settings.Devices.Count}");
            return Task.CompletedTask;
        }, thread, NullLogger.Instance, browserApp: null);
        return (remote, settings, saved, thread);
    }

    [Fact]
    public async Task Remote_access_switches_on_on_its_port_remembers_it_and_switches_off()
    {
        var (remote, settings, saved, _) = NewRemote();
        await using var _ = remote;
        int changes = 0;
        remote.Changed += () => changes++;
        int port = FreePort();

        Assert.Null(await remote.SetEnabledAsync(true, port));

        Assert.True(remote.IsOn);
        Assert.Equal((true, port), (settings.Enabled, settings.Port));
        Assert.Equal($"on=True port={port} devices=0", Assert.Single(saved));
        using var http = new HttpClient();
        Assert.True((await http.GetAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken)).IsSuccessStatusCode);

        Assert.Null(await remote.SetEnabledAsync(false, port));
        Assert.False(remote.IsOn);
        Assert.False(settings.Enabled);
        Assert.Equal(2, changes);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public async Task A_port_something_else_uses_says_so_and_stays_off()
    {
        var (remote, settings, saved, _) = NewRemote();
        await using var _ = remote;

        string? problem = await remote.SetEnabledAsync(true, _server.Port);   // the test's own server is on it

        Assert.Equal($"Couldn't start on port {_server.Port}: something else is using it. Pick another port and switch it on again.", problem);
        Assert.False(remote.IsOn);
        Assert.Equal((false, RemoteSettings.DefaultPort), (settings.Enabled, settings.Port));
        Assert.Empty(saved);
    }

    [Fact]
    public async Task It_starts_again_if_it_was_left_on()
    {
        var (remote, _, _, _) = NewRemote(new RemoteSettings { Enabled = true, Port = FreePort() });
        await using var _ = remote;

        Assert.Null(await remote.StartIfEnabledAsync());

        Assert.True(remote.IsOn);
    }

    [Fact]
    public async Task A_device_pairing_is_saved_and_told_on_the_show_s_thread()
    {
        var (remote, settings, saved, thread) = NewRemote();
        await using var _ = remote;
        int port = FreePort();
        Assert.Null(await remote.SetEnabledAsync(true, port));
        int changes = 0;
        remote.Changed += () => changes++;
        saved.Clear();

        using var http = new HttpClient();
        var paired = await http.PostAsync($"http://127.0.0.1:{port}/api/pair",
            new StringContent($$"""{"pin":"{{remote.Server.Pairing.Pin}}","name":"Stage iPad"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.True(paired.IsSuccessStatusCode);
        await Eventually.True(() => thread.Waiting > 0, "the pairing is posted to the show's thread");
        Assert.Equal(0, changes);   // not on the server's thread

        thread.RunAll();

        Assert.Equal(1, changes);
        Assert.Equal($"on=True port={port} devices=1", Assert.Single(saved));
        Assert.Equal("Stage iPad", Assert.Single(settings.Devices).Name);
    }
}

public class ShowThreadTests
{
    [Fact]
    public async Task Invoke_runs_on_the_show_s_thread_and_returns_its_result_or_its_exception()
    {
        var thread = new ManualShowThread();

        var answer = thread.InvokeAsync(() => 42);
        var failure = thread.InvokeAsync<int>(() => throw new InvalidOperationException("No show"));
        Assert.False(answer.IsCompleted);   // waits for the thread

        thread.RunAll();

        Assert.Equal(42, await answer);
        Assert.Equal("No show", (await Assert.ThrowsAsync<InvalidOperationException>(() => failure)).Message);
    }

    [Fact]
    public void A_heartbeat_never_has_more_than_one_tick_waiting()
    {
        var thread = new ManualShowThread();
        int ticks = 0;
        using var heartbeat = new ShowHeartbeat(thread, Timeout.InfiniteTimeSpan, () => ticks++);

        for (int i = 0; i < 5; i++) heartbeat.Due();   // the thread is busy: one waits, the rest are skipped
        Assert.Equal(1, thread.Waiting);
        thread.RunAll();
        Assert.Equal(1, ticks);

        heartbeat.Due();   // done: the next one is posted
        thread.RunAll();
        Assert.Equal(2, ticks);
    }

    [Fact]
    public async Task A_heartbeat_ticks_by_itself()
    {
        var thread = new ManualShowThread();
        using var heartbeat = new ShowHeartbeat(thread, TimeSpan.FromMilliseconds(10), () => { });

        await Eventually.True(() => thread.Waiting == 1, "a tick is posted");
    }
}
