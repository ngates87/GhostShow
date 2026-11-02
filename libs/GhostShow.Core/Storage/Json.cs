using System.Text.Json;

namespace GhostShow.Core.Storage;

/// <summary>One JSON style for every file GhostShow writes: indented camelCase, read case-insensitively.</summary>
static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,   // also reads the PascalCase files the v1 prototype wrote
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,           // hand-edited profiles shouldn't fail over a trailing comma
    };
}
