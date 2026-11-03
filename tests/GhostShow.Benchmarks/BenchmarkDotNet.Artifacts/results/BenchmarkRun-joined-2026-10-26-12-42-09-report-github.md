```

BenchmarkDotNet v0.15.8, Linux Pop!_OS 24.04 LTS
12th Gen Intel Core i7-12700H 0.40GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3


```
| Type                | Method                                                   | FixtureCount | Mean         | Error      | StdDev     | Gen0   | Gen1   | Allocated |
|-------------------- |--------------------------------------------------------- |------------- |-------------:|-----------:|-----------:|-------:|-------:|----------:|
| **FramePathBenchmarks** | **&#39;Engine → frame snapshot (output thread, per frame)&#39;**     | **?**            |     **29.57 ns** |   **0.006 ns** |   **0.006 ns** |      **-** |      **-** |         **-** |
| FramePathBenchmarks | &#39;Frame → Enttec Pro packet&#39;                              | ?            |     14.42 ns |   0.222 ns |   0.208 ns |      - |      - |         - |
| FramePathBenchmarks | &#39;Frame → Art-Net packet&#39;                                 | ?            |     15.49 ns |   0.333 ns |   0.356 ns |      - |      - |         - |
| FramePathBenchmarks | &#39;SetChannel command (raw fader)&#39;                         | ?            |     17.14 ns |   0.004 ns |   0.003 ns |      - |      - |         - |
| FramePathBenchmarks | &#39;SetFixtureControl command (fixture fader, 100 patched)&#39; | ?            |     26.41 ns |   0.176 ns |   0.165 ns |      - |      - |         - |
| **PatchBenchmarks**     | **&#39;Validate and patch fixtures&#39;**                            | **10**           |  **1,764.55 ns** |  **12.364 ns** |  **11.565 ns** | **0.4425** | **0.0038** |    **5568 B** |
| **PatchBenchmarks**     | **&#39;Validate and patch fixtures&#39;**                            | **100**          | **16,597.76 ns** | **128.786 ns** | **120.467 ns** | **3.6926** | **0.2441** |   **46577 B** |
