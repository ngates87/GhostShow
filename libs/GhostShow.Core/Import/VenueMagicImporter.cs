using System.Text.RegularExpressions;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Import;

/// <summary>What an import made: the show, the fixture types it needs (custom types, to save), and notes for the user.</summary>
public sealed record ImportResult(Project Project, IReadOnlyList<FixtureProfile> Profiles, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a VenueMagic project into a GhostShow show:
/// - the patch: each VenueMagic fixture type becomes a custom fixture type ("VenueMagic" as its maker, one mode with
///   its channels, what each does guessed from its name), and each fixture is placed at its address on universe 1;
/// - each timeline (its .vmt, or the .vmt.bak backup when that's all there is), in the same folders: lamp and
///   control tracks become lighting tracks (Level Control clips with their envelopes; the "Lightning Storm" and
///   "Brownout" expressions become the Lightning and Brown out effects), audio tracks keep their clips (files found
///   in the project's folder).
/// - event tracks keep their Run Timeline events, each running the timeline of that name (looped, one only, as set),
///   and their Cue List Commands that stop or end the loop of a cue-list item (also inside Multi-Events);
/// - cue buttons become show buttons, in their groups' order: their colour, the key their caption marks ("&amp;"), and
///   a Run timeline (looped, one only, as set), or their event's Stop all, Stop or End loop.
/// What can't be converted (other expressions, volume envelopes, channels not in the patch, events and buttons whose
/// timeline isn't found, other commands and kinds of button, timelines missing or saved by older VenueMagic versions)
/// is left out and listed in the notes.
/// </summary>
public static class VenueMagicImporter
{
    public const string Manufacturer = "VenueMagic";

    public static ImportResult Import(VenueMagicProject source, string showName)
    {
        var notes = new List<string>();
        var profiles = new Dictionary<string, FixtureProfile>();
        foreach (var type in source.FixtureTypes)
        {
            string model = ModelName(type.Name);
            var functions = type.Channels.Select(FunctionOf).ToList();
            profiles[type.Id] = new FixtureProfile
            {
                Id = "custom/venuemagic-" + Slug(model),
                Manufacturer = Manufacturer,
                Model = model,
                Type = functions.Contains(ChannelFunction.Pan) ? "Moving Head"
                     : functions.Any(f => f is ChannelFunction.Red or ChannelFunction.Green or ChannelFunction.Blue) ? "Color Changer"
                     : functions.All(f => f == ChannelFunction.Intensity) ? "Dimmer"
                     : "Other",
                Modes =
                [
                    new FixtureMode
                    {
                        Name = $"{type.Channels.Count} channel",
                        Channels = type.Channels.Select(c => new FixtureChannel { Name = c, Function = FunctionOf(c) }).ToList(),
                    },
                ],
            };
        }

        var project = new Project { Name = showName, Timelines = [] };
        var placed = new Dictionary<string, (VmFixture Source, FixtureInstance Fixture)>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in source.Fixtures)
        {
            var profile = profiles[fixture.TypeId];
            if (fixture.Channels.Count != profile.Modes[0].Channels.Count)
                notes.Add($"{fixture.Name}: {fixture.Channels.Count} channels, but its type has {profile.Modes[0].Channels.Count}; check it.");
            var instance = new FixtureInstance
            {
                Name = fixture.Name, ProfileId = profile.Id, ModeName = profile.Modes[0].Name, StartAddress = fixture.Address, Universe = 1,
            };
            project.Fixtures.Add(instance);
            placed.TryAdd(fixture.Name, (fixture, instance));
        }

        // Each timeline, in the same folders, so the tree matches VenueMagic's.
        var runs = new List<Pending>();
        foreach (var path in source.Timelines)
        {
            var timeline = new ShowTimeline { Name = Path.GetFileNameWithoutExtension(path), Folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "" };
            project.Timelines.Add(timeline);
            if (timeline.Folder.Length > 0) project.AddFolder(timeline.Folder);
            var file = Path.Combine(source.Folder, path);
            if (!File.Exists(file) && File.Exists(file + ".bak"))
            {
                file += ".bak";
                notes.Add($"{timeline.TreePath}: only its backup (.vmt.bak) was there; imported from that.");
            }
            if (!File.Exists(file))
            {
                notes.Add($"{timeline.TreePath}: its file wasn't found; left empty.");
                timeline.Tracks = NewTracks();
                continue;
            }
            VenueMagicTimeline read;
            try
            {
                read = VenueMagicTimeline.Read(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                notes.Add($"{timeline.TreePath}: couldn't be read ({ex.Message}); left empty.");
                timeline.Tracks = NewTracks();
                continue;
            }
            if (read.Version != 475)
                notes.Add($"{timeline.TreePath}: saved by an older VenueMagic (record version {read.Version}), which isn't read yet; left empty.");
            timeline.Tracks = read.Tracks.Select(t => ConvertTrack(t, timeline, placed, source.Folder, runs, notes)).OfType<TimelineTrack>().ToList();
            if (timeline.Tracks.Count == 0) timeline.Tracks = NewTracks();
        }
        // Run Timeline events name their timeline: now every timeline is there, find it (by name; in the same folder
        // first, as names can repeat across rooms).
        foreach (var pending in runs.Where(p => !p.ByCueName))
        {
            var (run, target, from, track) = (pending.Event, pending.Target, pending.From, pending.Track);
            var found = Named(project, target, from.Folder, out int count);
            if (found is not null) run.TimelineId = found.Id;
            else
            {
                track.Events.Remove(run);
                notes.Add($"{from.TreePath} · {track.Name}: runs \"{target}\" at {run.Start.TotalSeconds:0.##} s, "
                          + (count == 0 ? "which isn't in the project; left out." : "and more than one timeline has that name; left out."));
            }
        }
        // Cue List Commands name a cue-list item: what a Run Timeline named it in the cue list, else a timeline's name.
        var cueNames = runs.Where(p => !p.ByCueName && p.CueName is { Length: > 0 } && p.Event.TimelineId != Guid.Empty)
            .GroupBy(p => p.CueName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Event.TimelineId).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var button in source.Buttons.Where(b => b.Kind == VmCueButton.Timeline && b.CueName.Length > 0))
            if (Named(project, button.Target, "", out _) is { } runByButton)
                cueNames.TryAdd(button.CueName, [runByButton.Id]);
        ShowTimeline? ByCueName(string item, string folder)
        {
            if (cueNames.TryGetValue(item, out var ids) && ids.Count == 1) return project.Timelines.FirstOrDefault(t => t.Id == ids[0]);
            return Named(project, item.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) ? item[..^4] : item, folder, out _);
        }
        foreach (var pending in runs.Where(p => p.ByCueName))
        {
            var (e, item, from, track) = (pending.Event, pending.Target, pending.From, pending.Track);
            if (ByCueName(item, from.Folder) is { } found) e.TimelineId = found.Id;
            else
            {
                track.Events.Remove(e);
                notes.Add($"{from.TreePath} · {track.Name}: a {(e.Command == TimelineCommand.Stop ? "Stop item" : "End loop")} of \"{item}\" at "
                          + $"{e.Start.TotalSeconds:0.##} s, which no timeline here is called in the cue list; left out.");
            }
        }

        ImportButtons(source.Buttons, project, ByCueName, notes);
        if (project.Timelines.Count == 0) project.Timelines.Add(Project.NewTimeline("Main"));
        // The same note many times (a missing file used by ten clips) once, with a count.
        var grouped = notes.GroupBy(n => n).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} (×{g.Count()})").ToList();
        return new ImportResult(project, profiles.Values.Distinct().ToList(), grouped);
    }

    static List<TimelineTrack> NewTracks() => Project.NewTimeline("").Tracks;

    /// <summary>
    /// An event to point at its timeline once every timeline is imported: by the timeline's name (a Run Timeline), or
    /// by its name in the cue list (a Cue List Command's item). CueName: a Run Timeline's name for it in the cue list.
    /// </summary>
    sealed record Pending(TimelineCommandEvent Event, string Target, ShowTimeline From, TimelineTrack Track, bool ByCueName = false, string? CueName = null);

    /// <summary>The timeline with that name: in the folder given first, else the only one. Count: how many have it.</summary>
    static ShowTimeline? Named(Project project, string name, string folder, out int count)
    {
        var named = project.Timelines.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        count = named.Count;
        return named.FirstOrDefault(t => t.Folder == folder) ?? (named.Count == 1 ? named[0] : null);
    }

    /// <summary>
    /// Cue buttons, group after group: a button for each that does something GhostShow can (a timeline, a Run Timeline,
    /// or a Cue List Command's Stop all, Stop item or End loop), with its colour and the key its caption marks.
    /// </summary>
    static void ImportButtons(IReadOnlyList<VmCueButton> buttons, Project project, Func<string, string, ShowTimeline?> byCueName, List<string> notes)
    {
        int empty = 0;
        foreach (var source in buttons)
        {
            string where = $"Cue button \"{source.Caption}\"" + (source.Group.Length > 0 ? $" ({source.Group})" : "");
            ButtonAction? action = null;
            string? problem = null;
            switch (source.Kind)
            {
                case VmCueButton.Timeline when source.Target.Length == 0:
                    empty++;
                    continue;
                case VmCueButton.Timeline:
                    action = Run(source.Target, source.Loop, source.OneOnly);
                    if (action is null) problem = NotFound(source.Target);
                    break;
                case VmCueButton.Event when source.Run is { } run:
                    action = Run(run.Timeline, run.Loop, run.OneOnly);
                    if (action is null) problem = NotFound(run.Timeline);
                    break;
                case VmCueButton.Event when source.Command is { } command:
                    switch (command.Command)
                    {
                        case VmCueListCommand.StopAll:
                            action = new StopAllAction();
                            break;
                        case VmCueListCommand.StopItem or VmCueListCommand.EndLoop:
                            if (byCueName(command.Item, "") is not { } target)
                                problem = $"its {command.Name} names \"{command.Item}\", which no timeline here is called in the cue list";
                            else if (command.Command == VmCueListCommand.StopItem) action = new StopTimelineAction { TimelineId = target.Id };
                            else action = new EndLoopAction { TimelineId = target.Id };
                            break;
                        default:
                            problem = $"its event is a Cue List Command's {command.Name}, which GhostShow doesn't have";
                            break;
                    }
                    break;
                case VmCueButton.Event:
                    problem = "its event isn't a Run Timeline or a Cue List Command";
                    break;
                default:
                    problem = source.Kind == VmCueButton.Playlist ? "it plays a playlist" : "it plays an audio file, not a timeline";
                    break;
            }
            if (action is null)
            {
                notes.Add($"{where}: {problem ?? "it does nothing GhostShow can do"}; left out.");
                continue;
            }
            var button = new ShowButton
            {
                Name = source.Caption.Length > 0 ? source.Caption : "Button",
                Color = source.Color == "#404040" ? null : source.Color,   // VenueMagic's default grey: the panel's own
                Actions = [action],
            };
            if (source.Key is { } key && KeyName(key) is { } name)
                button.Triggers.Add(new ButtonTrigger { Source = ButtonTrigger.Keyboard, Address = name });
            project.Buttons.Add(button);
        }
        if (empty > 0) notes.Add(empty == 1 ? "A cue button that plays nothing was left out." : $"{empty} cue buttons that play nothing were left out.");

        string NotFound(string timeline) => Named(project, timeline, "", out int count) is null && count > 1
            ? $"more than one timeline is called \"{timeline}\"" : $"it runs \"{timeline}\", which isn't in the project";

        RunTimelineAction? Run(string timeline, bool loop, bool oneOnly)
        {
            var target = Named(project, timeline, "", out _);
            return target is null ? null : new RunTimelineAction { TimelineId = target.Id, Loop = loop, OneOnly = oneOnly };
        }
    }

    /// <summary>A caption's "&amp;" key as GhostShow names keys: "Q", "D1" for the 1 key; null for other characters.</summary>
    static string? KeyName(char key) => key switch
    {
        >= 'A' and <= 'Z' => key.ToString(),
        >= '0' and <= '9' => $"D{key}",
        _ => null,
    };

    static TimelineTrack? ConvertTrack(VmTrack source, ShowTimeline owner, IReadOnlyDictionary<string, (VmFixture Source, FixtureInstance Fixture)> placed,
                                       string projectFolder, List<Pending> runs, List<string> notes)
    {
        string timeline = owner.TreePath;
        // A track left unnamed in VenueMagic shows its ID where the name would be.
        var track = source.Name.StartsWith('$') || source.Name.Length == 0 ? source with { Name = $"{source.Kind} Track" } : source;
        string where = $"{timeline} · {track.Name}";
        switch (track.Kind)
        {
            case "Lamp" or "Control":
            {
                var result = new TimelineTrack { Name = track.Name, Kind = TrackKind.Lighting };
                var missing = new SortedSet<string>();
                LaneTarget? TargetOf(string channel)
                {
                    if (Target(channel, placed) is { } target)
                    {
                        if (result.Channels.All(c => c.Target != target)) result.Channels.Add(new ChannelLane { Target = target });
                        return target;
                    }
                    missing.Add(channel);
                    return null;
                }
                foreach (var channel in track.Channels) TargetOf(channel);
                // The first clip's start handle on a channel is where the channel starts (GhostShow keeps that on the
                // track: its starting level); later clips' start handles just carry on from the clip before.
                var started = new HashSet<LaneTarget>();
                foreach (var clip in track.Items.OfType<VmLevelClip>().OrderBy(c => c.Start))
                    foreach (var envelope in clip.Envelopes)
                        if (TargetOf(envelope.Channel) is { } target && started.Add(target) && envelope.Handles.FirstOrDefault(h => h.Index == 1) is { } first)
                            result.Channels.First(c => c.Target == target).StartLevel = Dmx(first.Level);
                foreach (var item in track.Items)
                {
                    switch (item)
                    {
                        case VmLevelClip clip when clip.Length > 0:
                        {
                            var length = Seconds(clip.Length);
                            var converted = new LightingClip { Name = LightingClip.DefaultName, Start = Seconds(clip.Start), Length = length };
                            foreach (var envelope in clip.Envelopes)
                            {
                                if (TargetOf(envelope.Channel) is not { } target) continue;
                                // The start handle (index 1) carries on from the previous clip, as in GhostShow; the rest are kept.
                                var handles = envelope.Handles.Where(h => h.Index >= 2).OrderBy(h => h.Position).ToList();
                                if (handles.Count == 0) continue;
                                var points = handles.Select(h => new CurvePoint { Time = length * Math.Clamp(h.Position / 100, 0, 1), Value = Dmx(h.Level) }).ToList();
                                if (points[^1].Time < length) points.Add(new CurvePoint { Time = length, Value = points[^1].Value });
                                converted.Lanes.Add(new ChannelLane { Target = target, Points = points });
                            }
                            result.Events.Add(converted);
                            break;
                        }
                        case VmExpression expression when EffectOf(expression.Name) is { } kind && expression.Length > 0:
                            result.Events.Add(new LightingClip
                            {
                                Name = expression.Name, Start = Seconds(expression.Start), Length = Seconds(expression.Length),
                                Effect = LightingEffect.Create(kind, StableSeed($"{where}@{expression.Start}")),
                            });
                            notes.Add($"{where}: \"{expression.Name}\" became GhostShow's {EffectInfo.Of(kind).Name} effect with its usual settings (VenueMagic's settings aren't converted); check it.");
                            break;
                        case VmExpression expression:
                            notes.Add($"{where}: the \"{expression.Name}\" effect ({expression.Start:0.#}–{expression.Start + expression.Length:0.#} s) has no GhostShow equivalent yet; left out.");
                            break;
                    }
                }
                if (missing.Count > 0) notes.Add($"{where}: channels not in the patch, left out: {string.Join(", ", missing)}.");
                return result;
            }
            case "Audio":
            {
                var result = new TimelineTrack { Name = track.Name, Kind = TrackKind.Audio };
                foreach (var clip in track.Items.OfType<VmAudioClip>())
                {
                    var file = LocalFile(clip.File, projectFolder);
                    if (file is null) notes.Add($"{where}: {Path.GetFileName(clip.File.Replace('\\', '/'))} wasn't found in the project's folder; the clip points at its old place.");
                    double volume = clip.VolumeEnd ?? 100;
                    if (clip.VolumeStart is { } from && Math.Abs(from - volume) > 0.5)
                        notes.Add($"{where}: {clip.Name} fades its volume ({from:0}% → {volume:0}%); imported at {volume:0}%.");
                    result.Events.Add(new AudioEvent
                    {
                        Name = clip.Name, Start = Seconds(clip.Start), Length = Seconds(clip.Length), File = file ?? clip.File,
                        Volume = Math.Clamp(volume / 100, 0, 1),
                    });
                }
                return result;
            }
            case "Event":
            {
                // Run Timeline events and Cue List Commands: their timelines are found once every timeline is imported (see Import).
                var result = new TimelineTrack { Name = track.Name, Kind = TrackKind.Event };
                foreach (var item in track.Items.OrderBy(r => r.Start))
                {
                    switch (item)
                    {
                        case VmRunTimeline run:
                        {
                            var e = new TimelineCommandEvent { Start = Seconds(run.Start), Command = TimelineCommand.Run, Loop = run.Loop, OneOnly = run.OneOnly };
                            result.Events.Add(e);
                            runs.Add(new Pending(e, run.Timeline, owner, result, CueName: run.CueName));
                            break;
                        }
                        case VmCommandEvent { Command: { Command: VmCueListCommand.StopItem or VmCueListCommand.EndLoop } command } at:
                        {
                            var e = new TimelineCommandEvent
                            {
                                Start = Seconds(at.Start),
                                Command = command.Command == VmCueListCommand.StopItem ? TimelineCommand.Stop : TimelineCommand.EndLoop,
                            };
                            result.Events.Add(e);
                            runs.Add(new Pending(e, command.Item, owner, result, ByCueName: true));
                            break;
                        }
                        case VmCommandEvent other:
                            notes.Add($"{where}: a Cue List Command's {other.Command.Name} at {other.Start:0.##} s, which GhostShow's events don't have; left out.");
                            break;
                    }
                }
                return result;
            }
            default:
                notes.Add($"{where}: a {track.Kind} track, which isn't imported.");
                return null;
        }
    }

    /// <summary>"Zone_3_Area_8_Dim_1.Channel_3" or "RGB_REC_WINDOWS.RGB.Red": the fixture's channel by number.</summary>
    static LaneTarget? Target(string channel, IReadOnlyDictionary<string, (VmFixture Source, FixtureInstance Fixture)> placed)
    {
        var parts = channel.Split('.');
        if (parts.Length < 2 || !placed.TryGetValue(parts[0], out var fixture)) return null;
        int index = fixture.Source.Channels.ToList().FindIndex(c => string.Equals(c, parts[^1], StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : LaneTarget.FixtureChannel(fixture.Fixture.Id, index + 1);
    }

    /// <summary>A Windows path from the project ("C:\...\2024\audio\x.mp3"), found in the project's folder here: by its path under it, else by name.</summary>
    static string? LocalFile(string windowsPath, string projectFolder)
    {
        var parts = windowsPath.Split('\\');
        string folderName = Path.GetFileName(projectFolder);
        int at = Array.FindLastIndex(parts, p => string.Equals(p, folderName, StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && Path.Combine([projectFolder, .. parts[(at + 1)..]]) is var direct && File.Exists(direct)) return direct;
        if (!Directory.Exists(projectFolder)) return null;
        return Directory.EnumerateFiles(projectFolder, parts[^1], new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
    }

    static EffectKind? EffectOf(string expression) => expression.ToLowerInvariant() switch
    {
        var n when n.Contains("lightning") => EffectKind.Lightning,
        var n when n.Contains("brown") => EffectKind.BrownOut,
        "explosion" => EffectKind.Explosion,
        _ => null,
    };

    /// <summary>The same number every import (string.GetHashCode differs between runs), so an effect keeps its pattern.</summary>
    static int StableSeed(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) hash = (hash ^ c) * 16777619;
        return (int)(hash % 1_000_000);
    }

    static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds), 3));
    static byte Dmx(double percent) => (byte)Math.Round(Math.Clamp(percent, 0, 100) * 2.55);

    /// <summary>"MF2:DMX-4" → "DMX-4" (VenueMagic prefixes its library's types with an ID).</summary>
    static string ModelName(string name) => Regex.Replace(name, @"^MF\d+:", "").Trim();

    static string Slug(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    /// <summary>What a channel does, from its name ("Red_dimming" is red, "Total_Dimming" and "Level3" are dimmers).</summary>
    public static ChannelFunction FunctionOf(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("red")) return ChannelFunction.Red;
        if (n.Contains("green")) return ChannelFunction.Green;
        if (n.Contains("blue")) return ChannelFunction.Blue;
        if (n.Contains("white")) return ChannelFunction.White;
        if (n.Contains("amber")) return ChannelFunction.Amber;
        if (Regex.IsMatch(n, @"\buv\b|ultra")) return ChannelFunction.UV;
        if (n.Contains("strobe") || n.Contains("shutter")) return ChannelFunction.Strobe;
        if (n.Contains("pan") || n.Contains("horizontal")) return ChannelFunction.Pan;
        if (n.Contains("tilt") || n.Contains("vertical")) return ChannelFunction.Tilt;
        if (n.Contains("speed")) return ChannelFunction.Speed;
        if (n.Contains("effect") || n.Contains("macro") || n.Contains("program")) return ChannelFunction.Effect;
        if (n.Contains("gobo")) return ChannelFunction.Gobo;
        if (n.Contains("color") || n.Contains("colour")) return ChannelFunction.ColorWheel;
        if (Regex.IsMatch(n, "level|dim|intensity|channel|master")) return ChannelFunction.Intensity;
        return ChannelFunction.Generic;
    }
}
