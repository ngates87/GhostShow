using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShow.Core.Editing;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Editing;

public class ShowDocumentTests
{
    sealed class ManualClock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(double seconds) => Ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    readonly ManualClock _clock = new();
    readonly Project _project;
    readonly ShowTimeline _main;
    readonly TimelineTrack _lights;
    readonly LightingClip _clip;
    readonly ChannelLane _lane;
    readonly ShowDocument _doc;
    readonly List<ShowChange> _changes = [];

    public ShowDocumentTests()
    {
        _lane = new ChannelLane { Target = new LaneTarget(Channel: 1), Points = [new CurvePoint { Time = S(0), Value = 0 }, new CurvePoint { Time = S(4), Value = 255 }] };
        _clip = new LightingClip { Start = S(2), Length = S(4), Lanes = [_lane] };
        _lights = new TimelineTrack { Name = "Lights", Kind = TrackKind.Lighting, Events = [_clip], Channels = [new ChannelLane { Target = new LaneTarget(Channel: 1) }] };
        _main = new ShowTimeline { Name = "Main", Tracks = [_lights, new TimelineTrack { Name = "Sound", Kind = TrackKind.Audio }], HasUnsavedChanges = false };
        _project = new Project { Timelines = [_main], Buttons = [new ShowButton { Name = "Scare" }] };
        _doc = new ShowDocument(_project, _clock);
        _doc.Changed += _changes.Add;
    }

    ShowPath Clip => ShowPath.Timeline(_main.Id).Track(_lights.Id).Event(_clip.Id);
    ShowPath Track => ShowPath.Timeline(_main.Id).Track(_lights.Id);

    [Fact]
    public void A_set_changes_the_property_marks_the_timeline_unsaved_and_undoes_and_redoes()
    {
        var change = _doc.Apply(new Transaction("Move clip", new SetOp(Clip.Then("start"), S(5))));

        Assert.Equal(S(5), _clip.Start);
        Assert.True(_main.HasUnsavedChanges);
        Assert.Equal((1L, ChangeKind.Do, "Move clip"), (change.Version, change.Kind, change.Description));
        Assert.Equal([_main.Id], change.Timelines);
        Assert.Equal("Move clip", _doc.UndoDescription(_main.Id));

        var undo = _doc.Undo(_main.Id)!;
        Assert.Equal(S(2), _clip.Start);
        Assert.Equal((2L, ChangeKind.Undo), (undo.Version, undo.Kind));
        Assert.Equal([new SetOp(Clip.Then("start"), S(2))], undo.Ops);
        Assert.Equal("Move clip", _doc.RedoDescription(_main.Id));

        _doc.Redo(_main.Id);
        Assert.Equal(S(5), _clip.Start);
        Assert.Equal(3, _changes.Count);
    }

    [Fact]
    public void Insert_remove_and_move_change_lists_and_undo_restores_the_same_objects_in_place()
    {
        var added = new LightingClip { Start = S(10) };
        _doc.Apply(new Transaction("Add clip", new InsertOp(Track.Then("events"), -1, added)));
        Assert.Same(added, _lights.Events[^1]);

        _doc.Apply(new Transaction("Delete clip", new RemoveOp(Clip)));
        Assert.Equal([added], _lights.Events);
        _doc.Undo(_main.Id);
        Assert.Equal([_clip, added], _lights.Events);   // the same clip, back where it was

        var sound = _main.Tracks[1];
        _doc.Apply(new Transaction("Move track", new MoveOp(ShowPath.Timeline(_main.Id).Track(sound.Id), 0)));
        Assert.Equal([sound, _lights], _main.Tracks);
        _doc.Undo(_main.Id);
        Assert.Equal([_lights, sound], _main.Tracks);
    }

    [Fact]
    public void Items_without_an_ID_are_addressed_by_index()
    {
        var points = Clip.Lane(_lane.Id).Then("points");
        _doc.Apply(new Transaction("Add handle", new InsertOp(points, 1, new CurvePoint { Time = S(2), Value = 100 }), new SetOp(points.Then("2/value"), (byte)200)));

        Assert.Equal([(0, 0), (2, 100), (4, 200)], _lane.Points.Select(p => ((int)p.Time.TotalSeconds, (int)p.Value)));
        _doc.Undo(_main.Id);
        Assert.Equal([(0, 0), (4, 255)], _lane.Points.Select(p => ((int)p.Time.TotalSeconds, (int)p.Value)));
    }

    [Fact]
    public void A_transaction_that_doesnt_fit_changes_nothing()
    {
        var bad = new Transaction("Move two clips", new SetOp(Clip.Then("start"), S(9)), new SetOp(Track.Event(Guid.NewGuid()).Then("start"), S(1)));

        var error = Assert.Throws<EditException>(() => _doc.Apply(bad));

        Assert.StartsWith("Can't edit timelines/", error.Message);
        Assert.Equal(S(2), _clip.Start);   // the first set was taken back
        Assert.Empty(_changes);
        Assert.False(_doc.CanUndo(_main.Id));
        Assert.Throws<EditException>(() => _doc.Apply(new Transaction("Wrong type", new SetOp(Clip.Then("start"), "soon"))));
        Assert.Throws<EditException>(() => _doc.Apply(new Transaction("Read-only", new SetOp(Clip.Then("kindName"), "x"))));
        Assert.Throws<EditException>(() => _doc.Apply(new Transaction("Twice", new InsertOp(Track.Then("events"), 0, _clip))));
    }

    [Fact]
    public void Edits_with_one_merge_key_in_quick_succession_are_one_undo_step()
    {
        for (int i = 1; i <= 50; i++)
        {
            _doc.Apply(new Transaction("Move clip", new SetOp(Clip.Then("start"), S(2 + i * 0.1)), new SetOp(Clip.Then("length"), S(4 + i * 0.1))), mergeKey: "drag");
            _clock.Advance(0.016);
        }
        _clock.Advance(3);
        _doc.Apply(new Transaction("Move clip", new SetOp(Clip.Then("start"), S(20))), mergeKey: "drag");   // too late to merge

        _doc.Undo(_main.Id);
        Assert.Equal(S(7), _clip.Start);
        _doc.Undo(_main.Id);
        Assert.Equal((S(2), S(4)), (_clip.Start, _clip.Length));   // the whole drag at once
        Assert.False(_doc.CanUndo(_main.Id));

        _doc.Redo(_main.Id);
        Assert.Equal((S(7), S(9)), (_clip.Start, _clip.Length));
    }

    [Fact]
    public void A_new_edit_clears_redo_and_a_redone_edit_doesnt_merge_with_the_next()
    {
        _doc.Apply(new Transaction("one", new SetOp(Clip.Then("start"), S(1))));
        _doc.Apply(new Transaction("two", new SetOp(Clip.Then("start"), S(2.5))), mergeKey: "k");
        _doc.Undo(_main.Id);
        _doc.Apply(new Transaction("three", new SetOp(Clip.Then("start"), S(3))));
        Assert.False(_doc.CanRedo(_main.Id));

        _doc.Undo(_main.Id);
        _doc.Redo(_main.Id);
        _doc.Apply(new Transaction("four", new SetOp(Clip.Then("start"), S(4))), mergeKey: "k");
        _doc.Undo(_main.Id);
        Assert.Equal(S(3), _clip.Start);
    }

    [Fact]
    public void A_history_keeps_at_most_500_steps_dropping_the_oldest()
    {
        for (int i = 1; i <= 600; i++) _doc.Apply(new Transaction($"{i}", new SetOp(Clip.Then("start"), S(i))));
        int undone = 0;
        while (_doc.Undo(_main.Id) is not null) undone++;
        Assert.Equal(500, undone);
        Assert.Equal(S(100), _clip.Start);
    }

    [Fact]
    public void Opening_another_show_forgets_the_histories_without_a_change()
    {
        _doc.Apply(new Transaction("Move clip", new SetOp(Clip.Then("start"), S(5))));
        var other = new Project();
        _doc.Open(other);
        Assert.Same(other, _doc.Project);
        Assert.False(_doc.CanUndo(_main.Id));
        Assert.Equal(0, _doc.Version);
        Assert.Single(_changes);
    }

    [Fact]
    public void Each_timeline_has_its_own_history_and_the_show_has_one_for_the_rest()
    {
        var other = new ShowTimeline { Name = "Scare" };
        _doc.Apply(new Transaction("New timeline", new InsertOp("timelines", -1, other)));
        _doc.Apply(new Transaction("Move clip", new SetOp(Clip.Then("start"), S(3))));
        _doc.Apply(new Transaction("Loop", new SetOp(ShowPath.Timeline(other.Id).Then("loop"), true)));
        _doc.Apply(new Transaction("Rename timeline", new SetOp(ShowPath.Timeline(other.Id).Then("name"), "Big scare")));
        _doc.Apply(new Transaction("Rename button", new SetOp(ShowPath.Button(_project.Buttons[0].Id).Then("name"), "Boo")));

        Assert.Equal("Move clip", _doc.UndoDescription(_main.Id));
        Assert.Equal("Loop", _doc.UndoDescription(other.Id));
        Assert.Equal("Rename button", _doc.UndoDescription(Guid.Empty));

        _doc.Undo(Guid.Empty);
        _doc.Undo(Guid.Empty);
        Assert.Equal(("Scare", "Scare"), (other.Name, _project.Buttons[0].Name));
        Assert.Equal(S(3), _clip.Start);   // other histories untouched
        _doc.Undo(Guid.Empty);
        Assert.DoesNotContain(other, _project.Timelines);
    }

    [Fact]
    public void A_copy_of_the_show_that_applies_the_changes_as_JSON_stays_the_same()
    {
        var replica = Copy(_project);
        var remote = new ShowDocument(replica);
        _doc.Changed += change =>
        {
            foreach (var op in change.Ops)
            {
                var json = _doc.Encode(op).ToJsonString();
                remote.Apply(new Transaction(change.Description, ShowDocument.Decode(JsonNode.Parse(json)!)));
            }
        };

        var effect = new LightingClip
        {
            Start = S(8), Length = S(3),
            Effect = new LightingEffect { Kind = EffectKind.Lightning, Strength = 0.7 },
            Lanes = [new ChannelLane { Target = new LaneTarget(Channel: 2), Points = [new CurvePoint { Time = S(1), Value = 9, Curve = CurveKind.Step, Group = Guid.NewGuid() }] }],
        };
        _doc.Apply(new Transaction("Add effect", new InsertOp(Track.Then("events"), -1, effect)));
        _doc.Apply(new Transaction("Add event", new InsertOp(ShowPath.Timeline(_main.Id).Track(_main.Tracks[1].Id).Then("events"), 0,
            new TimelineCommandEvent { Start = S(1), TimelineId = Guid.NewGuid(), Symbol = EventSymbol.Skull })));
        _doc.Apply(new Transaction("Playback", new SetOp(ShowPath.Timeline(_main.Id).Then("playback"), new TimelinePlayback(Priority: 10, ReleaseFade: S(1.5)))));
        _doc.Apply(new Transaction("Length", new SetOp(ShowPath.Timeline(_main.Id).Then("length"), (TimeSpan?)null)));
        _doc.Apply(new Transaction("Strength", new SetOp(Track.Event(effect.Id).Then("effect/strength"), 0.25)));
        _doc.Apply(new Transaction("Handle", new SetOp(Clip.Lane(_lane.Id).Point(1).Then("value"), (byte)128)));
        _doc.Apply(new Transaction("Channel start", new SetOp(Track.Channel(_lights.Channels[0].Id).Then("start"), ChannelStart.LeaveAlone)));
        _doc.Apply(new Transaction("Move track", new MoveOp(ShowPath.Timeline(_main.Id).Track(_lights.Id), 1)));
        _doc.Apply(new Transaction("Delete clip", new RemoveOp(Clip)));
        _doc.Undo(_main.Id);
        _doc.Undo(_main.Id);

        Assert.Equal(Json(_project), Json(replica));
        Assert.IsType<LightingClip>(replica.Timelines[0].Tracks[0].Events[^1]);
        Assert.Equal(0.25, ((LightingClip)replica.Timelines[0].Tracks[0].Events[^1]).Effect!.Strength);
    }

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static string Json(Project project) => JsonSerializer.Serialize(new { project.Buttons, project.Timelines }, Options);

    static Project Copy(Project project) => new()
    {
        Buttons = JsonSerializer.Deserialize<List<ShowButton>>(JsonSerializer.Serialize(project.Buttons, Options), Options)!,
        Timelines = JsonSerializer.Deserialize<List<ShowTimeline>>(JsonSerializer.Serialize(project.Timelines, Options), Options)!,
    };
}
