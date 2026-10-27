using GhostShow.Audio;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Dmx;

namespace GhostShow.App;

/// <summary>
/// The long-lived services the app is built from, created once at startup (App.axaml.cs) and handed to the view
/// models. None of them depend on the UI; the same set runs headless in the CLI.
/// </summary>
public sealed record AppServices(
    ShowSession Session,
    OutputManager Outputs,
    ShowPlayer Player,
    PlaybackLoop PlaybackLoop,
    AudioService Audio,
    MachineSettings Settings,
    AppPaths Paths,
    RecentEventsLog Log,
    UiResponsivenessProbe Probe);
