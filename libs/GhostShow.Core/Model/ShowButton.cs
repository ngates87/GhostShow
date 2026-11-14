using System.Text.Json.Serialization;
using GhostShow.Core.Inputs;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Model;

/// <summary>
/// A show button (VenueMagic's): on the buttons panel, with a name, a colour and any number of triggers; pressing it
/// (a click or a trigger) runs its actions in order, e.g. stop the ambient, run the scare, nothing else to remember.
/// Buttons belong to the show (show.json).
/// </summary>
public sealed class ShowButton
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Button";

    /// <summary>"#RRGGBB"; null for the panel's default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    /// <summary>What presses it, apart from a click. A trigger does exactly what clicking the button does.</summary>
    public List<InputTrigger> Triggers { get; set; } = [];

    public List<ButtonAction> Actions { get; set; } = [];

    public ShowButton Clone() => new()
    {
        Name = Name, Color = Color,   // a copy doesn't take the triggers: one input presses one button
        Actions = Actions.Select(a => a.Clone()).ToList(),
    };
}

/// <summary>Something a show button does when pressed.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunTimelineAction), "runTimeline")]
[JsonDerivedType(typeof(StopTimelineAction), "stopTimeline")]
[JsonDerivedType(typeof(EndLoopAction), "endLoop")]
[JsonDerivedType(typeof(StopAllAction), "stopAll")]
[JsonDerivedType(typeof(BlackoutAction), "blackout")]
public abstract class ButtonAction
{
    public abstract ButtonAction Clone();
}

/// <summary>
/// Plays a timeline from its start (VenueMagic's Run Timeline): a new copy if it's already running, so a scare
/// fired twice plays twice, unless <see cref="OneOnly"/>.
/// </summary>
public sealed class RunTimelineAction : ButtonAction
{
    public Guid TimelineId { get; set; }

    /// <summary>It plays in a loop until it's stopped (or its loop is ended). Otherwise once.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Loop { get; set; }

    /// <summary>Nothing happens if the timeline is already playing, so a scare can't stack.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OneOnly { get; set; }

    [JsonIgnore]
    public RunOptions Options => new(Loop, OneOnly);

    public override ButtonAction Clone() => new RunTimelineAction { TimelineId = TimelineId, Loop = Loop, OneOnly = OneOnly };
}

/// <summary>Every copy of a timeline playing in a loop finishes the pass it's on, then stops (VenueMagic's End loop).</summary>
public sealed class EndLoopAction : ButtonAction
{
    public Guid TimelineId { get; set; }

    public override ButtonAction Clone() => new EndLoopAction { TimelineId = TimelineId };
}

/// <summary>Stops a timeline, every copy of it playing (each releasing its lights with its release fade).</summary>
public sealed class StopTimelineAction : ButtonAction
{
    public Guid TimelineId { get; set; }

    public override ButtonAction Clone() => new StopTimelineAction { TimelineId = TimelineId };
}

/// <summary>Stops every timeline playing.</summary>
public sealed class StopAllAction : ButtonAction
{
    public override ButtonAction Clone() => new StopAllAction();
}

/// <summary>The toolbar's Blackout: stops every timeline and sets every manual level to 0.</summary>
public sealed class BlackoutAction : ButtonAction
{
    public override ButtonAction Clone() => new BlackoutAction();
}
