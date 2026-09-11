# End-to-end throughput benchmarks

Measured 2026-09-11 18:05 UTC on AMD64 Family 25 Model 33 Stepping 0, AuthenticAMD (X64, 32 logical processors) using .NET 11.0.0-rc.1.26425.128.

These loopback results measure the complete Application → Client → Server → destination path. They isolate framing, copying, and codec cost; they do not predict throughput across a particular network.

## Results

| Rank | Codec profile | Settings | Input | Best MiB/s | Median MiB/s |
| ---: | --- | --- | --- | ---: | ---: |
| 1 | Zstandard -5 | level=-5 | Mixed DLL set | 145.4 | 129.9 |
| 2 | None | uncompressed framing | Mixed DLL set | 141.3 | 92.6 |
| 3 | Deflate fastest | level=Fastest | Mixed DLL set | 108.1 | 107.2 |
| 4 | LZ4 fastest | level=Fastest | Mixed DLL set | 106.0 | 100.9 |
| 5 | Brotli q1 | quality=1, window=22, history=false | Repeated mshtml.dll | 91.2 | 81.1 |
| 6 | GZip fastest | level=Fastest | Mixed DLL set | 88.9 | 85.7 |
| 7 | Zstandard 3 | level=3 | Mixed DLL set | 74.8 | 72.1 |
| 8 | ZLib fastest | level=Fastest | Mixed DLL set | 73.1 | 72.2 |
| 9 | Zstandard 9 | level=9 | Mixed DLL set | 41.0 | 40.3 |
| 10 | Brotli q4 | quality=4, window=22, history=false | Repeated mshtml.dll | 40.4 | 35.5 |
| 11 | Brotli q4 + history | quality=4, window=22, history=true | Repeated mshtml.dll | 39.2 | 38.5 |
| 12 | ZLib optimal | level=Optimal | Mixed DLL set | 37.7 | 36.6 |
| 13 | Deflate optimal | level=Optimal | Mixed DLL set | 37.3 | 35.7 |
| 14 | GZip optimal | level=Optimal | Mixed DLL set | 36.7 | 36.5 |
| 15 | Brotli q9 | quality=9, window=22, history=false | Repeated mshtml.dll | 9.9 | 9.9 |
| 16 | LZ4 HC | level=SmallestSize | Mixed DLL set | 8.1 | 8.1 |
| 17 | Brotli q9 + history | quality=9, window=22, history=true | Repeated mshtml.dll | 5.1 | 5.0 |

## Input files

| File | Size (MiB) | SHA-256 |
| --- | ---: | --- |
| mshtml.dll | 22.9 | `4093242363e953e6172b87f5b66ec42deaf63bb1fb12a2a48590c08f8fb6ec06` |
| Windows.UI.Xaml.dll | 17.0 | `b0a1d10f9766565658bf210ee521861dfa16fb22b331925fc1f039ef626ad2cb` |
| shell32.dll | 7.6 | `a7779772d197cd94a2309d4739b85587d7e86a20e406baec4932307d71bbc2d2` |

## Methodology

- Platform: Microsoft Windows 10.0.26200
- Execution: Async; 1 MiB tunnel and file-copy buffers; zero application batching delay.
- Runs: 1 warm-up(s), then 3 measured transfer(s) of at least 64 MiB per profile.
- Reported speed: highest measured application-data rate. Median is included to expose run-to-run variability.
- Brotli inputs: the largest selected DLL is repeated on one connection so history-enabled profiles can reuse earlier content. History-disabled Brotli profiles use the identical repeated sequence for a controlled comparison.
- Other inputs: the selected DLLs are cycled in order. Warm-ups populate the OS page cache so the measurement emphasizes tunnel throughput rather than storage latency.
- The sink validates the exact byte count. TCP and TTN2 framing preserve ordering; no network encryption is present in this benchmark.

## Reproduce

Run from the repository root on Windows with the SDK pinned by `global.json`:

```powershell
dotnet run --project src/Tedd.TcpTunnel.Benchmarks -c Release -- --throughput --target-mib 64 --warmups 1 --iterations 3 --output benchmarks.md --json-output website/benchmarks.json
```

Use repeated `--file PATH` arguments to supply a different corpus. On non-Windows systems, at least one `--file` is required.
