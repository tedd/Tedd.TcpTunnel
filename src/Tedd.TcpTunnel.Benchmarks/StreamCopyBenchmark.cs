using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Tedd.TcpTunnel.Archive;

namespace Tedd.TcpTunnel.Benchmarks;

[MemoryDiagnoser]
public class StreamCopyBenchmark
{
    [Params(1024, 65536)] public int BufferSize { get; set; }
    private MemoryStream _source = null!;
    private MemoryStream _destination = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = new byte[1024 * 1024]; new Random(42).NextBytes(data);
        _source = new MemoryStream(data);
        _destination = new MemoryStream(data.Length);
    }

    // Reset for every invocation: no accidental EOF-only benchmark iterations.
    private void Reset() { _source.Position = 0; _destination.Position = 0; }
    [Benchmark(Baseline = true)]
    public Task Legacy() { Reset(); return LegacyExtensionMethods.CopyToAsyncWithFlush(_source, _destination, BufferSize, CancellationToken.None); }
    [Benchmark]
    public Task Pooled() { Reset(); return ExtensionMethods.CopyToAsyncWithFlush(_source, _destination, BufferSize, CancellationToken.None); }
    [Benchmark]
    public async Task Pipes()
    {
        Reset();
        var reader = PipeReader.Create(_source, new StreamPipeReaderOptions(bufferSize: BufferSize, leaveOpen: true));
        await reader.CopyToAsync(_destination);
        await reader.CompleteAsync();
    }
    [GlobalCleanup]
    public void Cleanup() { _source.Dispose(); _destination.Dispose(); }
}
