# Soak test: Open DMX, 30 minutes (dark)

- Date: 2026-10-26 13:47
- Machine: teleetra, Pop!_OS 24.04 LTS, X64, 20 logical CPUs
- Runtime: .NET 10.0.13
- Device: Open DMX on /dev/ttyUSB0
- Load: 20 patched RGBW fixtures; 898 commands/s; show saved every 5 s (360 saves, 0 failed)

| Measure | Result |
|---|---|
| Frames sent | 69,427 (38.6 frames/s) |
| Frame interval p50 / p95 / p99 / max | 26.00 / 27.10 / 28.00 / 52.1 ms |
| Intervals over 50 ms | 7 |
| Command → device latency p50 / p95 / max (1799 samples) | 39.0 / 50.6 / 125.3 ms |
| Device errors | 0 |
| Managed heap after full GC, start → end | 0.4 → 0.8 MB |
| Working set, start → end | 46 → 80 MB |
| GCs gen0 / gen1 / gen2 | 8 / 2 / 2 |
| Stopped cleanly with blackout | yes |

Per minute (cumulative frame statistics):

| Minute | Frames/s | Interval p95 / max | Latency p95 / max | Heap | Working set | Status |
|---|---|---|---|---|---|---|
| 1 | 38.5 | 27.1 / 46.1 ms | 50.7 / 125.3 ms | 3.5 MB | 65 MB | Connected |
| 2 | 38.6 | 27.1 / 46.1 ms | 50.7 / 125.3 ms | 6.1 MB | 69 MB | Connected |
| 3 | 38.7 | 27.1 / 46.1 ms | 50.7 / 125.3 ms | 8.6 MB | 71 MB | Connected |
| 4 | 38.7 | 27.1 / 46.1 ms | 49.9 / 125.3 ms | 11.1 MB | 75 MB | Connected |
| 5 | 38.7 | 27.1 / 46.1 ms | 50.2 / 125.3 ms | 2.2 MB | 77 MB | Connected |
| 6 | 38.7 | 27.1 / 46.1 ms | 50.2 / 125.3 ms | 4.7 MB | 78 MB | Connected |
| 7 | 38.7 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 7.2 MB | 78 MB | Connected |
| 8 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 9.6 MB | 78 MB | Connected |
| 9 | 38.6 | 27.1 / 46.1 ms | 50.7 / 125.3 ms | 12.1 MB | 78 MB | Connected |
| 10 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 3.0 MB | 78 MB | Connected |
| 11 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 5.5 MB | 78 MB | Connected |
| 12 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 7.9 MB | 78 MB | Connected |
| 13 | 38.6 | 27.1 / 46.1 ms | 50.4 / 125.3 ms | 10.4 MB | 78 MB | Connected |
| 14 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 1.2 MB | 78 MB | Connected |
| 15 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 3.7 MB | 78 MB | Connected |
| 16 | 38.6 | 27.1 / 46.1 ms | 50.7 / 125.3 ms | 6.2 MB | 79 MB | Connected |
| 17 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 8.6 MB | 79 MB | Connected |
| 18 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 11.1 MB | 79 MB | Connected |
| 19 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 2.0 MB | 79 MB | Connected |
| 20 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 4.5 MB | 79 MB | Connected |
| 21 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 7.0 MB | 79 MB | Connected |
| 22 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 9.4 MB | 79 MB | Connected |
| 23 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 11.9 MB | 79 MB | Connected |
| 24 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 2.9 MB | 79 MB | Connected |
| 25 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 5.3 MB | 79 MB | Connected |
| 26 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 7.8 MB | 79 MB | Connected |
| 27 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 10.3 MB | 79 MB | Connected |
| 28 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 1.2 MB | 79 MB | Connected |
| 29 | 38.6 | 27.1 / 46.1 ms | 50.5 / 125.3 ms | 3.7 MB | 80 MB | Connected |
| 30 | 38.6 | 27.1 / 46.1 ms | 50.6 / 125.3 ms | 6.2 MB | 80 MB | Connected |
