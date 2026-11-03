using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GhostShow.Core.Fixtures;

/// <summary>
/// GhostShow's own grouping of fixtures, by what they are in a haunt rather than by lighting-console jargon. Listed in
/// the order the profile browser shows them.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixtureCategory>))]
public enum FixtureCategory
{
    /// <summary>Dimmer packs and single dimmer channels driving any load.</summary>
    Dimmer,
    /// <summary>Non-LED lights: tungsten/halogen PARs, plain bulbs, discharge-lamp fixtures that aren't movers.</summary>
    Conventional,
    /// <summary>LED colour fixtures: RGB(W/A/UV) PARs, bars, washes.</summary>
    Color,
    /// <summary>UV / blacklight.</summary>
    Blacklight,
    MovingHead,
    Scanner,
    Strobe,
    Laser,
    /// <summary>Flowers, derbies, moonflowers, flame and other light effects.</summary>
    EffectLight,
    Fog,
    Haze,
    Snow,
    Bubble,
    /// <summary>DMX relay and switch packs: props, animatronics, anything switched on and off.</summary>
    Relay,
    Other,
}

public static class FixtureCategories
{
    public static string Label(this FixtureCategory category) => category switch
    {
        FixtureCategory.Dimmer => "Dimmers",
        FixtureCategory.Conventional => "Conventional (non-LED)",
        FixtureCategory.Color => "Color / RGB",
        FixtureCategory.Blacklight => "Blacklight / UV",
        FixtureCategory.MovingHead => "Moving heads",
        FixtureCategory.Scanner => "Scanners",
        FixtureCategory.Strobe => "Strobes",
        FixtureCategory.Laser => "Lasers",
        FixtureCategory.EffectLight => "Effect lights",
        FixtureCategory.Fog => "Fog & smoke",
        FixtureCategory.Haze => "Haze",
        FixtureCategory.Snow => "Snow",
        FixtureCategory.Bubble => "Bubbles",
        FixtureCategory.Relay => "Relays & props",
        _ => "Other",
    };

    /// <summary>
    /// Works out a category for a profile that doesn't state one, from its free-text type (e.g. QLC+'s "Color
    /// Changer"), model name, lamp type and channel functions. Rules run in order; the first match wins. Effects are
    /// matched by model name first because libraries file fog, snow and bubble machines under vague types.
    /// </summary>
    /// <param name="bulbType">Lamp type if known, e.g. "LED", "MSD 250W", "Halogen". null or "" if unknown.</param>
    public static FixtureCategory Infer(string type, string model, string? bulbType, IReadOnlyList<FixtureMode> modes)
    {
        string name = model.ToLowerInvariant();
        string kind = type.ToLowerInvariant();

        if (SnowMachine.IsMatch(name)) return FixtureCategory.Snow;
        if (name.Contains("bubble")) return FixtureCategory.Bubble;
        if (name.Contains("haze") || kind.Contains("hazer")) return FixtureCategory.Haze;
        if (name.Contains("fog") || name.Contains("smoke") || kind.Contains("smoke") || kind.Contains("fog")) return FixtureCategory.Fog;
        if (name.Contains("relay") || name.Contains("switch pack") || kind.Contains("relay")) return FixtureCategory.Relay;
        if (kind.Contains("laser") || bulbType?.Contains("laser", StringComparison.OrdinalIgnoreCase) == true) return FixtureCategory.Laser;
        if (kind.Contains("moving head")) return FixtureCategory.MovingHead;
        if (kind.Contains("scanner")) return FixtureCategory.Scanner;
        if (kind.Contains("strobe")) return FixtureCategory.Strobe;
        if (kind.Contains("dimmer")) return FixtureCategory.Dimmer;
        if (kind.Contains("flower") || kind.Contains("effect") || name.Contains("flame") || name.Contains("fire")) return FixtureCategory.EffectLight;
        if (kind.Contains("conventional") || IsNonLedLamp(bulbType)) return FixtureCategory.Conventional;

        var functions = modes.SelectMany(m => m.Channels).Select(c => c.Function).ToHashSet();
        bool hasRgb = functions.Contains(ChannelFunction.Red) || functions.Contains(ChannelFunction.Green) || functions.Contains(ChannelFunction.Blue);
        if (!hasRgb && (functions.Contains(ChannelFunction.UV) || name.Contains("blacklight") || name.Contains("black light") || name.Contains("uv ")))
            return FixtureCategory.Blacklight;
        if (hasRgb || kind.Contains("color") || kind.Contains("led bar")) return FixtureCategory.Color;
        return FixtureCategory.Other;
    }

    /// <summary>"snow" as a word, but not a snowball (a mirror-ball effect light).</summary>
    static readonly Regex SnowMachine = new(@"\bsnow(?!\s?ball)", RegexOptions.Compiled);

    /// <summary>
    /// A lamp type naming something other than LED or laser, e.g. "MSD 250W", "HMI 575W", "EFR 150W". Lamp types are
    /// free text in QLC+; placeholders like "Other" or "Dimmer" say nothing about the lamp, so they don't count.
    /// </summary>
    static bool IsNonLedLamp(string? bulbType) =>
        !string.IsNullOrWhiteSpace(bulbType)
        && bulbType.Trim() is not ("Other" or "Dimmer")
        && !bulbType.Contains("LED", StringComparison.OrdinalIgnoreCase)
        && !bulbType.Contains("laser", StringComparison.OrdinalIgnoreCase);
}
