using BenchmarkDotNet.Attributes;
using System.Security.Cryptography;

namespace Tedd.TcpTunnel.Benchmarks;

[MemoryDiagnoser]
public class EncryptionBenchmark
{
    [Params(EncryptionAlgorithm.ChaCha20Poly1305, EncryptionAlgorithm.AesGcm, EncryptionAlgorithm.AesCcm)]
    public EncryptionAlgorithm Algorithm { get; set; }
    [Params(Codec.None, Codec.Lz4, Codec.Brotli)] public Codec Compression { get; set; }
    [Params(1024, 65536)] public int Bytes { get; set; }
    private FrameCipher _send = null!, _receive = null!;
    private BlockCodec _encoder = null!, _decoder = null!;
    private byte[] _input = null!, _encoded = null!, _encrypted = null!, _decoded = null!;
    private readonly byte[] _header = new byte[Protocol.HeaderSize];

    [GlobalSetup]
    public void Setup()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        _send = new(Algorithm, secret); _receive = new(Algorithm, secret);
        CryptographicOperations.ZeroMemory(secret);
        var options = new ForwardOptions { Compression = Compression };
        _encoder = new(options); _decoder = new(options);
        _input = new byte[Bytes]; new Random(42).NextBytes(_input.AsSpan(0, Bytes / 2));
        _input.AsSpan(0, Bytes / 2).CopyTo(_input.AsSpan(Bytes / 2));
        _encoded = new byte[BlockCodec.MaxEncodedLength(Bytes)];
        _encrypted = new byte[_encoded.Length + FrameCipher.TagSize]; _decoded = new byte[Bytes];
    }

    [Benchmark]
    public void CompressEncryptDecryptDecompress()
    {
        var length = _encoder.Encode(_input, _encoded, 0);
        Protocol.WriteHeader(_header, FrameType.Data, Bytes, length);
        _send.Encrypt(_header, _encoded.AsSpan(0, length), _encrypted);
        _receive.Decrypt(_header, _encrypted.AsSpan(0, length + FrameCipher.TagSize), _encoded.AsSpan(0, length));
        _decoder.Decode(_encoded, length, _decoded);
    }

    [GlobalCleanup]
    public void Cleanup() { _send.Dispose(); _receive.Dispose(); _encoder.Dispose(); _decoder.Dispose(); }
}
