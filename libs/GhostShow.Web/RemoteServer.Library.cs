using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GhostShow.Web;

// The fixture library, for the full app on a laptop. The library lives on the show computer. A device fetches the whole
// of it (GET /api/library) so that its type picker works as the desktop's does: thousands of profiles, but only about
// half a megabyte compressed. It's told when the library changes ("library" message), and fetches it again.
public sealed partial class RemoteServer
{
    /// <summary>The library's file, as last sent. Making it takes a moment, and it only changes when the library does.</summary>
    sealed record LibraryFile(long Version, byte[] Gzipped);

    /// <summary>What a custom fixture type's ID can be: it becomes a path on the show computer, so nothing that could leave its folder.</summary>
    static readonly Regex CustomIdFormat = new("^custom(/[A-Za-z0-9_-]+)+$", RegexOptions.CultureInvariant);

    /// <summary>Counts changes to the library's profiles: a device compares it with the version it fetched.</summary>
    long _libraryVersion = 1;
    volatile LibraryFile? _libraryFile;

    void SubscribeToLibrary()
    {
        _host.Library.StatusChanged += SendLibraryStatus;
        _host.Library.Changed += OnLibraryChanged;
    }

    void UnsubscribeFromLibrary()
    {
        _host.Library.StatusChanged -= SendLibraryStatus;
        _host.Library.Changed -= OnLibraryChanged;
    }

    void OnLibraryChanged()
    {
        Interlocked.Increment(ref _libraryVersion);
        SendLibraryStatus();
    }

    void SendLibraryStatus()
    {
        var message = LibraryJson();
        message["type"] = "library";
        SendToAll(message.ToJsonString());
    }

    JsonObject LibraryJson()
    {
        var status = _host.Library.Status;
        return new JsonObject
        {
            ["version"] = Interlocked.Read(ref _libraryVersion),
            ["summary"] = status.Summary,
            ["downloadStatus"] = status.DownloadStatus,
            ["downloadButtonText"] = status.DownloadButtonText,
        };
    }

    /// <summary>GET /api/library?token=…: every profile, one JSON object per line, compressed. Its version is in a header.</summary>
    async Task ServeLibraryAsync(HttpContext context)
    {
        if (Pairing.Find(context.Request.Query["token"]) is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var (version, profiles) = await _host.InvokeAsync(() => (Interlocked.Read(ref _libraryVersion), _host.Session.Profiles.Profiles));
        if (_libraryFile is not { } file || file.Version != version)
        {
            file = await Task.Run(() => new LibraryFile(version, Pack(profiles)));   // off the show's thread: it's a tenth of a second
            _libraryFile = file;
        }
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.ContentEncoding = "gzip";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Library-Version"] = version.ToString();
        await context.Response.Body.WriteAsync(file.Gzipped, context.RequestAborted);
    }

    static byte[] Pack(IReadOnlyList<FixtureProfile> profiles)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            foreach (var profile in profiles)
            {
                gzip.Write(Encoding.UTF8.GetBytes(FixtureProfileLoader.ToJsonLine(profile)));
                gzip.WriteByte((byte)'\n');
            }
        return packed.ToArray();
    }

    /// <summary>On the show's thread: "saveProfile", "deleteProfile" and "downloadLibrary". Returns why it couldn't, or null.</summary>
    Task<string?> LibraryCommandAsync(JsonObject message, string action)
    {
        switch (action)
        {
            case "saveProfile":
                var profile = FixtureProfileLoader.ParseJson(message["profile"]?.ToJsonString() ?? throw new FormatException("No profile."));
                if (!CustomIdFormat.IsMatch(profile.Id)) return Task.FromResult<string?>($"'{profile.Id}' can't be a custom fixture type's ID.");
                return _host.Library.SaveCustomAsync(profile);

            case "deleteProfile":
                string id = message["profile"]?.GetValue<string>() ?? throw new FormatException("No profile.");
                if (_host.Session.Profiles.Find(id) is not { } found || !CustomProfiles.IsCustom(found))
                    return Task.FromResult<string?>("That isn't a custom fixture type on the show computer.");
                if (_host.Session.Project.Fixtures.Any(f => string.Equals(f.ProfileId, found.Id, StringComparison.OrdinalIgnoreCase)))
                    return Task.FromResult<string?>("This show's fixtures use that fixture type, so it can't be deleted.");
                return _host.Library.DeleteCustomAsync(found);

            default:
                // Not waited for: it takes a while, and a device's commands run one at a time, so its Blackout would have
                // to wait too. The "library" messages say how it's going.
                _ = DownloadLibraryAsync();
                return Task.FromResult<string?>(null);
        }
    }

    async Task DownloadLibraryAsync()
    {
        try
        {
            await _host.Library.DownloadAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Downloading the fixture library failed.");
        }
    }
}
