using GhostShow.Core.Storage;

namespace GhostShow.App;

/// <summary>
/// Notes an exception that ends GhostShow in <see cref="AppPaths.CrashLogFile"/>. The app's log is kept in memory
/// (Diagnostics) and on the console, so without this a crash, e.g. on a device's reading thread, leaves no trace but
/// Windows' event log.
/// </summary>
public static class CrashLog
{
    const long MaxBytes = 1_000_000;   // a crash loop mustn't fill the disk: past this the file starts again

    /// <summary>Notes every exception that would end the process, from any thread.</summary>
    public static void Install(AppPaths paths) =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(paths.CrashLogFile, e.ExceptionObject);

    public static void Write(string file, object exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && new FileInfo(file).Length > MaxBytes) File.Delete(file);
            File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  GhostShow {typeof(CrashLog).Assembly.GetName().Version} ended:{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it: the process is ending anyway, and Windows' event log still has it.
        }
    }
}
