# Architecture

GhostShow is show control for haunted houses: lighting now, and later audio, video, timelines, props and remote
control. Phase 1 builds the foundation: DMX output, fixtures, manual control, configuration, diagnostics, and the
structure later phases plug into.

## Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `GhostShow.Core` | Engine state and commands, universe model, fixture profiles/patching/validation, storage, diagnostics primitives, the frozen timeline prototype | `Microsoft.Extensions.Logging.Abstractions` only |
| `GhostShow.Dmx` | Output service, device interface, simulator, Open DMX / USB Pro / Art-Net drivers, adapter discovery | Core, `System.IO.Ports` |
| `GhostShow.App` | Avalonia desktop UI | Core, Dmx (Dmx is only used at startup and on the DMX setup screen) |
| `GhostShow.Cli` | Running without a UI, timeline playback, soak test | Core, Dmx |
| `GhostShow.Tests` | xUnit v3 unit and integration tests | Core, Dmx |
| `GhostShow.Benchmarks` | BenchmarkDotNet benchmarks for the frame path | Core, Dmx |

The spec suggested separate Fixtures, Storage and Diagnostics projects. They are folders in Core instead: each is a
few hundred lines with no dependency of its own, so separate projects would add build plumbing without improving
separation. Dmx is separate because it is the only code that touches serial ports and OS-specific APIs; Core has
no hardware or UI dependency and runs anywhere, including in tests.

Dependencies only point downward (App/Cli → Dmx → Core). There are no cycles.

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
 UI / CLI / later: OSC, web, buttons, timelines
        │  IShowCommands: SetChannel, SetFixtureControl, SetFixtureChannel, ClearUniverse, Blackout
        ▼
 ShowEngine ── owns the desired values (one DmxUniverse per universe) and the fixture patch
        │  TryCopyUniverse: latest values, copied (no queue)
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

**Show vs machine.** `ShowSession` holds the open show (`Project`: universes, fixtures, scenes) and applies it to
the engine. Which hardware drives which universe is per computer, in `MachineSettings`, applied by `OutputManager`.
A show opened on another machine keeps its patch and uses that machine's hardware; universes with no hardware
assigned go to the simulator.

## Connection and failure behaviour

| Situation | What GhostShow does |
|---|---|
| Device missing or busy at start | Output status Faulted, logged once, retried every 2 s; the rest of the app works normally |
| Device unplugged mid-show | Next send fails → Faulted, logged, device closed, reconnect every 2 s. USB adapters are found again by serial number, even on a different USB socket |
| Device comes back | Reconnects, logs it, output resumes with current values |
| Output thread bug | Logged as critical, status Faulted; the app keeps running |
| Controlled stop (closing the app, ending a CLI run, changing an output) | Sends all-zero frames for 0.5 s (blackout), disconnects, releases the device. The window waits for this before closing. `StopAsync` gives up after 3 s and logs it if a driver hangs |
| Crash, power loss, unplugging | Nothing can be sent. Fixtures follow their own DMX-loss setting (see below) |

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
- **Timelines:** a player renders frames and writes them through the engine. The Phase 0 prototype already does this
  via `EngineFrameSink`. Merging several sources (priorities, HTP/LTP) belongs inside `ShowEngine`, which is the
  single place values are combined; today the most recent write wins.
- **More universes:** already universe-aware throughout (model, engine, outputs, settings, UI pickers). Phase 1 is
  tested with one; nothing assumes only one.
- **More output protocols:** implement `IDmxOutputDevice`, add a config record and a case in `OutputManager.CreateDevice`.
- **Show format:** `Project` is versioned (currently 2). Loaders migrate older versions (version 1 output assignments
  move to machine settings) and refuse newer ones with a clear message.

## Frozen prototype (timelines and audio)

Before Phase 1 there was a timeline/audio prototype: `Scene`, `ChannelTrack`, `AudioTrack`, `SceneRenderer`,
`SceneRunner` and the ffplay audio player. Timelines are out of scope for Phase 1, so it is frozen but kept
working: shows still store scenes, and `ghostshow run` plays them, now through the engine and output services
(`EngineFrameSink`) rather than straight to hardware. It will be revisited when timelines are designed properly.
