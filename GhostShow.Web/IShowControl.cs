namespace GhostShow.Web;

/// <summary>The editor's transport, as a device works it.</summary>
public enum TransportAction { Play, Pause, Stop, Restart, Seek }

/// <summary>
/// What a device can ask the show computer to do, run on the show's thread (see <see cref="IRemoteHost.InvokeAsync"/>).
/// Each returns null when it's done, or why it couldn't be (shown on the device).
/// </summary>
public interface IShowControl
{
    /// <summary>The timeline open in the editor (play, pause, stop, play from the start, or go to a position).</summary>
    string? Transport(TransportAction action, TimeSpan? position);

    /// <summary>Runs a timeline from its start, as a show button does: another copy if it's already running.</summary>
    string? Run(Guid timelineId);

    /// <summary>Stops a timeline and every copy of it.</summary>
    string? StopTimeline(Guid timelineId);

    /// <summary>Stops one running timeline or copy (an entry of the status's running list).</summary>
    string? StopRunning(Guid runningId);

    string? StopAll();

    /// <summary>The desktop's Blackout: playback stops and every manual level goes to 0.</summary>
    string? Blackout();

    /// <summary>Presses a show button (runs its actions).</summary>
    string? PressButton(Guid buttonId);

    /// <summary>Opens a timeline in the editor: it becomes the one the transport works.</summary>
    string? Open(Guid timelineId);

    /// <summary>Plays a short test tone on an audio output (null: the default), to check which speakers it is.</summary>
    string? TestSound(string? device);
}
