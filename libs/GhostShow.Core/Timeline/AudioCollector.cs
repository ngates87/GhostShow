namespace GhostShow.Core.Timeline;

/// <summary>A sound file outside the show's folder, and where its copy goes (relative to the folder, e.g. "audio/thunder.wav").</summary>
public sealed record AudioToCollect(string Source, string Reference, long Bytes);

/// <summary>
/// Copies the sound files a show uses from outside its folder into its "audio" folder, and points the clips at the
/// copies, so the show folder holds everything it needs: it can be zipped and moved to another computer, and its
/// sounds play from the local disk (a file on a cloud drive such as Google Drive can take seconds to open the first
/// time). VenueMagic likewise imports audio into the project.
///
/// Three steps, so the slow one runs in the background and the show is only changed on its own thread:
/// <see cref="Plan"/> (which files, where to), <see cref="CopyAsync"/> (the copying), <see cref="Repoint"/> (the clips).
/// </summary>
public static class AudioCollector
{
    /// <summary>The folder inside the show folder that sounds are copied into.</summary>
    public const string Folder = "audio";

    /// <summary>
    /// The files to copy: every sound a clip uses that exists and is outside the show folder, once each. A file of the
    /// same name and size already in the audio folder is taken to be the same file (copied before) and reused;
    /// another file of the same name gets a numbered name.
    /// </summary>
    public static IReadOnlyList<AudioToCollect> Plan(IEnumerable<ShowTimeline> timelines, string showFolder)
    {
        var plan = new List<AudioToCollect>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // names given out in this plan
        var sources = timelines.SelectMany(t => t.Tracks).SelectMany(t => t.Events).OfType<AudioEvent>()
            .Where(e => !string.IsNullOrWhiteSpace(e.File) && Path.IsPathRooted(AudioPaths.ToReference(AudioPaths.Resolve(e.File, showFolder), showFolder)))
            .Select(e => AudioPaths.Resolve(e.File, showFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var info = new FileInfo(source);
            if (!info.Exists) continue;   // a missing file is reported when the timeline is compiled
            string name = NameFor(info, Path.Combine(showFolder, Folder), taken);
            plan.Add(new AudioToCollect(source, $"{Folder}/{name}", info.Length));
        }
        return plan;
    }

    static string NameFor(FileInfo source, string folder, HashSet<string> taken)
    {
        string stem = Path.GetFileNameWithoutExtension(source.Name), extension = source.Extension;
        for (int n = 1; ; n++)
        {
            string name = n == 1 ? source.Name : $"{stem} ({n}){extension}";
            if (taken.Contains(name)) continue;
            var existing = new FileInfo(Path.Combine(folder, name));
            if (!existing.Exists || existing.Length == source.Length)
            {
                taken.Add(name);
                return name;
            }
        }
    }

    /// <summary>
    /// Copies the files (skipping ones already there: see <see cref="Plan"/>). Each is written to a temporary name and
    /// then renamed, so a half-copied file is never played. Reports bytes copied so far out of the total.
    /// </summary>
    public static async Task CopyAsync(IReadOnlyList<AudioToCollect> plan, string showFolder, IProgress<(long Done, long Total)>? progress = null,
                                       CancellationToken cancellationToken = default)
    {
        long total = plan.Sum(p => p.Bytes), done = 0;
        var buffer = new byte[1 << 20];
        Directory.CreateDirectory(Path.Combine(showFolder, Folder));
        foreach (var item in plan)
        {
            string target = Path.Combine(showFolder, item.Reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                progress?.Report((done += item.Bytes, total));
                continue;
            }
            string temporary = target + ".copying";
            try
            {
                await using (var from = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true))
                await using (var to = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    int read;
                    while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        progress?.Report((done += read, total));
                    }
                }
                File.Move(temporary, target);
            }
            catch
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    /// <summary>
    /// Points every clip that uses a copied file at its copy. Returns the clips changed. Call on the show's thread,
    /// inside an edit (one undo step), once <see cref="CopyAsync"/> has finished.
    /// </summary>
    public static IReadOnlyList<AudioEvent> Repoint(IEnumerable<ShowTimeline> timelines, IReadOnlyList<AudioToCollect> plan, string showFolder)
    {
        var bySource = plan.ToDictionary(p => p.Source, p => p.Reference, StringComparer.OrdinalIgnoreCase);
        var changed = new List<AudioEvent>();
        foreach (var e in Clips(timelines, plan, showFolder))
        {
            e.File = bySource[AudioPaths.Resolve(e.File, showFolder)];
            changed.Add(e);
        }
        return changed;
    }

    /// <summary>The clips <see cref="Repoint"/> would change (for the edit's undo roots).</summary>
    public static IReadOnlyList<AudioEvent> Clips(IEnumerable<ShowTimeline> timelines, IReadOnlyList<AudioToCollect> plan, string showFolder)
    {
        var sources = plan.Select(p => p.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return timelines.SelectMany(t => t.Tracks).SelectMany(t => t.Events).OfType<AudioEvent>()
            .Where(e => !string.IsNullOrWhiteSpace(e.File) && sources.Contains(AudioPaths.Resolve(e.File, showFolder)))
            .ToList();
    }
}
