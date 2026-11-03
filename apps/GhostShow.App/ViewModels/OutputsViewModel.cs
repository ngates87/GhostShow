using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Dmx;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Model;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The DMX setup screen: the show's universes and, for the show computer, which device each one is output on. Universes
/// are edited through the show's document, so they can be undone and reach other devices; the rows follow the show (see
/// <see cref="Sync"/>). The hardware itself is behind <see cref="IOutputSetup"/>, since it's only on the show computer.
/// </summary>
public sealed partial class OutputsViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly IOutputSetup _setup;
    readonly Action<string> _reportError;

    public OutputsViewModel(ShowSession session, IOutputSetup setup, Action<string> reportError, IEditHistory? history = null)
    {
        _session = session;
        _setup = setup;
        _reportError = reportError;
        History = new ShowHistoryViewModel(history ?? session.Document);
        session.Document.Changed += change =>
        {
            if (change.Touches("universes")) Sync();
        };
        setup.Changed += ShowHardware;
        Reload();
    }

    public ShowHistoryViewModel History { get; }

    public ObservableCollection<UniverseRowViewModel> Universes { get; } = new();
    public ObservableCollection<AdapterRow> Adapters { get; } = new();

    [ObservableProperty]
    public partial string DetectStatus { get; set; } = OutputSetupState.Empty.DetectStatus;

    /// <summary>Rebuilds the rows from the show, e.g. after New or Open.</summary>
    public void Reload()
    {
        Universes.Clear();
        Sync();
    }

    /// <summary>This screen came on show, or went away: adapters are only looked for while it's on show.</summary>
    public void SetOnScreen(bool onScreen) => _setup.SetWatching(this, onScreen);

    /// <summary>Forgets every earlier probe and checks all ports again.</summary>
    [RelayCommand]
    Task ScanAgain() => _setup.ScanAgainAsync();

    [RelayCommand]
    void AddUniverse()
    {
        int next = _session.Project.Universes.Select(u => u.Number).DefaultIfEmpty(Project.FirstUniverse - 1).Max() + 1;
        var universe = new UniverseConfig { Number = next, Name = $"Universe {next}" };
        _session.Document.Record("Add universe", () => _session.Project.Universes.Add(universe), [ShowDocument.Shallow(_session.Project)]);
    }

    [RelayCommand]
    void RemoveUniverse(UniverseRowViewModel row) =>
        _session.Document.Record("Remove universe", () => _session.Project.Universes.Remove(row.Model), [ShowDocument.Shallow(_session.Project)]);

    UniverseRowViewModel NewRow(UniverseConfig universe) => new(universe,
        (description, change, mergeKey) => _session.Document.Record(description, change, [ShowDocument.Shallow(universe)], mergeKey),
        OnDevicePicked);

    /// <summary>Matches the rows to the show's universes, keeping rows that stay.</summary>
    void Sync()
    {
        var model = _session.Project.Universes;
        for (int i = 0; i < model.Count; i++)
        {
            if (i < Universes.Count && Universes[i].Model == model[i]) continue;
            int existing = -1;
            for (int j = i + 1; j < Universes.Count && existing < 0; j++)
                if (Universes[j].Model == model[i]) existing = j;
            if (existing >= 0) Universes.Move(existing, i);
            else Universes.Insert(i, NewRow(model[i]));
        }
        while (Universes.Count > model.Count) Universes.RemoveAt(Universes.Count - 1);
        foreach (var row in Universes) row.Refresh();
        ShowHardware();
    }

    void ShowHardware()
    {
        var state = _setup.State;
        DetectStatus = state.DetectStatus;
        if (!state.Adapters.SequenceEqual(Adapters))
        {
            Adapters.Clear();
            foreach (var adapter in state.Adapters) Adapters.Add(adapter);
        }
        foreach (var row in Universes) row.Show(state.Universes.FirstOrDefault(u => u.Universe == row.Model.Number));
    }

    async void OnDevicePicked(UniverseRowViewModel row, string choice)
    {
        if (await _setup.AssignAsync(row.Model.Number, choice) is not { } error) return;
        _reportError(error);
        ShowHardware();   // back to the device it's really on
    }
}
