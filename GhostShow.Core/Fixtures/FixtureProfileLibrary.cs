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
        foreach (var profile in profiles) _byId[profile.Id] = profile;
        Profiles = _byId.Values
            .OrderBy(p => p.Manufacturer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        LoadErrors = loadErrors?.ToArray() ?? [];
    }

    public static FixtureProfileLibrary Empty { get; } = new([]);

    /// <summary>Sorted by manufacturer, then model.</summary>
    public IReadOnlyList<FixtureProfile> Profiles { get; }

    public IReadOnlyList<ProfileLoadError> LoadErrors { get; }

    public FixtureProfile? Find(string id) => _byId.GetValueOrDefault(id);
}
