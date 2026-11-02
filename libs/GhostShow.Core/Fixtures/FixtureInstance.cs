namespace GhostShow.Core.Fixtures;

/// <summary>A physical fixture in the rig, as saved in the show file. Edited freely; validated by <see cref="FixturePatcher"/>.</summary>
public sealed class FixtureInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Fixture";
    public string ProfileId { get; set; } = "";
    public string ModeName { get; set; } = "";
    public int Universe { get; set; } = Model.Project.FirstUniverse;

    /// <summary>1-512: the DMX address of the fixture's first channel.</summary>
    public int StartAddress { get; set; } = 1;

    /// <summary>Disabled fixtures stay in the show but are not patched, so they neither output nor conflict.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional label for grouping fixtures on screen, e.g. "Graveyard".</summary>
    public string? Group { get; set; }
}
