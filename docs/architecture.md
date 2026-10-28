# Architecture

GhostShow is show control for haunted houses: lighting and audio on timelines now, and later video, props and
remote control. Phase 1 built the foundation (DMX output, fixtures, manual control, configuration, diagnostics);
Phase 2 added timelines, show playback and audio. Timelines, playback and audio are described in
[timeline.md](timeline.md).

## Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `GhostShow.Core` | Engine state and commands, universe model, fixture profiles/patching/validation, storage, diagnostics primitives, timeline model and compiler, players (one per timeline), the playback manager that merges them, and the playback loop | `Microsoft.Extensions.Logging.Abstractions` only |
| `GhostShow.Dmx` | Output service, device interface, simulator, Open DMX / USB Pro / Art-Net drivers, adapter discovery | Core, `System.IO.Ports` |
| `GhostShow.Audio` | Audio output (`AudioService`, implementing Core's `IAudioOutput`): devices, streaming WAV/MP3, mixing, device-loss recovery | Core, SoundFlow (MIT) |
| `GhostShow.App` | Avalonia desktop UI | Core, Dmx, Audio (Dmx and Audio are used at startup and on their setup screens) |
| `GhostShow.Cli` | Running without a UI: timeline playback, soak tests | Core, Dmx, Audio |
| `GhostShow.Tests` | xUnit v3 unit and integration tests for the engine, DMX output, fixtures, storage, timelines, playback and audio | Core, Dmx, Audio |
| `GhostShow.App.Tests` | xUnit v3 tests for the app's view models (add-fixtures wizard, profile browser, paging, save guard, timeline editor, undo/redo); no UI is shown | App, Core |
| `GhostShow.Benchmarks` | BenchmarkDotNet benchmarks for the frame path, patching, timeline evaluation and compiling, show save/load | Core, Dmx |

The spec suggested separate Fixtures, Storage and Diagnostics projects. They are folders in Core instead: each is a
few hundred lines with no dependency of its own, so separate projects would add build plumbing without improving
separation. Dmx and Audio are separate because they are the only code that touches hardware, native libraries and
OS-specific APIs; Core has no hardware or UI dependency and runs anywhere, including in tests. The player in Core
talks to audio through `IAudioOutput`, so playback logic is tested with a fake.

Dependencies only point downward (App/Cli → Dmx, Audio → Core). There are no cycles.

## UI technology: Avalonia

| | Avalonia 12 | WPF | WinUI 3 | .NET MAUI |
|---|---|---|---|---|
| Runs on Linux (the development machine) | yes | no | no | no desktop Linux |
| Runs on Windows and macOS | yes | Windows only | Windows only | yes |
| Responsiveness / startup | good (Skia renderer, compiled bindings) | good | fair | fair |
| Deployment | self-contained folder, no installer needed | Windows only | MSIX / Windows App SDK | heavy |
| Long-term outlook | active, frequent releases | maintenance mode | uncertain | mobile-focused |

Avalonia wins on the deciding requirements: development happens on Linux, and haunt rigs run on whatever PC or Mac
is spare. It is XAML/MVVM like WPF, so the skills transfer. The UI only talks to Core's command interface and
read-only state, so it can be replaced without touching the engine.

## How output works

```
 UI / CLI / later: OSC, web, buttons          PlaybackManager (playback thread, 100 Hz): one ShowPlayer
        │  IShowCommands: SetChannel,                │  per playing timeline, merged by priority → SetTimelineLevels
        │  SetFixtureControl, ClearUniverse, ...     │
        ▼                                            ▼
 ShowEngine ── manual layer + timeline layer per universe, and the fixture patch
        │  TryCopyUniverse: timeline value where the timeline controls a channel, else manual (copied, no queue)
        ▼
 DmxOutputService ── one dedicated thread per universe, its own preallocated frame buffer
        │  IDmxOutputDevice.SendFrame(ReadOnlySpan<byte>)
        ▼
 OpenDmxDevice | UsbProDevice | ArtNetDevice | SimulatorDevice
```

**State ownership.** `ShowEngine` is the only owner of channel values and the patch. `DmxUniverse` arrays are never
handed out; readers get copies. Every engine method takes one short lock and does no I/O under it.

**Latest value wins, no queue.** Commands overwrite values in place. Each output thread copies the current values
once per frame, so a burst of slider moves costs one frame of work, not one per move, and stale values can't pile
up. Measured: 50,000 updates in a burst produce frames only at the frame rate (`DmxOutputServiceTests`).

**Output threads.** One dedicated thread per universe, at above-normal priority. Dedicated rather than the thread
pool, because Open DMX output blocks for most of every frame (it *is* the frame timing) and frame timing must not
depend on pool load. The target interval is 25 ms (40 frames/s). The thread copies the frame, sends it, then waits
precisely (sleep, then spin for the last 2 ms) until the next frame is due.

**Device interface.** `ConnectAsync`/`DisconnectAsync` are async because finding a device can take a while.
`SendFrame` is synchronous: it runs on the output thread, and for Open DMX the blocking call is the timing. Devices
only connect, send and throw `DmxDeviceException`; status, retries and reporting live in `DmxOutputService` so every
device behaves the same.

**UI refresh.** The UI reads engine values and the last frame each output sent on a 30 Hz timer, independent of
output timing. Only visible screens refresh, and only changed monitor cells are updated.

**Two layers.** Manual commands and the playing timeline write separate layers; output takes the timeline's value
on the channels it controls. Stop releases the timeline layer, so manual levels come back exactly as they were.

**Show vs machine.** `ShowSession` holds the open show (`Project`: universes, fixtures, timelines) and applies it to
the engine. Which hardware drives which universe, and which audio device and master volume to use, is per computer,
in `MachineSettings`, applied by `OutputManager` and `AudioService`.
A show opened on another machine keeps its patch and uses that machine's hardware; universes with no hardware
assigned go to the simulator.

## User interface

A menu bar (File, Edit, View, Playback) and a slim toolbar (transport, warnings, Blackout) are always there. On
start the last show reopens; with no show open (first start, or after File → Close Show) the window shows **Home**:
new show, open show, recent shows (kept in machine settings). With a show open, the tabs are:

| Tab | Screens |
|---|---|
| Timeline | The timeline tree, open timelines as tabs, the editor |
| Lighting | DMX setup (universes and their hardware), Fixtures, Control, Channels (raw faders), Monitor |
| Audio | Output device, master volume, test sound, the show's sound files |
| Diagnostics | Output and playback timing, audio status, UI responsiveness, memory, recent events |

The editor is the first tab because it's where most of the work happens. Closing a show leaves an empty show
underneath, so outputs keep running (dark) and Blackout always works.

**Timeline editor.** `TimelineEditorViewModel` holds the selection and every editing command; every change is an
`EditHistory` entry (apply and revert closures capturing only the changed values), after which the timeline is
recompiled and swapped into the player, so edits apply while playing. The lanes (`Controls/TimelineCanvas`) are
drawn directly and only the visible part; the playhead is a separate one-line layer, so moving it 30 times a second
doesn't redraw the events.

**Screens stay built.** The tab strips only choose which screen is visible; every screen is created once and then
shown or hidden. A tab control that rebuilds its content on every visit made switching to the 512-channel monitor
take over 200 ms each time; now a repeat visit costs about 20 ms. The monitor and the address map are drawn
directly (one control each) rather than built from hundreds of templated controls, for the same reason.

**View models, no logic in views.** Each screen has a view model (CommunityToolkit.Mvvm). Views only bind, apart
from the address map's and timeline's input events, the Timeline screen's keyboard shortcuts and scrolling, and the
Channels screen measuring how many faders fit. View models talk to the
engine through `IShowCommands` and to the show through `ShowSession`, and never touch hardware.

**Fixtures screen.** The left column has a heat map of the universe (one square per address; collisions red) above
the fixture list. The centre is in one of three steps (`SetupStep`):

1. *Overview*: the selected fixture's settings, and the address map. Click a block to select a fixture, drag it to
   re-address.
2. *Choose type* (Add fixtures…, or Change type…): the profile browser, by manufacturer or by category, then a mode.
3. *Place*: the address map outlines every opening big enough for the chosen type. Each click on one adds a
   fixture there; quick-add places N back to back; Undo removes the last; Done returns to the overview.

The address map (`Controls/PatchMap`) is drawn directly rather than built from 512 controls, so pointer previews
stay cheap. The gap arithmetic it shows (used channels, free ranges, collisions, openings) is `UniverseUsage` in
Core, where it is unit tested.

**Unsaved changes.** New, Open and closing the window go through `UnsavedChangesGuard`. With unsaved show
changes it asks Save / Don't save / Cancel, and only proceeds once the show is saved or the user chose to discard.
A save that doesn't happen (e.g. Save As cancelled) cancels the action. Machine settings (hardware assignments) are
saved as soon as they change.

## Connection and failure behaviour

| Situation | What GhostShow does |
|---|---|
| Device missing or busy at start | Output status Faulted, logged once, retried every 2 s; the rest of the app works normally |
| Device unplugged mid-show | Next send fails → Faulted, logged, device closed, reconnect every 2 s. USB adapters are found again by serial number, even on a different USB socket |
| Device comes back | Reconnects, logs it, output resumes with current values |
| Output thread bug | Logged as critical, status Faulted; the app keeps running |
| Controlled stop (closing the app, ending a CLI run, changing an output) | Sends all-zero frames for 0.5 s (blackout), disconnects, releases the device. The window waits for this before closing. `StopAsync` gives up after 3 s and logs it if a driver hangs |
| Crash, power loss, unplugging | Nothing can be sent. Fixtures follow their own DMX-loss setting (see below) |
| Audio device missing or unplugged | Reported on the Audio screen and in Diagnostics, retried every 3 s; when it's back, sounds that should be playing restart at the right place. Lighting is unaffected |
| Missing sound file, unpatched fixture, other timeline problems | That event is skipped and listed on the Timeline screen; the rest of the show plays |
| Playback update throws (a bug) | Logged once, counted in Diagnostics; playback carries on with the next update |

### Blackout and safety policy

GhostShow blacks out on every controlled stop. It cannot guarantee darkness, or that effects stop, when it isn't
running. What fixtures do then depends on the interface:

- **Open DMX** (bare FTDI): has no memory. When GhostShow stops sending, the DMX line goes idle and every fixture
  sees DMX loss.
- **Enttec Pro-compatible**: keeps repeating the last frame it received until it loses power. A GhostShow crash
  leaves the last look on stage.
- **Art-Net nodes**: typically hold the last frame for a while, then follow their own loss setting.

**Operational rule:** set every fixture's DMX-loss behaviour deliberately (most offer hold-last or blackout), and for
anything that is a hazard when left on (fog, motors, pneumatics), use the fixture's own fail-safe. Don't rely on
software for life safety. Per-channel "safe values" other than zero are a planned extension, not in Phase 1.

## Extension points for later phases

- **OSC, web server, physical buttons:** call `IShowCommands`, exactly as the UI does. None of them touch hardware.
- **More sources of levels** (several timelines at once, cue stacks, HTP/LTP merging): belong inside `ShowEngine`,
  which is the single place values are combined. Today there are two layers, and the timeline's wins where it
  controls a channel.
- **Triggers** (a button, a sensor, OSC starting a timeline): call `ShowPlayer` transport methods, which are
  thread-safe, exactly as the toolbar does.
- **Universe numbers** start at 1, as on consoles and in sACN. Art-Net's wire addressing starts at 0: the CLI's
  `--ip` sends universe 1 as Art-Net universe 0, and Art-Net assignments in machine settings name their Art-Net
  universe explicitly. Shows saved before version 3, and machine settings before
  version 2, numbered from 0; loading them shifts every universe reference up by one.
- **More universes:** already universe-aware throughout (model, engine, outputs, settings, UI pickers). Phase 1 is
  tested with one; nothing assumes only one.
- **More output protocols:** implement `IDmxOutputDevice`, add a config record and a case in `OutputManager.CreateDevice`.
- **Show format:** a show is a folder (version 5): `show.json` (`Project`) plus a file per timeline under
  `timelines/`, whose folders are the timeline tree (`ShowFolderStore`; see [timeline.md](timeline.md)). Loaders
  migrate older versions (version 1 output assignments move to machine settings; before 3, universes are renumbered
  from 1; before 4, the prototype's scenes become timelines; before 5, a single file is read and then saved as a
  folder) and refuse newer ones with a clear message. Timeline files carry their own format version.

## The Phase 0 prototype

Before Phase 1 there was a timeline/audio prototype (scenes of keyframed channel tracks and audio clips, rendered to
frames and played with ffplay). Phase 2 replaced it: the timeline model, compiler, player and SoundFlow audio
superseded it, and shows that stored scenes are converted to timelines when opened (`LegacyScenes`), so nothing
made with it is lost.
