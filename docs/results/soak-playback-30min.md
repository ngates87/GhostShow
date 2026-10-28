# Playback soak test: 30 minutes

- Date: 2026-10-27 15:13
- Machine: teleetra, Pop!_OS 24.04 LTS, X64, 20 logical CPUs
- Runtime: .NET 10.0.13
- Show: 20 RGBW fixtures; 60 s looping timeline with 2,000 lighting events, 4 sounds and 12 markers per loop; audio on the null device; DMX to the simulator
- Live edits: 897 (one lighting point moved + recompile + swap every 2 s)

| Measure | Result |
|---|---|
| Playback update interval, worst window p95 / max | 10.1 / 64.3 ms (target 10 ms) |
| Longest single update (evaluate + audio + markers) | 7.96 ms |
| Show clock vs wall time, worst difference | 0.14 ms |
| Markers reported / expected | 360 / 360 |
| Sounds started / expected | 120 / 120 (failed: 0) |
| Playback update errors | 0 |
| DMX frames | 72,016 (40.0/s), interval p95 25.1 / max 65.4 ms, errors 0 |
| Managed heap after full GC, start → end | 1.3 → 1.4 MB |
| Working set, start → end | 64 → 83 MB |
| GCs gen0 / gen1 / gen2 | 720 / 658 / 635 |
| Stopped cleanly | yes |

Per minute (cumulative):

| Minute | Markers / expected | Sounds / expected | Update p95 / max | Longest update | Clock drift | DMX frames/s | Heap | Working set |
|---|---|---|---|---|---|---|---|---|
| 1 | 12 / 12 | 4 / 4 | 10.1 / 17.9 ms | 7.96 ms | 0.14 ms | 40.0 | 5.5 MB | 72 MB |
| 2 | 24 / 24 | 8 / 8 | 10.1 / 46.6 ms | 7.96 ms | 0.14 ms | 40.0 | 3.6 MB | 78 MB |
| 3 | 36 / 36 | 12 / 12 | 10.1 / 46.6 ms | 7.96 ms | 0.14 ms | 40.0 | 8.2 MB | 81 MB |
| 4 | 48 / 48 | 16 / 16 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 5.4 MB | 82 MB |
| 5 | 60 / 60 | 20 / 20 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.6 MB | 83 MB |
| 6 | 72 / 72 | 24 / 24 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.2 MB | 83 MB |
| 7 | 84 / 84 | 28 / 28 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.6 MB | 83 MB |
| 8 | 96 / 96 | 32 / 32 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 5.4 MB | 84 MB |
| 9 | 108 / 108 | 36 / 36 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.6 MB | 83 MB |
| 10 | 120 / 120 | 40 / 40 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.2 MB | 84 MB |
| 11 | 132 / 132 | 44 / 44 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 4.0 MB | 82 MB |
| 12 | 144 / 144 | 48 / 48 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.2 MB | 84 MB |
| 13 | 156 / 156 | 52 / 52 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 5.3 MB | 84 MB |
| 14 | 168 / 168 | 56 / 56 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 5.4 MB | 84 MB |
| 15 | 180 / 180 | 60 / 60 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.6 MB | 90 MB |
| 16 | 192 / 192 | 65 / 65 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.4 MB | 85 MB |
| 17 | 204 / 204 | 69 / 69 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.4 MB | 86 MB |
| 18 | 216 / 216 | 73 / 73 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 5.7 MB | 86 MB |
| 19 | 228 / 228 | 77 / 77 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 5.4 MB | 86 MB |
| 20 | 240 / 240 | 81 / 81 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.4 MB | 86 MB |
| 21 | 252 / 252 | 85 / 85 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.8 MB | 86 MB |
| 22 | 264 / 264 | 89 / 89 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 5.8 MB | 86 MB |
| 23 | 276 / 276 | 93 / 93 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.8 MB | 85 MB |
| 24 | 288 / 288 | 97 / 97 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.4 MB | 86 MB |
| 25 | 300 / 300 | 101 / 101 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 8.4 MB | 86 MB |
| 26 | 312 / 312 | 105 / 105 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.8 MB | 85 MB |
| 27 | 325 / 325 | 109 / 109 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.8 MB | 86 MB |
| 28 | 337 / 337 | 113 / 113 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.8 MB | 86 MB |
| 29 | 349 / 349 | 117 / 117 | 10.1 / 64.3 ms | 7.96 ms | 0.14 ms | 40.0 | 3.8 MB | 86 MB |
