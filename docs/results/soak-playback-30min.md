# Playback soak test: 30 minutes

- Date: 2026-10-27 10:38
- Machine: teleetra, Pop!_OS 24.04 LTS, X64, 20 logical CPUs
- Runtime: .NET 10.0.13
- Show: 20 RGBW fixtures; 60 s looping timeline with 2,000 lighting events, 4 sounds and 12 markers per loop; audio on the null device; DMX to the simulator
- Live edits: 899 (one event moved + recompile + swap every 2 s)

| Measure | Result |
|---|---|
| Playback update interval, worst window p95 / max | 10.0 / 31.8 ms (target 10 ms) |
| Longest single update (evaluate + audio + markers) | 6.68 ms |
| Show clock vs wall time, worst difference | 0.16 ms |
| Markers reported / expected | 360 / 360 |
| Sounds started / expected | 120 / 120 (failed: 0) |
| Playback update errors | 0 |
| DMX frames | 72,035 (40.0/s), interval p95 25.1 / max 49.6 ms, errors 0 |
| Managed heap after full GC, start → end | 1.2 → 1.5 MB |
| Working set, start → end | 60 → 79 MB |
| GCs gen0 / gen1 / gen2 | 451 / 451 / 451 |
| Stopped cleanly | yes |

Per minute (cumulative):

| Minute | Markers / expected | Sounds / expected | Update p95 / max | Longest update | Clock drift | DMX frames/s | Heap | Working set |
|---|---|---|---|---|---|---|---|---|
| 1 | 12 / 12 | 4 / 4 | 10.0 / 16.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 70 MB |
| 2 | 24 / 24 | 8 / 8 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 73 MB |
| 3 | 36 / 36 | 12 / 12 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 77 MB |
| 4 | 48 / 48 | 16 / 16 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 78 MB |
| 5 | 60 / 60 | 20 / 20 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 6 | 72 / 72 | 24 / 24 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 7 | 84 / 84 | 28 / 28 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 8 | 96 / 96 | 32 / 32 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 9 | 108 / 108 | 36 / 36 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 10 | 120 / 120 | 40 / 40 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 11 | 132 / 132 | 44 / 44 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 12 | 144 / 144 | 48 / 48 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 13 | 156 / 156 | 52 / 52 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 14 | 168 / 168 | 56 / 56 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 15 | 180 / 180 | 60 / 60 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 16 | 192 / 192 | 64 / 64 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 17 | 204 / 204 | 68 / 68 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 18 | 216 / 216 | 72 / 72 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 19 | 228 / 228 | 76 / 76 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 20 | 240 / 240 | 80 / 80 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 79 MB |
| 21 | 252 / 252 | 84 / 84 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 80 MB |
| 22 | 264 / 264 | 88 / 88 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
| 23 | 276 / 276 | 92 / 92 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
| 24 | 288 / 288 | 96 / 96 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
| 25 | 300 / 300 | 100 / 100 | 10.0 / 20.4 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
| 26 | 312 / 312 | 104 / 104 | 10.0 / 30.5 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
| 27 | 324 / 324 | 108 / 108 | 10.0 / 30.5 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
| 28 | 336 / 336 | 112 / 112 | 10.0 / 31.8 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
| 29 | 348 / 348 | 116 / 116 | 10.0 / 31.8 ms | 6.68 ms | 0.16 ms | 40.0 | 6.1 MB | 81 MB |
