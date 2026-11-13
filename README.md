# GhostShow

Open-source, cross-platform show control for haunted houses: DMX lighting (fixtures, patching, manual control) and
audio, played together from timelines. Built with C# / .NET 10 (LTS), Avalonia and SoundFlow.

## Folders

| Folder | What's in it |
|---|---|
| `apps/` | The programs: `GhostShow.App` (desktop), `GhostShow.Browser` (the same app for browsers), `GhostShow.Cli` (command line) |
| `libs/` | The parts they're built from: `GhostShow.Core`, `GhostShow.Dmx`, `GhostShow.Audio`, `GhostShow.Web` |
| `tests/` | `GhostShow.Tests`, `GhostShow.App.Tests`, `GhostShow.Benchmarks` |
| `installer/` | The Windows installer |
| `docs/` | Documentation |

## Build, test, run

Requires the .NET 10 SDK. Node.js 20.19 or newer builds the web remote's page (without it the build still works,
and the page says it wasn't built).

    dotnet run dev.cs                                         # build everything, then start the desktop app
    dotnet build
    dotnet test
    dotnet run --project apps/GhostShow.App                   # the desktop app

`dev.cs` works the same on Windows and Linux, and F5 in VS Code uses it too (with Microsoft's C# extension). Unlike a
plain `dotnet build`, it also builds the full app for browsers (`/app/`), which needs .NET's WebAssembly tools. It only
rebuilds that when it's out of date, which takes about 5 minutes; `dotnet run dev.cs -- --skip-browser` leaves it as it is.

Command line:

    dotnet run --project apps/GhostShow.Cli -- detect         # find USB DMX adapters and their serial numbers
    dotnet run --project apps/GhostShow.Cli -- soak --minutes 30 [--opendmx /dev/ttyUSB0 --dark]
    dotnet run --project apps/GhostShow.Cli -- soak-playback --minutes 30
    dotnet run --project apps/GhostShow.Cli -- demo Demo      # sample show folder (put a thunder.wav in Demo/audio/)
    dotnet run --project apps/GhostShow.Cli -- run Demo "Rooms/Lightning Room" --dry --silent

Benchmarks: `dotnet run -c Release --project tests/GhostShow.Benchmarks -- --filter '*'`

Windows installer: `dotnet run installer/build.cs` builds the current source into `artifacts/GhostShow-Setup-0.1.0.exe`.
An optional version can be supplied, for example `dotnet run installer/build.cs -- 1.2.0`. See
[docs/windows-installer.md](docs/windows-installer.md).

## The app

The app opens straight into the show you used last (File → Close Show, or no recent show, gives the **Home** page:
new show, open a show, recent shows). The **menu bar** has File (New, Open, Open Recent, Save, Close Show…), Edit,
View, and Playback; below it are the transport and Blackout. With a show open, the window has these tabs:

| Tab | Screen | For |
|---|---|---|
| Timeline | | Several timelines can play at once, with priorities and overrides that pause or keep the ambient and release back to it. A tree of the show's timelines in folders, open timelines as tabs, and the editor: lighting tracks with Level Control clips and envelopes (and an Envelope Editor window), audio tracks; drag, fade, copy/paste, undo/redo; edits apply while playing ([docs/timeline.md](docs/timeline.md)). Beside it, the show's **buttons**: run or stop timelines (a new copy each press, as VenueMagic does), stop all, blackout; each can have a key ([docs/plan-buttons.md](docs/plan-buttons.md)) |
| Lighting | DMX setup | The show's universes, and which device (simulator or USB adapter) outputs each on this computer |
| | Fixtures | A heat map of the universe above the fixture list, and an address map for re-addressing (drag a fixture). **Add fixtures…** is a two-step wizard: choose the type (grid by manufacturer or type, or search), then click green openings on the map to add one per click, or quick-add several in a row, and press Done |
| | Control | Faders for a fixture's logical controls (intensity, colours) and every channel |
| | Channels | Raw faders, as many as fit the window, paged through all 512 channels |
| | Monitor | All 512 channels as actually sent to the device; changes highlighted |
| Audio | | Default output device and master volume for this computer, a test sound, the outputs audio tracks are routed to (each audio track picks its output device in its properties panel, as in VenueMagic), and every sound file the show uses |
| Inputs | Serial input and Input monitor | The serial port a sensor box or prop controller sends commands on, and its speed (this computer's setting). Below it, every input as it arrives (keys and serial commands; Stream Decks later) and what it did: the button it pressed, or why not (no button uses it, triggers off, re-arming). It keeps listening on every screen |
| Diagnostics | | Frame rate and timing per output, playback timing, audio status, UI responsiveness, memory, recent events |

Problems that need attention (patch errors, an output that keeps failing) show as **⚠** on the toolbar.

A show is a **folder**: `show.json` plus one file per timeline in `timelines/`, in the same folders as the tree.
Zip the folder to move a show to another computer.

**Play/Pause, Stop** and **Blackout** (Ctrl+B) are always on the toolbar. Stop releases the lights back to their
manual levels; Blackout also stops playback. Closing the app stops the show and blacks out every output before it
exits.

## Hardware

Phase 1 targets **Open DMX** adapters: Enttec Open DMX USB and most cheap FTDI-based USB-DMX cables. Enttec Pro
compatibles and Art-Net are implemented but untested on real hardware. Setup, limitations and troubleshooting:
[docs/hardware.md](docs/hardware.md). On Linux, you need to be in the `dialout` group.

Fixture profiles: built-in generics, the Open Fixture Library (~650 fixtures; download it from the Fixtures screen
or with `ghostshow library download`), the QLC+ library (~1,400) if QLC+ happens to be installed, and your own. See [docs/fixture-profiles.md](docs/fixture-profiles.md).

## Documentation

- [Architecture](docs/architecture.md): structure, UI technology choice, threading, failure and blackout policy, extension points
- [Timelines and playback](docs/timeline.md): model, playback rules, transport, editing while playing, audio, the Timeline screen
- [Remote access](docs/remote.md): running the show from an iPad, laptop or phone on the show's network
- [Hardware](docs/hardware.md): Open DMX setup, testing procedure, troubleshooting
- [Windows installer](docs/windows-installer.md): building it, what it does on the show computer, what to expect
- [Fixture profiles](docs/fixture-profiles.md): profile format, QLC+ import, patch validation
- [Performance](docs/performance.md): targets, method, measured results, known limitations
