using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Storage;

public class ShowFolderStoreTests : IDisposable
{
    readonly TempDirectory _dir = new();
    readonly string _show;

    public ShowFolderStoreTests() => _show = _dir.File("Haunt");

    public void Dispose() => _dir.Dispose();

    static CancellationToken Token => TestContext.Current.CancellationToken;

    string TimelineFile(string relative) => Path.Combine(_show, "timelines", relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Every file under timelines/, as '/'-separated relative paths.</summary>
    string[] Files() => Directory.EnumerateFiles(Path.Combine(_show, "timelines"), "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(Path.Combine(_show, "timelines"), f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    static ShowTimeline Timeline(string name, string folder = "") => new()
    {
        Name = name, Folder = folder, Tracks = [new TimelineTrack { Name = "Lights", Kind = TrackKind.Lighting }],
    };

    static Project Show(params ShowTimeline[] timelines)
    {
        var project = new Project { Name = "Haunt", Timelines = [.. timelines] };
        foreach (var t in timelines) project.AddFolder(t.Folder);
        return project;
    }

    async Task<Project> Save(Project project, string? previous = "same")
    {
        await ShowFolderStore.SaveAsync(project, _show, previous == "same" ? _show : previous, Token);
        return project;
    }

    async Task<ProjectLoadResult> Load() => await ShowFolderStore.LoadAsync(_show, Token);

    [Fact]
    public async Task Saved_show_reloads_with_its_universes_fixtures_and_timelines()
    {
        var fixture = new FixtureInstance { Name = "Graveyard wash", ProfileId = "generic/rgbw", ModeName = "4 channel", StartAddress = 17, Group = "Graveyard", Enabled = false };
        var red = new ChannelLane { Target = LaneTarget.FixtureControl(fixture.Id, ChannelFunction.Red), Color = "#FF0000" };
        var block = new LightingClip
        {
            Name = "Flash", Start = TimeSpan.FromSeconds(1.5), Length = TimeSpan.FromSeconds(2),
            Lanes = [new ChannelLane { Target = red.Target, Points = [new CurvePoint { Time = TimeSpan.Zero, Value = 3 }, new CurvePoint { Time = TimeSpan.FromSeconds(0.5), Value = 255, Curve = CurveKind.Step, Group = Guid.Parse("6b1f0a52-0000-4000-8000-000000000001") }, new CurvePoint { Time = TimeSpan.FromSeconds(2), Value = 9, Curve = CurveKind.Spline }] }],
        };
        var sound = new AudioEvent { File = "audio/z.wav", Start = TimeSpan.FromSeconds(2), Volume = 0.8, FadeIn = TimeSpan.FromSeconds(1), Loop = true };
        var lightning = new ShowTimeline
        {
            Name = "Lightning", Folder = "Rooms/Graveyard", Loop = true, Length = TimeSpan.FromSeconds(8),
            Tracks =
            [
                new TimelineTrack { Name = "Lights", Kind = TrackKind.Lighting, Color = "#FF0000", Channels = [red, new ChannelLane { Target = LaneTarget.Raw(3, 512) }], Events = [block] },
                new TimelineTrack { Name = "Sound", Kind = TrackKind.Audio, Enabled = false, Volume = 0.4, Events = [sound] },
                new TimelineTrack
                {
                    Name = "Storm", Kind = TrackKind.Lighting, Channels = [new ChannelLane { Target = red.Target }],
                    Events = [new LightingClip { Start = TimeSpan.FromSeconds(3), Effect = new LightingEffect { Kind = EffectKind.Lightning, Strength = 0.8, Rate = 3, Color = "#CCDDFF", Seed = 42, Ending = EffectEnding.Dark } }],
                },
            ],
        };
        var project = Show(lightning, Timeline("Lobby"));
        project.Universes = [new UniverseConfig { Number = 1, Name = "Front" }, new UniverseConfig { Number = 3, Name = "Back" }];
        project.Fixtures = [fixture];
        project.AddFolder("Empty for now");

        await Save(project, previous: null);
        var result = await Load();
        var loaded = result.Project!;

        Assert.Null(result.Error);
        Assert.Empty(result.Warnings!);
        Assert.False(result.IsSingleFileShow);
        Assert.Equal(["Lobby.timeline.json", "Rooms/Graveyard/Lightning.timeline.json"], Files());
        Assert.Equal(["Empty for now", "Rooms", "Rooms/Graveyard"], loaded.TimelineFolders);
        Assert.Equal(("Haunt", project.Id), (loaded.Name, loaded.Id));
        Assert.Equal([1, 3], loaded.Universes.Select(u => u.Number));
        var reloaded = Assert.Single(loaded.Fixtures);
        Assert.Equal((fixture.Id, "generic/rgbw", "4 channel", 17, "Graveyard", false),
                     (reloaded.Id, reloaded.ProfileId, reloaded.ModeName, reloaded.StartAddress, reloaded.Group, reloaded.Enabled));

        var timeline = loaded.FindTimeline("Rooms/Graveyard/Lightning")!;
        Assert.Equal((lightning.Id, "Lightning", "Rooms/Graveyard", true, TimeSpan.FromSeconds(8), false),
                     (timeline.Id, timeline.Name, timeline.Folder, timeline.Loop, timeline.Length, timeline.HasUnsavedChanges));
        var lanes = timeline.Tracks[0].Channels;
        Assert.Equal([red.Target, LaneTarget.Raw(3, 512)], lanes.Select(l => l.Target));
        Assert.Equal((red.Id, "#FF0000"), (lanes[0].Id, lanes[0].Color));
        var reloadedBlock = Assert.IsType<LightingClip>(Assert.Single(timeline.Tracks[0].Events));
        Assert.Equal((block.Id, "Flash", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2)), (reloadedBlock.Id, reloadedBlock.Name, reloadedBlock.Start, reloadedBlock.Length));
        Assert.Equal([(TimeSpan.Zero, (byte)3, CurveKind.Linear), (TimeSpan.FromSeconds(0.5), (byte)255, CurveKind.Step), (TimeSpan.FromSeconds(2), (byte)9, CurveKind.Spline)],
                     Assert.Single(reloadedBlock.Lanes).Points.Select(p => (p.Time, p.Value, p.Curve)));
        Assert.Equal([null, Guid.Parse("6b1f0a52-0000-4000-8000-000000000001"), null], reloadedBlock.Lanes[0].Points.Select(p => p.Group));
        Assert.Equal((false, 0.4, 1.0), (timeline.Tracks[1].Enabled, timeline.Tracks[1].Volume, timeline.Tracks[0].Volume));
        var storm = Assert.IsType<LightingClip>(Assert.Single(timeline.Tracks[2].Events)).Effect!;
        Assert.Equal((EffectKind.Lightning, 0.8, 3.0, "#CCDDFF", 42, EffectEnding.Dark), (storm.Kind, storm.Strength, storm.Rate, storm.Color, storm.Seed, storm.Ending));
        Assert.Null(((LightingClip)timeline.Tracks[0].Events[0]).Effect);
        var reloadedSound = Assert.IsType<AudioEvent>(timeline.Tracks[1].Events[0]);
        Assert.Equal(("audio/z.wav", 0.8, TimeSpan.FromSeconds(1), true), (reloadedSound.File, reloadedSound.Volume, reloadedSound.FadeIn, reloadedSound.Loop));
    }

    [Fact]
    public async Task A_version_1_timeline_file_has_its_lighting_events_converted_and_is_rewritten_on_save()
    {
        Directory.CreateDirectory(TimelineFile(""));
        await File.WriteAllTextAsync(Path.Combine(_show, "show.json"), """{ "version": 5, "name": "Old" }""", Token);
        await File.WriteAllTextAsync(TimelineFile("Room.timeline.json"), """
            { "version": 1, "id": "6f0e9d1c-1c49-4a0e-9a53-2b1d2b8f6a11",
              "tracks": [ { "name": "L", "kind": "Lighting", "events": [ { "type": "levels", "universe": 1, "start": "00:00:01", "fade": "00:00:02", "levels": [ { "to": 200, "channel": 5 } ] } ] } ] }
            """, Token);

        var project = (await Load()).Project!;
        var timeline = Assert.Single(project.Timelines);

        Assert.True(timeline.HasUnsavedChanges);
        Assert.Equal(LaneTarget.Raw(1, 5), Assert.Single(timeline.Tracks[0].Channels).Target);
        var block = Assert.IsType<LightingClip>(Assert.Single(timeline.Tracks[0].Events));
        Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), (block.Start, block.Length));
        Assert.Equal([(byte)0, (byte)200], Assert.Single(block.Lanes).Points.Select(p => p.Value));

        await Save(project);
        Assert.Contains("\"version\": 5", await File.ReadAllTextAsync(TimelineFile("Room.timeline.json"), Token));
    }

    [Fact]
    public async Task Marker_tracks_in_older_timeline_files_are_dropped()
    {
        Directory.CreateDirectory(TimelineFile(""));
        await File.WriteAllTextAsync(Path.Combine(_show, "show.json"), """{ "version": 5, "name": "Old" }""", Token);
        await File.WriteAllTextAsync(TimelineFile("Room.timeline.json"), """
            { "version": 4, "id": "6f0e9d1c-1c49-4a0e-9a53-2b1d2b8f6a11",
              "tracks": [ { "name": "Cues", "kind": "Markers", "events": [ { "type": "marker", "name": "Scare", "start": "00:00:02" } ] },
                          { "name": "Sound", "kind": "Audio", "events": [ { "type": "marker", "name": "Stray" }, { "type": "audio", "file": "a.wav" } ] } ] }
            """, Token);

        var result = await Load();
        var timeline = Assert.Single(result.Project!.Timelines);

        Assert.Empty(result.Warnings!);
        Assert.Equal("Sound", Assert.Single(timeline.Tracks).Name);
        Assert.IsType<AudioEvent>(Assert.Single(timeline.Tracks[0].Events));
        Assert.True(timeline.HasUnsavedChanges);   // rewritten without them on the next save
    }

    [Fact]
    public async Task A_version_3_timeline_file_gets_envelope_rules_and_its_steps_read_as_step_curves()
    {
        Directory.CreateDirectory(TimelineFile(""));
        await File.WriteAllTextAsync(Path.Combine(_show, "show.json"), """{ "version": 5, "name": "Old" }""", Token);
        await File.WriteAllTextAsync(TimelineFile("Room.timeline.json"), """
            { "version": 3, "id": "6f0e9d1c-1c49-4a0e-9a53-2b1d2b8f6a11",
              "tracks": [ { "name": "L", "kind": "Lighting", "channels": [ { "target": { "universe": 1, "channel": 5 } } ],
                            "events": [ { "type": "lighting", "start": "00:00:01", "length": "00:00:04",
                                          "lanes": [ { "target": { "universe": 1, "channel": 5 },
                                                       "points": [ { "time": "00:00:00.5", "value": 40 }, { "time": "00:00:02", "value": 200, "step": true }, { "time": "00:00:09", "value": 1 } ] } ] } ] } ] }
            """, Token);

        var project = (await Load()).Project!;
        var timeline = Assert.Single(project.Timelines);

        Assert.True(timeline.HasUnsavedChanges);
        var clip = Assert.IsType<LightingClip>(Assert.Single(timeline.Tracks[0].Events));
        // The first point was a jump (now a step); the point past the end never played; an end handle holds the last level.
        Assert.Equal([(0.5, (byte)40, CurveKind.Step), (2, 200, CurveKind.Step), (4, 200, CurveKind.Linear)],
                     Assert.Single(clip.Lanes).Points.Select(p => (p.Time.TotalSeconds, p.Value, p.Curve)));
        await Save(project);
        var saved = await File.ReadAllTextAsync(TimelineFile("Room.timeline.json"), Token);
        Assert.DoesNotContain("\"step\"", saved);
        Assert.Contains("\"curve\": \"Step\"", saved);
    }

    [Fact]
    public async Task Only_changed_timelines_are_written()
    {
        var a = Timeline("A");
        var b = Timeline("B");
        var project = await Save(Show(a, b), previous: null);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var f in Files()) File.SetLastWriteTimeUtc(TimelineFile(f), old);

        b.Loop = true;
        b.HasUnsavedChanges = true;
        await Save(project);

        Assert.Equal(old, File.GetLastWriteTimeUtc(TimelineFile("A.timeline.json")));
        Assert.NotEqual(old, File.GetLastWriteTimeUtc(TimelineFile("B.timeline.json")));
        Assert.False(b.HasUnsavedChanges);
    }

    [Fact]
    public async Task Renaming_and_moving_a_timeline_moves_its_file_and_keeps_its_id()
    {
        var a = Timeline("A");
        var project = await Save(Show(a), previous: null);

        a.Name = "Scare";
        project.AddFolder("Rooms");
        a.Folder = "Rooms";
        await Save(project);

        Assert.Equal(["Rooms/Scare.timeline.json"], Files());
        Assert.Equal(a.Id, (await Load()).Project!.FindTimeline("Rooms/Scare")!.Id);
    }

    [Fact]
    public async Task Swapping_two_names_keeps_both_files_right()
    {
        var a = Timeline("A");
        var b = Timeline("B");
        b.Loop = true;
        var project = await Save(Show(a, b), previous: null);

        (a.Name, b.Name) = ("B", "A");
        await Save(project);

        var loaded = (await Load()).Project!;
        Assert.Equal(["A.timeline.json", "B.timeline.json"], Files());
        Assert.Equal((b.Id, true), (loaded.FindTimeline("A")!.Id, loaded.FindTimeline("A")!.Loop));
        Assert.Equal(a.Id, loaded.FindTimeline("B")!.Id);
    }

    [Fact]
    public async Task Changing_only_capitals_renames_the_file_without_leaving_a_copy()
    {
        var a = Timeline("room", "entry");
        var project = await Save(Show(a), previous: null);

        a.Name = "Room";
        project.TimelineFolders = new(TimelineNames.Comparer) { "Entry" };
        a.Folder = "Entry";
        await Save(project);

        Assert.Single((await Load()).Project!.Timelines);
        if (OperatingSystem.IsLinux()) Assert.Equal(["Entry/Room.timeline.json"], Files());
    }

    [Fact]
    public async Task Deleting_timelines_and_folders_removes_their_files_but_never_files_it_did_not_create()
    {
        var keep = Timeline("Keep");
        var gone = Timeline("Gone", "Old/Sub");
        var project = await Save(Show(keep, gone, Timeline("Also gone", "Empty")), previous: null);
        await File.WriteAllTextAsync(TimelineFile("Old/notes.txt"), "mine", Token);

        project.Timelines.RemoveAll(t => t != keep);
        project.TimelineFolders.Clear();
        var warnings = await ShowFolderStore.SaveAsync(project, _show, _show, Token);

        Assert.Equal(["Keep.timeline.json", "Old/notes.txt"], Files());
        Assert.False(Directory.Exists(TimelineFile("Empty")));
        Assert.False(Directory.Exists(TimelineFile("Old/Sub")));
        Assert.Contains(warnings, w => w.Contains("timelines/Old"));
    }

    [Fact]
    public async Task A_hand_copied_timeline_file_gets_its_own_id()
    {
        var a = Timeline("A");
        await Save(Show(a), previous: null);
        File.Copy(TimelineFile("A.timeline.json"), TimelineFile("A copy.timeline.json"));

        var result = await Load();

        Assert.Equal(2, result.Project!.Timelines.Select(t => t.Id).Distinct().Count());
        Assert.Contains(result.Warnings!, w => w.Contains("same ID"));
        // Which file is the original can't be known (a copy can keep the file date); one keeps the ID, the other is
        // given a new one, and saved with it next time.
        var renumbered = Assert.Single(result.Project.Timelines, t => t.Id != a.Id);
        Assert.True(renumbered.HasUnsavedChanges);
    }

    [Fact]
    public async Task Unreadable_and_newer_timeline_files_are_skipped_reported_and_left_alone_on_save()
    {
        var project = await Save(Show(Timeline("Good")), previous: null);
        await File.WriteAllTextAsync(TimelineFile("Broken.timeline.json"), "{ nope", Token);
        await File.WriteAllTextAsync(TimelineFile("Future.timeline.json"), """{ "version": 99, "id": "0b1c4a4e-4a49-4bd0-8b31-1f0b5e3e1c11" }""", Token);

        var result = await Load();
        await ShowFolderStore.SaveAsync(result.Project!, _show, _show, Token);

        Assert.Equal(["Good"], result.Project!.Timelines.Select(t => t.Name));
        Assert.Equal(2, result.Warnings!.Count);
        Assert.Contains(result.Warnings, w => w.Contains("Broken") && w.Contains("Skipped"));
        Assert.Contains(result.Warnings, w => w.Contains("Future") && w.Contains("newer"));
        Assert.Equal(["Broken.timeline.json", "Future.timeline.json", "Good.timeline.json"], Files());
    }

    [Fact]
    public async Task Hidden_folders_are_not_part_of_the_tree()
    {
        await Save(Show(Timeline("A")), previous: null);
        Directory.CreateDirectory(TimelineFile(".backup"));
        File.Copy(TimelineFile("A.timeline.json"), TimelineFile(".backup/A.timeline.json"));

        var loaded = (await Load()).Project!;

        Assert.Single(loaded.Timelines);
        Assert.Empty(loaded.TimelineFolders);
    }

    [Fact]
    public async Task Saving_to_another_folder_writes_everything_and_leaves_the_original_alone()
    {
        var project = await Save(Show(Timeline("A"), Timeline("B", "F")), previous: null);
        string copy = _dir.File("Copy");

        await ShowFolderStore.SaveAsync(project, copy, _show, Token);

        Assert.True(File.Exists(Path.Combine(copy, "timelines", "A.timeline.json")));
        Assert.True(File.Exists(Path.Combine(copy, "timelines", "F", "B.timeline.json")));
        Assert.Equal(["A.timeline.json", "F/B.timeline.json"], Files());
    }

    [Fact]
    public async Task A_tree_mistake_is_refused_before_anything_is_written()
    {
        var project = Show(Timeline("Same"), Timeline("same"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ShowFolderStore.SaveAsync(project, _show, null, Token));

        Assert.False(Directory.Exists(_show));
    }

    [Fact]
    public async Task A_failed_save_leaves_the_previous_files_intact_and_no_temp_files()
    {
        var project = await Save(Show(Timeline("A")), previous: null);
        project.Name = "Half-written";
        project.Timelines[0].HasUnsavedChanges = true;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShowFolderStore.SaveAsync(project, _show, _show, cancelled.Token));

        Assert.Equal("Haunt", (await Load()).Project!.Name);
        Assert.DoesNotContain(Directory.EnumerateFiles(_show, "*", SearchOption.AllDirectories), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Opening_a_folder_without_a_show_is_explained()
    {
        Directory.CreateDirectory(_show);
        var result = await Load();

        Assert.Null(result.Project);
        Assert.Contains("isn't a show folder", result.Error);
    }

    [Theory]
    [InlineData("Troll room", null)]
    [InlineData("Scare #2 (loud)", null)]
    [InlineData("v1.2", null)]
    [InlineData("", "empty")]
    [InlineData("a/b", "contain")]
    [InlineData("what?", "contain")]
    [InlineData("trailing.", "end")]
    [InlineData(".hidden", "dot")]
    [InlineData("CON", "reserved")]
    [InlineData("con.txt", "reserved")]
    public void Names_must_work_as_file_names_everywhere(string name, string? problemContains)
    {
        string? problem = TimelineNames.Problem(name);
        if (problemContains is null) Assert.Null(problem);
        else Assert.Contains(problemContains, problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unique_names_count_up_ignoring_case()
    {
        Assert.Equal("New timeline 3", TimelineNames.Unique("New timeline", ["new timeline", "New timeline 2"]));
    }
}
