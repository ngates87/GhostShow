# GhostShow

Open-source, cross-platform show control for haunted houses. Phase 1: DMX output, fixtures, manual control,
configuration, diagnostics. Built with C# / .NET 10 (LTS) and Avalonia.

## Build, test, run

Requires the .NET 10 SDK.

    dotnet build
    dotnet test
    dotnet run --project GhostShow.App                   # the desktop app

Command line:

    dotnet run --project GhostShow.Cli -- detect         # find USB DMX adapters and their serial numbers
    dotnet run --project GhostShow.Cli -- soak --minutes 30 [--opendmx /dev/ttyUSB0 --dark]
    dotnet run --project GhostShow.Cli -- demo demo.json # sample show with a timeline scene
    dotnet run --project GhostShow.Cli -- run demo.json "Lightning Room" --dry --silent

Benchmarks: `dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*'`

## The app

Three sections, each with its own screens:

| Section | Screen | For |
|---|---|---|
| Show | Overview | Show status, warnings, output health, shortcuts to every screen |
| | Diagnostics | Frame rate and timing per output, UI responsiveness, memory, recent events |
| Lighting | DMX setup | The show's universes, and which device (simulator or USB adapter) outputs each on this computer |
| | Fixtures | A heat map of the universe above the fixture list, and an address map for re-addressing (drag a fixture). **Add fixtures…** is a two-step wizard: choose the type (grid by manufacturer or type, or search), then click green openings on the map to add one per click, or quick-add several in a row, and press Done |
| | Control | Faders for a fixture's logical controls (intensity, colours) and every channel |
| | Channels | Raw faders, as many as fit the window, paged through all 512 channels |
| | Monitor | All 512 channels as actually sent to the device; changes highlighted |
| Audio | | Placeholder: audio comes in a later phase |

**Blackout** (Ctrl+B) is always on the toolbar. Closing the app blacks out every output before it exits.

## Hardware

Phase 1 targets **Open DMX** adapters: Enttec Open DMX USB and most cheap FTDI-based USB-DMX cables. Enttec Pro
compatibles and Art-Net are implemented but untested on real hardware. Setup, limitations and troubleshooting:
[docs/hardware.md](docs/hardware.md). On Linux, you need to be in the `dialout` group.

Fixture profiles: built-in generics, the Open Fixture Library (~650 fixtures; download it from the Fixtures screen
or with `ghostshow library download`), the QLC+ library (~1,400) if QLC+ happens to be installed, and your own. See [docs/fixture-profiles.md](docs/fixture-profiles.md).

## Documentation

- [Architecture](docs/architecture.md): structure, UI technology choice, threading, failure and blackout policy, extension points
- [Hardware](docs/hardware.md): Open DMX setup, testing procedure, troubleshooting
- [Fixture profiles](docs/fixture-profiles.md): profile format, QLC+ import, patch validation
- [Performance](docs/performance.md): targets, method, measured results, known limitations
