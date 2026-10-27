namespace GhostShow.Core.Timeline;

/// <summary>
/// How audio file references in a show are stored and found:
/// - A file inside the show file's folder (or below it) is stored relative to that folder, e.g. "audio/thunder.wav",
///   so a show folder can be moved or copied to another computer with its sounds.
/// - Any other file is stored as an absolute path, and only works where that path exists.
/// - A relative reference is resolved against the show file's folder; for a show not yet saved, against the current
///   directory (references made then are absolute, so this only matters for hand-edited files).
/// </summary>
public static class AudioPaths
{
    /// <param name="showFolder">The folder of the show file, or null if the show hasn't been saved.</param>
    public static string Resolve(string reference, string? showFolder) =>
        Path.IsPathRooted(reference) || showFolder is null
            ? Path.GetFullPath(reference)
            : Path.GetFullPath(Path.Combine(showFolder, reference));

    /// <summary>The reference to store for a file: relative if it's inside the show's folder, otherwise absolute.</summary>
    public static string ToReference(string fullPath, string? showFolder)
    {
        fullPath = Path.GetFullPath(fullPath);
        if (showFolder is null) return fullPath;
        string relative = Path.GetRelativePath(Path.GetFullPath(showFolder), fullPath);
        bool outside = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
        return outside ? fullPath : relative.Replace('\\', '/');
    }

    /// <summary>
    /// When a show is saved to a (new) folder: makes every audio reference follow the rule above for that folder.
    /// References are resolved against the old folder first, so nothing changes which file is meant.
    /// </summary>
    public static void Rebase(IEnumerable<ShowTimeline> timelines, string? oldFolder, string newFolder)
    {
        foreach (var audio in timelines.SelectMany(t => t.Tracks).SelectMany(t => t.Events).OfType<AudioEvent>())
        {
            if (string.IsNullOrWhiteSpace(audio.File)) continue;
            audio.File = ToReference(Resolve(audio.File, oldFolder), newFolder);
        }
    }
}
