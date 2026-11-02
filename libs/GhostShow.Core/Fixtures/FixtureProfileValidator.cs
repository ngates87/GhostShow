using GhostShow.Core.Dmx;

namespace GhostShow.Core.Fixtures;

/// <summary>Checks a profile is usable before it goes into the library. Returns one message per problem.</summary>
public static class FixtureProfileValidator
{
    public static IReadOnlyList<string> Validate(FixtureProfile profile)
    {
        var problems = new List<string>();
        if (profile.SchemaVersion > FixtureProfile.CurrentSchemaVersion)
            problems.Add($"schema version {profile.SchemaVersion} is newer than this version of GhostShow understands ({FixtureProfile.CurrentSchemaVersion}).");
        if (string.IsNullOrWhiteSpace(profile.Id)) problems.Add("has no id.");
        if (string.IsNullOrWhiteSpace(profile.Model)) problems.Add("has no model name.");
        if (profile.Modes is null || profile.Modes.Count == 0)
        {
            problems.Add("has no modes.");
            return problems;
        }

        foreach (var duplicate in profile.Modes.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"has more than one mode named '{duplicate.Key}'.");

        foreach (var mode in profile.Modes)
        {
            if (string.IsNullOrWhiteSpace(mode.Name)) problems.Add("has a mode without a name.");
            if (mode.Channels is null || mode.Channels.Count == 0)
                problems.Add($"mode '{mode.Name}' has no channels.");
            else if (mode.Channels.Count > DmxUniverse.ChannelCount)
                problems.Add($"mode '{mode.Name}' has {mode.Channels.Count} channels; a universe only has {DmxUniverse.ChannelCount}.");
            else if (mode.Channels.Any(c => string.IsNullOrWhiteSpace(c.Name)))
                problems.Add($"mode '{mode.Name}' has a channel without a name.");
        }
        return problems;
    }
}
