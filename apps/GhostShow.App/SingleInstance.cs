using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace GhostShow.App;

/// <summary>
/// Only one GhostShow runs at a time: two copies would fight over the DMX interfaces and sound cards, and a show
/// saved in one would overwrite the other's changes. The first copy holds a named lock for as long as it runs and
/// listens for a "show yourself" message. A second copy finds the lock taken, sends that message so the running
/// window comes to the front, and quits.
///
/// The lock is a named mutex: the operating system releases it when the process ends, even after a crash, so a
/// crashed GhostShow never blocks the next start. The message goes over a named pipe (Windows) or a local socket
/// (Linux, macOS); .NET's NamedPipe classes do both.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string ShowMessage = "show";

    readonly Mutex _lock;
    readonly string _pipeName;
    readonly CancellationTokenSource _stop = new();

    SingleInstance(Mutex held, string pipeName)
    {
        _lock = held;
        _pipeName = pipeName;
        _ = ListenAsync();
    }

    /// <summary>
    /// Raised (on a worker thread) when another copy was started and wants this one shown instead.
    /// </summary>
    public event Action? ShowRequested;

    /// <summary>
    /// The name both copies agree on. Per user, because Windows pipe names are shared by everyone on the computer.
    /// </summary>
    public static string DefaultName => $"GhostShow-{Environment.UserName}";

    /// <summary>
    /// Takes the lock if no other copy holds it, and starts listening for later copies. Returns null if another copy is running.
    /// </summary>
    public static SingleInstance? TryClaim(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, $"{name}-lock", out bool createdNew);
        if (createdNew) return new SingleInstance(mutex, $"{name}-show");
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Asks the running copy to show its window. Returns false if it didn't answer within the timeout (still
    /// starting, or not responding).
    /// </summary>
    public static bool AskRunningToShow(string name, TimeSpan timeout)
    {
        // Windows only lets the program the user just started bring a window to the front. This copy is that
        // program, so it hands the permission on; without it the running window would only flash in the taskbar.
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AnyProcess);
        try
        {
            using var pipe = new NamedPipeClientStream(".", $"{name}-show", PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeout);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(ShowMessage);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Answers later copies one at a time until disposed.</summary>
    async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                                 PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe);
                if (await reader.ReadLineAsync(_stop.Token) == ShowMessage) ShowRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A copy that gave up halfway (closed before writing): wait for the next one.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _lock.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from another thread than the one that took it: closing it below frees it all the same.
        }
        _lock.Dispose();
    }

    const int AnyProcess = -1;   // ASFW_ANY

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
