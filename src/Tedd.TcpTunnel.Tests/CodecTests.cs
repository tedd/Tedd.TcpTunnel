using System.IO.Compression;

namespace Tedd.TcpTunnel.Tests;

public sealed class CodecTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BrotliRejectsUndersizedEncoderDestination(bool history)
    {
        using var codec = new BlockCodec(new() { Compression = Codec.Brotli, CompressionHistory = history });
        var input = new byte[65536]; new Random(2).NextBytes(input);
        Assert.Throws<InvalidDataException>(() => codec.Encode(input, new byte[1], 0));
    }

    [Fact]
    public void InvalidCodecCannotCreateAStream()
    {
        using var codec = new BlockCodec(new() { Compression = (Codec)255 });
        Assert.Throws<InvalidOperationException>(() => codec.Encode(new byte[1], new byte[20], 0));
    }
    public static IEnumerable<object[]> Cases()
    {
        foreach (var codec in Enum.GetValues<Codec>())
        foreach (var size in new[] { 1, 1024, 65536, 1048576 })
        foreach (var random in new[] { false, true }) yield return [codec, size, random];
    }

    [Theory, MemberData(nameof(Cases))]
    public void CodecRoundTrip(Codec method, int size, bool random)
    {
        var options = new ForwardOptions { Compression = method, BrotliQuality = 3 };
        using var codec = new BlockCodec(options);
        var data = new byte[size];
        if (random) new Random(719).NextBytes(data); else Array.Fill(data, (byte)'A');
        var compressed = new byte[BlockCodec.MaxEncodedLength(size)];
        var decoded = new byte[size];
        var length = codec.Encode(data, compressed, 0);
        codec.Decode(compressed, length, decoded);
        Assert.Equal(data, decoded);
    }

    [Theory]
    [InlineData(0, 10)] [InlineData(4, 20)] [InlineData(11, 24)]
    public void BrotliHistoryLearnsAcrossFrames(int quality, int window)
    {
        using var sender = new BlockCodec(new() { Compression = Codec.Brotli, CompressionHistory = true, BrotliQuality = quality, BrotliWindow = window });
        using var receiver = new BlockCodec(new() { Compression = Codec.Brotli, CompressionHistory = true });
        var input = new byte[1024]; new Random(42).NextBytes(input);
        var wire = new byte[BlockCodec.MaxEncodedLength(input.Length)];
        var output = new byte[input.Length];
        var first = 0; var last = 0;
        for (var i = 0; i < 30; i++)
        {
            last = sender.Encode(input, wire, 0);
            if (i == 0) first = last;
            receiver.Decode(wire, last, output);
            Assert.Equal(input, output);
        }
        if (quality > 0 && window > 10) Assert.True(last < first, $"History did not improve compression: {first} -> {last}");
    }

    [Theory]
    [InlineData(Codec.None)] [InlineData(Codec.Brotli)] [InlineData(Codec.Lz4)] [InlineData(Codec.Zstandard)]
    public void InvalidCompressedFramesAreRejected(Codec codec)
    {
        using var subject = new BlockCodec(new() { Compression = codec });
        Assert.ThrowsAny<Exception>(() => subject.Decode(new byte[20], 20, new byte[100]));
    }

    [Theory]
    [InlineData(Codec.Deflate)] [InlineData(Codec.GZip)] [InlineData(Codec.ZLib)]
    public void ExpansionBeyondDeclaredSizeIsRejected(Codec codec)
    {
        using var subject = new BlockCodec(new() { Compression = codec, CompressionLevel = CompressionLevel.SmallestSize });
        var encoded = new byte[1000];
        var count = subject.Encode(new byte[500], encoded, 0);
        Assert.Throws<InvalidDataException>(() => subject.Decode(encoded, count, new byte[10]));
    }

    [Fact]
    public void BufferStreamBoundsAndCapabilities()
    {
        using var stream = new BufferStream(); var buffer = new byte[10];
        stream.Reset(buffer, 2, 5, true);
        Assert.True(stream.CanWrite); Assert.False(stream.CanRead); Assert.False(stream.CanSeek);
        stream.Write(new byte[] { 1, 2, 3 }, 0, 3); stream.Flush();
        Assert.Equal(3, stream.Position); Assert.Equal(5, stream.Length);
        Assert.Throws<InvalidDataException>(() => stream.Write(new byte[3]));
        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        stream.Reset(buffer, 2, 3, false);
        var output = new byte[10]; Assert.Equal(3, stream.Read(output, 0, 10)); Assert.Equal(0, stream.Read(output));
        Assert.Equal(new byte[] { 1, 2, 3 }, output[..3]);
        Assert.Throws<InvalidDataException>(() => stream.Write([1]));
    }
}
