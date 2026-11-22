using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Storage;
using GhostShow.Server;
using GhostShow.Web;

namespace GhostShow.App.ViewModels;

/// <summary>
/// View → Remote Access…: lets devices on the show's network (an iPad, a laptop, a phone) open GhostShow in their
/// browser. The screen over the server's <see cref="ShowRemote"/>: switching it on starts the web remote; devices pair
/// with the PIN shown here, as an editor or an operator, and stay paired until removed here. Off until switched on; it
/// stays on (it starts with the app) until switched off. See docs/plan-remote.md.
/// </summary>
public sealed partial class RemoteViewModel : ObservableObject, IAsyncDisposable
{
    readonly ShowRemote _remote;

    internal RemoteViewModel(ShowRemote remote)
    {
        _remote = remote;
        _remote.Changed += Refresh;   // on the show's thread: this one
        Port = remote.Port;
        Refresh();
    }

    public RemoteServer Server => _remote.Server;

    /// <summary>Where the full app is, for a device: the first address with /app/ (null if it isn't served).</summary>
    public string? FullAppAddress => Server.BrowserAppFolder is null ? null : Addresses.FirstOrDefault() is { } first ? first + "app/" : null;

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

    public string Pin => Server.Pairing.Pin;

    public IReadOnlyList<string> Addresses => Server.Addresses;

    /// <summary>What the QR code holds: the first address (a phone's camera opens it).</summary>
    public string? QrText => Addresses.FirstOrDefault();

    public ObservableCollection<RemoteDeviceRow> Devices { get; } = new();

    public int ConnectedCount => Server.Connected.Count;

    /// <summary>For the menu bar's indicator: "2 devices", "1 device", "On".</summary>
    public string ConnectedLabel => ConnectedCount switch { 0 => "Remote on", 1 => "1 device", var n => $"{n} devices" };

    /// <summary>At startup: back on if it was left on.</summary>
    public async Task StartIfEnabledAsync() => Failed(await _remote.StartIfEnabledAsync());

    public async Task SetEnabledAsync(bool on) => Failed(await _remote.SetEnabledAsync(on, (int)Math.Clamp(Port, 1024, 65535)));

    /// <summary>It couldn't start: says why, and shows it off.</summary>
    void Failed(string? problem)
    {
        if (problem is null) return;
        Refresh();
        Status = problem;
        OnPropertyChanged(nameof(IsEnabled));
    }

    [RelayCommand]
    void NewPin()
    {
        _remote.NewPin();
        OnPropertyChanged(nameof(Pin));
    }

    /// <summary>Another show was opened (on the UI thread): every device gets it.</summary>
    public void ShowReplaced() => _remote.ShowReplaced();

    internal void Remove(RemoteDeviceRow row) => _remote.Remove(row.Id);

    internal void SetRole(RemoteDeviceRow row, RemoteRole role) => _remote.SetRole(row.Id, role);

    void Refresh()
    {
        IsOn = _remote.IsOn;
        OnPropertyChanged(nameof(IsEnabled));
        var connected = Server.Connected;
        var rows = Server.Pairing.Devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
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

    public async ValueTask DisposeAsync() => await _remote.DisposeAsync();
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
