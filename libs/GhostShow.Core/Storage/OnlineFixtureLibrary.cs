using System.IO.Compression;
using System.Text.Json;

namespace GhostShow.Core.Storage;

/// <summary>When and from where the downloaded library was installed; saved next to it as library.json.</summary>
public sealed record DownloadedLibraryInfo(string Source, string Url, DateTimeOffset DownloadedAt, int FixtureCount);

/// <summary>
/// Downloads the Open Fixture Library (open-fixture-library.org, MIT licensed, community maintained) so GhostShow has
/// a full fixture library without QLC+ installed. OFL publishes its whole library as one ZIP in QLC+'s .qxf format,
/// which <see cref="Fixtures.QlcFixtureImporter"/> already reads; about 650 fixtures, ~3 MB.
///
/// Only runs when the user asks. The download is unpacked to a temporary folder and swapped in only once complete,
/// so a failed or interrupted update leaves the previous library working.
/// </summary>
public static class OnlineFixtureLibrary
{
    public const string SourceName = "Open Fixture Library";
    public const string DownloadUrl = "https://open-fixture-library.org/download.qlcplus_4.12.2";

    // Guards against a corrupt or hostile archive filling the disk; the real library unpacks to ~10 MB.
    const long MaxUnpackedBytes = 200L * 1024 * 1024;
    const string InfoFileName = "library.json";

    /// <summary>Where the downloaded library lives: &lt;config&gt;/libraries/open-fixture-library, with library.json beside a fixtures folder.</summary>
    public static string Directory(AppPaths paths) => Path.Combine(paths.ConfigDirectory, "libraries", "open-fixture-library");

    /// <summary>The .qxf files only, so the profile loader never mistakes library.json for a profile.</summary>
    public static string FixturesDirectory(AppPaths paths) => Path.Combine(Directory(paths), "fixtures");

    /// <summary>The installed library's details, or null if it has never been downloaded (or its info is unreadable).</summary>
    public static DownloadedLibraryInfo? Installed(AppPaths paths)
    {
        string infoPath = Path.Combine(Directory(paths), InfoFileName);
        try
        {
            return File.Exists(infoPath) ? JsonSerializer.Deserialize<DownloadedLibraryInfo>(File.ReadAllText(infoPath), Json.Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <exception cref="HttpRequestException">The download failed.</exception>
    /// <exception cref="InvalidDataException">The download wasn't a fixture library ZIP.</exception>
    public static async Task<DownloadedLibraryInfo> DownloadAsync(HttpClient http, AppPaths paths, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Buffer to a temp file: ZipArchive needs a seekable stream, and 3 MB doesn't belong in memory twice.
        string tempZip = Path.Combine(Path.GetTempPath(), $"ghostshow-ofl-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = File.Create(tempZip))
                await response.Content.CopyToAsync(file, cancellationToken);
            await using var zip = File.OpenRead(tempZip);
            return Install(zip, paths, new DownloadedLibraryInfo(SourceName, DownloadUrl, DateTimeOffset.Now, 0));
        }
        finally
        {
            File.Delete(tempZip);
        }
    }

    /// <summary>
    /// Unpacks the .qxf files under fixtures/ in the ZIP and replaces the installed library with them. Every other
    /// entry is ignored, and entries are flattened to their file name, so nothing can be written outside the folder.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a ZIP, no fixtures in it, or too large.</exception>
    public static DownloadedLibraryInfo Install(Stream zip, AppPaths paths, DownloadedLibraryInfo source)
    {
        string target = Directory(paths);
        string staging = $"{target}.new-{Guid.NewGuid():N}";
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            int count = ExtractFixtures(zip, System.IO.Directory.CreateDirectory(Path.Combine(staging, "fixtures")).FullName);
            if (count == 0) throw new InvalidDataException("The download contained no fixture definitions.");

            var info = source with { FixtureCount = count };
            File.WriteAllText(Path.Combine(staging, InfoFileName), JsonSerializer.Serialize(info, Json.Options));
            SwapIn(staging, target);
            return info;
        }
        catch
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    static int ExtractFixtures(Stream zip, string destination)
    {
        ZipArchive archive;
        try { archive = new ZipArchive(zip, ZipArchiveMode.Read); }
        catch (InvalidDataException ex) { throw new InvalidDataException("The download isn't a valid ZIP file.", ex); }

        using (archive)
        {
            long unpacked = 0;
            int count = 0;
            foreach (var entry in archive.Entries)
            {
                bool isFixture = entry.FullName.StartsWith("fixtures/", StringComparison.Ordinal)
                                 && entry.Name.EndsWith(".qxf", StringComparison.OrdinalIgnoreCase);
                if (!isFixture) continue;

                unpacked += entry.Length;
                if (unpacked > MaxUnpackedBytes) throw new InvalidDataException("The download unpacks to more than 200 MB; refusing it.");
                entry.ExtractToFile(Path.Combine(destination, entry.Name), overwrite: true);
                count++;
            }
            return count;
        }
    }

    /// <summary>Moves the old library aside, the new one into place, then deletes the old; restores it if the move fails.</summary>
    static void SwapIn(string staging, string target)
    {
        string old = $"{target}.old-{Guid.NewGuid():N}";
        bool hadOld = System.IO.Directory.Exists(target);
        if (hadOld) System.IO.Directory.Move(target, old);
        try
        {
            System.IO.Directory.Move(staging, target);
        }
        catch
        {
            if (hadOld) System.IO.Directory.Move(old, target);
            throw;
        }
        if (hadOld) System.IO.Directory.Delete(old, recursive: true);
    }
}
