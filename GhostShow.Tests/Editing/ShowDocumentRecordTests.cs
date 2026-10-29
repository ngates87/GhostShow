using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShow.Core.Editing;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Editing;

/// <summary>
/// <see cref="ShowDocument.Record"/>: an edit made directly on the model becomes a transaction. Every test also keeps
/// a copy of the show in step through the changes as JSON, so the operations it makes are complete.
/// </summary>
public class ShowDocumentRecordTests
{
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    readonly Project _project;
    readonly ShowTimeline _main;
    readonly TimelineTrack _lights, _sound;
    readonly LightingClip _clip;
    readonly ChannelLane _lane;
    readonly ShowDocument _doc;
    readonly Project _replica;

    public ShowDocumentRecordTests()
    {
        _lane = new ChannelLane { Target = new LaneTarget(Channel: 1), Points = [new CurvePoint { Time = S(0), Value = 0 }, new CurvePoint { Time = S(4), Value = 255 }] };
        _clip = new LightingClip { Start = S(2), Length = S(4), Lanes = [_lane] };
        _lights = new TimelineTrack { Name = "Lights", Kind = TrackKind.Lighting, Events = [_clip, new LightingClip { Start = S(8) }] };
        _sound = new TimelineTrack { Name = "Sound", Kind = TrackKind.Audio, Events = [new AudioEvent { File = "boo.wav", Start = S(1) }] };
        _main = new ShowTimeline { Name = "Main", Tracks = [_lights, _sound] };
        _project = new Project { Timelines = [_main] };
        _doc = new ShowDocument(_project);
        _replica = Copy(_project);
        var remote = new ShowDocument(_replica);
        _doc.Changed += change => remote.Apply(new Transaction(change.Description,
            change.Ops.Select(op => ShowDocument.Decode(JsonNode.Parse(_doc.Encode(op).ToJsonString())!)).ToList()));
    }

    void AssertReplicaInStep() => Assert.Equal(Json(_project), Json(_replica));

    [Fact]
    public void Changes_inside_objects_become_sets_and_keep_the_same_instances()
    {
        var first = _lane.Points[0];
        var change = _doc.Record("Resize clip", () => { _clip.Start = S(3); _lane.Points[1].Time = S(2); _lane.Points[1].Value = 99; }, _clip)!;

        Assert.Equal(["set", "set", "set"], change.Ops.Select(o => _doc.Encode(o)["op"]!.GetValue<string>()));
        Assert.Equal((S(3), S(2), (byte)99), (_clip.Start, _lane.Points[1].Time, _lane.Points[1].Value));
        Assert.Same(first, _lane.Points[0]);
        AssertReplicaInStep();

        _doc.Undo(_main.Id);
        Assert.Equal((S(2), S(4), (byte)255), (_clip.Start, _lane.Points[1].Time, _lane.Points[1].Value));
        AssertReplicaInStep();
    }

    [Fact]
    public void Handles_added_and_removed_keep_the_handles_that_stay()
    {
        var kept = _lane.Points[1];
        _doc.Record("Add envelope handle", () =>
        {
            _lane.Points.Insert(1, new CurvePoint { Time = S(1), Value = 50 });
            _lane.Points.RemoveAt(0);
            kept.Value = 10;
        }, _main);

        Assert.Equal([(1, 50), (4, 10)], _lane.Points.Select(p => ((int)p.Time.TotalSeconds, (int)p.Value)));
        Assert.Same(kept, _lane.Points[1]);
        AssertReplicaInStep();
        _doc.Undo(_main.Id);
        Assert.Equal([(0, 0), (4, 255)], _lane.Points.Select(p => ((int)p.Time.TotalSeconds, (int)p.Value)));
        Assert.Same(kept, _lane.Points[1]);
        AssertReplicaInStep();
    }

    [Fact]
    public void Events_and_tracks_added_removed_reordered_and_moved_between_tracks()
    {
        var second = _lights.Events[1];
        var boo = _sound.Events[0];
        var added = new LightingClip { Start = S(20), Effect = new LightingEffect { Kind = EffectKind.BrownOut } };
        var newTrack = new TimelineTrack { Name = "Events", Kind = TrackKind.Event };
        _doc.Record("Lots", () =>
        {
            _lights.Events.Remove(second);
            _lights.Events.Insert(0, added);
            _sound.Events.Remove(boo);
            _lights.Events.Add(boo);   // to another track
            boo.Start = S(30);
            _main.Tracks.Reverse();
            _main.Tracks.Add(newTrack);
        }, _main);

        Assert.Equal([_sound, _lights, newTrack], _main.Tracks);
        Assert.Equal([added, _clip, boo], _lights.Events);
        AssertReplicaInStep();

        _doc.Undo(_main.Id);
        Assert.Equal([_lights, _sound], _main.Tracks);
        Assert.Equal([_clip, second], _lights.Events);
        Assert.Equal([boo], _sound.Events);
        Assert.Equal(S(1), boo.Start);
        AssertReplicaInStep();

        _doc.Redo(_main.Id);
        Assert.Equal([added, _clip, boo], _lights.Events);
        AssertReplicaInStep();
    }

    [Fact]
    public void A_replaced_list_or_object_and_a_change_inside_an_object()
    {
        var effectClip = new LightingClip { Start = S(12), Effect = new LightingEffect { Kind = EffectKind.Lightning, Strength = 1 } };
        _doc.Apply(new Transaction("Add", new InsertOp(ShowPath.Timeline(_main.Id).Track(_lights.Id).Then("events"), -1, effectClip)));

        _doc.Record("Strength", () => effectClip.Effect!.Strength = 0.5, effectClip);
        Assert.Equal(0.5, effectClip.Effect!.Strength);
        var effect = effectClip.Effect;
        _doc.Record("Convert to Level Control", () =>
        {
            effectClip.Effect = null;
            effectClip.Lanes = [new ChannelLane { Target = new LaneTarget(Channel: 3), Points = [new CurvePoint { Time = S(1), Value = 7 }] }];
            _clip.Lanes = [];
        }, effectClip, _clip);
        AssertReplicaInStep();

        _doc.Undo(_main.Id);
        Assert.Same(effect, effectClip.Effect);
        Assert.Empty(effectClip.Lanes);
        Assert.Equal([_lane], _clip.Lanes);
        AssertReplicaInStep();
    }

    [Fact]
    public void Shallow_roots_see_their_own_properties_and_lists_and_each_object_is_compared_once()
    {
        var added = new LightingClip { Start = S(30) };
        _doc.Record("Change channels", () =>
        {
            _lights.Name = "Spots";
            _lights.Events.Add(added);
            _clip.Lanes = [];                      // the clip is a root, and in the track's list too
            _lane.Points[0].Value = 42;            // inside a shallow root: not seen
        }, ShowDocument.Shallow(_lights), ShowDocument.Shallow(_clip));

        _lane.Points[0].Value = 0;                 // (put back by hand: it wasn't part of the edit)
        AssertReplicaInStep();
        _doc.Undo(_main.Id);
        Assert.Equal(("Lights", 2), (_lights.Name, _lights.Events.Count));
        Assert.Equal([_lane], _clip.Lanes);
        AssertReplicaInStep();
    }

    [Fact]
    public void Nothing_changed_records_nothing_and_a_change_that_throws_is_taken_back()
    {
        Assert.Null(_doc.Record("Nothing", () => { }, _main));
        Assert.Throws<InvalidOperationException>(() => _doc.Record("Fails", () => { _clip.Start = S(9); throw new InvalidOperationException("no"); }, _clip));
        Assert.Equal(S(2), _clip.Start);
        Assert.False(_doc.CanUndo(_main.Id));
        Assert.Throws<EditException>(() => _doc.Record("Not in the show", () => { }, new LightingClip()));
    }

    [Fact]
    public void Recorded_drags_merge_into_one_step()
    {
        for (int i = 1; i <= 20; i++) _doc.Record("Move handle", () => _lane.Points[1].Value = (byte)(255 - i), [_lane], mergeKey: "drag");
        _doc.Undo(_main.Id);
        Assert.Equal((byte)255, _lane.Points[1].Value);
        Assert.False(_doc.CanUndo(_main.Id));
        AssertReplicaInStep();
    }

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static string Json(Project project) => JsonSerializer.Serialize(project.Timelines, Options);

    static Project Copy(Project project) => new()
    {
        Timelines = JsonSerializer.Deserialize<List<ShowTimeline>>(JsonSerializer.Serialize(project.Timelines, Options), Options)!,
    };
}
