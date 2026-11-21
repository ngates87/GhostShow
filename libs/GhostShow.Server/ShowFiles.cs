using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.Server;

/// <summary>
/// The open show's files: whether it has unsaved changes, saving (and saving again by itself shortly after each edit),
/// opening, new, copying its sounds into its folder, the recent list, and a device's File menu, which works by name in
/// the shows folder (<see cref="AppPaths.ShowsDirectory"/>). The desktop's folder and file pickers stay in its window and
/// hand paths here. Called on the show's thread.
/// </summary>
public sealed class ShowFiles : IDisposable
{
    public static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(500);

    readonly ShowSession _session;
    readonly MachineSettings _settings;
    readonly AppPaths _paths;
    readonly Func<Task> _saveSettings;
    readonly Debouncer _autoSave;
    readonly SemaphoreSlim _saveGate = new(1, 1);
    long _dirtyGeneration;
    bool _collectingAudio;

    /// <param name="saveSettings">Saves this computer's settings (the recent list is in them).</param>
    public ShowFiles(ShowSession session, MachineSettings settings, AppPaths paths, Func<Task> saveSettings, IShowThread thread)
    {
        _session = session;
        _settings = settings;
        _paths = paths;
        _saveSettings = saveSettings;
        _autoSave = new Debouncer(thread, AutoSaveDelay, OnAutoSave);
    }

    public void Dispose() => _autoSave.Dispose();

    // ---- what the host is told ----

    /// <summary>Another show is open (new, opened, imported, closed): the host's screens and this computer's devices follow it.</summary>
    public Func<Task> AfterShowReplaced { get; set; } = () => Task.CompletedTask;

    /// <summary>Whether there are unsaved changes, the show's name or where it's saved changed.</summary>
    public event Action? Changed;

    /// <summary>The show was written: its timelines' titles lose their unsaved mark.</summary>
    public event Action? Saved;

    /// <summary>The show got a name from a device (saved by name, or renamed): devices show it.</summary>
    public event Action? Renamed;

    /// <summary>What a file action did, for the status bar.</summary>
    public event Action<string>? Status;

    /// <summary>Why a file action couldn't be done, for the status bar (and the log).</summary>
    public event Action<string>? Problem;

    void Say(string message) => Status?.Invoke(message);
    void Fail(string message) => Problem?.Invoke(message);

    // ---- the open show ----

    /// <summary>Edited since it was last saved.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>It has nowhere to save to yet: never saved, or an older single-file show (saved as a folder from then on).</summary>
    public bool NeedsFirstSave => _session.FilePath is null || _session.IsSingleFileShow;

    /// <summary>Its folder's name, or its old file's, or (unsaved) its name.</summary>
    public string ShowName => _session.ShowFolder is { } folder && !_session.IsSingleFileShow ? Path.GetFileName(folder)
                            : _session.FilePath is { } file ? Path.GetFileName(file)
                            : _session.Project.Name;

    /// <summary>Where it's saved, in words.</summary>
    public string ShowFile => _session.IsSingleFileShow ? $"{_session.FilePath} (older single-file show: Save will ask for a folder)"
                            : _session.ShowFolder ?? "Not saved yet";

    /// <summary>The show was edited: it's saved again by itself shortly (once it has a folder).</summary>
    public void MarkDirty()
    {
        IsDirty = true;
        _dirtyGeneration++;
        Changed?.Invoke();
        ScheduleAutoSave(AutoSaveDelay);
    }

    void ScheduleAutoSave(TimeSpan delay)
    {
        if (NeedsFirstSave || !IsDirty) return;
        _autoSave.Poke(delay);
    }

    async void OnAutoSave()
    {
        if (IsDirty && !NeedsFirstSave) await WriteAsync(folder: null, automatic: true);
    }

    async Task ReplacedAsync()
    {
        _autoSave.Cancel();
        IsDirty = false;
        await AfterShowReplaced();
        Changed?.Invoke();
    }

    // ---- new, open, import, close ----

    /// <summary>A new, empty show (unsaved until Save picks its folder). Unsaved changes must have been dealt with.</summary>
    public async Task NewAsync()
    {
        _session.New();
        await ReplacedAsync();
    }

    /// <summary>Uses this show (an imported one): unsaved until Save picks its folder.</summary>
    public async Task UseAsync(Project project)
    {
        _session.Use(project);
        await ReplacedAsync();
        MarkDirty();
    }

    /// <summary>
    /// Opens a show folder, or an older single-file show. Returns whether it opened; says what to know about it (or why
    /// it couldn't open) as <see cref="Status"/> (or <see cref="Problem"/>).
    /// </summary>
    public async Task<bool> OpenAsync(string path)
    {
        var result = await _session.OpenAsync(path);
        if (result.Project is null)
        {
            Fail(result.Error!);
            return false;
        }
        await ReplacedAsync();
        var notes = new List<string>();
        if (_settings.AdoptMigratedOutputs(result.MigratedOutputs) > 0)
        {
            await _saveSettings();
            notes.Add("Its output assignments were moved to this machine's settings.");
        }
        await RememberAsync();
        if (result.IsSingleFileShow) notes.Add("This show is from an older version (a single file). Saving asks for a folder to save it in; the old file isn't changed.");
        if (result.Warnings is { Count: > 0 } warnings) notes.Add($"{warnings.Count} problem(s) opening it: {string.Join(" ", warnings)}");
        if (!result.IsSingleFileShow && _session.ShowFolder is { } folder && AudioCollector.Plan(_session.Project.Timelines, folder) is { Count: > 0 } outside)
            notes.Add($"{(outside.Count == 1 ? "1 sound is" : $"{outside.Count} sounds are")} outside the show folder: File → Copy Audio Into Show Folder (or the next save) copies them in.");
        Say(notes.Count > 0 ? string.Join(" ", notes) : $"Opened {ShowName}.");
        return true;
    }

    // ---- saving ----

    /// <summary>Saves where it's saved already. Only for a show that has a folder (see <see cref="NeedsFirstSave"/>).</summary>
    public Task<bool> SaveAsync() => WriteAsync(folder: null);

    /// <summary>Saves it as this folder (see <see cref="FolderFor"/>).</summary>
    public Task<bool> SaveAsAsync(string folder) => WriteAsync(folder);

    /// <summary>
    /// The folder to save in, for a place picked: an empty folder (e.g. one just made in the picker) is used as it is;
    /// anywhere else, a new folder named after the show is made inside it, so a show is never mixed into another
    /// folder's files.
    /// </summary>
    public string FolderFor(string picked)
    {
        bool usable = !Directory.EnumerateFileSystemEntries(picked).Any()
                      || (_session.ShowFolder is { } current && !_session.IsSingleFileShow && Path.GetFullPath(picked) == Path.GetFullPath(current));
        if (usable) return picked;
        string name = TimelineNames.Sanitize(_session.Project.Name, "Show");
        return Path.Combine(picked, TimelineNames.Unique(name, Directory.EnumerateFileSystemEntries(picked).Select(Path.GetFileName).OfType<string>()));
    }

    async Task<bool> WriteAsync(string? folder, bool automatic = false)
    {
        await _saveGate.WaitAsync();
        long generation = _dirtyGeneration;
        bool saved = false;
        try
        {
            if (automatic && !IsDirty) return true;
            var warnings = await _session.SaveAsync(folder);
            saved = true;
            if (_dirtyGeneration == generation) IsDirty = false;
            else
            {
                // The disk snapshot was taken before a newer edit. ShowFolderStore may also have cleared timeline
                // dirty flags while finishing its writes, so make the next pass write the complete current state.
                foreach (var timeline in _session.Project.Timelines) timeline.HasUnsavedChanges = true;
                IsDirty = true;
            }
            Saved?.Invoke();
            if (!automatic) await RememberAsync();
            if (!automatic)
                Say(warnings.Count == 0 ? $"Saved {_session.ShowFolder}." : $"Saved {_session.ShowFolder}. {string.Join(" ", warnings)}");
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail($"Couldn't save the show: {ex.Message}. Files already saved are complete; nothing was deleted.");
        }
        finally
        {
            _saveGate.Release();
        }

        if (saved) _ = CollectAudioAsync(quiet: true);   // sounds added from elsewhere: copied into the show folder
        if (saved && _dirtyGeneration != generation) ScheduleAutoSave(AutoSaveDelay);
        else if (!saved && IsDirty && !NeedsFirstSave) ScheduleAutoSave(TimeSpan.FromSeconds(2));
        return saved;
    }

    /// <summary>
    /// Copies the sounds the show uses from outside its folder into its "audio" folder, then points the clips at the
    /// copies (one undo step): the show folder then holds everything, so it can be zipped and moved, and its sounds
    /// play from the local disk instead of a slow or cloud drive. Runs after every save too (quietly when there's
    /// nothing to copy), so a sound added from elsewhere is copied in shortly after.
    /// </summary>
    public async Task CollectAudioAsync(bool quiet)
    {
        if (_collectingAudio) return;
        if (_session.ShowFolder is not { } folder || _session.IsSingleFileShow)
        {
            if (!quiet) Say("Save the show first: its sounds are copied into its folder.");
            return;
        }
        var project = _session.Project;
        var plan = AudioCollector.Plan(project.Timelines, folder);
        if (plan.Count == 0)
        {
            if (!quiet) Say("Every sound is already in the show folder.");
            return;
        }

        _collectingAudio = true;
        string what = plan.Count == 1 ? Path.GetFileName(plan[0].Source) : $"{plan.Count} sounds";
        try
        {
            long shown = -1;
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                long percent = p.Done * 100 / Math.Max(1, p.Total);
                if (percent == shown) return;
                shown = percent;
                Say($"Copying {what} into the show folder… {percent}%");
            });
            await Task.Run(() => AudioCollector.CopyAsync(plan, folder, progress));
            if (_session.Project != project || _session.ShowFolder != folder) return;   // another show meanwhile

            var clips = AudioCollector.Clips(project.Timelines, plan, folder);
            if (clips.Count > 0)
                _session.Document.Record($"Copy {what} into the show folder", () => AudioCollector.Repoint(project.Timelines, plan, folder),
                                         [.. clips.Select(ShowDocument.Shallow)]);
            Say($"Copied {what} into {Path.Combine(folder, AudioCollector.Folder)}; the show plays them from there now.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Couldn't copy {what} into the show folder: {ex.Message}. The show still plays them from where they are.");
        }
        finally
        {
            _collectingAudio = false;
        }
    }

    // ---- shows opened or saved here, most recent first ----

    /// <summary>The recent list: each show's path, and whether it's still there.</summary>
    public IReadOnlyList<(string Path, bool Exists)> RecentShows =>
        [.. _settings.RecentShows.Select(path => (path, Directory.Exists(path) ? ShowFolderStore.IsShowFolder(path) : File.Exists(path)))];

    public async Task ForgetRecentAsync(string path)
    {
        _settings.RemoveRecentShow(path);
        await _saveSettings();
    }

    /// <summary>After opening or saving: this show goes to the top of the recent list.</summary>
    async Task RememberAsync()
    {
        string? path = _session.IsSingleFileShow ? _session.FilePath : _session.ShowFolder;
        if (path is null) return;
        _settings.AddRecentShow(path);
        await _saveSettings();
    }

    // ---- a device's File menu: by name, in the shows folder (each returns null when done, otherwise why not) ----

    public IReadOnlyList<string> SavedShows()
    {
        if (!Directory.Exists(_paths.ShowsDirectory)) return [];
        return [.. Directory.EnumerateDirectories(_paths.ShowsDirectory)
            .Where(ShowFolderStore.IsShowFolder)
            .Select(folder => Path.GetFileName(folder)!)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Save, asked for by a device. Only where the show is saved already: the folder picker is on the show computer, so
    /// a device can't choose a place.
    /// </summary>
    public async Task<string?> SaveForDeviceAsync()
    {
        if (NeedsFirstSave) return "This show hasn't got a name yet: use File → Save.";
        return await WriteAsync(folder: null) ? null : "Couldn't save the show: see the show computer.";
    }

    public async Task<string?> NewForDeviceAsync(bool discardUnsaved)
    {
        if (await ReadyToLeaveAsync(discardUnsaved) is { } problem) return problem;
        await NewAsync();
        Say("New show, started from a device.");
        return null;
    }

    public async Task<string?> OpenByNameAsync(string name, bool discardUnsaved)
    {
        // TimelineNames.Problem refuses separators and leading dots, so a name can't reach outside the shows folder.
        if (TimelineNames.Problem(name) is { } invalid) return invalid;
        string folder = Path.GetFullPath(Path.Combine(_paths.ShowsDirectory, name));
        if (!ShowFolderStore.IsShowFolder(folder)) return $"There's no show called {name} any more.";
        if (await ReadyToLeaveAsync(discardUnsaved) is { } problem) return problem;
        await OpenAsync(folder);
        return _session.ShowFolder == folder ? null : $"Couldn't open {name}: see the show computer.";
    }

    public async Task<string?> SaveAsByNameAsync(string name)
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
        if (!await WriteAsync(Path.Combine(_paths.ShowsDirectory, name)))
        {
            _session.Project.Name = oldName;
            return "Couldn't save the show: see the show computer.";
        }
        Renamed?.Invoke();
        return null;
    }

    public async Task<string?> RenameAsync(string name)
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
        await RememberAsync();
        Renamed?.Invoke();
        Changed?.Invoke();
        return null;
    }

    /// <summary>Case-insensitive, since a Windows folder can't differ from another by case alone.</summary>
    static bool NameIsTaken(string folder, string name) =>
        Directory.EnumerateFileSystemEntries(folder).Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A saved show is simply saved, as auto-save would in a moment. There's nowhere to save one that was never named,
    /// so the device has to ask its user first.
    /// </summary>
    async Task<string?> ReadyToLeaveAsync(bool discardUnsaved)
    {
        if (!IsDirty) return null;
        if (NeedsFirstSave) return discardUnsaved ? null : "This show hasn't been saved, and would be lost.";
        return await WriteAsync(folder: null) ? null : "Couldn't save the open show, so it stays open: see the show computer.";
    }
}
