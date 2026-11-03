using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The browser's File menu. A device can't use the desktop's folder pickers, which are on this computer, so it works by
/// name inside one folder (<see cref="AppPaths.ShowsDirectory"/>). Each returns null when done, otherwise why not.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<string> SavedShowsForDevice()
    {
        if (!Directory.Exists(_paths.ShowsDirectory)) return [];
        return [.. Directory.EnumerateDirectories(_paths.ShowsDirectory)
            .Where(ShowFolderStore.IsShowFolder)
            .Select(folder => Path.GetFileName(folder)!)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<string?> NewShowForDeviceAsync(bool discardUnsaved)
    {
        if (await ReadyToLeaveShowAsync(discardUnsaved) is { } problem) return problem;
        _session.New();
        await AfterShowReplacedAsync();
        ShowEditor();
        StatusMessage = "New show, started from a device.";
        return null;
    }

    public async Task<string?> OpenShowForDeviceAsync(string name, bool discardUnsaved)
    {
        // TimelineNames.Problem refuses separators and leading dots, so a name can't reach outside the shows folder.
        if (TimelineNames.Problem(name) is { } invalid) return invalid;
        string folder = Path.GetFullPath(Path.Combine(_paths.ShowsDirectory, name));
        if (!ShowFolderStore.IsShowFolder(folder)) return $"There's no show called {name} any more.";
        if (await ReadyToLeaveShowAsync(discardUnsaved) is { } problem) return problem;
        await OpenPathAsync(folder);
        return _session.ShowFolder == folder ? null : $"Couldn't open {name}: see the show computer.";
    }

    public async Task<string?> SaveShowAsForDeviceAsync(string name)
    {
        if (!NeedsFirstSave) return "This show is already saved. Use Rename to change its name.";
        if (TimelineNames.Problem(name) is { } invalid) return invalid;
        try
        {
            Directory.CreateDirectory(_paths.ShowsDirectory);
            if (NameIsTaken(_paths.ShowsDirectory, name)) return $"There's already a show called {name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't use {_paths.ShowsDirectory}: {ex.Message}";
        }

        string oldName = _session.Project.Name;
        _session.Project.Name = name;
        if (!await WriteShowAsync(Path.Combine(_paths.ShowsDirectory, name)))
        {
            _session.Project.Name = oldName;
            return "Couldn't save the show: see the show computer.";
        }
        Remote.ShowReplaced();   // devices show the new name
        return null;
    }

    public async Task<string?> RenameShowForDeviceAsync(string name)
    {
        if (NeedsFirstSave || _session.ShowFolder is not { } folder) return "This show hasn't got a name yet: use File → Save.";
        if (TimelineNames.Problem(name) is { } invalid) return invalid;
        if (name == Path.GetFileName(folder)) return null;
        string parent = Path.GetDirectoryName(folder)!;
        string target = Path.Combine(parent, name);

        // An automatic save mustn't be writing into the folder while it moves.
        await _saveGate.WaitAsync();
        try
        {
            bool onlyCaseChanges = string.Equals(name, Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase);
            if (!onlyCaseChanges && NameIsTaken(parent, name)) return $"There's already a show called {name}.";
            Directory.Move(folder, target);
            _session.FolderMoved(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't rename the show, so nothing changed. A file in its folder may be in use: {ex.Message}";
        }
        finally
        {
            _saveGate.Release();
        }

        _session.Project.Name = name;
        MarkDirty();   // show.json holds the name too
        _settings.RemoveRecentShow(folder);
        await RememberShowAsync();
        Remote.ShowReplaced();   // devices show the new name
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShowFile));
        return null;
    }

    bool NeedsFirstSave => _session.FilePath is null || _session.IsSingleFileShow;

    /// <summary>Case-insensitive, since a Windows folder can't differ from another by case alone.</summary>
    static bool NameIsTaken(string folder, string name) =>
        Directory.EnumerateFileSystemEntries(folder).Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A saved show is simply saved, as auto-save would in a moment. There's nowhere to save one that was never named,
    /// so the device has to ask its user first.
    /// </summary>
    async Task<string?> ReadyToLeaveShowAsync(bool discardUnsaved)
    {
        if (!IsDirty) return null;
        if (NeedsFirstSave) return discardUnsaved ? null : "This show hasn't been saved, and would be lost.";
        return await WriteShowAsync(folder: null) ? null : "Couldn't save the open show, so it stays open: see the show computer.";
    }
}
