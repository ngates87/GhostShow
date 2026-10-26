using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*'
// Run one class: dotnet run -c Release --project GhostShow.Benchmarks -- --filter '*FramePath*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
