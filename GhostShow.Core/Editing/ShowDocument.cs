using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Editing;

/// <summary>Whether a change was made, undone or redone.</summary>
public enum ChangeKind { Do, Undo, Redo }

/// <summary>
/// A change to the show, as every client hears of it.
/// </summary>
/// <param name="Version">Counts every change since the show was opened; a client that missed one can tell.</param>
/// <param name="Ops">What was applied, in order (for an undo, the operations that undid it), so a copy of the show
/// that applies them stays the same as this one.</param>
/// <param name="Origin">Who made it (a device's name; null: the desktop).</param>
/// <param name="Timelines">The timelines it changed (to recompile and to save); empty for a change elsewhere (buttons).</param>
public sealed record ShowChange(long Version, ChangeKind Kind, string Description, IReadOnlyList<EditOp> Ops, string? Origin,
                                IReadOnlySet<Guid> Timelines);

/// <summary>
/// The open show, and the one way to change it: <see cref="Apply"/> a <see cref="Transaction"/> of edit operations.
/// Every client (the desktop, and later each web device) edits through it, so all of them share one undo history and
/// hear of every change (<see cref="Changed"/>).
///
/// Undo works per timeline, as in the editor: each timeline has its own history, and so does everything outside the
/// timelines (buttons, and the timeline tree: adding, deleting, renaming and moving timelines). A transaction's history
/// is the timeline all its operations are inside, or the show's otherwise.
///
/// Edits with the same merge key in quick succession (typing into a field, dragging) become one undo step. Not thread
/// safe: use it from one thread (the UI's).
/// </summary>
public sealed class ShowDocument
{
    static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(2);
    const int Limit = 500;

    /// <param name="Order">When it last went onto its list (made, merged into, undone or redone), to find the latest across histories.</param>
    sealed record Step(string Description, IReadOnlyList<EditOp> Forward, IReadOnlyList<EditOp> Inverse, object? MergeKey, long Time, long Order = 0);

    long _order;

    sealed class History
    {
        public readonly List<Step> Undo = [];
        public readonly List<Step> Redo = [];
    }

    /// <summary>
    /// The type each applied operation's value was set or inserted as, noted when it's applied, so it can be encoded
    /// later without finding its path again (an object it touched may have moved since, within the same change).
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditOp, Type> ValueTypes = new();

    readonly TimeProvider _clock;
    readonly Dictionary<Guid, History> _histories = [];

    public ShowDocument(Project project, TimeProvider? clock = null)
    {
        Project = project;
        _clock = clock ?? TimeProvider.System;
    }

    public Project Project { get; private set; }

    /// <summary>Counts changes (made, undone, redone) since the show was opened.</summary>
    public long Version { get; private set; }

    /// <summary>Raised after every change, undo and redo.</summary>
    public event Action<ShowChange>? Changed;

    /// <summary>Another show is open: forgets every history (not a change to the show, so <see cref="Changed"/> isn't raised).</summary>
    public void Open(Project project)
    {
        Project = project;
        _histories.Clear();
        Version = 0;
    }

    /// <summary>The history a transaction goes in: the timeline its operations are all in, or the show's (<see cref="Guid.Empty"/>).</summary>
    public static Guid ScopeOf(IEnumerable<EditOp> ops)
    {
        Guid? scope = null;
        foreach (var op in ops)
        {
            var timeline = InsideTimeline(op.Path) ?? Guid.Empty;
            if (scope is null) scope = timeline;
            else if (scope != timeline) return Guid.Empty;
        }
        return scope ?? Guid.Empty;
    }

    /// <summary>Applies a transaction, all of it or (when one of its operations doesn't fit) none of it, and records it.</summary>
    /// <param name="mergeKey">Transactions with equal keys in quick succession are one undo step; null never merges.</param>
    /// <exception cref="EditException">An operation doesn't fit the show; nothing was changed.</exception>
    public ShowChange Apply(Transaction transaction, object? mergeKey = null, string? origin = null)
    {
        var inverse = ApplyAll(transaction.Ops);
        var history = HistoryOf(ScopeOf(transaction.Ops));
        long now = _clock.GetTimestamp();
        if (mergeKey is not null && history.Undo.Count > 0 && Equals(history.Undo[^1].MergeKey, mergeKey)
            && _clock.GetElapsedTime(history.Undo[^1].Time, now) < MergeWindow)
        {
            var first = history.Undo[^1];
            history.Undo[^1] = first with
            {
                Forward = Compact([.. first.Forward, .. transaction.Ops]),
                Inverse = Compact([.. inverse, .. first.Inverse]),   // the first's inverse last: it restores the original values
                Time = now,
                Order = ++_order,
            };
        }
        else
        {
            history.Undo.Add(new Step(transaction.Description, transaction.Ops, inverse, mergeKey, now, ++_order));
            if (history.Undo.Count > Limit) history.Undo.RemoveAt(0);
        }
        history.Redo.Clear();
        return Raise(ChangeKind.Do, transaction.Description, transaction.Ops, origin);
    }

    /// <summary>
    /// Makes an edit by changing the model directly (as editing code that predates the document does, and helpers
    /// such as resizing a clip's envelopes), then records it as a transaction: the given objects (and everything in
    /// them) are compared with how they were, put back, and the differences applied as operations. The objects end up
    /// exactly as the change left them (the same instances), and the edit undoes, merges and reaches other clients
    /// like any other. Null if nothing changed.
    /// </summary>
    /// <param name="roots">What the change touches: objects of the show (a timeline, a clip, a lane), compared with
    /// everything in them; or, wrapped in <see cref="Shallow"/>, only their own properties and which items their lists
    /// hold (adding a track to a timeline, moving a clip). Changes outside them aren't seen. Comparing costs time in
    /// proportion to what's compared, so a fast drag should name only what it changes.</param>
    /// <param name="within">Where the roots are (a timeline), so they're found without searching the whole show.</param>
    public ShowChange? Record(string description, Action change, IEnumerable<object> roots, object? mergeKey = null, string? origin = null, object? within = null)
    {
        var hint = within is null ? null : ShowDiff.PathOf(Project, within);
        var found = new List<(object Root, string Path, bool Deep)>();
        foreach (var given in roots)
        {
            var (root, deep) = given is ShallowRoot s ? (s.Object, false) : (given, true);
            if (found.Any(f => ReferenceEquals(f.Root, root)))
            {
                if (deep) found[found.FindIndex(f => ReferenceEquals(f.Root, root))] = found.Find(f => ReferenceEquals(f.Root, root)) with { Deep = true };
                continue;
            }
            var path = (hint is null ? null : ShowDiff.PathOf(within!, root) is { } inside ? Join(hint, inside) : null) ?? ShowDiff.PathOf(Project, root);
            found.Add((root, path ?? throw new EditException($"Can't edit: the {root.GetType().Name} isn't in the show."), deep));
        }
        // A root inside one compared deeply is already compared as part of it.
        found = found.Where(f => !found.Any(o => o.Deep && o.Path != f.Path && (o.Path.Length == 0 || f.Path.StartsWith(o.Path + "/", StringComparison.Ordinal)))).ToList();
        var diff = new ShowDiff(found.Where(f => f.Deep).Select(f => f.Root), found.Where(f => !f.Deep).Select(f => f.Root));
        try
        {
            change();
        }
        catch
        {
            diff.Revert();
            throw;
        }
        var ops = new List<EditOp>();
        foreach (var (root, path, _) in found) diff.Diff(root, path, ops);
        diff.Revert();
        return ops.Count == 0 ? null : Apply(new Transaction(description, ops), mergeKey, origin);
    }

    public ShowChange? Record(string description, Action change, params object[] roots) => Record(description, change, (IEnumerable<object>)roots);

    /// <summary>For <see cref="Record"/>: compare only this object's own properties and which items its lists hold, not what's inside them.</summary>
    public static object Shallow(object o) => new ShallowRoot(o);

    sealed record ShallowRoot(object Object);

    static string Join(string path, string inside) => path.Length == 0 ? inside : inside.Length == 0 ? path : $"{path}/{inside}";

    /// <summary>
    /// Of these histories, the one whose next undo is the most recent change (e.g. the open timeline's and the show's,
    /// so Undo takes back a timeline rename made after the last clip edit); null if none has anything to undo.
    /// </summary>
    public Guid? LatestUndo(params IEnumerable<Guid> scopes) => Latest(scopes, h => h.Undo);

    /// <summary>Of these histories, the one whose next redo was undone most recently; null if none has anything to redo.</summary>
    public Guid? LatestRedo(params IEnumerable<Guid> scopes) => Latest(scopes, h => h.Redo);

    Guid? Latest(IEnumerable<Guid> scopes, Func<History, List<Step>> steps)
    {
        Guid? best = null;
        long order = -1;
        foreach (var scope in scopes)
            if (_histories.TryGetValue(scope, out var h) && steps(h) is { Count: > 0 } list && list[^1].Order > order)
                (best, order) = (scope, list[^1].Order);
        return best;
    }

    public bool CanUndo(Guid scope) => _histories.TryGetValue(scope, out var h) && h.Undo.Count > 0;
    public bool CanRedo(Guid scope) => _histories.TryGetValue(scope, out var h) && h.Redo.Count > 0;
    public string? UndoDescription(Guid scope) => _histories.TryGetValue(scope, out var h) && h.Undo.Count > 0 ? h.Undo[^1].Description : null;
    public string? RedoDescription(Guid scope) => _histories.TryGetValue(scope, out var h) && h.Redo.Count > 0 ? h.Redo[^1].Description : null;

    /// <summary>Undoes the last step of a history (a timeline's ID, or <see cref="Guid.Empty"/> for the show's).</summary>
    public ShowChange? Undo(Guid scope, string? origin = null)
    {
        if (!_histories.TryGetValue(scope, out var history) || history.Undo.Count == 0) return null;
        var step = history.Undo[^1];
        history.Undo.RemoveAt(history.Undo.Count - 1);
        ApplyAll(step.Inverse);
        history.Redo.Add(step with { MergeKey = null, Order = ++_order });
        return Raise(ChangeKind.Undo, step.Description, step.Inverse, origin);
    }

    public ShowChange? Redo(Guid scope, string? origin = null)
    {
        if (!_histories.TryGetValue(scope, out var history) || history.Redo.Count == 0) return null;
        var step = history.Redo[^1];
        history.Redo.RemoveAt(history.Redo.Count - 1);
        ApplyAll(step.Forward);
        history.Undo.Add(step with { MergeKey = null, Order = ++_order });   // a redone step never merges with what follows
        return Raise(ChangeKind.Redo, step.Description, step.Forward, origin);
    }

    // ---- JSON, for clients elsewhere ----

    /// <summary>
    /// An operation as JSON, its value written as the type it was set as (so an event keeps its "type"). Encode a
    /// change's operations when it's raised: the paths are read against the show as it is then.
    /// </summary>
    public JsonNode Encode(EditOp op)
    {
        var node = new JsonObject { ["op"] = op switch { SetOp => "set", InsertOp => "insert", RemoveOp => "remove", MoveOp => "move", _ => throw new ArgumentOutOfRangeException(nameof(op)) }, ["path"] = op.Path };
        switch (op)
        {
            case SetOp set:
                node["value"] = JsonSerializer.SerializeToNode(set.Value, TypeOf(op) ?? ModelPaths.Property(Project, set.Path).Property.PropertyType, Storage.Json.Options);
                break;
            case InsertOp insert:
                node["index"] = insert.Index;
                node["item"] = JsonSerializer.SerializeToNode(insert.Item, TypeOf(op) ?? ModelPaths.List(Project, insert.Path).ItemType, Storage.Json.Options);
                break;
            case MoveOp move:
                node["index"] = move.Index;
                break;
        }
        return node;
    }

    /// <summary>An operation from JSON (its value stays JSON until it's applied, then it's read as the property's type).</summary>
    public static EditOp Decode(JsonNode json) => json.Deserialize<EditOp>(Storage.Json.Options) ?? throw new EditException("Can't edit: no operation.");

    // ---- applying ----

    static Type? TypeOf(EditOp op) => ValueTypes.TryGetValue(op, out var type) ? type : null;

    static T Typed<T>(T op, Type type) where T : EditOp
    {
        ValueTypes.AddOrUpdate(op, type);
        return op;
    }

    History HistoryOf(Guid scope) => _histories.TryGetValue(scope, out var h) ? h : _histories[scope] = new History();

    ShowChange Raise(ChangeKind kind, string description, IReadOnlyList<EditOp> ops, string? origin)
    {
        var timelines = new HashSet<Guid>();
        foreach (var op in ops)
        {
            if (TimelineOf(op.Path) is { } id) timelines.Add(id);
            else if (op is InsertOp { Item: ShowTimeline added }) timelines.Add(added.Id);
        }
        foreach (var timeline in Project.Timelines)
            if (timelines.Contains(timeline.Id)) timeline.HasUnsavedChanges = true;
        var change = new ShowChange(++Version, kind, description, ops, origin, timelines);
        Changed?.Invoke(change);
        return change;
    }

    /// <summary>Applies operations in order; if one fails, undoes the ones before it and throws.</summary>
    List<EditOp> ApplyAll(IReadOnlyList<EditOp> ops)
    {
        var inverse = new List<EditOp>(ops.Count);
        try
        {
            foreach (var op in ops) inverse.Add(ApplyOne(op));
        }
        catch (Exception e) when (e is EditException or JsonException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            for (int i = inverse.Count - 1; i >= 0; i--) ApplyOne(inverse[i]);
            throw e as EditException ?? new EditException($"Can't edit: {e.Message}");
        }
        inverse.Reverse();
        return inverse;
    }

    EditOp ApplyOne(EditOp op)
    {
        switch (op)
        {
            case SetOp set:
            {
                var (owner, property) = ModelPaths.Property(Project, set.Path);
                if (property.SetMethod is not { IsPublic: true }) throw new EditException($"Can't edit {set.Path}: it can't be set.");
                var value = ModelPaths.Convert(set.Value, property.PropertyType, set.Path);
                var old = property.GetValue(owner);
                property.SetValue(owner, value);
                Typed(set, property.PropertyType);
                return Typed(new SetOp(set.Path, old), property.PropertyType);
            }
            case InsertOp insert:
            {
                var (list, type) = ModelPaths.List(Project, insert.Path);
                var item = ModelPaths.Convert(insert.Item, type, insert.Path) ?? throw new EditException($"Can't edit {insert.Path}: nothing to insert.");
                if (ModelPaths.IdOf(item) is { } id && list.Cast<object>().Any(i => ModelPaths.IdOf(i) == id))
                    throw new EditException($"Can't edit {insert.Path}: it already has {id}.");
                int index = insert.Index < 0 || insert.Index > list.Count ? list.Count : insert.Index;
                list.Insert(index, item);
                Typed(insert, type);
                return new RemoveOp($"{insert.Path}/{ModelPaths.KeyOf(item, index)}");
            }
            case RemoveOp remove:
            {
                var (list, index, listPath) = ModelPaths.Item(Project, remove.Path);
                var item = list[index]!;
                list.RemoveAt(index);
                return Typed(new InsertOp(listPath, index, item), ModelPaths.ItemTypeOf(list.GetType()));
            }
            case MoveOp move:
            {
                var (list, from, listPath) = ModelPaths.Item(Project, move.Path);
                int to = Math.Clamp(move.Index, 0, list.Count - 1);
                var item = list[from]!;
                list.RemoveAt(from);
                list.Insert(to, item);
                return new MoveOp($"{listPath}/{ModelPaths.KeyOf(item, to)}", from);
            }
            default:
                throw new EditException($"Can't edit {op.Path}: unknown operation.");
        }
    }

    /// <summary>
    /// A merged step's operations: where they're all property sets (a drag, typing), only the last set of each
    /// property is kept, so a long drag stays one small step.
    /// </summary>
    static List<EditOp> Compact(List<EditOp> ops)
    {
        if (!ops.All(o => o is SetOp)) return ops;
        var last = new Dictionary<string, int>();
        for (int i = 0; i < ops.Count; i++) last[ops[i].Path] = i;
        return ops.Where((o, i) => last[o.Path] == i).ToList();
    }

    /// <summary>
    /// The timeline a path edits the inside of, for undo: not the timeline itself (adding or deleting it) or its place
    /// in the tree (its name and folder), which are the show's.
    /// </summary>
    static Guid? InsideTimeline(string path)
    {
        if (TimelineOf(path) is not { } id) return null;
        var rest = path.AsSpan(Math.Min(path.Length, "timelines/".Length + 36));
        return rest is "" or "/name" or "/folder" ? null : id;
    }

    /// <summary>The timeline a path is in ("timelines/&lt;id&gt;/…"), if it's in one.</summary>
    static Guid? TimelineOf(string path)
    {
        const string prefix = "timelines/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int end = path.IndexOf('/', prefix.Length);
        var id = end < 0 ? path[prefix.Length..] : path[prefix.Length..end];
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
