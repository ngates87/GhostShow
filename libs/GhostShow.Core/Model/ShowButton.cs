using System.Text.Json.Serialization;

namespace GhostShow.Core.Model;

/// <summary>
/// A show button (VenueMagic's): on the buttons panel, with a name, a colour and optionally a key; pressing it (a
/// click or the key) runs its actions in order, e.g. stop the ambient, run the scare, nothing else to remember.
/// Buttons belong to the show (show.json). Later, triggers (MIDI, sensors) can press them too.
/// </summary>
public sealed class ShowButton
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Button";

    /// <summary>"#RRGGBB"; null for the panel's default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    /// <summary>The keyboard key that presses it (a key name: "F5", "D1" for 1, "Q"); null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }

    public List<ButtonAction> Actions { get; set; } = [];

    public ShowButton Clone() => new()
    {
        Name = Name, Color = Color, Key = null,   // a copy doesn't take the key
        Actions = Actions.Select(a => a.Clone()).ToList(),
    };
}

/// <summary>Something a show button does when pressed.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunTimelineAction), "runTimeline")]
[JsonDerivedType(typeof(StopTimelineAction), "stopTimeline")]
[JsonDerivedType(typeof(StopAllAction), "stopAll")]
[JsonDerivedType(typeof(BlackoutAction), "blackout")]
public abstract class ButtonAction
{
    public abstract ButtonAction Clone();
}

/// <summary>
/// Plays a timeline from its start (VenueMagic's Run Timeline): a new copy if it's already running, so a scare
/// fired twice plays twice; with <see cref="PlayOnceOnly"/>, nothing while it's still running.
/// </summary>
public sealed class RunTimelineAction : ButtonAction
{
    public Guid TimelineId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PlayOnceOnly { get; set; }

    public override ButtonAction Clone() => new RunTimelineAction { TimelineId = TimelineId, PlayOnceOnly = PlayOnceOnly };
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
