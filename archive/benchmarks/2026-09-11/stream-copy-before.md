```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
Memory: 127.91 GB Total, 64.38 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v3
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method | BufferSize | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------- |----------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| **Legacy** | **1024**       |  **59.58 μs** | **29.72 μs** | **1.629 μs** |  **1.00** |    **0.00** | **0.0610** |    **1120 B** |        **1.00** |
| Pooled | 1024       |  56.41 μs | 61.74 μs | 3.384 μs |  0.95 |    0.05 |      - |      72 B |        0.06 |
| Pipes  | 1024       | 178.49 μs | 48.52 μs | 2.660 μs |  3.00 |    0.08 |      - |     272 B |        0.24 |
|        |            |           |          |          |       |         |        |           |             |
| **Legacy** | **65536**      |  **57.86 μs** | **10.80 μs** | **0.592 μs** |  **1.00** |    **0.00** | **3.9063** |   **65632 B** |       **1.000** |
| Pooled | 65536      |  50.69 μs | 25.52 μs | 1.399 μs |  0.88 |    0.02 |      - |      72 B |       0.001 |
| Pipes  | 65536      | 196.15 μs | 21.41 μs | 1.173 μs |  3.39 |    0.03 |      - |     272 B |       0.004 |
