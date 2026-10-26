# Soak test: Simulator, 30 minutes

- Date: 2026-10-26 13:47
- Machine: teleetra, Pop!_OS 24.04 LTS, X64, 20 logical CPUs
- Runtime: .NET 10.0.13
- Device: Simulator: Software output; no hardware
- Load: 20 patched RGBW fixtures; 897 commands/s; show saved every 5 s (360 saves, 0 failed)

| Measure | Result |
|---|---|
| Frames sent | 72,011 (40.0 frames/s) |
| Frame interval p50 / p95 / p99 / max | 25.10 / 25.10 / 25.10 / 38.3 ms |
| Intervals over 50 ms | 0 |
| Command → device latency p50 / p95 / max (1799 samples) | 12.4 / 23.8 / 25.0 ms |
| Device errors | 0 |
| Managed heap after full GC, start → end | 0.4 → 0.7 MB |
| Working set, start → end | 45 → 73 MB |
| GCs gen0 / gen1 / gen2 | 2 / 2 / 2 |
| Stopped cleanly with blackout | yes |

Per minute (cumulative frame statistics):

| Minute | Frames/s | Interval p95 / max | Latency p95 / max | Heap | Working set | Status |
|---|---|---|---|---|---|---|
| 1 | 40.0 | 25.1 / 25.0 ms | 22.9 / 24.9 ms | 1.3 MB | 59 MB | Connected |
| 2 | 40.0 | 25.1 / 25.3 ms | 23.1 / 24.9 ms | 1.6 MB | 61 MB | Connected |
| 3 | 40.0 | 25.1 / 26.5 ms | 23.1 / 24.9 ms | 1.9 MB | 62 MB | Connected |
| 4 | 40.0 | 25.1 / 26.5 ms | 23.7 / 24.9 ms | 2.2 MB | 63 MB | Connected |
| 5 | 40.0 | 25.1 / 38.3 ms | 23.6 / 24.9 ms | 2.5 MB | 63 MB | Connected |
| 6 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 2.8 MB | 64 MB | Connected |
| 7 | 40.0 | 25.1 / 38.3 ms | 23.6 / 24.9 ms | 3.1 MB | 64 MB | Connected |
| 8 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 3.5 MB | 65 MB | Connected |
| 9 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 3.8 MB | 65 MB | Connected |
| 10 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 4.2 MB | 67 MB | Connected |
| 11 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 4.5 MB | 67 MB | Connected |
| 12 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 4.8 MB | 67 MB | Connected |
| 13 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 5.1 MB | 67 MB | Connected |
| 14 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 5.5 MB | 68 MB | Connected |
| 15 | 40.0 | 25.1 / 38.3 ms | 23.5 / 24.9 ms | 5.8 MB | 68 MB | Connected |
| 16 | 40.0 | 25.1 / 38.3 ms | 23.6 / 25.0 ms | 6.2 MB | 69 MB | Connected |
| 17 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 6.5 MB | 69 MB | Connected |
| 18 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 6.8 MB | 69 MB | Connected |
| 19 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 7.1 MB | 70 MB | Connected |
| 20 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 7.4 MB | 70 MB | Connected |
| 21 | 40.0 | 25.1 / 38.3 ms | 23.6 / 25.0 ms | 7.8 MB | 71 MB | Connected |
| 22 | 40.0 | 25.1 / 38.3 ms | 23.6 / 25.0 ms | 8.1 MB | 71 MB | Connected |
| 23 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 8.4 MB | 72 MB | Connected |
| 24 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 8.8 MB | 72 MB | Connected |
| 25 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 9.1 MB | 72 MB | Connected |
| 26 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 9.4 MB | 73 MB | Connected |
| 27 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 9.8 MB | 74 MB | Connected |
| 28 | 40.0 | 25.1 / 38.3 ms | 23.7 / 25.0 ms | 10.1 MB | 74 MB | Connected |
| 29 | 40.0 | 25.1 / 38.3 ms | 23.8 / 25.0 ms | 10.5 MB | 74 MB | Connected |
| 30 | 40.0 | 25.1 / 38.3 ms | 23.8 / 25.0 ms | 10.8 MB | 73 MB | Connected |
