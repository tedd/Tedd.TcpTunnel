```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
Memory: 127.91 GB Total, 64.18 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v3
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method | BufferSize | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------- |----------- |----------:|-----------:|---------:|------:|--------:|-------:|----------:|------------:|
| **Legacy** | **1024**       |  **66.43 μs** | **122.214 μs** | **6.699 μs** |  **1.00** |    **0.00** | **0.0610** |    **1120 B** |        **1.00** |
| Pooled | 1024       |  81.85 μs | 134.034 μs | 7.347 μs |  1.24 |    0.14 |      - |         - |        0.00 |
| Pipes  | 1024       | 201.56 μs | 117.949 μs | 6.465 μs |  3.05 |    0.27 |      - |     272 B |        0.24 |
|        |            |           |            |          |       |         |        |           |             |
| **Legacy** | **65536**      |  **58.58 μs** |   **9.118 μs** | **0.500 μs** |  **1.00** |    **0.00** | **3.9063** |   **65632 B** |       **1.000** |
| Pooled | 65536      |  52.17 μs |   6.478 μs | 0.355 μs |  0.89 |    0.01 |      - |         - |       0.000 |
| Pipes  | 65536      | 195.05 μs |  32.964 μs | 1.807 μs |  3.33 |    0.04 |      - |     272 B |       0.004 |
