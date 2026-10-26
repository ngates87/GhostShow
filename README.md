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

| Screen | For |
|---|---|
| Console | Show status, outputs and their health, warnings, blackout |
| Outputs | The show's universes, and which device (simulator or USB adapter) outputs each on this computer |
| Fixtures | Add and patch fixtures. Pick profiles from a grid by manufacturer or by fixture type, or search; address conflicts and other problems show as you type |
| Control | Faders for a fixture's logical controls (intensity, colours) and every channel |
| Channels | Raw faders, as many as fit the window, paged through all 512 channels |
| Monitor | All 512 channels as actually sent to the device; changes highlighted |
| Diagnostics | Frame rate and timing per output, UI responsiveness, memory, recent events |

**Blackout** (Ctrl+B) is always on the toolbar. Closing the app blacks out every output before it exits.

## Hardware

Phase 1 targets **Open DMX** adapters: Enttec Open DMX USB and most cheap FTDI-based USB-DMX cables. Enttec Pro
compatibles and Art-Net are implemented but untested on real hardware. Setup, limitations and troubleshooting:
[docs/hardware.md](docs/hardware.md). On Linux, you need to be in the `dialout` group.

Fixture profiles: built-in generics, plus the QLC+ fixture library (~1,400 fixtures) if QLC+ is installed, plus
your own. See [docs/fixture-profiles.md](docs/fixture-profiles.md).

## Documentation

- [Architecture](docs/architecture.md): structure, UI technology choice, threading, failure and blackout policy, extension points
- [Hardware](docs/hardware.md): Open DMX setup, testing procedure, troubleshooting
- [Fixture profiles](docs/fixture-profiles.md): profile format, QLC+ import, patch validation
- [Performance](docs/performance.md): targets, method, measured results, known limitations
