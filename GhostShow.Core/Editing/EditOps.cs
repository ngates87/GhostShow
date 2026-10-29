using System.Text.Json.Serialization;

namespace GhostShow.Core.Editing;

/// <summary>
/// One change to the show, as data: what the desktop and web clients send to <see cref="ShowDocument"/>, and what
/// it sends back to every client. Applying one gives the operation that undoes it.
///
/// Paths address objects from the <see cref="Model.Project"/> down, '/'-separated: a property by its camelCase name,
/// an item of a list by its ID if its items have one (<c>Guid Id</c>), otherwise by its index. For example
/// <c>timelines/&lt;id&gt;/tracks/&lt;id&gt;/events/&lt;id&gt;/start</c>, or <c>…/lanes/&lt;id&gt;/points/3/value</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(SetOp), "set")]
[JsonDerivedType(typeof(InsertOp), "insert")]
[JsonDerivedType(typeof(RemoveOp), "remove")]
[JsonDerivedType(typeof(MoveOp), "move")]
public abstract record EditOp(string Path);

/// <summary>Sets a property (the path's last part). A value from the network is a JSON element, read as the property's type.</summary>
public sealed record SetOp(string Path, object? Value) : EditOp(Path);

/// <summary>Inserts an item into the list at the path; an index past the end (or -1) appends.</summary>
public sealed record InsertOp(string Path, int Index, object Item) : EditOp(Path);

/// <summary>Removes the list item at the path.</summary>
public sealed record RemoveOp(string Path) : EditOp(Path);

/// <summary>Moves the list item at the path to another index in its list.</summary>
public sealed record MoveOp(string Path, int Index) : EditOp(Path);

/// <summary>A user's action as one undo step: its operations, applied in order, all or none.</summary>
/// <param name="Description">For the Undo menu ("Move clip"), without the "Undo".</param>
public sealed record Transaction(string Description, IReadOnlyList<EditOp> Ops)
{
    public Transaction(string description, params EditOp[] ops) : this(description, (IReadOnlyList<EditOp>)ops) { }
}

/// <summary>Builds paths: <c>ShowPath.Timeline(id).Track(id).Event(id).Then("start")</c>.</summary>
public readonly record struct ShowPath(string Value)
{
    public static ShowPath Root => new("");
    public static ShowPath Timeline(Guid id) => new($"timelines/{id}");
    public static ShowPath Button(Guid id) => new($"buttons/{id}");

    public ShowPath Track(Guid id) => Then($"tracks/{id}");
    public ShowPath Event(Guid id) => Then($"events/{id}");
    public ShowPath Channel(Guid id) => Then($"channels/{id}");
    public ShowPath Lane(Guid id) => Then($"lanes/{id}");
    public ShowPath Point(int index) => Then($"points/{index}");

    /// <summary>A property or list of the object at this path, by its camelCase name ("start", "points").</summary>
    public ShowPath Then(string part) => new(Value.Length == 0 ? part : $"{Value}/{part}");

    public override string ToString() => Value;

    public static implicit operator string(ShowPath path) => path.Value;
}
