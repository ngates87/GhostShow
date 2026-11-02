namespace GhostShow.Core.Storage;

/// <summary>
/// Writes files so a crash, power cut or full disk mid-save never leaves a half-written file in place of the old one.
/// The content goes to a temporary file in the same folder, is flushed to disk, and only then renamed over the
/// target. A rename within one folder is atomic on every supported OS.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                                     bufferSize: 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Leaving a stray .tmp file behind is harmless; the save error being reported matters more.
        }
    }
}
