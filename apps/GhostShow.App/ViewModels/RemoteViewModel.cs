using System.Collections.ObjectModel;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Dmx;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Web;
using Microsoft.Extensions.Logging;

namespace GhostShow.App.ViewModels;

/// <summary>The web remote's host: the desktop app, with the show edited on the UI thread.</summary>
sealed class AppRemoteHost(ShowSession session, ShowPlayback playback, PlaybackViewModel focus, IShowControl control,
                            Func<IReadOnlyList<string>> warnings, AudioDeviceList audioDevices, Func<bool> hasUnsavedChanges,
                            Func<bool> needsFirstSave, IFixtureLibrary library, IOutputSetup outputs, ILiveLevels levels) : IRemoteHost
{
    public Task<T> InvokeAsync<T>(Func<T> function) => Dispatcher.UIThread.InvokeAsync(function).GetTask();
    public ShowSession Session => session;
    public ShowPlayback Playback => playback;
    public Guid? FocusedTimeline => focus.FocusedId;
    public IShowControl Control => control;
    public IReadOnlyList<string> Warnings => warnings();
    public bool HasUnsavedChanges => hasUnsavedChanges();
    public bool NeedsFirstSave => needsFirstSave();
    public IFixtureLibrary Library => library;
    public IOutputSetup Outputs => outputs;
    public ILiveLevels Levels => levels;
    public IReadOnlyList<string> AudioDevices => audioDevices.Names;

    public event Action? AudioDevicesChanged
    {
        add => audioDevices.Changed += value;
        remove => audioDevices.Changed -= value;
    }
}

/// <summary>
/// A device's commands, done the way the desktop's own controls do them (the transport, the Playing list, show buttons,
/// Blackout), so a device and the desktop never behave differently. On the UI thread.
/// </summary>
sealed class AppShowControl(MainViewModel app) : IShowControl
{
    PlaybackViewModel Playback => app.Playback;

    ShowTimeline? Find(Guid id) => app.Timeline.Tree.AllNodes().OfType<TimelineNode>().FirstOrDefault(n => n.Timeline.Id == id)?.Timeline;

    const string Gone = "That timeline isn't in the show any more.";

    public string? Transport(TransportAction action, TimeSpan? position)
    {
        if (Playback.Timeline is null) return "No timeline is open in the editor on the show computer.";
        switch (action)
        {
            case TransportAction.Play when !Playback.IsPlaying:
            case TransportAction.Pause when Playback.IsPlaying:
                Playback.PlayPauseCommand.Execute(null);
                break;
            case TransportAction.Stop:
                Playback.StopCommand.Execute(null);
                break;
            case TransportAction.Restart:
                Playback.RestartCommand.Execute(null);
                break;
            case TransportAction.Seek:
                Playback.Seek(position ?? TimeSpan.Zero);
                break;
        }
        return null;
    }

    public string? Run(Guid timelineId)
    {
        if (Find(timelineId) is not { } timeline) return Gone;
        Playback.Launch(timeline);
        return null;
    }

    public string? StopTimeline(Guid timelineId)
    {
        if (Find(timelineId) is not { } timeline) return Gone;
        Playback.StopTimeline(timeline);
        return null;
    }

    public string? StopRunning(Guid runningId)
    {
        Playback.StopTimeline(runningId);
        return null;
    }

    public string? StopAll()
    {
        Playback.StopAllCommand.Execute(null);
        return null;
    }

    public string? Blackout()
    {
        app.BlackoutCommand.Execute(null);
        return null;
    }

    public string? PressButton(Guid buttonId)
    {
        if (app.Buttons.Buttons.FirstOrDefault(b => b.Button.Id == buttonId) is not { } button) return "That button isn't in the show any more.";
        app.Buttons.Press(button);
        return null;
    }

    public string? Open(Guid timelineId)
    {
        if (Find(timelineId) is not { } timeline) return Gone;
        app.Timeline.OpenTimeline(timeline);
        app.Timeline.Tree.Reveal(timeline);
        return null;
    }

    public Task<string?> SaveAsync() => app.SaveForDeviceAsync();
    public IReadOnlyList<string> SavedShows() => app.SavedShowsForDevice();
    public Task<string?> NewShowAsync(bool discardUnsaved) => app.NewShowForDeviceAsync(discardUnsaved);
    public Task<string?> OpenShowAsync(string name, bool discardUnsaved) => app.OpenShowForDeviceAsync(name, discardUnsaved);
    public Task<string?> SaveShowAsAsync(string name) => app.SaveShowAsForDeviceAsync(name);
    public Task<string?> RenameShowAsync(string name) => app.RenameShowForDeviceAsync(name);

    public string? TestSound(string? device) =>
        app.AudioDevices.PlayTestSound(device) ? null : $"Couldn't play on {device ?? "the default output"}: it isn't connected (or is still opening).";
}

/// <summary>
/// View → Remote Access…: lets devices on the show's network (an iPad, a laptop, a phone) open GhostShow in their
/// browser. Switching it on starts the web remote (<see cref="RemoteServer"/>); devices pair with the PIN shown here,
/// as an editor or an operator, and stay paired until removed here. Off until switched on; it stays on (it starts with
/// the app) until switched off. See docs/plan-remote.md.
/// </summary>
public sealed partial class RemoteViewModel : ObservableObject, IAsyncDisposable
{
    readonly RemoteServer _server;
    readonly RemoteSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly ILogger _logger;

    internal RemoteViewModel(IRemoteHost host, RemoteSettings settings, Func<Task> saveSettings, ILogger logger)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _logger = logger;
        _server = new RemoteServer(host, settings, logger) { BrowserAppFolder = FindBrowserApp() };
        // Both come from the server's threads.
        _server.Pairing.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            _ = _saveSettings();
        });
        _server.ConnectionsChanged += () => Dispatcher.UIThread.Post(Refresh);
        Port = settings.Port;
        Refresh();
    }

    public RemoteServer Server => _server;

    /// <summary>
    /// The browser build of the app (GhostShow.Browser, published), served at /app/: the folder in
    /// GHOSTSHOW_BROWSER_APP if that's set, the browser-app folder next to the program in an installed copy (see
    /// installer/build.sh), or else the one published in this source tree
    /// (apps/GhostShow.Browser/bin/Release/net10.0-browser/publish/wwwroot), found by looking up from where the app runs.
    /// Null if there's none: then only the simple remote page is served.
    /// </summary>
    static string? FindBrowserApp()
    {
        if (Environment.GetEnvironmentVariable("GHOSTSHOW_BROWSER_APP") is { Length: > 0 } set) return set;
        var installed = Path.Combine(AppContext.BaseDirectory, "browser-app");
        if (File.Exists(Path.Combine(installed, "index.html"))) return installed;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var published = Path.Combine(folder.FullName, "GhostShow.Browser", "bin", "Release", "net10.0-browser", "publish", "wwwroot");
            if (File.Exists(Path.Combine(published, "index.html"))) return published;
        }
        return null;
    }

    /// <summary>Where the full app is, for a device: the first address with /app/ (null if it isn't served).</summary>
    public string? FullAppAddress => _server.BrowserAppFolder is null ? null : Addresses.FirstOrDefault() is { } first ? first + "app/" : null;

    /// <summary>Whether devices can connect (the web remote is running).</summary>
    [ObservableProperty]
    public partial bool IsOn { get; private set; }

    /// <summary>The switch: on starts the web remote, off stops it (and disconnects every device).</summary>
    public bool IsEnabled
    {
        get => IsOn;
        set
        {
            if (value != IsOn) _ = SetEnabledAsync(value);
        }
    }

    [ObservableProperty]
    public partial decimal Port { get; set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    public string Pin => _server.Pairing.Pin;

    public IReadOnlyList<string> Addresses => _server.Addresses;

    /// <summary>What the QR code holds: the first address (a phone's camera opens it).</summary>
    public string? QrText => Addresses.FirstOrDefault();

    public ObservableCollection<RemoteDeviceRow> Devices { get; } = new();

    public int ConnectedCount => _server.Connected.Count;

    /// <summary>For the menu bar's indicator: "2 devices", "1 device", "On".</summary>
    public string ConnectedLabel => ConnectedCount switch { 0 => "Remote on", 1 => "1 device", var n => $"{n} devices" };

    /// <summary>At startup: back on if it was left on.</summary>
    public Task StartIfEnabledAsync() => _settings.Enabled ? SetEnabledAsync(true) : Task.CompletedTask;

    public async Task SetEnabledAsync(bool on)
    {
        if (on)
        {
            int port = (int)Math.Clamp(Port, 1024, 65535);
            try
            {
                await _server.StartAsync(port);
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
            {
                _logger.LogWarning(e, "The web remote couldn't start on port {Port}.", port);
                Status = $"Couldn't start on port {port}: {Reason(e)} Pick another port and switch it on again.";
                IsOn = false;
                OnPropertyChanged(nameof(IsEnabled));
                return;
            }
            _settings.Port = port;
        }
        else
        {
            await _server.StopAsync();
        }
        _settings.Enabled = on;
        IsOn = on;
        OnPropertyChanged(nameof(IsEnabled));
        Refresh();
        await _saveSettings();
    }

    static string Reason(Exception e) => e.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } || e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        ? "something else is using it." : e.Message;

    [RelayCommand]
    void NewPin()
    {
        _server.Pairing.NewPin();
        OnPropertyChanged(nameof(Pin));
    }

    /// <summary>Another show was opened (on the UI thread): every device gets it.</summary>
    public void ShowReplaced() => _server.ShowReplaced();

    internal void Remove(RemoteDeviceRow row) => _server.Remove(row.Id);

    internal void SetRole(RemoteDeviceRow row, RemoteRole role) => _server.Pairing.SetRole(row.Id, role);

    void Refresh()
    {
        var connected = _server.Connected;
        var rows = _server.Pairing.Devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new RemoteDeviceRow(d, connected.FirstOrDefault(c => c.Id == d.Id), this)).ToList();
        Devices.Clear();
        foreach (var row in rows) Devices.Add(row);
        Status = !IsOn ? "Off: nothing on the network can reach GhostShow."
               : Addresses.Count == 0 ? "On, but this computer isn't on a network devices could reach it on."
               : "On: on a device on this network, open the address below, then enter the PIN.";
        OnPropertyChanged(nameof(Pin));
        OnPropertyChanged(nameof(Addresses));
        OnPropertyChanged(nameof(QrText));
        OnPropertyChanged(nameof(FullAppAddress));
        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(ConnectedLabel));
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}

/// <summary>A paired device, in Remote Access.</summary>
public sealed partial class RemoteDeviceRow(PairedDevice device, ConnectedDevice? connection, RemoteViewModel owner) : ObservableObject
{
    public static IReadOnlyList<string> Roles { get; } = ["Editor", "Operator"];

    public Guid Id => device.Id;
    public string Name => device.Name;

    public int RoleIndex
    {
        get => device.Role == RemoteRole.Operator ? 1 : 0;
        set => owner.SetRole(this, value == 1 ? RemoteRole.Operator : RemoteRole.Editor);
    }

    public bool IsConnected => connection is not null;

    public string Seen => connection is { } c ? $"Connected from {c.Address}"
                        : device.LastSeen is { } seen ? $"Last seen {Ago(DateTimeOffset.UtcNow - seen)}"
                        : "Never connected";

    static string Ago(TimeSpan time) => time.TotalMinutes < 1 ? "just now"
        : time.TotalHours < 1 ? $"{(int)time.TotalMinutes} min ago"
        : time.TotalDays < 1 ? $"{(int)time.TotalHours} h ago"
        : $"{(int)time.TotalDays} days ago";

    [RelayCommand]
    void Remove() => owner.Remove(this);
}
