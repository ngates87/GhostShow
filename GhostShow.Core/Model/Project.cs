using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Model;

/// <summary>
/// The show file: everything about a show that should travel with it to another computer. Hardware assignments
/// (which adapter drives which universe) are per machine and live in <see cref="Storage.MachineSettings"/>.
/// </summary>
public sealed class Project
{
    /// <summary>2: fixtures added, output hardware moved to machine settings. 1: the timeline prototype.</summary>
    public const int CurrentSchemaVersion = 2;

    public int Version { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "Untitled";
    public List<UniverseConfig> Universes { get; set; } = [new UniverseConfig { Number = 0, Name = "Universe 0" }];
    public List<FixtureInstance> Fixtures { get; set; } = [];

    /// <summary>Timeline scenes from the prototype. Frozen for Phase 1: loaded, saved and playable from the CLI.</summary>
    public List<Scene> Scenes { get; set; } = [];

    public Scene? FindScene(string name) =>
        Scenes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A self-contained show: one room, one prop, one scare.</summary>
public sealed class Scene
{
    public string Name { get; set; } = "Scene";
    public int DurationMs { get; set; } = 10_000;
    public bool Loop { get; set; }
    public List<Track> Tracks { get; set; } = new();
}

/// <summary>A DMX universe in the show. Fixtures and channel tracks refer to it by number.</summary>
public sealed class UniverseConfig
{
    /// <summary>0-32767 (Art-Net's range, the widest of the protocols GhostShow speaks).</summary>
    public int Number { get; set; }
    public string Name { get; set; } = "Universe";
}
