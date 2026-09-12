using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Tedd.TcpTunnel.Tests;

public sealed class TdsTlsTests
{
    private static byte[] Prelogin(byte encryption = 1) =>
        [0, 0, 11, 0, 6, 1, 0, 17, 0, 1, 255, 16, 0, 0, 0, 0, 0, encryption];

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(3, false)]
    [InlineData(1, true)] [InlineData(3, true)]
    public void RequiresFullSessionEncryption(byte value, bool response)
    {
        var payload = Prelogin(value);
        var version = payload.AsSpan(11, 6).ToArray();
        TdsPrelogin.RequireEncryption(payload, response);
        Assert.Equal(1, payload[17]); Assert.Equal(version, payload.AsSpan(11, 6).ToArray());
    }

    public static IEnumerable<object[]> MalformedPrelogin()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 1, 255 }];
        yield return [new byte[] { 0, 1 }];
        yield return [new byte[] { 0, 0, 11, 0, 6, 0, 0, 17, 0, 1, 255, 0, 0, 0, 0, 0, 0, 1 }];
        yield return [new byte[] { 0, 0, 5, 0, 0 }];
        foreach (var index in new[] { 2, 4, 7, 9, 10 })
        {
            var payload = Prelogin(); payload[index] = 254; yield return [payload];
        }
        foreach (var encryption in new byte[] { 2, 0x80, 0x81, 255 }) yield return [Prelogin(encryption)];
        var overlap = Prelogin(); overlap[7] = 12; yield return [overlap];
    }

    [Theory]
    [MemberData(nameof(MalformedPrelogin))]
    public void RejectsMalformedAndUnsupportedPrelogin(byte[] payload) =>
        Assert.Throws<InvalidDataException>(() => TdsPrelogin.RequireEncryption(payload, false));

    [Fact]
    public void RejectsLoginOnlyEncryptionResponse() =>
        Assert.Throws<InvalidDataException>(() => TdsPrelogin.RequireEncryption(Prelogin(0), true));

    [Theory]
    [InlineData(1, 1, 9, 0)]
    [InlineData(18, 2, 9, 0)]
    [InlineData(18, 1, 8, 0)]
    [InlineData(18, 1, 7, 0)]
    [InlineData(18, 1, 9, 1)]
    public void RejectsInvalidPacketHeaders(byte type, byte status, byte length, byte window)
    {
        byte[] header = [type, status, 0, length, 0, 0, 1, window];
        Assert.Throws<InvalidDataException>(() => TdsPrelogin.PayloadLength(header, 0x12));
    }

    [Fact]
    public async Task BoundsAndTruncationAreEnforced()
    {
        using var stream = new MemoryStream();
        var token = TestContext.Current.CancellationToken;
        await TdsPrelogin.WriteAsync(stream, new byte[TdsPrelogin.MaxPayload + 1], 0x12, token);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => TdsPrelogin.ReadAsync(stream, 0x12, token));
        using var truncated = new MemoryStream(new byte[] { 0x12, 1, 0, 10, 0, 0, 1, 0, 42 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => TdsPrelogin.ReadAsync(truncated, 0x12, token));
        using var headerOnly = new MemoryStream(new byte[] { 0x12, 1, 0, 9, 0, 0, 1, 0 });
        using var handshake = new TdsHandshakeStream(headerOnly);
        Assert.Equal(0, await handshake.ReadAsync(Memory<byte>.Empty, token));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => _ = await handshake.ReadAsync(new byte[1], token));
    }

    [Fact]
    public async Task HandshakeFramingSwitchesToDirectTlsWithoutLosingBufferedBytes()
    {
        var token = TestContext.Current.CancellationToken;
        using var network = new MemoryStream();
        using var framed = new TdsHandshakeStream(network);
        Assert.True(framed.CanRead); Assert.True(framed.CanWrite); Assert.False(framed.CanSeek);
        Assert.Throws<NotSupportedException>(() => framed.Length);
        Assert.Throws<NotSupportedException>(() => framed.Position);
        Assert.Throws<NotSupportedException>(() => framed.Position = 0);
        Assert.Throws<NotSupportedException>(() => framed.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => framed.SetLength(1));
        framed.Flush(); await framed.FlushAsync(token);
        framed.Write(new byte[] { 1, 2, 3 }, 0, 3);
        await framed.WriteAsync(new byte[] { 4, 5 }, 0, 2, token);
        network.Position = 0;
        var bytes = new byte[3];
        Assert.Equal(1, framed.Read(bytes, 0, 1));
        Assert.Throws<InvalidDataException>(framed.CompleteHandshake);
        Assert.Equal(2, await framed.ReadAsync(bytes, 1, 2, token));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal(2, await framed.ReadAsync(bytes, token));
        framed.CompleteHandshake();
        await framed.WriteAsync(new byte[] { 9, 8 }, token);
        network.Position -= 2;
        Assert.Equal(2, await framed.ReadAsync(bytes, token)); Assert.Equal(9, bytes[0]); Assert.Equal(8, bytes[1]);
    }

    [Fact]
    public async Task StrictListenerNegotiatesTds8Alpn()
    {
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new()
        {
            ListenPort = 0, RemotePort = rig.EchoPort,
            ListenTls = new() { Mode = TlsMode.SqlServerStrict, GenerateSelfSigned = true }
        });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        using var ssl = new SslStream(client.GetStream());
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [new("tds/8.0")],
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }, rig.Token);
        Assert.Equal(new SslApplicationProtocol("tds/8.0"), ssl.NegotiatedApplicationProtocol);
        await ssl.WriteAsync(new byte[] { 7 }, rig.Token);
        var data = new byte[1]; await ssl.ReadExactlyAsync(data, rig.Token); Assert.Equal(7, data[0]);
    }

    [Fact]
    public async Task StrictListenerRejectsMissingAlpn()
    {
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new()
        {
            ListenPort = 0, RemotePort = rig.EchoPort,
            ListenTls = new() { Mode = TlsMode.SqlServerStrict, GenerateSelfSigned = true }
        });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        using var ssl = await TlsIntegrationTests.ConnectAsync(client, TlsMode.Tls, rig.Token);
        var error = await rig.FirstError.Task.WaitAsync(rig.Token);
        Assert.IsType<AuthenticationException>(error.Exception);
    }
}
