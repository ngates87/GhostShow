# Performance

All measurements on the reference development machine, 2026-10-26 (Phase 1) and 2026-10-27 (Phase 2: timelines,
playback, audio):

- Intel Core i7-12700H (20 logical CPUs), 16 GB RAM
- Pop!_OS 24.04 LTS, Linux 7.2.6, Wayland
- .NET 10.0.13 (SDK 10.0.113), Release builds
- DMX hardware: FTDI FT232R Open DMX adapter (S/N AB0PFDWZ), `ftdi_sio` driver

The two soak tests and the UI load test ran **at the same time**, so each saw the others' CPU load. That makes the
results conservative.

## Results against the targets

| Target | Result | Met? |
|---|---|---|
| UI input response < 50 ms at p95 | UI thread wait p95 7–8 ms, max 29 ms, with all 512 channels changing ~1,000×/s (current UI, 26 Oct). Switching screens: ~20 ms on repeat visits | yes |
| No routine UI blocking from disk, network or hardware | Profile library, show/settings files, adapter scans and the library download run off the UI thread; outputs have their own threads. Worst UI wait, including startup, 29 ms | yes |
| DMX output stable under sustained updates | 30 min, ~900 commands/s + a save every 5 s: Open DMX 38.6 frames/s, p99 interval 28.0 ms, 0 errors; simulator 40.0 frames/s, max interval 38.3 ms | yes |
| No unbounded backlog of obsolete values | No queue exists: values are overwritten in place and each frame takes the latest. 50,000-update burst → frames only at the frame rate (unit test) | yes |
| No unexplained memory growth | Managed heap after full GC 0.4 → 0.8 MB over 30 min; working set flat at 78–80 MB from minute 6 to 30 | yes |
| Hardware failure reported without crash or freeze | Missing, failing and returning devices covered by `DmxOutputServiceTests` with a scripted device. **Not yet exercised by physically unplugging the adapter** | partly |
| Saving doesn't block output | 360 saves during each soak, no effect on frame timing; JSON is built on the caller's thread, the write is async and atomic | yes |
| Timeline playback: every update far below its 10 ms interval | Evaluating 10,000 events: 45 µs per update, no allocation. Longest single update in the 30 min playback soak: 6.7 ms (opening a sound file) | yes |
| Show clock doesn't drift | Position derives from a monotonic clock, never accumulated; worst difference from wall time over 30 min of playback with 899 live edits: 0.16 ms | yes |
| Sounds happen exactly once | 30 min soak: 120/120 sound starts (and 360/360 markers, before markers were removed) | yes |
| Editing while playing stays responsive | Recompile on every edit: 52 µs (100 events), 10 ms (10,000). UI thread wait while playing 2,000 events and editing every 2 s: p95 0.2–0.6 ms, max 46.5 ms after startup | yes |
| Large shows open and save quickly | 10,000 events (8.7 MB of JSON): open 84 ms, save 30 ms | yes |
| Shutdown within a defined timeout | Each output sends 0.5 s of blackout, then disconnects; `StopAsync` gives up after 3 s and logs it if a driver hangs (tested). Every soak run stopped cleanly | yes |

## Frame path benchmarks

BenchmarkDotNet 0.15.8, `dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*'`. Re-measured
2026-10-27, after fixing a setup error: the benchmarks patched fixtures into universe 0 after universes became
1-based, so the fixture command measured its error path. Setup now throws if the patch has problems.

| Operation | Mean | Allocated |
|---|---|---|
| Engine → frame snapshot (output thread, per frame) | 25.1 ns | 0 |
| Frame → Enttec Pro packet | 14.1 ns | 0 |
| Frame → Art-Net packet | 14.4 ns | 0 |
| `SetChannel` (raw fader) | 17.3 ns | 0 |
| `SetFixtureControl` (100 fixtures patched) | 26.2 ns | 0 |
| Validate and patch 10 fixtures | 1.7 µs | 5.4 KB |
| Validate and patch 100 fixtures | 17.0 µs | 45.5 KB |

The software side of a frame costs well under a microsecond, so frame rate and latency are set by the hardware
(wire time) and the frame interval, not by GhostShow's code. Patch validation runs on every setup edit; 17 µs for
100 fixtures is far below anything a user would notice.

## Timeline benchmarks

100 RGBW fixtures; lighting events spread over 10 minutes on 4 tracks, each setting 1–5 controls with a 0–5 s fade.

| Operation | 100 events | 10,000 events | Allocated |
|---|---|---|---|
| Evaluate levels at the next position (per playback update) | 0.41 µs | 44.5 µs | 0 |
| Evaluate + hand to the engine (per playback update) | 0.47 µs | 53.8 µs | 0 |
| Compile the timeline (on every edit) | 52 µs | 10.2 ms | 185 KB / 12.9 MB |

| Show file with 10,000 events (8.7 MB of JSON) | Mean | Allocated |
|---|---|---|
| Save (serialize + atomic write) | 30.0 ms | 17.7 MB |
| Open (parse + migrate + validate) | 84.0 ms | 21.4 MB |

A playback update for a very large show costs about 0.5% of its 10 ms interval. Recompiling runs on the UI thread
after each edit; at 10,000 events it takes 10 ms, a noticeable but acceptable cost per edit. A real haunt timeline
is more likely to have hundreds of events (microseconds). Evaluation time varies with how many fades are running at
the position, hence the large spread at 10,000 events.

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

## Playback soak test

`ghostshow soak-playback --minutes 30`. Full report: [results/soak-playback-30min.md](results/soak-playback-30min.md).

**Load:** 20 RGBW fixtures; a 60 s looping timeline with 2,000 lighting events (0–5 s fades), 4 sounds per loop
(WAVs at 44.1 kHz mono and 48 kHz stereo, decoded and mixed on the audio null device) and, in these runs, a
marker every 5 s (markers have since been removed; the test no longer counts them). Every
2 s one event is moved and its fade changed, and the timeline is recompiled and swapped in while playing (899 edits).
DMX goes to the simulator.

| Measure | Result |
|---|---|
| Playback update interval, worst 2.5 s window p95 / max | 10.0 / 31.8 ms (target 10 ms) |
| Longest single update | 6.7 ms |
| Show clock vs wall time, worst difference | 0.16 ms |
| Markers reported / expected | 360 / 360 |
| Sounds started / expected | 120 / 120, none failed |
| Playback errors | 0 |
| DMX frames | 40.0/s, interval p95 25.1 / max 49.6 ms, 0 errors |
| Heap after full GC, start → end | 1.2 → 1.5 MB |

**Re-run with lighting as blocks** (2026-10-27 15:13, the report linked above; the timeline's 2,000 lighting events
converted to blocks, a point moved every 2 s): 360/360 markers, 120/120 sound starts, clock within 0.14 ms, longest
update 8.0 ms, heap 1.3 → 1.4 MB. The worst update interval (64 ms) and DMX frame interval (65 ms) happened while
builds and test runs were using the same machine; p95 stayed at 10.1 ms and 25.1 ms.

**What the first run found.** The first 30-minute run reported 359 of 360 markers and 121 of 120 sound starts.
Both were races with live edits: swapping in an edited timeline was treated like a seek, so a marker crossed on that
update wasn't reported, and a sound whose file had just finished decoding (a few ms before its computed end) was
started again. Edits are now handled separately from jumps in time: markers are still reported, and only sounds
whose event changed are moved (which also stops every edit from making playing sounds skip slightly). Tests
reproduce both cases; the run above is after the fix.

The longest updates (5–7 ms) are opening a sound file, which happens on the playback thread when a sound is due;
that is still within the 10 ms interval, and the next update is on time because position comes from the clock.

## UI responsiveness

`GHOSTSHOW_UI_LOAD_TEST=1 dotnet run -c Release --project GhostShow.App`. The real app window opens on the DMX
monitor while a worker changes all 512 channels about 1,000 times a second (every monitor cell changes every
frame: the worst case for redraws). A probe posts an input-priority no-op to the UI thread every 100 ms and logs how
long it waited, every 30 s. 3.5 minutes on the simulator.

| Run | First 30 s (startup, library loading) | Each later 60 s window |
|---|---|---|
| Phase 1 UI, before the Show/Lighting/Audio layout (run alongside both soaks) | p95 19.0 ms · max 61.8 ms | p95 12–15 ms · max 24–25 ms |
| Current UI, first run (on its own) | p95 22.3 ms · max **313.9 ms** | p95 13 ms · max 23–25 ms |
| Current UI after the fixes below (on its own) | p95 8.4 ms · max 28.9 ms | p95 7.2–8.0 ms · max 20 ms |

**What the 314 ms was, and the fixes.** Timing each step showed that applying the loaded fixture library takes
about 6 ms, so that wasn't it. Switching screens was: the tab control rebuilt a screen's controls on every visit,
which cost 220 ms for the Monitor (512 templated cells) every time, and 650 ms the first time.

- **Screens stay built**, and the tabs only show or hide them.
- **The Monitor is drawn directly** as one control.
- **The profile browser's ~150 tiles are created only while it's open.**

Screen switch times in the headless renderer, before → after:

| Screen | First visit | Repeat visit |
|---|---|---|
| Monitor | 653 → 259 ms | 222 → 23 ms |
| Fixtures | 206 → 138 ms | → 22 ms |
| Channels | 279 → 344 ms | → 16 ms |

The first visit per session still costs 140–340 ms in that renderer, for first-time layout and code. The real
window's worst startup wait was 29 ms. The Diagnostics screen shows the same probe live during normal use.

**During playback and editing** (`GHOSTSHOW_UI_LOAD_TEST=timeline`): a 2,000-event looping timeline plays on the
Timeline screen (playhead moving, lanes scrolling to follow it) while an event is moved through the editor every 2 s
(undoable edit, recompile and swap, all on the UI thread). 3.5 minutes, run while the playback soak was running.

| Window | UI thread wait p50 / p95 / max |
|---|---|
| First 30 s (startup, building the test show, first layout of the Timeline screen) | 0.1 / 0.2 / 91.8 ms |
| Each later 60 s window | 0.1 / 0.2–0.6 / 16–46 ms |

**Expanded lighting tracks** (envelopes drawn inside clips, every channel overlaid). Whole-window frames in the
headless renderer (CPU Skia, 1400×820) after a forced redraw of the timeline, median of 15; the window without the
timeline's drawing costs about 25 ms of each:

| Show | Frame |
|---|---|
| 4 tracks × 12 channels × 30 clips (3 handles each, Linear and Spline), all expanded, the whole show in view | 71 ms |
| The same, zoomed in (70 px/s) | 47 ms |
| The load test's 2,000 events as 4 tracks of ~196 channels (785 envelopes, 9,876 handles), all expanded | 227 ms |
| The same, collapsed | 25 ms |

The timeline's own drawing code takes 9–18 ms of that; the rest is rasterising the paths. To keep it down, each
channel's handles are one shape, colours' pens and brushes are made once, nothing outside the view is drawn in
detail, and handles that would overlap aren't drawn (selected ones always are). The timeline only redraws on an
edit, a scroll or a zoom, not as the playhead moves. Not measured with the real window's GPU renderer.

## Known limitations

- **Very wide lighting tracks are slow to redraw when expanded** (a quarter of a second for ~200 channels on one
  track, in the headless renderer): every channel is drawn over every clip. Collapsed, they cost nothing extra.

- **Only Linux measured.** Windows' default timer resolution (~15.6 ms) may make the output thread's sleep coarser
  and frame timing less regular; measure with `ghostshow soak` on each new platform before a show.
- **Open DMX frame timing depends on the OS.** It's a userspace serial writer. Occasional ~50 ms gaps were seen
  under heavy load; heavier load (USB hubs, many devices, a busy machine) could make them longer.
- **Open DMX break timing hasn't been checked with a scope or DMX tester** (see [hardware.md](hardware.md)).
- **Physical unplug/replug** of the adapter hasn't been tested yet; the same code paths are covered by tests with a
  scripted failing device.
- Only one universe has been run on real hardware (only one adapter on hand).
- **Audio measured on the null device** (decoding, mixing and timing run for real, nothing is heard). A real-device
  run of the demo (`ghostshow run`) played correctly, but there's no long real-device soak yet, and no measurement
  of output latency from "sound due" to sound at the speaker.
- **Recompiling allocates** (12.9 MB at 10,000 events, much of it in large arrays), so frequent edits to a huge show
  cause full garbage collections. No effect on playback timing was measured, but it's the first thing to optimise
  if very large shows appear.

## Re-running

    dotnet test                                                        # includes rapid-update, concurrency, shutdown tests
    dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*'
    dotnet run -c Release --project GhostShow.Cli -- soak --minutes 30                         # simulator
    dotnet run -c Release --project GhostShow.Cli -- soak --minutes 30 --opendmx /dev/ttyUSB0 --dark
    dotnet run -c Release --project GhostShow.Cli -- soak-playback --minutes 30
    GHOSTSHOW_UI_LOAD_TEST=1 dotnet run -c Release --project GhostShow.App                     # watch the log
    GHOSTSHOW_UI_LOAD_TEST=timeline dotnet run -c Release --project GhostShow.App              # replaces the open show

The UI load tests use this computer's output assignments. To keep real fixtures out of it, point the app at an
empty settings folder first (Linux: `XDG_CONFIG_HOME=/tmp/gs-test`), which leaves every universe on the simulator.
