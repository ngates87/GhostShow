using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Storage;

/// <summary>
/// Fixture types the user makes in GhostShow (for a fixture the libraries don't have). Each is an ordinary GhostShow
/// profile file in the user's profile folder (<see cref="AppPaths.UserProfilesDirectory"/>), under custom/, so it
/// loads like any other profile. Its ID ("custom/acme/fogger-3000") is fixed when it's made: fixtures refer to it,
/// so renaming the type later doesn't break them, and the file is named after the ID.
/// </summary>
public static class CustomProfiles
{
    public const string IdPrefix = "custom/";

    public static bool IsCustom(FixtureProfile profile) => profile.Id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>A new ID from the names ("custom/acme/fogger-3000"), different from every ID the library has.</summary>
    public static string NewId(string manufacturer, string model, FixtureProfileLibrary library)
    {
        string baseId = $"{IdPrefix}{Slug(manufacturer, "custom")}/{Slug(model, "fixture")}";
        string id = baseId;
        for (int n = 2; library.Find(id) is not null; n++) id = $"{baseId}-{n}";
        return id;
    }

    /// <summary>Where a custom profile's file is: the ID's path under the user's profile folder.</summary>
    public static string PathOf(AppPaths paths, string id) =>
        Path.Combine(paths.UserProfilesDirectory, Path.Combine(id.Split('/')) + ".json");

    /// <summary>Writes the profile's file (replacing it, if it's an edit). Written to a temporary file first, so a failed write leaves the old one.</summary>
    /// <exception cref="InvalidDataException">The profile isn't valid (the message says why) or isn't a custom one.</exception>
    public static void Save(AppPaths paths, FixtureProfile profile)
    {
        if (!IsCustom(profile)) throw new InvalidDataException($"'{profile.Id}' isn't a custom profile.");
        var problems = FixtureProfileValidator.Validate(profile);
        if (problems.Count > 0) throw new InvalidDataException(string.Join(" ", problems));
        string path = PathOf(paths, profile.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, FixtureProfileLoader.ToJson(profile));
        File.Move(temporary, path, overwrite: true);
    }

    public static void Delete(AppPaths paths, FixtureProfile profile)
    {
        if (!IsCustom(profile)) return;
        string path = PathOf(paths, profile.Id);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>"Pro-Lights LED Par 64!" → "pro-lights-led-par-64": letters and digits, words joined by dashes.</summary>
    static string Slug(string text, string fallback)
    {
        var words = new string(text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? fallback : string.Join('-', words);
    }
}
