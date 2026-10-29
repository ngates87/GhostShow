# GhostShow

Open-source, cross-platform show control for haunted houses: DMX lighting (fixtures, patching, manual control) and
audio, played together from timelines. Built with C# / .NET 10 (LTS), Avalonia and SoundFlow.

## Build, test, run

Requires the .NET 10 SDK.

    dotnet build
    dotnet test
    dotnet run --project GhostShow.App                   # the desktop app

Command line:

    dotnet run --project GhostShow.Cli -- detect         # find USB DMX adapters and their serial numbers
    dotnet run --project GhostShow.Cli -- soak --minutes 30 [--opendmx /dev/ttyUSB0 --dark]
    dotnet run --project GhostShow.Cli -- soak-playback --minutes 30
    dotnet run --project GhostShow.Cli -- demo Demo      # sample show folder (put a thunder.wav in Demo/audio/)
    dotnet run --project GhostShow.Cli -- run Demo "Rooms/Lightning Room" --dry --silent

Benchmarks: `dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*'`

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
- [Hardware](docs/hardware.md): Open DMX setup, testing procedure, troubleshooting
- [Fixture profiles](docs/fixture-profiles.md): profile format, QLC+ import, patch validation
- [Performance](docs/performance.md): targets, method, measured results, known limitations
