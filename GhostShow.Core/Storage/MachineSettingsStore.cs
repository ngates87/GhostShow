using System.Text.Json;

namespace GhostShow.Core.Storage;

/// <param name="Error">Set when the file existed but couldn't be used; Settings are then the defaults.</param>
public sealed record MachineSettingsLoadResult(MachineSettings Settings, string? Error);

public static class MachineSettingsStore
{
    /// <summary>
    /// Loads machine settings. A missing file just means defaults. An unreadable or invalid one is renamed to
    /// machine.json.invalid-&lt;time&gt; before defaults are used, so the next save can't overwrite what the user may
    /// want to repair by hand.
    /// </summary>
    public static async Task<MachineSettingsLoadResult> LoadAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        string path = paths.MachineSettingsFile;
        if (!File.Exists(path)) return new MachineSettingsLoadResult(new MachineSettings(), null);

        string? problem;
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            var settings = JsonSerializer.Deserialize<MachineSettings>(json, Json.Options);
            problem = settings switch
            {
                null => "the file is empty",
                { SchemaVersion: > MachineSettings.CurrentSchemaVersion } =>
                    $"it was written by a newer GhostShow (version {settings.SchemaVersion})",
                _ => null,
            };
            if (problem is null) return new MachineSettingsLoadResult(settings!, null);
        }
        catch (JsonException ex)
        {
            problem = $"it isn't valid: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MachineSettingsLoadResult(new MachineSettings(),
                $"Couldn't read machine settings ({path}): {ex.Message}. Using defaults.");
        }

        string backup = $"{path}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, backup);
        }
        catch (IOException ex)
        {
            return new MachineSettingsLoadResult(new MachineSettings(),
                $"Machine settings ({path}) couldn't be used because {problem}, and couldn't be set aside: {ex.Message}. Using defaults.");
        }
        return new MachineSettingsLoadResult(new MachineSettings(),
            $"Machine settings couldn't be used because {problem}. The file was kept as {Path.GetFileName(backup)}; using defaults.");
    }

    public static Task SaveAsync(MachineSettings settings, AppPaths paths, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllTextAsync(paths.MachineSettingsFile, JsonSerializer.Serialize(settings, Json.Options), cancellationToken);
}
