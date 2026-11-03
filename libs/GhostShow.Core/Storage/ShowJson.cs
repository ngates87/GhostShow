using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhostShow.Core.Storage;

/// <summary>
/// The show's JSON format (the same as its files: camelCase, enums as names) for code outside Core, e.g. the web
/// remote sending fixtures and profiles to a laptop, and the laptop reading them back.
/// </summary>
public static class ShowJson
{
    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Json.Options) ?? new JsonObject();

    public static T? FromNode<T>(JsonNode node) => node.Deserialize<T>(Json.Options);
}
