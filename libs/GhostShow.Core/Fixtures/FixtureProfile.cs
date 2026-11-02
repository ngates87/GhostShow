using System.Text.Json.Serialization;

namespace GhostShow.Core.Fixtures;

/// <summary>
/// What a channel does. The first group are the logical controls the fixture control screen and future
/// OSC/web commands can address by name; the rest describe the channel so it can be labelled sensibly.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChannelFunction>))]
public enum ChannelFunction
{
    Generic,
    Intensity,
    Red,
    Green,
    Blue,
    White,
    Amber,
    UV,
    Strobe,
    Shutter,
    Pan,
    Tilt,
    ColorWheel,
    Gobo,
    Speed,
    Effect,
    Maintenance,
}

/// <summary>
/// A type of fixture, e.g. "Eurolite LED PARty RGBW". Stored apart from the show (see docs/fixture-profiles.md)
/// so one library serves every show. Immutable once loaded.
/// </summary>
public sealed class FixtureProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Stable key that fixture instances refer to, e.g. "generic/rgbw" or "qlcplus/Eurolite/LED PARty RGBW".</summary>
    public required string Id { get; init; }

    public string Manufacturer { get; init; } = "Generic";
    public required string Model { get; init; }

    /// <summary>Free text, e.g. "Dimmer", "Color Changer", "Moving Head".</summary>
    public string Type { get; init; } = "Other";

    /// <summary>
    /// GhostShow's grouping (fog, dimmer, moving head, ...). Optional in profile files; when absent it's worked out
    /// from the type, model and channels (see <see cref="EffectiveCategory"/>).
    /// </summary>
    public FixtureCategory? Category { get; init; }

    [JsonIgnore]
    public FixtureCategory EffectiveCategory => Category ?? FixtureCategories.Infer(Type, Model, null, Modes);

    /// <summary>Most fixtures offer several DMX layouts ("3 channel", "8 channel"); each is a mode.</summary>
    public required IReadOnlyList<FixtureMode> Modes { get; init; }

    public FixtureMode? FindMode(string name) =>
        Modes.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Manufacturer} {Model}";
}

public sealed class FixtureMode
{
    public required string Name { get; init; }

    /// <summary>In DMX order: the first entry is at the fixture's start address.</summary>
    public required IReadOnlyList<FixtureChannel> Channels { get; init; }
}

public sealed class FixtureChannel
{
    public required string Name { get; init; }
    public ChannelFunction Function { get; init; } = ChannelFunction.Generic;

    /// <summary>
    /// Value written when the fixture is patched. Usually 0, but e.g. a shutter channel must sit at its "open"
    /// value or the fixture emits no light at all.
    /// </summary>
    public byte DefaultValue { get; init; }
}
