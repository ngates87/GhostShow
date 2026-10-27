using System.Text.Json.Serialization;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Model;

/// <summary>
/// A show: everything that should travel with it to another computer. Stored as a folder (show.json for this object,
/// one file per timeline; see <see cref="Storage.ShowFolderStore"/>). Hardware assignments (which adapter drives which
/// universe) are per machine and live in <see cref="Storage.MachineSettings"/>.
/// </summary>
public sealed class Project
{
    /// <summary>
    /// 5: a show is a folder: show.json (this) plus a file per timeline under timelines/, in folders.
    /// 4: timelines (tracks of levels, audio and marker events) replace the prototype's scenes, which are converted.
    /// 3: universes numbered from 1, like consoles and sACN (earlier versions started at 0; loading shifts them up).
    /// 2: fixtures added, output hardware moved to machine settings. 1: the timeline prototype.
    /// </summary>
    public const int CurrentSchemaVersion = 5;

    /// <summary>Universe numbers run 1-63999: sACN's range, the widest of the protocols GhostShow will speak.</summary>
    public const int FirstUniverse = 1, LastUniverse = 63999;

    public int Version { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled";
    public List<UniverseConfig> Universes { get; set; } = [new UniverseConfig { Number = 1, Name = "Universe 1" }];
    public List<FixtureInstance> Fixtures { get; set; } = [];

    /// <summary>
    /// The show's sequences, in every folder; a new show has one, "Main", with a lighting and an audio track.
    /// Each is stored in its own file, not in show.json.
    /// </summary>
    [JsonIgnore]
    public List<ShowTimeline> Timelines { get; set; } = [NewTimeline("Main")];

    /// <summary>Every folder of the timeline tree ('/'-separated paths), including empty ones and each parent.</summary>
    [JsonIgnore]
    public SortedSet<string> TimelineFolders { get; set; } = new(TimelineNames.Comparer);

    /// <summary>The timeline files and folders as last loaded or saved (relative to timelines/), so saving can tidy up.</summary>
    [JsonIgnore]
    internal HashSet<string> SavedTimelinePaths { get; set; } = new(TimelineNames.Comparer);

    [JsonIgnore]
    internal HashSet<string> SavedFolders { get; set; } = new(TimelineNames.Comparer);

    /// <summary>By tree path ("Rooms/Troll room"), or by name alone when that's unambiguous.</summary>
    public ShowTimeline? FindTimeline(string pathOrName)
    {
        var byPath = Timelines.FirstOrDefault(t => TimelineNames.Comparer.Equals(t.TreePath, pathOrName));
        if (byPath is not null) return byPath;
        var byName = Timelines.Where(t => TimelineNames.Comparer.Equals(t.Name, pathOrName)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Adds a folder and every parent of it.</summary>
    public void AddFolder(string folder)
    {
        for (var f = folder; f.Length > 0; f = TimelineNames.ParentOf(f)) TimelineFolders.Add(f);
    }

    public static ShowTimeline NewTimeline(string name) => new()
    {
        Name = name,
        Tracks =
        [
            new TimelineTrack { Name = "Lighting", Kind = TrackKind.Lighting },
            new TimelineTrack { Name = "Audio", Kind = TrackKind.Audio },
        ],
    };
}

/// <summary>A DMX universe in the show. Fixtures and channel tracks refer to it by number.</summary>
public sealed class UniverseConfig
{
    /// <summary>1-63999, as shown to the user (see <see cref="Project.FirstUniverse"/>).</summary>
    public int Number { get; set; } = Project.FirstUniverse;
    public string Name { get; set; } = "Universe";
}
