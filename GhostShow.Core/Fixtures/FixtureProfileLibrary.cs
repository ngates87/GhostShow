namespace GhostShow.Core.Fixtures;

/// <summary>A profile file that couldn't be used, and why.</summary>
public sealed record ProfileLoadError(string Source, string Message)
{
    public override string ToString() => $"{Source}: {Message}";
}

/// <summary>
/// All fixture profiles available on this machine, looked up by ID. Immutable: to pick up new files, load a new
/// library (see <see cref="Storage.FixtureProfileLoader"/>). When two sources define the same ID, the one added
/// later wins, so a user's own profile can replace a built-in or imported one.
/// </summary>
public sealed class FixtureProfileLibrary
{
    readonly Dictionary<string, FixtureProfile> _byId = new(StringComparer.OrdinalIgnoreCase);

    public FixtureProfileLibrary(IEnumerable<FixtureProfile> profiles, IEnumerable<ProfileLoadError>? loadErrors = null)
    {
        // The same fixture often comes from two sources under slightly different names ("SIXPAR 200 IP" and
        // "SIXPAR 200IP"). Every ID stays findable, so shows using either keep working, but only the later source's
        // copy is listed for browsing.
        var listed = new Dictionary<string, FixtureProfile>();
        foreach (var profile in profiles)
        {
            _byId[profile.Id] = profile;
            listed[SameFixtureKey(profile)] = profile;
        }
        Profiles = listed.Values
            .OrderBy(p => p.Manufacturer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        LoadErrors = loadErrors?.ToArray() ?? [];
    }

    public static FixtureProfileLibrary Empty { get; } = new([]);

    /// <summary>Sorted by manufacturer, then model; one entry per fixture even if several sources define it.</summary>
    public IReadOnlyList<FixtureProfile> Profiles { get; }

    public IReadOnlyList<ProfileLoadError> LoadErrors { get; }

    public FixtureProfile? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// A name with case, spaces and punctuation removed, so "Pro-Lights" and "Prolights", or "KAM" and "Kam", compare
    /// equal. Used to merge manufacturer spellings and spot the same fixture from different sources.
    /// </summary>
    public static string NormalizeName(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    static string SameFixtureKey(FixtureProfile profile) => $"{NormalizeName(profile.Manufacturer)}/{NormalizeName(profile.Model)}";
}
