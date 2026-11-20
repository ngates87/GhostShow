using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.ViewModels;
using GhostShow.Browser.Remote;
using GhostShow.Core.Engine;

namespace GhostShow.Browser;

/// <summary>
/// The shared main view's model in a device's browser: the screens work on this page's copy of the show, and what
/// needs the show computer is asked of it over the connection. Screens that haven't come across yet say they're on
/// the show computer (docs/plan-shared-view.md, steps 4-7).
/// </summary>
sealed class BrowserShellViewModel : ShellViewModel
{
    readonly RemoteLink _link;
    readonly FileMenu _fileMenu;
    readonly DispatcherTimer _tick;
    long _noticeNumber;

    public BrowserShellViewModel(ShowSession session, ShellServices services, RemoteLink link, FileMenu fileMenu) : base(session, services)
    {
        _link = link;
        _fileMenu = fileMenu;
        BottomWorkspace = new BottomWorkspaceViewModel(Playback, null, Lighting, null);
        ButtonsPart = new BrowserButtonsViewModel(session, link);
        ShowEditor();   // a device always has the show computer's show open
        // As often as the desktop's screens refresh; the show's state itself arrives with each status.
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        _tick.Start();
    }

    public override bool IsShowComputer => false;
    protected override string ShowName => Session.Project.Name;

    public override object AudioPage { get; } = new OnShowComputerViewModel("The Audio screen");
    public override object InputsPage { get; } = new OnShowComputerViewModel("The Inputs screen");
    public override object DiagnosticsPage { get; } = new OnShowComputerViewModel("Diagnostics");
    public override object ButtonsPart { get; }
    public BrowserStatusViewModel Status { get; } = new();
    public override object? StatusPart => Status;

    /// <summary>Edits are saved on the show computer, which says so in its status.</summary>
    protected override void MarkDirty() { }

    protected override void BlackoutShow() => _link.Send("blackout");

    /// <summary>Sounds are chosen on the show computer for now (uploading one is planned).</summary>
    protected override Task<string?> PickAudioFileAsync()
    {
        ShowNotice("Sounds are added on the show computer for now.");
        return Task.FromResult<string?>(null);
    }

    protected override Task NewShowAsync()
    {
        _fileMenu.New();
        return Task.CompletedTask;
    }

    protected override Task OpenShowAsync()
    {
        _fileMenu.Open();
        return Task.CompletedTask;
    }

    protected override Task SaveShowAsync()
    {
        _fileMenu.Save();
        return Task.CompletedTask;
    }

    protected override Task Rename()
    {
        _fileMenu.Rename();
        return Task.CompletedTask;
    }

    protected override void ReportError(string message) => ShowNotice(message);

    /// <summary>A message in the status bar, for a few seconds.</summary>
    public void ShowNotice(string text)
    {
        StatusMessage = text;
        long number = ++_noticeNumber;
        DispatcherTimer.RunOnce(() =>
        {
            if (number == _noticeNumber) StatusMessage = "";
        }, TimeSpan.FromSeconds(6));
    }

    /// <summary>The show computer's hello: who this device is paired as.</summary>
    public void OnHello(string who, bool isOperator)
    {
        Status.Who = who;
        Status.ShowsSaveState = !isOperator;
        IsEditor = !isOperator;
    }

    /// <summary>From each status: the warnings, and whether the show is saved.</summary>
    public void OnStatus(IReadOnlyList<string> warnings, bool unsaved, bool needsFirstSave)
    {
        ShowWarnings(warnings);
        IsDirty = unsaved;
        Status.SaveState = !unsaved ? "Saved"
            : needsFirstSave ? "Not saved: File → Save gives it a name"
            : "Saving…";
        OnPropertyChanged(nameof(Title));   // the show may have been renamed
    }
}

/// <summary>The right of the status bar on a device: who it's paired as, and whether the show is saved.</summary>
sealed partial class BrowserStatusViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Who { get; set; } = "";

    [ObservableProperty]
    public partial string SaveState { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowsSaveState { get; set; } = true;
}

/// <summary>
/// The show's buttons on a device, until the Buttons panel comes across (docs/plan-shared-view.md, step 4): a column of
/// buttons that press, on the show computer.
/// </summary>
sealed class BrowserButtonsViewModel
{
    readonly ShowSession _session;
    readonly RemoteLink _link;

    public BrowserButtonsViewModel(ShowSession session, RemoteLink link)
    {
        _session = session;
        _link = link;
        Refresh();
        session.Document.Changed += _ => Dispatcher.UIThread.Post(Refresh);
    }

    public ObservableCollection<BrowserButtonRow> Buttons { get; } = new();

    /// <summary>The show's buttons now (another show, or the buttons were edited).</summary>
    public void Refresh()
    {
        var rows = _session.Project.Buttons.Select(b => new BrowserButtonRow(b.Id, b.Name, b.Color, _link)).ToList();
        if (rows.Select(r => (r.Id, r.Name, r.Color)).SequenceEqual(Buttons.Select(r => (r.Id, r.Name, r.Color)))) return;
        Buttons.Clear();
        foreach (var row in rows) Buttons.Add(row);
    }
}

/// <summary>One show button on a device: pressing it presses it on the show computer.</summary>
sealed partial class BrowserButtonRow(Guid id, string name, string? color, RemoteLink link)
{
    public Guid Id => id;
    public string Name => name;
    public string? Color => color;
    public IBrush? Background { get; } = color is not null && Avalonia.Media.Color.TryParse(color, out var parsed) ? new ImmutableSolidColorBrush(parsed) : null;

    [RelayCommand]
    void Press() => link.Send("press", ("button", id.ToString()));
}
