namespace GhostShow.App.Tests;

public class CrashLogTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), "ghostshow-crash-tests", Guid.NewGuid().ToString("N"), "crash.log");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true);

    [Fact]
    public void What_ended_GhostShow_is_noted_with_when_newest_last()
    {
        CrashLog.Write(_file, new OperationCanceledException("The operation was canceled."));
        CrashLog.Write(_file, new InvalidOperationException("Second"));

        string text = File.ReadAllText(_file);
        Assert.Contains("ended:", text);
        Assert.True(text.IndexOf("OperationCanceledException", StringComparison.Ordinal) < text.IndexOf("Second", StringComparison.Ordinal));
    }
}
