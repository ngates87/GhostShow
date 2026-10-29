using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace GhostShow.Core.Editing;

/// <summary>Finds what an <see cref="EditOp"/> path points at in the show model (by reflection, cached per type).</summary>
static class ModelPaths
{
    static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Properties = new();
    static readonly ConcurrentDictionary<Type, PropertyInfo?> Ids = new();
    static readonly ConcurrentDictionary<Type, Type> ElementTypes = new();

    /// <summary>The object a property path's parent part points at, and the property.</summary>
    public static (object Owner, PropertyInfo Property) Property(object root, string path)
    {
        var (parent, name) = Split(path);
        var owner = Walk(root, parent, path);
        var property = PropertyOf(owner.GetType(), name) ?? throw Missing(path, $"{owner.GetType().Name} has no property \"{name}\"");
        return (owner, property);
    }

    /// <summary>The list at a path, and its item type.</summary>
    public static (IList List, Type ItemType) List(object root, string path)
    {
        var target = Walk(root, path, path);
        if (target is not IList list) throw Missing(path, "it isn't a list");
        return (list, ItemTypeOf(list.GetType()));
    }

    /// <summary>A list item's list, index and the list's path.</summary>
    public static (IList List, int Index, string ListPath) Item(object root, string path)
    {
        var (parent, key) = Split(path);
        var (list, _) = List(root, parent);
        int index = IndexOf(list, key);
        if (index < 0) throw Missing(path, "there's no such item");
        return (list, index, parent);
    }

    /// <summary>How a path names a list item: its ID if its type has one, otherwise its index.</summary>
    public static string KeyOf(object item, int index) => IdOf(item) is { } id ? id.ToString() : index.ToString();

    public static Guid? IdOf(object? item) =>
        item is not null && Ids.GetOrAdd(item.GetType(), t => t.GetProperty("Id") is { PropertyType: var p } property && p == typeof(Guid) ? property : null) is { } id
            ? (Guid)id.GetValue(item)! : null;

    /// <summary>Makes a value the type a property or list takes: a JSON element (from the network) is read as it.</summary>
    public static object? Convert(object? value, Type type, string path)
    {
        if (value is JsonElement json) return JsonSerializer.Deserialize(json, type, Storage.Json.Options);
        if (value is null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null) throw Missing(path, $"null isn't a {type.Name}");
            return null;
        }
        if (type.IsInstanceOfType(value)) return value;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (value is IConvertible && target.IsPrimitive) return System.Convert.ChangeType(value, target);
        throw Missing(path, $"a {value.GetType().Name} isn't a {type.Name}");
    }

    public static Type ItemTypeOf(Type listType) => ElementTypes.GetOrAdd(listType, t =>
        t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))?.GetGenericArguments()[0] ?? typeof(object));

    static object Walk(object root, string path, string whole)
    {
        object current = root;
        if (path.Length == 0) return current;
        foreach (var part in path.Split('/'))
        {
            if (current is IList list)
            {
                int index = IndexOf(list, part);
                if (index < 0) throw Missing(whole, $"there's no \"{part}\"");
                current = list[index]!;
            }
            else
            {
                var property = PropertyOf(current.GetType(), part) ?? throw Missing(whole, $"{current.GetType().Name} has no \"{part}\"");
                current = property.GetValue(current) ?? throw Missing(whole, $"\"{part}\" is empty");
            }
        }
        return current;
    }

    static int IndexOf(IList list, string key)
    {
        if (Guid.TryParse(key, out var id))
        {
            for (int i = 0; i < list.Count; i++)
                if (IdOf(list[i]) == id) return i;
            return -1;
        }
        return int.TryParse(key, out int index) && index >= 0 && index < list.Count ? index : -1;
    }

    static (string Parent, string Last) Split(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
    }

    /// <summary>A public property by its camelCase name (as in the show's JSON; also ones the files don't store, like a timeline's folder).</summary>
    static PropertyInfo? PropertyOf(Type type, string name) => Properties.GetOrAdd((type, name), key =>
        key.Item1.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && JsonNamingPolicy.CamelCase.ConvertName(p.Name) == key.Item2));

    static InvalidOperationException Missing(string path, string why) => new EditException($"Can't edit {path}: {why}.");
}

/// <summary>An edit that doesn't fit the show (its object was deleted, a wrong type): nothing of it was applied.</summary>
public sealed class EditException(string message) : InvalidOperationException(message);
