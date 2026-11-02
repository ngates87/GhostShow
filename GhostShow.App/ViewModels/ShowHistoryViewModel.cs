using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Editing;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Undo and Redo for a setup screen (Fixtures, DMX setup), on the show's history: what's edited outside the timelines.
/// A timeline's edits have their own history, so Undo here never takes back something on a screen you can't see.
/// </summary>
public sealed partial class ShowHistoryViewModel : ObservableObject
{
    readonly IEditHistory _history;

    public ShowHistoryViewModel(IEditHistory history)
    {
        _history = history;
        _history.HistoryChanged += () =>
        {
            OnPropertyChanged(nameof(UndoLabel));
            OnPropertyChanged(nameof(RedoLabel));
        };
    }

    public string UndoLabel => _history.NextUndo(null) is { } description ? $"Undo {description}" : "Undo";
    public string RedoLabel => _history.NextRedo(null) is { } description ? $"Redo {description}" : "Redo";

    [RelayCommand]
    void Undo() => _history.UndoLatest(null);

    [RelayCommand]
    void Redo() => _history.RedoLatest(null);
}
