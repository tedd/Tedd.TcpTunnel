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
| Method              | History | Quality | Mean        | Error        | StdDev    | Allocated |
|-------------------- |-------- |-------- |------------:|-------------:|----------:|----------:|
| **EncodeRepeatedFrame** | **False**   | **1**       |    **23.66 μs** |     **9.221 μs** |  **0.505 μs** |         **-** |
| **EncodeRepeatedFrame** | **False**   | **4**       |    **96.84 μs** |    **30.306 μs** |  **1.661 μs** |         **-** |
| **EncodeRepeatedFrame** | **False**   | **9**       | **3,187.67 μs** | **1,557.223 μs** | **85.357 μs** |         **-** |
| **EncodeRepeatedFrame** | **True**    | **1**       |    **23.06 μs** |    **11.395 μs** |  **0.625 μs** |         **-** |
| **EncodeRepeatedFrame** | **True**    | **4**       |    **12.98 μs** |     **5.571 μs** |  **0.305 μs** |         **-** |
| **EncodeRepeatedFrame** | **True**    | **9**       |   **205.52 μs** |    **70.425 μs** |  **3.860 μs** |         **-** |
