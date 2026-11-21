using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;

namespace GhostShow.Server;

/// <summary>
/// What a device asks the show computer to do: the transport, running and stopping timelines, buttons, Blackout, and
/// the File menu. The same playback, triggers and files the desktop's own controls use, so a device and the desktop
/// never behave differently. Called on the show's thread.
/// </summary>
/// <param name="focused">The timeline open in the editor on the show computer (the transport works it), as its screen says.</param>
/// <param name="blackout">The show computer's Blackout.</param>
/// <param name="testSound">Plays a test tone on an output; false if it isn't connected.</param>
public sealed class ShowControl(ShowSession session, IPlayback playback, ShowTriggers triggers, ShowFiles files, Func<Guid?> focused,
                                Action blackout, Func<string?, AudioSide, bool> testSound) : IShowControl
{
    const string Gone = "That timeline isn't in the show any more.";

    /// <summary>A device opened a timeline: the editor on the show computer shows it, and the transport works it.</summary>
    public event Action<Guid>? OpenRequested;

    bool Has(Guid id) => session.Project.Timelines.Any(t => t.Id == id);

    public string? Transport(TransportAction action, TimeSpan? position)
    {
        if (focused() is not { } id || !Has(id)) return "No timeline is open in the editor on the show computer.";
        bool playing = playback.StatusOf(id).State == TransportState.Playing;
        switch (action)
        {
            case TransportAction.Play when !playing:
            case TransportAction.Pause when playing:
                playback.PlayOrPause(id);
                break;
            case TransportAction.Stop:
                playback.StopTimeline(id);
                break;
            case TransportAction.Restart:
                playback.Restart(id);
                break;
            case TransportAction.Seek:
                playback.Seek(id, position ?? TimeSpan.Zero);
                break;
        }
        return null;
    }

    public string? Run(Guid timelineId, RunOptions? how = null)
    {
        if (!Has(timelineId)) return Gone;
        playback.Launch(timelineId, how);
        return null;
    }

    public string? EndLoop(Guid timelineId)
    {
        if (!Has(timelineId)) return Gone;
        playback.EndLoop(timelineId);
        return null;
    }

    public string? StopTimeline(Guid timelineId)
    {
        if (!Has(timelineId)) return Gone;
        playback.StopTimeline(timelineId);
        return null;
    }

    public string? StopRunning(Guid runningId)
    {
        playback.StopPlayer(runningId);
        return null;
    }

    public string? StopAll()
    {
        playback.StopAll();
        return null;
    }

    public string? Blackout()
    {
        blackout();
        return null;
    }

    public string? PressButton(Guid buttonId) => triggers.Press(buttonId);

    public string? Open(Guid timelineId)
    {
        if (!Has(timelineId)) return Gone;
        OpenRequested?.Invoke(timelineId);
        return null;
    }

    public Task<string?> SaveAsync() => files.SaveForDeviceAsync();
    public IReadOnlyList<string> SavedShows() => files.SavedShows();
    public Task<string?> NewShowAsync(bool discardUnsaved) => files.NewForDeviceAsync(discardUnsaved);
    public Task<string?> OpenShowAsync(string name, bool discardUnsaved) => files.OpenByNameAsync(name, discardUnsaved);
    public Task<string?> SaveShowAsAsync(string name) => files.SaveAsByNameAsync(name);
    public Task<string?> RenameShowAsync(string name) => files.RenameAsync(name);

    public string? TestSound(string? device, AudioSide side) =>
        testSound(device, side) ? null : $"Couldn't play on {device ?? "the default output"}: it isn't connected (or is still opening).";
}
