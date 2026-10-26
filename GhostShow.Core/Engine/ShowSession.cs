using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
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

    /// <summary>null until the show has been opened from or saved to a file.</summary>
    public string? FilePath { get; private set; }

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
        Apply();
    }

    /// <summary>Opens a show. On failure the current show stays open and the error is returned.</summary>
    public async Task<ProjectLoadResult> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await ProjectStore.LoadAsync(path, cancellationToken);
        if (result.Project is null)
        {
            _logger.LogWarning("Couldn't open {Path}: {Error}", path, result.Error);
            return result;
        }

        Project = result.Project;
        FilePath = path;
        Apply();
        _logger.LogInformation("Opened {Path}: {Fixtures} fixtures, {Issues} patch issues.", path, Project.Fixtures.Count, Patch.Issues.Count);
        return result;
    }

    /// <summary>
    /// Saves the show. The JSON is produced on the calling thread (a consistent snapshot, and fast); only the disk
    /// write is asynchronous. Throws IOException/UnauthorizedAccessException if the write fails, leaving the
    /// previous file intact.
    /// </summary>
    public async Task SaveAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= FilePath ?? throw new InvalidOperationException("The show has no file yet; give a path.");
        string json = ProjectStore.ToJson(Project);
        await AtomicFile.WriteAllTextAsync(path, json, cancellationToken);
        FilePath = path;
        _logger.LogInformation("Saved {Path}.", path);
    }

    static IEnumerable<PatchIssue> ValidateUniverses(Project project)
    {
        foreach (var universe in project.Universes.Where(u => u.Number is < 0 or > 32767))
            yield return new PatchIssue(PatchIssueSeverity.Error, null, $"Universe {universe.Number} ('{universe.Name}') is out of range; use 0-32767.");
        foreach (var duplicate in project.Universes.GroupBy(u => u.Number).Where(g => g.Count() > 1))
            yield return new PatchIssue(PatchIssueSeverity.Error, null, $"Universe {duplicate.Key} is defined {duplicate.Count()} times.");
        if (project.Universes.Count == 0)
            yield return new PatchIssue(PatchIssueSeverity.Warning, null, "The show has no universes, so nothing can be output.");
    }
}
