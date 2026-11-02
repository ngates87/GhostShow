using System.Xml;
using System.Xml.Linq;

namespace GhostShow.Core.Fixtures;

/// <summary>
/// Reads QLC+ fixture definitions (.qxf). QLC+ is Apache-2.0 licensed and ships ~1,400 definitions; the Open Fixture
/// Library (MIT) exports to this format too, so one importer covers both. Files are read from where the user has
/// them; GhostShow doesn't redistribute them.
///
/// Only what GhostShow uses is imported: channel names, a function per channel, defaults, modes, and enough of the
/// type, model and lamp data to pick a <see cref="FixtureCategory"/>.
/// Capability ranges (e.g. "11-255 strobe slow to fast"), physical data and heads are ignored for now.
/// Format reference: https://github.com/mcallegari/qlcplus/wiki/Fixture-definition-file-format
/// </summary>
public static class QlcFixtureImporter
{
    public const string IdPrefix = "qlcplus/";

    /// <exception cref="InvalidDataException">The file isn't a usable QLC+ fixture definition.</exception>
    public static FixtureProfile Import(string path)
    {
        XDocument document;
        try { document = XDocument.Load(path); }
        catch (XmlException ex) { throw new InvalidDataException($"not valid XML: {ex.Message}", ex); }
        return Import(document);
    }

    public static FixtureProfile Import(XDocument document)
    {
        var root = document.Root;
        if (root is null || root.Name.LocalName != "FixtureDefinition")
            throw new InvalidDataException("not a QLC+ fixture definition (no <FixtureDefinition> element).");

        string manufacturer = Text(root, "Manufacturer") ?? throw new InvalidDataException("has no <Manufacturer>.");
        string model = Text(root, "Model") ?? throw new InvalidDataException("has no <Model>.");

        // Keyed by the exact name, untrimmed: some shipped files name a channel "Red " and refer to it as "Red ".
        var channelsByName = new Dictionary<string, FixtureChannel>(StringComparer.Ordinal);
        foreach (var element in Children(root, "Channel"))
        {
            string name = (string?)element.Attribute("Name") ?? throw new InvalidDataException("has a <Channel> without a name.");
            channelsByName.TryAdd(name, ReadChannel(name, element));
        }

        var modes = Children(root, "Mode").Select(m => ReadMode(m, channelsByName)).ToList();
        if (modes.Count == 0) throw new InvalidDataException("defines no modes.");

        string type = Text(root, "Type") ?? "Other";
        string? bulbType = (string?)Child(root, "Physical")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Bulb")?.Attribute("Type");
        return new FixtureProfile
        {
            Id = $"{IdPrefix}{manufacturer}/{model}",
            Manufacturer = manufacturer,
            Model = model,
            Type = type,
            Category = FixtureCategories.Infer(type, model, bulbType, modes),
            Modes = modes,
        };
    }

    static FixtureMode ReadMode(XElement mode, Dictionary<string, FixtureChannel> channelsByName)
    {
        string name = (string?)mode.Attribute("Name") ?? throw new InvalidDataException("has a <Mode> without a name.");
        var channels = Children(mode, "Channel")
            .Select(c => (Number: (int?)c.Attribute("Number") ?? -1, Name: c.Value))
            .OrderBy(c => c.Number)
            .Select(c => channelsByName.TryGetValue(c.Name, out var channel)
                ? channel
                : throw new InvalidDataException($"mode '{name}' uses channel '{c.Name}', which isn't defined."))
            .ToList();
        return new FixtureMode { Name = name, Channels = channels };
    }

    static FixtureChannel ReadChannel(string name, XElement channel)
    {
        var function = FunctionOf(channel);
        return new FixtureChannel
        {
            Name = name.Trim(),
            Function = function,
            DefaultValue = DefaultValueOf(channel, function),
        };
    }

    /// <summary>
    /// Newer files use a channel Preset ("IntensityRed"); older ones a Group ("Intensity") plus an optional Colour.
    /// Fine (16-bit low byte) channels map to Generic: GhostShow only drives the coarse byte for now.
    /// </summary>
    static ChannelFunction FunctionOf(XElement channel)
    {
        var preset = (string?)channel.Attribute("Preset");
        if (preset is not null) return FunctionOfPreset(preset);

        var group = Child(channel, "Group");
        if (group is null || (string?)group.Attribute("Byte") == "1") return ChannelFunction.Generic;
        return group.Value.Trim() switch
        {
            "Intensity" => ColourFunction(Text(channel, "Colour")),
            "Pan" => ChannelFunction.Pan,
            "Tilt" => ChannelFunction.Tilt,
            "Colour" => ChannelFunction.ColorWheel,
            "Gobo" => ChannelFunction.Gobo,
            "Shutter" => ChannelFunction.Shutter,
            "Speed" => ChannelFunction.Speed,
            "Effect" or "Prism" or "Beam" => ChannelFunction.Effect,
            "Maintenance" => ChannelFunction.Maintenance,
            _ => ChannelFunction.Generic,
        };
    }

    static ChannelFunction FunctionOfPreset(string preset)
    {
        if (preset.EndsWith("Fine", StringComparison.Ordinal)) return ChannelFunction.Generic;
        return preset switch
        {
            "IntensityDimmer" or "IntensityMasterDimmer" => ChannelFunction.Intensity,
            "IntensityRed" => ChannelFunction.Red,
            "IntensityGreen" => ChannelFunction.Green,
            "IntensityBlue" => ChannelFunction.Blue,
            "IntensityWhite" => ChannelFunction.White,
            "IntensityAmber" => ChannelFunction.Amber,
            "IntensityUV" => ChannelFunction.UV,
            "PositionPan" => ChannelFunction.Pan,
            "PositionTilt" => ChannelFunction.Tilt,
            _ when preset.StartsWith("Shutter", StringComparison.Ordinal) => ChannelFunction.Shutter,
            _ when preset.StartsWith("Strobe", StringComparison.Ordinal) => ChannelFunction.Strobe,
            _ when preset.StartsWith("Speed", StringComparison.Ordinal) => ChannelFunction.Speed,
            _ when preset.StartsWith("Color", StringComparison.Ordinal) => ChannelFunction.ColorWheel,
            _ when preset.StartsWith("Gobo", StringComparison.Ordinal) => ChannelFunction.Gobo,
            _ when preset.StartsWith("Beam", StringComparison.Ordinal) => ChannelFunction.Effect,
            _ => ChannelFunction.Generic,
        };
    }

    static ChannelFunction ColourFunction(string? colour) => colour switch
    {
        null => ChannelFunction.Intensity,
        "Red" => ChannelFunction.Red,
        "Green" => ChannelFunction.Green,
        "Blue" => ChannelFunction.Blue,
        "White" => ChannelFunction.White,
        "Amber" => ChannelFunction.Amber,
        "UV" => ChannelFunction.UV,
        _ => ChannelFunction.Generic,   // cyan, magenta, yellow, lime, ... not logical controls yet
    };

    /// <summary>
    /// An explicit Default wins. Otherwise a shutter channel defaults to its first "open" range, because a closed
    /// shutter makes the fixture look dead however high the intensity is.
    /// </summary>
    static byte DefaultValueOf(XElement channel, ChannelFunction function)
    {
        if ((int?)channel.Attribute("Default") is { } explicitDefault) return (byte)Math.Clamp(explicitDefault, 0, 255);
        if (function != ChannelFunction.Shutter) return 0;

        var open = Children(channel, "Capability").FirstOrDefault(c =>
            (string?)c.Attribute("Preset") == "ShutterOpen"
            || c.Value.Contains("open", StringComparison.OrdinalIgnoreCase));
        return (byte)Math.Clamp((int?)open?.Attribute("Min") ?? 0, 0, 255);
    }

    // QLC+ files use a default XML namespace (most of them) or none (some old ones); match on local names.
    static IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements().Where(e => e.Name.LocalName == name);
    static XElement? Child(XElement parent, string name) => Children(parent, name).FirstOrDefault();
    static string? Text(XElement parent, string name) => Child(parent, name)?.Value.Trim() is { Length: > 0 } s ? s : null;
}
