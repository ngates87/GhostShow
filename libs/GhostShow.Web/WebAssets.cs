using System.Reflection;

namespace GhostShow.Web;

/// <summary>
/// The web app's files (built from client/ into wwwroot/ and embedded in this assembly). Paths without a file are the
/// app's own routes, so they get index.html. Without a built web app, every page says so.
/// </summary>
static class WebAssets
{
    static readonly Assembly Assembly = typeof(WebAssets).Assembly;

    /// <summary>Resource names by path ("index.html", "assets/app-1234.js"), with '/' on every OS.</summary>
    static readonly Dictionary<string, string> Files = Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("wwwroot", StringComparison.Ordinal))
        .ToDictionary(n => n["wwwroot".Length..].Replace('\\', '/').TrimStart('/'), n => n, StringComparer.Ordinal);

    public static bool IsBuilt => Files.ContainsKey("index.html");

    public const string NotBuiltPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>GhostShow</title><style>body{font-family:system-ui,sans-serif;background:#111;color:#eee;margin:0;padding:24px}</style></head>
        <body><h1>GhostShow</h1><p>The web remote's page wasn't built into this copy of GhostShow (it needs Node.js to build).
        The server is running, though: build GhostShow again with Node.js installed.</p></body></html>
        """;

    /// <summary>The file for a request path, or index.html for one of the app's routes; null for a missing asset.</summary>
    public static (Stream Content, string Type, bool Immutable)? Open(string path)
    {
        path = path.TrimStart('/');
        if (path.Length == 0 || !Files.ContainsKey(path))
        {
            // An asset (it has an extension) that isn't there is a 404; anything else is a route of the app.
            if (Path.HasExtension(path) && path != "index.html") return null;
            path = "index.html";
        }
        if (!Files.TryGetValue(path, out var name) || Assembly.GetManifestResourceStream(name) is not { } stream) return null;
        // Vite names its assets by content hash: they never change, so browsers may keep them.
        return (stream, ContentType(path), path.StartsWith("assets/", StringComparison.Ordinal));
    }

    static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" or ".webmanifest" => "application/manifest+json",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
