using System.Text.Json;
using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Storage;

/// <summary>
/// Builds the fixture profile library from, in order (later sources replace earlier ones with the same ID):
/// GhostShow's built-in profiles, QLC+'s fixture folders, folders listed in machine settings, and the user's own
/// profile folder. Reading ~1,400 QLC+ files takes about a second, so call it off the UI thread.
/// A broken file is skipped and listed in <see cref="FixtureProfileLibrary.LoadErrors"/>; it never stops the load.
/// </summary>
public static class FixtureProfileLoader
{
    const string BuiltInResourcePrefix = "GhostShow.Core.Fixtures.Profiles.";

    public static FixtureProfileLibrary Load(AppPaths paths, MachineSettings settings)
    {
        var profiles = new List<FixtureProfile>(LoadBuiltIn());
        var errors = new List<ProfileLoadError>();

        IEnumerable<string> directories = [.. AppPaths.QlcPlusFixtureDirectories, .. settings.ProfileDirectories, paths.UserProfilesDirectory];
        foreach (var directory in directories.Where(Directory.Exists))
            LoadDirectory(directory, profiles, errors);

        return new FixtureProfileLibrary(profiles, errors);
    }

    public static IReadOnlyList<FixtureProfile> LoadBuiltIn()
    {
        var assembly = typeof(FixtureProfileLoader).Assembly;
        var profiles = new List<FixtureProfile>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(BuiltInResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            // Built-in profiles are covered by tests, so a failure here is a bug: let it throw.
            profiles.Add(ParseJson(reader.ReadToEnd()));
        }
        return profiles;
    }

    /// <summary>Reads every .json and .qxf file under directory (including subfolders, as QLC+ sorts by manufacturer).</summary>
    public static void LoadDirectory(string directory, List<FixtureProfile> profiles, List<ProfileLoadError> errors)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new ProfileLoadError(directory, ex.Message));
            return;
        }

        foreach (var file in files)
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".json" or ".qxf")) continue;
            try
            {
                var profile = extension == ".qxf" ? QlcFixtureImporter.Import(file) : ParseJson(File.ReadAllText(file));
                var problems = FixtureProfileValidator.Validate(profile);
                if (problems.Count == 0) profiles.Add(profile);
                else errors.Add(new ProfileLoadError(file, string.Join(" ", problems)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add(new ProfileLoadError(file, ex.Message));
            }
        }
    }

    /// <exception cref="InvalidDataException">Not a valid GhostShow profile.</exception>
    public static FixtureProfile ParseJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FixtureProfile>(json, Json.Options)
                   ?? throw new InvalidDataException("the file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    public static string ToJson(FixtureProfile profile) => JsonSerializer.Serialize(profile, Json.Options);
}
