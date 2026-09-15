using BenchmarkDotNet.Attributes;

namespace Tedd.TcpTunnel.Benchmarks;

[MemoryDiagnoser]
public class CompressionBenchmark
{
    [Params(Codec.None, Codec.Lz4, Codec.Brotli, Codec.Zstandard, Codec.GZip, Codec.Deflate, Codec.ZLib)] public Codec Codec { get; set; }
    [Params(1024, 65536)] public int Size { get; set; }
    [Params(false, true)] public bool RandomData { get; set; }
    private byte[] _input = null!, _wire = null!, _output = null!;
    private BlockCodec _codec = null!;
    [GlobalSetup]
    public void Setup()
    {
        _codec = new(new() { Compression = Codec }); _input = new byte[Size];
        if (RandomData) new Random(42).NextBytes(_input); else for (var i = 0; i < Size; i++) _input[i] = (byte)(i % 73);
        _wire = new byte[BlockCodec.MaxEncodedLength(Size)]; _output = new byte[Size];
    }
    [Benchmark] public int Encode() => _codec.Encode(_input, _wire, 0);
    [Benchmark] public int RoundTrip()
    {
        var bytes = _codec.Encode(_input, _wire, 0); _codec.Decode(_wire, bytes, _output); return bytes;
    }
    [GlobalCleanup] public void Cleanup() => _codec.Dispose();
}

[MemoryDiagnoser]
public class BrotliHistoryBenchmark
{
    [Params(false, true)] public bool History { get; set; }
    [Params(1, 4, 9)] public int Quality { get; set; }
    private byte[] _input = null!, _wire = null!;
    private BlockCodec _codec = null!;
    [GlobalSetup]
    public void Setup()
    {
        _codec = new(new() { Compression = Codec.Brotli, CompressionHistory = History, BrotliQuality = Quality });
        _input = new byte[4096]; new Random(42).NextBytes(_input); _wire = new byte[BlockCodec.MaxEncodedLength(_input.Length)];
    }
    [Benchmark] public int EncodeRepeatedFrame() => _codec.Encode(_input, _wire, 0);
    [GlobalCleanup] public void Cleanup() => _codec.Dispose();
}
