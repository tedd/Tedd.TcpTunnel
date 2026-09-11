using System.Buffers.Binary;

namespace Tedd.TcpTunnel.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(Codec.None, false)] [InlineData(Codec.Brotli, true)] [InlineData(Codec.GZip, false)]
    public void HelloNegotiatesRoleCodecAndLimit(Codec codec, bool history)
    {
        var hello = new byte[Protocol.HelloSize];
        Protocol.WriteHello(hello, new() { Mode = TunnelMode.Client, Compression = codec, CompressionHistory = history, BufferSize = 4096 });
        Assert.Equal(4096, Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server, Compression = codec, CompressionHistory = history }));
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(12)]
    public void MalformedHelloIsRejected(int offset)
    {
        var hello = new byte[Protocol.HelloSize];
        Protocol.WriteHello(hello, new() { Mode = TunnelMode.Client }); hello[offset] = 255;
        Assert.Throws<InvalidDataException>(() => Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server }));
    }

    [Theory]
    [InlineData(1, 1, 1, true)] [InlineData(1, 1024, 2048, true)]
    [InlineData(2, 0, 0, true)] [InlineData(3, 0, 0, true)]
    [InlineData(0, 0, 0, false)] [InlineData(4, 0, 0, false)]
    [InlineData(1, 0, 1, false)] [InlineData(1, -1, 1, false)]
    [InlineData(1, 1025, 1, false)] [InlineData(1, 1, -1, false)]
    [InlineData(1, 1, 0, false)] [InlineData(1, 1, int.MaxValue, false)]
    [InlineData(2, 0, 1, false)] [InlineData(3, 1, 0, false)]
    public void FrameLimitsAreEnforced(int type, int raw, int wire, bool valid)
    {
        var header = new byte[Protocol.HeaderSize];
        Protocol.WriteHeader(header, (FrameType)type, raw, wire);
        if (valid) Assert.Equal(((FrameType)type, raw, wire), Protocol.ReadHeader(header, 1024));
        else Assert.Throws<InvalidDataException>(() => Protocol.ReadHeader(header, 1024));
    }

    [Fact]
    public void RejectsSmallPeerBuffer()
    {
        var hello = new byte[Protocol.HelloSize]; Protocol.WriteHello(hello, new() { Mode = TunnelMode.Client });
        BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(8), 1);
        Assert.Throws<InvalidDataException>(() => Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server }));
    }
}
