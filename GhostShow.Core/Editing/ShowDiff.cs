using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace GhostShow.Core.Editing;

/// <summary>
/// Records how part of the show model was, so that after it's been changed in place the change can be described as
/// <see cref="EditOp"/>s (<see cref="Diff"/>) and the part put back as it was (<see cref="Revert"/>). How
/// <see cref="ShowDocument.Record"/> turns an edit made directly on the model into a transaction.
///
/// What it compares, per object, by its public settable properties:
/// - values (numbers, strings, enums, times, IDs, and records such as a lane's target) by equality;
/// - model objects (a clip's effect) by reference: the same object is compared inside; another one is a set;
/// - lists by their items: inserts, removes and moves for items with an ID (events, tracks, lanes), or the whole list
///   for items without one (handles), then each item that was there before is compared inside;
/// - sets of values (the timeline tree's folders) by their contents, as a whole.
/// </summary>
sealed class ShowDiff
{
    enum Kind { Value, Object, List, Set }

    /// <summary>A property, with compiled accessors (reflection's are too slow for a timeline of tens of thousands of handles).</summary>
    sealed record Prop(PropertyInfo Info, string Name, Kind Kind, Func<object, object?> Get, Action<object, object?> Set);

    static readonly ConcurrentDictionary<Type, Prop[]> PropsByType = new();
    static readonly ConcurrentDictionary<Type, bool> ModelTypes = new();

    /// <summary>Types with model objects or lists inside (a handle has none, so searches don't look into it).</summary>
    static readonly ConcurrentDictionary<Type, bool> Containers = new();

    /// <summary>Not part of the show (bookkeeping the model keeps on itself).</summary>
    static readonly HashSet<string> Skipped = ["HasUnsavedChanges"];

    readonly Dictionary<object, object?[]> _values = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<object, object?[]> _lists = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<object> _compared = new(ReferenceEqualityComparer.Instance);   // each object once, however it's reached

    /// <summary>Records these objects: each with everything in it, or (shallow) only its own properties and which items its lists hold.</summary>
    public ShowDiff(IEnumerable<object> roots, IEnumerable<object>? shallow = null)
    {
        foreach (var root in roots) Take(root, deep: true);
        foreach (var root in shallow ?? []) Take(root, deep: false);
    }

    void Take(object o, bool deep = true)
    {
        if (_values.ContainsKey(o)) return;
        var props = PropsOf(o.GetType());
        var values = new object?[props.Length];
        _values[o] = values;
        for (int i = 0; i < props.Length; i++)
        {
            var value = values[i] = props[i].Get(o);
            if (value is null) continue;
            if (props[i].Kind == Kind.Set)
            {
                values[i] = ((IEnumerable)value).Cast<object>().ToArray();   // its contents: the set itself changes in place
                continue;
            }
            if (props[i].Kind == Kind.Object)
            {
                if (deep) Take(value);
            }
            else if (props[i].Kind == Kind.List)
            {
                var list = (IList)value;
                var items = new object?[list.Count];
                list.CopyTo(items, 0);
                _lists[list] = items;
                if (!deep) continue;
                foreach (var item in items)
                    if (item is not null && IsModelObject(item.GetType())) Take(item);
            }
        }
    }

    /// <summary>The changes since this was taken, to the root at the path (the root object itself is kept).</summary>
    public void Diff(object root, string path, List<EditOp> ops) => DiffObject(root, new PathTo(null, path, -1), ops);

    /// <summary>A path, built into a string only for what changed (most objects compared haven't).</summary>
    sealed class PathTo(PathTo? parent, string? name, int index)
    {
        string? _value;

        public string Value => _value ??= parent is null ? name! : Join(parent.Value, name ?? index.ToString());

        public PathTo Then(string name) => new(this, name, -1);
        public PathTo At(int index) => new(this, null, index);
    }

    void DiffObject(object o, PathTo path, List<EditOp> ops)
    {
        if (!_values.TryGetValue(o, out var before) || !_compared.Add(o)) return;   // new (it's in the op that added it), or done
        var props = PropsOf(o.GetType());
        for (int i = 0; i < props.Length; i++)
        {
            var prop = props[i];
            var now = prop.Get(o);
            switch (prop.Kind)
            {
                case Kind.Value:
                    if (!Equals(now, before[i])) ops.Add(new SetOp(path.Then(prop.Name).Value, now));
                    break;
                case Kind.Set:
                    var items = ((IEnumerable?)now)?.Cast<object>().ToArray() ?? [];
                    if (!items.SequenceEqual((object[])before[i]!))
                        ops.Add(new SetOp(path.Then(prop.Name).Value, Activator.CreateInstance(prop.Info.PropertyType, now)));
                    break;
                case Kind.Object:
                    if (!ReferenceEquals(now, before[i])) ops.Add(new SetOp(path.Then(prop.Name).Value, now));
                    else if (now is not null) DiffObject(now, path.Then(prop.Name), ops);
                    break;
                case Kind.List:
                    if (now is null || before[i] is null)
                    {
                        if (!ReferenceEquals(now, before[i])) ops.Add(new SetOp(path.Then(prop.Name).Value, now));
                        break;
                    }
                    DiffList((IList)before[i]!, (IList)now, prop.Info.PropertyType, path.Then(prop.Name), ops);
                    break;
            }
        }
    }

    void DiffList(IList beforeList, IList now, Type listType, PathTo path, List<EditOp> ops)
    {
        var before = _lists.TryGetValue(beforeList, out var items) ? items : [];
        var after = new object?[now.Count];
        now.CopyTo(after, 0);
        var sample = after.Length > 0 ? after[0] : before.Length > 0 ? before[0] : null;
        if (sample is null || ModelPaths.IdOf(sample) is null)
        {
            if (!ReferenceEquals(beforeList, now) || before.Length != after.Length || !Same(now, before))
            {
                var copy = (IList)Activator.CreateInstance(listType.IsInterface ? typeof(List<>).MakeGenericType(ModelPaths.ItemTypeOf(listType)) : listType)!;
                foreach (var item in after) copy.Add(item);
                ops.Add(new SetOp(path.Value, copy));
            }
            for (int i = 0; i < after.Length; i++)
                if (after[i] is { } item && _values.ContainsKey(item)) DiffObject(item, path.At(i), ops);
            return;
        }

        // Turn the old list into the new one: remove what went, then insert or move each item into place.
        if (!ReferenceEquals(beforeList, now) || before.Length != after.Length || !Same(now, before))
        {
            var afterSet = new HashSet<object>(after.OfType<object>(), ReferenceEqualityComparer.Instance);
            var sim = new List<object>();
            foreach (var item in before.OfType<object>())
            {
                if (afterSet.Contains(item)) sim.Add(item);
                else ops.Add(new RemoveOp($"{path.Value}/{ModelPaths.IdOf(item)}"));
            }
            for (int i = 0; i < after.Length; i++)
            {
                var item = after[i]!;
                if (i < sim.Count && ReferenceEquals(sim[i], item)) continue;
                int from = sim.FindIndex(s => ReferenceEquals(s, item));
                if (from >= 0)
                {
                    ops.Add(new MoveOp($"{path.Value}/{ModelPaths.IdOf(item)}", i));
                    sim.RemoveAt(from);
                }
                else ops.Add(new InsertOp(path.Value, i, item));
                sim.Insert(i, item);
            }
        }
        foreach (var item in after)
            if (item is not null && _values.ContainsKey(item)) DiffObject(item, new PathTo(path, ModelPaths.IdOf(item).ToString(), -1), ops);
    }

    /// <summary>Puts every recorded object and list back as it was (only what changed is touched).</summary>
    public void Revert()
    {
        foreach (var (list, items) in _lists)
        {
            var l = (IList)list;
            if (l.Count == items.Length && Same(l, items)) continue;
            l.Clear();
            foreach (var item in items) l.Add(item);
        }
        foreach (var (o, values) in _values)
        {
            var props = PropsOf(o.GetType());
            for (int i = 0; i < props.Length; i++)
            {
                var now = props[i].Get(o);
                if (props[i].Kind == Kind.Set)
                {
                    var items = (object[])values[i]!;
                    if (now is SortedSet<string> live && !live.Cast<object>().SequenceEqual(items))
                    {
                        live.Clear();
                        foreach (string item in items) live.Add(item);
                    }
                    continue;
                }
                if (!ReferenceEquals(now, values[i]) && !Equals(now, values[i])) props[i].Set(o, values[i]);
            }
        }
    }

    static bool Same(IList list, object?[] items)
    {
        for (int i = 0; i < items.Length; i++)
            if (!ReferenceEquals(list[i], items[i])) return false;
        return true;
    }

    static string Join(string path, string name) => path.Length == 0 ? name : $"{path}/{name}";

    static Prop[] PropsOf(Type type) => PropsByType.GetOrAdd(type, t => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true } && !Skipped.Contains(p.Name))
        .Select(p => (Prop: p, Kind: KindOf(p.PropertyType)))
        .Where(p => p.Kind is not null)
        .Select(p => new Prop(p.Prop, JsonNamingPolicy.CamelCase.ConvertName(p.Prop.Name), p.Kind!.Value, Getter(p.Prop), Setter(p.Prop)))
        .ToArray());

    static Func<object, object?> Getter(PropertyInfo property)
    {
        var o = Expression.Parameter(typeof(object));
        var get = Expression.Property(Expression.Convert(o, property.DeclaringType!), property);
        return Expression.Lambda<Func<object, object?>>(Expression.Convert(get, typeof(object)), o).Compile();
    }

    static Action<object, object?> Setter(PropertyInfo property)
    {
        var (o, value) = (Expression.Parameter(typeof(object)), Expression.Parameter(typeof(object)));
        var set = Expression.Assign(Expression.Property(Expression.Convert(o, property.DeclaringType!), property), Expression.Convert(value, property.PropertyType));
        return Expression.Lambda<Action<object, object?>>(set, o, value).Compile();
    }

    /// <summary>How a property is compared; null for one that isn't (sets other than lists, which edits don't use).</summary>
    static Kind? KindOf(Type type)
    {
        if (typeof(IList).IsAssignableFrom(type)) return Kind.List;
        if (type == typeof(SortedSet<string>)) return Kind.Set;
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type)) return null;
        return IsModelObject(type) ? Kind.Object : Kind.Value;
    }

    /// <summary>A mutable class of the show model (compared inside), as opposed to a value or a record (compared whole).</summary>
    static bool IsModelObject(Type type) => ModelTypes.GetOrAdd(type, t =>
        t.IsClass && t != typeof(string) && t.GetMethod("<Clone>$") is null && t.Namespace?.StartsWith("GhostShow", StringComparison.Ordinal) == true);

    static bool IsContainer(Type type) => Containers.GetOrAdd(type, t => PropsOf(t).Any(p => p.Kind is Kind.Object or Kind.List));

    /// <summary>
    /// The path of an object of the show (by reference); null if it isn't in the show. Searches level by level, so a
    /// clip is found without looking through every handle of the clips before it.
    /// </summary>
    public static string? PathOf(object project, object target)
    {
        if (ReferenceEquals(project, target)) return "";
        // A timeline, directly (the usual root), rather than searching every fixture and button first.
        if (project is Model.Project { Timelines: var timelines } && target is Timeline.ShowTimeline timeline)
            return timelines.Contains(timeline) ? $"timelines/{timeline.Id}" : null;
        var queue = new Queue<(object Object, PathTo Path)>();
        queue.Enqueue((project, new PathTo(null, "", -1)));
        while (queue.TryDequeue(out var next))
        {
            var (o, path) = next;
            foreach (var prop in PropsOf(o.GetType()))
            {
                if (prop.Kind is Kind.Value or Kind.Set || prop.Get(o) is not { } value) continue;
                if (ReferenceEquals(value, target)) return path.Then(prop.Name).Value;
                if (prop.Kind == Kind.Object)
                {
                    if (IsContainer(value.GetType())) queue.Enqueue((value, path.Then(prop.Name)));
                    continue;
                }
                var list = (IList)value;
                var listPath = path.Then(prop.Name);
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (ReferenceEquals(item, target)) return Join(listPath.Value, ModelPaths.KeyOf(item!, i));
                    if (item is not null && IsModelObject(item.GetType()) && IsContainer(item.GetType()))
                        queue.Enqueue((item, new PathTo(listPath, ModelPaths.KeyOf(item, i), -1)));
                }
            }
        }
        return null;
    }
}
