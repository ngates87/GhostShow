using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Storage;

/// <summary>
/// A show on disk is a folder; the timeline tree in the app is its timelines folder:
/// <code>
/// Haunt 2026/
///   show.json                         fixtures, universes (ProjectStore)
///   timelines/
///     Entry/Lobby loop.timeline.json  one file per timeline; folders are tree folders
///   audio/...                         sound files, referenced relative to the show folder
/// </code>
/// Zipping the show folder exports the whole show.
///
/// Saving writes show.json and only the timelines that changed, renames and moves files whose timeline was renamed
/// or moved in the tree, and deletes the files of timelines deleted in the tree. It only ever deletes or moves files
/// it loaded or wrote itself: anything else in the folder (a backup, a file it couldn't read) is left alone. Every
/// file is written atomically (see <see cref="AtomicFile"/>).
/// </summary>
public static class ShowFolderStore
{
    public const string ShowFileName = "show.json";
    public const string TimelinesFolderName = "timelines";
    public const string TimelineExtension = ".timeline.json";

    /// <summary>
    /// Version of a timeline file's own format. 5: no marker tracks (older files' are dropped, see
    /// <see cref="LegacyMarkers"/>). 4: clips' envelopes have end handles and curve types, and carry on
    /// from the previous clip. 3: lighting is in clips (<see cref="LightingClip"/>). 2: lighting tracks had channels
    /// with points. 1: lighting events. Older forms are converted when read (<see cref="LevelEventConversion.Upgrade"/>).
    /// </summary>
    public const int TimelineFileVersion = 5;

    /// <summary>What a timeline file holds. Its name and folder come from where the file is.</summary>
    sealed class TimelineFile
    {
        public int Version { get; set; } = TimelineFileVersion;
        public Guid Id { get; set; }
        public TimeSpan? Length { get; set; }
        public bool Loop { get; set; }
        public TimelinePlayback? Playback { get; set; }
        public List<TimelineTrack> Tracks { get; set; } = [];
    }

    /// <summary>Whether a folder holds a show (has a show.json).</summary>
    public static bool IsShowFolder(string folder) => File.Exists(Path.Combine(folder, ShowFileName));

    /// <summary>
    /// Opens a show from its folder, its show.json, or an older single-file show (which then has every timeline at
    /// the top level and must be saved to a folder: <see cref="ProjectLoadResult.IsSingleFileShow"/>).
    /// </summary>
    public static async Task<ProjectLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        string showFile = Directory.Exists(path) ? Path.Combine(path, ShowFileName) : path;
        if (!File.Exists(showFile))
            return ProjectLoadResult.Failed(Directory.Exists(path) ? $"{path} isn't a show folder (it has no {ShowFileName})." : $"{path} doesn't exist.");

        string json;
        try
        {
            json = await File.ReadAllTextAsync(showFile, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProjectLoadResult.Failed($"Couldn't read {Path.GetFileName(showFile)}: {ex.Message}");
        }
        var result = ProjectStore.FromJson(json);
        if (result.Project is not { } project || result.IsSingleFileShow) return result;

        var warnings = new List<string>();
        project.Timelines = await LoadTimelinesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(showFile))!, TimelinesFolderName),
                                                     project, warnings, cancellationToken);
        return result with { Warnings = warnings };
    }

    static async Task<List<ShowTimeline>> LoadTimelinesAsync(string root, Project project, List<string> warnings, CancellationToken cancellationToken)
    {
        var timelines = new List<ShowTimeline>();
        project.TimelineFolders.Clear();
        project.SavedFolders.Clear();
        project.SavedTimelinePaths.Clear();
        if (!Directory.Exists(root)) return timelines;

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string folder = Relative(root, directory);
            if (folder.Split('/').Any(part => part.StartsWith('.'))) continue;   // hidden folders aren't part of the tree
            project.TimelineFolders.Add(folder);
            project.SavedFolders.Add(folder);
        }

        var ids = new HashSet<Guid>();
        foreach (var file in Directory.EnumerateFiles(root, "*" + TimelineExtension, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Relative(root, file);
            string folder = TimelineNames.ParentOf(relative);
            if (folder.Length > 0 && !project.TimelineFolders.Contains(folder)) continue;   // in a hidden folder
            string name = TimelineNames.LeafOf(relative)[..^TimelineExtension.Length];

            TimelineFile? content;
            bool droppedMarkers;
            try
            {
                var json = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                });
                droppedMarkers = LegacyMarkers.Drop(json);
                content = json.Deserialize<TimelineFile>(Json.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Skipped timeline '{relative}': {ex.Message}");
                continue;
            }
            if (content is null) continue;
            if (content.Version > TimelineFileVersion)
            {
                warnings.Add($"Skipped timeline '{relative}': it was saved by a newer GhostShow (timeline file version {content.Version}).");
                continue;
            }

            var timeline = new ShowTimeline
            {
                Id = content.Id, Name = name, Folder = folder, Length = content.Length, Loop = content.Loop, Tracks = content.Tracks,
                Playback = content.Playback ?? new TimelinePlayback(),
                SavedPath = relative, HasUnsavedChanges = false,
            };
            // Older lighting becomes clips with envelopes; the file is rewritten in the new form on the next save.
            if (LevelEventConversion.Upgrade(timeline, content.Version) || droppedMarkers) timeline.HasUnsavedChanges = true;
            if (timeline.Id == Guid.Empty || !ids.Add(timeline.Id))
            {
                // Typically a timeline file copied by hand: both copies had the same ID. The copy gets its own.
                if (timeline.Id != Guid.Empty) warnings.Add($"'{relative}' had the same ID as another timeline (a copied file?); it was given a new one.");
                timeline.Id = Guid.NewGuid();
                ids.Add(timeline.Id);
                timeline.HasUnsavedChanges = true;
            }
            timelines.Add(timeline);
            project.SavedTimelinePaths.Add(relative);
        }
        return timelines;
    }

    /// <summary>
    /// Saves a show to a folder. previousFolder is where it was loaded from or last saved (null if never, or if it
    /// came from a single-file show): saving back there writes only changes and tidies up renamed and deleted
    /// timelines; saving anywhere else writes everything. Returns warnings (e.g. a folder left behind because it
    /// holds other files). Throws IOException/UnauthorizedAccessException if writing fails; files written before the
    /// failure stay written, and nothing is deleted after it.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SaveAsync(Project project, string folder, string? previousFolder,
                                                               CancellationToken cancellationToken = default)
    {
        CheckTree(project);
        var warnings = new List<string>();
        folder = Path.GetFullPath(folder);
        bool sameFolder = previousFolder is not null && PathsEqual(Path.GetFullPath(previousFolder), folder);
        if (!sameFolder)
        {
            project.SavedTimelinePaths.Clear();
            project.SavedFolders.Clear();
            foreach (var timeline in project.Timelines) timeline.SavedPath = null;
        }

        string root = Path.Combine(folder, TimelinesFolderName);
        Directory.CreateDirectory(root);
        // Snapshot the JSON first (consistent, and fast), then write.
        string showJson = ProjectStore.ToJson(project);
        var writes = project.Timelines
            .Where(t => t.HasUnsavedChanges || t.SavedPath is null || !PathsEqual(t.SavedPath, FileOf(t)))
            .Select(t => (Timeline: t, Path: FileOf(t), Json: JsonSerializer.Serialize(
                new TimelineFile { Id = t.Id, Length = t.Length, Loop = t.Loop, Playback = t.Playback, Tracks = t.Tracks }, Json.Options)))
            .ToList();

        await AtomicFile.WriteAllTextAsync(Path.Combine(folder, ShowFileName), showJson, cancellationToken);
        foreach (var folderPath in project.TimelineFolders) Directory.CreateDirectory(Path.Combine(root, Native(folderPath)));

        var current = project.Timelines.Select(FileOf).ToHashSet(TimelineNames.Comparer);
        foreach (var (timeline, path, json) in writes)
        {
            string? oldPath = timeline.SavedPath;
            string target = Path.Combine(root, Native(path));
            bool renamed = oldPath is not null && !string.Equals(oldPath, path, StringComparison.Ordinal);
            bool caseOnly = renamed && TimelineNames.Comparer.Equals(oldPath, path);
            // A change of capitals only: rename the file itself, so it works whether or not the file system ignores case.
            if (caseOnly && File.Exists(Path.Combine(root, Native(oldPath!)))) File.Move(Path.Combine(root, Native(oldPath!)), target, overwrite: true);
            await AtomicFile.WriteAllTextAsync(target, json, cancellationToken);
            // Renamed or moved: remove the old file, unless another timeline's file now lives there.
            if (renamed && !caseOnly && !current.Contains(oldPath!)) TryDelete(Path.Combine(root, Native(oldPath!)), warnings);
            timeline.SavedPath = path;
            timeline.HasUnsavedChanges = false;
        }

        // Timelines deleted in the tree.
        foreach (var removed in project.SavedTimelinePaths.Where(p => !current.Contains(p)).ToList())
            TryDelete(Path.Combine(root, Native(removed)), warnings);
        project.SavedTimelinePaths = current;

        // Folders deleted or renamed in the tree: remove them if nothing else is in them (deepest first).
        // On Linux a folder renamed only in capitals is a different folder; elsewhere it's the same one, still in use.
        bool StillUsed(string f) => OperatingSystem.IsLinux() ? project.TimelineFolders.Any(t => t == f) : project.TimelineFolders.Contains(f);
        foreach (var removed in project.SavedFolders.Where(f => !StillUsed(f)).OrderByDescending(f => f.Length).ToList())
        {
            string directory = Path.Combine(root, Native(removed));
            if (!Directory.Exists(directory)) continue;
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                warnings.Add($"Kept the folder 'timelines/{removed}' on disk: it has files GhostShow didn't create.");
            else
                Directory.Delete(directory);
        }
        project.SavedFolders = new HashSet<string>(project.TimelineFolders, TimelineNames.Comparer);
        return warnings;
    }

    /// <summary>Catches tree mistakes before anything is written, since they'd make two timelines share a file.</summary>
    static void CheckTree(Project project)
    {
        foreach (var timeline in project.Timelines)
        {
            if (TimelineNames.Problem(timeline.Name) is { } problem) throw new InvalidOperationException($"Timeline '{timeline.Name}': {problem}");
            if (timeline.Folder.Length > 0 && !project.TimelineFolders.Contains(timeline.Folder))
                throw new InvalidOperationException($"Timeline '{timeline.Name}' is in folder '{timeline.Folder}', which the show doesn't have.");
        }
        foreach (var clash in project.Timelines.GroupBy(FileOf, TimelineNames.Comparer).Where(g => g.Count() > 1))
            throw new InvalidOperationException($"{clash.Count()} timelines would be saved as '{clash.Key}'.");
    }

    static string FileOf(ShowTimeline timeline) => TimelineNames.Combine(timeline.Folder, timeline.Name + TimelineExtension);

    static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    static void TryDelete(string path, List<string> warnings)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Couldn't remove the old file {path}: {ex.Message}");
        }
    }
}
