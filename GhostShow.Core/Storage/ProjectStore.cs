using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShow.Core.Model;

namespace GhostShow.Core.Storage;

/// <param name="Project">null if the file couldn't be used; Error says why.</param>
/// <param name="MigratedOutputs">
/// Output assignments found in a version 1 file. They now belong in machine settings; the caller decides whether to
/// move them there.
/// </param>
public sealed record ProjectLoadResult(Project? Project, string? Error, IReadOnlyList<OutputBinding> MigratedOutputs)
{
    public static ProjectLoadResult Failed(string error) => new(null, error, []);
}

/// <summary>Reads and writes show files. Problems with a file are returned as messages, not thrown.</summary>
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

    /// <summary>Writes atomically: if saving fails, the previous file is untouched.</summary>
    public static Task SaveAsync(Project project, string path, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllTextAsync(path, ToJson(project), cancellationToken);

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

        var migratedOutputs = version == 1 ? ExtractVersion1Outputs(obj) : [];

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

        project.Version = Project.CurrentSchemaVersion;
        return new ProjectLoadResult(project, null, migratedOutputs);
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
