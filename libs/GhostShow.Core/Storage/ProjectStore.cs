using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Storage;

/// <param name="Project">null if the file couldn't be used; Error says why.</param>
/// <param name="MigratedOutputs">
/// Output assignments found in a version 1 file. They now belong in machine settings; the caller decides whether to
/// move them there.
/// </param>
/// <param name="FileVersion">The version the file was written in. Below 5 it's a single-file show (timelines inside).</param>
/// <param name="Warnings">Problems that didn't stop the show opening, e.g. a timeline file that couldn't be read.</param>
public sealed record ProjectLoadResult(Project? Project, string? Error, IReadOnlyList<OutputBinding> MigratedOutputs,
                                       int FileVersion = Project.CurrentSchemaVersion, IReadOnlyList<string>? Warnings = null)
{
    public static ProjectLoadResult Failed(string error) => new(null, error, []);

    /// <summary>A show from before shows were folders: it opens, and is saved as a folder (Save As).</summary>
    public bool IsSingleFileShow => FileVersion < 5;
}

/// <summary>
/// Reads and writes show.json, the show-wide part of a show folder (<see cref="ShowFolderStore"/> handles the
/// folder), and reads older single-file shows. Problems with a file are returned as messages, not thrown.
/// </summary>
public static class ProjectStore
{
    public static async Task<ProjectLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProjectLoadResult.Failed($"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
        }
        return FromJson(json);
    }

    /// <summary>show.json's content: everything but the timelines, which have their own files.</summary>
    public static string ToJson(Project project) => JsonSerializer.Serialize(project, Json.Options);

    public static ProjectLoadResult FromJson(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return ProjectLoadResult.Failed($"The show file isn't valid JSON: {ex.Message}");
        }
        if (root is not JsonObject obj) return ProjectLoadResult.Failed("The show file is empty or isn't a JSON object.");

        int version = GetIgnoringCase(obj, "version")?.GetValue<int>() ?? 1;
        if (version > Project.CurrentSchemaVersion)
            return ProjectLoadResult.Failed($"This show was saved by a newer GhostShow (file version {version}; this version reads up to {Project.CurrentSchemaVersion}).");

        IReadOnlyList<OutputBinding> migratedOutputs = version == 1 ? ExtractVersion1Outputs(obj) : [];

        Project? project;
        try
        {
            project = obj.Deserialize<Project>(Json.Options);
        }
        catch (JsonException ex)
        {
            return ProjectLoadResult.Failed($"The show file has invalid content: {ex.Message}");
        }
        if (project is null) return ProjectLoadResult.Failed("The show file is empty.");

        if (version < 3)
        {
            // A file without a universe list got today's default (universe 1); its own default was universe 0.
            if (GetIgnoringCase(obj, "universes") is null) project.Universes = [new UniverseConfig { Number = 0, Name = "Universe 0" }];
            NumberUniversesFromOne(project);
            migratedOutputs = migratedOutputs.Select(b => b with { Universe = b.Universe + 1 }).ToList();
        }
        if (version < 4)
        {
            var timelines = LegacyScenes.Convert(GetIgnoringCase(obj, "scenes"), zeroBasedUniverses: version < 3);
            if (timelines.Count > 0) project.Timelines = timelines;
        }
        else if (version == 4)
        {
            // Single-file shows kept their timelines inside; from version 5 they are separate files.
            try
            {
                var stored = GetIgnoringCase(obj, "timelines");
                foreach (var timeline in (stored as JsonArray ?? [])) LegacyMarkers.Drop(timeline);
                if (stored?.Deserialize<List<ShowTimeline>>(Json.Options) is { Count: > 0 } timelines)
                    project.Timelines = timelines;
            }
            catch (JsonException ex)
            {
                return ProjectLoadResult.Failed($"The show file has invalid timelines: {ex.Message}");
            }
        }
        if (version < 5)
        {
            DisambiguateNames(project);
            foreach (var timeline in project.Timelines) LevelEventConversion.Upgrade(timeline);
        }
        project.Version = Project.CurrentSchemaVersion;
        return new ProjectLoadResult(project, null, migratedOutputs, version);
    }

    /// <summary>
    /// Single-file shows allowed any timeline name, including duplicates and characters a file name can't have.
    /// As files, names must be valid and unique, so fix them up (they're shown, and saved, as fixed).
    /// </summary>
    static void DisambiguateNames(Project project)
    {
        var used = new List<string>();
        foreach (var timeline in project.Timelines)
        {
            string name = TimelineNames.Problem(timeline.Name) is null ? timeline.Name : TimelineNames.Sanitize(timeline.Name, "Timeline");
            timeline.Name = TimelineNames.Unique(name, used);
            used.Add(timeline.Name);
        }
    }

    /// <summary>
    /// Versions 1 and 2 numbered universes from 0; version 3 numbers them from 1, as consoles do. Every universe
    /// reference moves up by one together (universe list, fixtures, and the old scenes' channel tracks), so the patch is unchanged.
    /// </summary>
    static void NumberUniversesFromOne(Project project)
    {
        foreach (var universe in project.Universes)
        {
            universe.Number++;
            if (universe.Name == $"Universe {universe.Number - 1}") universe.Name = $"Universe {universe.Number}";
        }
        foreach (var fixture in project.Fixtures) fixture.Universe++;
        // Timeline tracks in these old files are prototype scenes; LegacyScenes shifts their universes as it converts them.
    }

    /// <summary>
    /// Version 1 stored each universe's USB adapter inside the show:
    /// "universes": [{ "number": 0, "output": { "type": "openDmx", "port": "...", "serialNumber": "..." } }].
    /// </summary>
    static List<OutputBinding> ExtractVersion1Outputs(JsonObject root)
    {
        var bindings = new List<OutputBinding>();
        if (GetIgnoringCase(root, "universes") is not JsonArray universes) return bindings;

        foreach (var universe in universes.OfType<JsonObject>())
        {
            if (GetIgnoringCase(universe, "output") is not JsonObject output) continue;
            int number = GetIgnoringCase(universe, "number")?.GetValue<int>() ?? 0;
            string? type = GetIgnoringCase(output, "type")?.GetValue<string>();
            string port = GetIgnoringCase(output, "port")?.GetValue<string>() ?? "";
            string? serial = GetIgnoringCase(output, "serialNumber")?.GetValue<string>();
            OutputDeviceConfig? device = type switch
            {
                "openDmx" => new OpenDmxDeviceConfig(port, serial),
                "usbDmxPro" => new UsbDmxProDeviceConfig(port, serial),
                _ => null,
            };
            if (device is not null) bindings.Add(new OutputBinding(number, device));
        }
        return bindings;
    }

    static JsonNode? GetIgnoringCase(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
