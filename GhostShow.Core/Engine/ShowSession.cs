using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;

namespace GhostShow.Core.Engine;

/// <summary>
/// The open show: its file, its configuration, and applying that configuration to the <see cref="ShowEngine"/>.
/// Setup screens edit <see cref="Project"/> and call <see cref="Apply"/>; live control goes straight to the engine.
///
/// Not thread-safe: use it from one thread (the UI thread, or the CLI's main thread). The engine it drives is
/// thread-safe, so output keeps running while a show is opened, validated or saved.
/// </summary>
public sealed class ShowSession
{
    readonly ILogger _logger;

    public ShowSession(ShowEngine engine, FixtureProfileLibrary profiles, ILogger<ShowSession> logger)
    {
        Engine = engine;
        Profiles = profiles;
        _logger = logger;
        Apply();
    }

    public ShowEngine Engine { get; }

    public Project Project { get; private set; } = new();

    /// <summary>
    /// The show's show.json (in its folder), or an older single-file show it was opened from; null until the show has
    /// been opened or saved.
    /// </summary>
    public string? FilePath { get; private set; }

    /// <summary>
    /// Opened from an older single-file show: it can't be saved back into that file (shows are folders now), so the
    /// next save needs a folder. The old file is never changed.
    /// </summary>
    public bool IsSingleFileShow { get; private set; }

    public FixtureProfileLibrary Profiles { get; private set; }

    /// <summary>The result of the last <see cref="Apply"/>: what was patched and every problem found.</summary>
    public PatchResult Patch { get; private set; } = new([], []);

    /// <summary>Raised after every Apply, so screens can refresh patch warnings and fixture lists.</summary>
    public event Action? Applied;

    /// <summary>
    /// Validates the show and pushes its universes and patch to the engine. Fixtures with errors are left out;
    /// everything else keeps working. Call after any setup change.
    /// </summary>
    public PatchResult Apply()
    {
        var universeIssues = ValidateUniverses(Project);
        var universes = Project.Universes.Select(u => u.Number).Distinct().ToArray();
        var patch = FixturePatcher.Patch(Project.Fixtures, universes, Profiles);

        Patch = patch with { Issues = [.. universeIssues, .. patch.Issues] };
        Engine.Configure(universes, patch.Fixtures);
        Applied?.Invoke();
        return Patch;
    }

    /// <summary>Swaps in a newly loaded profile library (e.g. once the background load finishes) and re-applies.</summary>
    public void UseProfiles(FixtureProfileLibrary profiles)
    {
        Profiles = profiles;
        Apply();
    }

    public void New()
    {
        Project = new Project();
        FilePath = null;
        IsSingleFileShow = false;
        Apply();
    }

    /// <summary>
    /// Opens a show: its folder, its show.json, or an older single-file show. On failure the current show stays open
    /// and the error is returned. Timeline files that couldn't be read are skipped and listed in the warnings.
    /// </summary>
    public async Task<ProjectLoadResult> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await ShowFolderStore.LoadAsync(path, cancellationToken);
        if (result.Project is null)
        {
            _logger.LogWarning("Couldn't open {Path}: {Error}", path, result.Error);
            return result;
        }

        Project = result.Project;
        FilePath = Directory.Exists(path) ? Path.Combine(path, ShowFolderStore.ShowFileName) : path;
        IsSingleFileShow = result.IsSingleFileShow;
        Apply();
        foreach (var warning in result.Warnings ?? []) _logger.LogWarning("{Warning}", warning);
        _logger.LogInformation("Opened {Path}: {Fixtures} fixtures, {Timelines} timelines, {Issues} patch issues.",
                               path, Project.Fixtures.Count, Project.Timelines.Count, Patch.Issues.Count);
        return result;
    }

    /// <summary>
    /// Saves the show to a folder: back to its own folder when none is given (not possible for a single-file show,
    /// which needs a folder). See <see cref="ShowFolderStore.SaveAsync"/> for what is written. Throws
    /// IOException/UnauthorizedAccessException if writing fails; each file is either fully old or fully new.
    /// Returns warnings (e.g. a folder that was kept because it holds other files).
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveAsync(string? folder = null, CancellationToken cancellationToken = default)
    {
        folder ??= !IsSingleFileShow ? ShowFolder : null;
        if (folder is null) throw new InvalidOperationException("The show has no folder yet; give one.");
        folder = Path.GetFullPath(folder);
        // Audio references follow the show: relative when inside its folder, absolute otherwise (see AudioPaths).
        if (folder != ShowFolder) AudioPaths.Rebase(Project.Timelines, ShowFolder, folder);
        var warnings = await ShowFolderStore.SaveAsync(Project, folder, IsSingleFileShow ? null : ShowFolder, cancellationToken);
        FilePath = Path.Combine(folder, ShowFolderStore.ShowFileName);
        IsSingleFileShow = false;
        foreach (var warning in warnings) _logger.LogWarning("{Warning}", warning);
        _logger.LogInformation("Saved {Folder}.", folder);
        return warnings;
    }

    /// <summary>The show's folder (for a single-file show, the folder the file is in); relative audio references resolve against it.</summary>
    public string? ShowFolder => FilePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(FilePath));

    /// <summary>
    /// Compiles one of the show's timelines against the current patch, for playback. Problems (missing fixtures,
    /// missing audio files, ...) come back in <see cref="CompiledTimeline.Issues"/>; the rest still plays.
    /// </summary>
    /// <param name="audioLength">Known audio file lengths (from the audio service); null when not known yet.</param>
    public CompiledTimeline CompileTimeline(ShowTimeline timeline, Func<string, TimeSpan?> audioLength)
    {
        var folder = ShowFolder;
        var context = new TimelineContext(
            Patch.Fixtures.ToDictionary(f => f.Id),
            Project.Fixtures.Select(f => f.Id).ToHashSet(),
            Project.Universes.Select(u => u.Number).ToArray(),
            reference => AudioPaths.Resolve(reference, folder),
            audioLength);
        return TimelineCompiler.Compile(timeline, context);
    }

    static IEnumerable<PatchIssue> ValidateUniverses(Project project)
    {
        foreach (var universe in project.Universes.Where(u => u.Number is < Project.FirstUniverse or > Project.LastUniverse))
            yield return new PatchIssue(PatchIssueSeverity.Error, null,
                $"Universe {universe.Number} ('{universe.Name}') is out of range; universes are numbered {Project.FirstUniverse}-{Project.LastUniverse}.");
        foreach (var duplicate in project.Universes.GroupBy(u => u.Number).Where(g => g.Count() > 1))
            yield return new PatchIssue(PatchIssueSeverity.Error, null, $"Universe {duplicate.Key} is defined {duplicate.Count()} times.");
        if (project.Universes.Count == 0)
            yield return new PatchIssue(PatchIssueSeverity.Warning, null, "The show has no universes, so nothing can be output.");
    }
}
