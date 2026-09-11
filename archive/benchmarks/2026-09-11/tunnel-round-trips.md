```

BenchmarkDotNet v0.16.0-preview.1, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
Memory: 127.91 GB Total, 64.06 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v3
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                  | Execution | Connections | PayloadSize | Mean       | Error       | StdDev    | Gen0   | Allocated |
|------------------------ |---------- |------------ |------------ |-----------:|------------:|----------:|-------:|----------:|
| **RoundTripAllConnections** | **Async**     | **1**           | **64**          |   **127.4 μs** |    **19.89 μs** |   **1.09 μs** |      **-** |     **555 B** |
| **RoundTripAllConnections** | **Async**     | **1**           | **65536**       |   **576.2 μs** |     **9.21 μs** |   **0.50 μs** |      **-** |     **553 B** |
| **RoundTripAllConnections** | **Async**     | **32**          | **64**          | **1,246.6 μs** |   **230.28 μs** |  **12.62 μs** | **0.9766** |   **17838 B** |
| **RoundTripAllConnections** | **Async**     | **32**          | **65536**       | **6,482.4 μs** | **5,388.72 μs** | **295.37 μs** |      **-** |   **17806 B** |
| **RoundTripAllConnections** | **Dedicated** | **1**           | **64**          |   **158.3 μs** |     **6.78 μs** |   **0.37 μs** |      **-** |     **264 B** |
| **RoundTripAllConnections** | **Dedicated** | **1**           | **65536**       |   **596.9 μs** |   **299.69 μs** |  **16.43 μs** |      **-** |     **265 B** |
| **RoundTripAllConnections** | **Dedicated** | **32**          | **64**          | **1,250.6 μs** |   **640.22 μs** |  **35.09 μs** |      **-** |    **8617 B** |
| **RoundTripAllConnections** | **Dedicated** | **32**          | **65536**       | **6,982.6 μs** | **4,029.99 μs** | **220.90 μs** |      **-** |    **8597 B** |
