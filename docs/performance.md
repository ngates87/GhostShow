# Performance

All measurements on the reference development machine, 2026-10-26:

- Intel Core i7-12700H (20 logical CPUs), 16 GB RAM
- Pop!_OS 24.04 LTS, Linux 7.2.6, Wayland
- .NET 10.0.13 (SDK 10.0.113), Release builds
- DMX hardware: FTDI FT232R Open DMX adapter (S/N AB0PFDWZ), `ftdi_sio` driver

The two soak tests and the UI load test ran **at the same time**, so each saw the others' CPU load. That makes the
results conservative.

## Results against the targets

| Target | Result | Met? |
|---|---|---|
| UI input response < 50 ms at p95 | UI thread wait p95 12–15 ms with all 512 channels changing ~1,000×/s (19 ms in the first 30 s while 1,441 profiles loaded) | yes |
| No routine UI blocking from disk, network or hardware | Profile library, show/settings files and adapter scans run off the UI thread; outputs have their own threads. Worst UI wait after startup 25 ms | yes |
| DMX output stable under sustained updates | 30 min, ~900 commands/s + a save every 5 s: Open DMX 38.6 frames/s, p99 interval 28.0 ms, 0 errors; simulator 40.0 frames/s, max interval 38.3 ms | yes |
| No unbounded backlog of obsolete values | No queue exists: values are overwritten in place and each frame takes the latest. 50,000-update burst → frames only at the frame rate (unit test) | yes |
| No unexplained memory growth | Managed heap after full GC 0.4 → 0.8 MB over 30 min; working set flat at 78–80 MB from minute 6 to 30 | yes |
| Hardware failure reported without crash or freeze | Missing, failing and returning devices covered by `DmxOutputServiceTests` with a scripted device. **Not yet exercised by physically unplugging the adapter** | partly |
| Saving doesn't block output | 360 saves during each soak, no effect on frame timing; JSON is built on the caller's thread, the write is async and atomic | yes |
| Shutdown within a defined timeout | Each output sends 0.5 s of blackout, then disconnects; `StopAsync` gives up after 3 s and logs it if a driver hangs (tested). Every soak run stopped cleanly | yes |

## Frame path benchmarks

BenchmarkDotNet 0.15.8, `dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*'`.

| Operation | Mean | Allocated |
|---|---|---|
| Engine → frame snapshot (output thread, per frame) | 29.6 ns | 0 |
| Frame → Enttec Pro packet | 14.4 ns | 0 |
| Frame → Art-Net packet | 15.5 ns | 0 |
| `SetChannel` (raw fader) | 17.1 ns | 0 |
| `SetFixtureControl` (100 fixtures patched) | 26.4 ns | 0 |
| Validate and patch 10 fixtures | 1.8 µs | 5.6 KB |
| Validate and patch 100 fixtures | 16.6 µs | 46.6 KB |

The software side of a frame costs well under a microsecond, so frame rate and latency are set by the hardware
(wire time) and the frame interval, not by GhostShow's code. Patch validation runs on every setup edit; 17 µs for
100 fixtures is far below anything a user would notice.

## Soak tests

`ghostshow soak --minutes 30 [--opendmx /dev/ttyUSB0 --dark]`. Full reports, with per-minute rows:
[results/soak-opendmx-30min.md](results/soak-opendmx-30min.md),
[results/soak-simulator-30min.md](results/soak-simulator-30min.md).

**Load:** 20 patched RGBW fixtures, ~900 commands/s (random fixture controls and raw channels, more than any
operator and comparable to a busy timeline), and the show saved every 5 s. The Open DMX run used `--dark`: the same
load, but every value is 0 so connected fixtures stay off. The frame path does identical work either way.

**Measured:** every frame interval over the whole run (a 0.1 ms histogram, not a sampled window). Command-to-device
latency: once a second a marker value goes to channel 512, timed until a frame carrying it has been sent. Memory,
GCs and errors are also recorded.

| | Open DMX (FT232R) | Simulator |
|---|---|---|
| Frames | 69,427 (38.6/s) | 72,011 (40.0/s) |
| Frame interval p50 / p95 / p99 / max | 26.0 / 27.1 / 28.0 / 52.1 ms | 25.1 / 25.1 / 25.1 / 38.3 ms |
| Intervals over 50 ms | 7 of 69,426 | 0 |
| Command → device latency p50 / p95 / max | 39.0 / 50.6 / 125.3 ms | 12.4 / 23.8 / 25.0 ms |
| Device errors | 0 | 0 |
| Heap after full GC, start → end | 0.4 → 0.8 MB | 0.4 → 0.7 MB |
| Working set, start → end | 46 → 80 MB (flat from minute 6) | 45 → 73 MB |

**Reading the Open DMX numbers.** A frame takes ~23 ms on the wire plus ~3 ms of break, baud switching and USB
round trips, so frames are 26 ms apart rather than the 25 ms target. Latency is measured until the *whole* frame
carrying the change has left the adapter: a change waits on average half a frame for the next frame to start (~13 ms),
then the frame takes ~26 ms to send, which gives the 39 ms median. In practice the fixture sees the change earlier,
because its channel goes out partway through the frame; channel 512 is the worst case. The 125 ms maximum happened
in the first minute (startup) and never again. The 7 intervals over 50 ms (max 52 ms) are about two missed frames
each, under 0.01% of frames. That's invisible on lights, but it shows the OS can briefly delay a userspace
serial writer.

## UI responsiveness

`GHOSTSHOW_UI_LOAD_TEST=1 dotnet run -c Release --project GhostShow.App`. The real app window opens on the DMX
monitor while a worker changes all 512 channels about 1,000 times a second (every monitor cell changes every
frame: the worst case for redraws). A probe posts an input-priority no-op to the UI thread every 100 ms and logs how
long it waited, every 30 s. Ran 3.5 minutes on the simulator, concurrently with both soaks.

| Window | p50 | p95 | max |
|---|---|---|---|
| First 30 s (fixture library loading) | 0.2 ms | 19.0 ms | 61.8 ms |
| Each later 60 s window | 0.1–0.2 ms | 12.1–15.3 ms | 23.7–25.3 ms |

The Diagnostics screen shows the same probe live during normal use.

## Known limitations

- **Only Linux measured.** Windows' default timer resolution (~15.6 ms) may make the output thread's sleep coarser
  and frame timing less regular; measure with `ghostshow soak` on each new platform before a show.
- **Open DMX frame timing depends on the OS.** It's a userspace serial writer. Occasional ~50 ms gaps were seen
  under heavy load; heavier load (USB hubs, many devices, a busy machine) could make them longer.
- **Open DMX break timing hasn't been checked with a scope or DMX tester** (see [hardware.md](hardware.md)).
- **Physical unplug/replug** of the adapter hasn't been tested yet; the same code paths are covered by tests with a
  scripted failing device.
- Only one universe has been run on real hardware (only one adapter on hand).

## Re-running

    dotnet test                                                        # includes rapid-update, concurrency, shutdown tests
    dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*'
    dotnet run -c Release --project GhostShow.Cli -- soak --minutes 30                         # simulator
    dotnet run -c Release --project GhostShow.Cli -- soak --minutes 30 --opendmx /dev/ttyUSB0 --dark
    GHOSTSHOW_UI_LOAD_TEST=1 dotnet run -c Release --project GhostShow.App                     # watch the log
