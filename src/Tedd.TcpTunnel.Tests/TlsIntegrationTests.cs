using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Tedd.TcpTunnel.Tests;

public sealed class TlsIntegrationTests
{
    public static IEnumerable<object[]> Modes()
    {
        foreach (var mode in new[] { TlsMode.Tls, TlsMode.SqlServer })
        foreach (var execution in Enum.GetValues<ExecutionMode>())
        foreach (var codec in new[] { Codec.None, Codec.Lz4, Codec.Brotli, Codec.Zstandard })
        foreach (var encrypted in new[] { false, true })
            yield return [mode, execution, codec, encrypted];
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task EndpointTlsCarriesPlaintextThroughCompressionAndPreservesHalfClose(TlsMode mode, ExecutionMode execution, Codec codec, bool encrypted)
    {
        using var capture = new TempDirectory();
        using var certificate = ListenTlsOptions.CreateSelfSigned();
        await using var rig = new TunnelRig();
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var key = EncryptionOptions.GenerateKey();
        var request = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("select database_id, name from sys.databases;\n", 3000)));
        var reply = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("master tempdb model msdb application\n", 3000)));
        var server = await rig.AddAsync(new()
        {
            Name = "tls-server", Mode = TunnelMode.Server, ListenPort = 0,
            RemotePort = ((IPEndPoint)destination.LocalEndpoint).Port,
            RemoteTls = new() { Mode = mode, TrustServerCertificate = true },
            Compression = codec, CompressionHistory = codec == Codec.Brotli, Execution = execution,
            BufferSize = 1024, HeartbeatMilliseconds = 15, BatchMilliseconds = 3,
            Encryption = encrypted ? new() { Algorithm = EncryptionAlgorithm.AesGcm, Keys = new() { ["test"] = key } } : new()
        });
        var clientEndpoint = await rig.AddAsync(new()
        {
            Name = "tls-client", Mode = TunnelMode.Client, ListenPort = 0, RemotePort = server.Port,
            ListenTls = new() { Mode = mode, GenerateSelfSigned = true },
            Compression = codec, CompressionHistory = codec == Codec.Brotli, Execution = execution,
            BufferSize = 4096, HeartbeatMilliseconds = 15, BatchMilliseconds = 3,
            Capture = new() { Directory = capture.Path },
            Encryption = encrypted ? new() { Algorithm = EncryptionAlgorithm.AesGcm, KeyId = "test", Key = key } : new()
        });
        var destinationTask = Task.Run(async () =>
        {
            using var accepted = await destination.AcceptTcpClientAsync(rig.Token);
            using var ssl = await AcceptAsync(accepted, certificate, mode, rig.Token);
            // Leave enough time for several heartbeats before application data arrives.
            var received = new byte[request.Length];
            await ssl.ReadExactlyAsync(received, rig.Token);
            Assert.Equal(request, received);
            Assert.Equal(0, await ssl.ReadAsync(new byte[1], rig.Token));
            await ssl.WriteAsync(reply, rig.Token);
            await ssl.ShutdownAsync();
        }, rig.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(clientEndpoint, rig.Token);
        using var application = await ConnectAsync(client, mode, rig.Token);
        await Task.Delay(80, rig.Token);
        await application.WriteAsync(request, rig.Token);
        await application.ShutdownAsync();
        var response = new byte[reply.Length];
        await application.ReadExactlyAsync(response, rig.Token);
        Assert.Equal(reply, response);
        Assert.Equal(0, await application.ReadAsync(new byte[1], rig.Token));
        await destinationTask;
        Assert.Empty(rig.Errors);
        Assert.Equal(2, rig.Events.Count(e => e.Event == "tls-established"));
        // Capture must see the original SQL bytes, not encrypted TLS records.
        using var captureFile = File.Open(Assert.Single(Directory.GetFiles(capture.Path, "*.pcap")), FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var captured = new MemoryStream(); captureFile.CopyTo(captured); var pcap = captured.ToArray();
        Assert.True(pcap.AsSpan().IndexOf(request.AsSpan(0, 40)) >= 0);
    }

    [Theory]
    [InlineData(true, false, TlsMode.Tls)]
    [InlineData(false, true, TlsMode.Tls)]
    [InlineData(true, true, TlsMode.Tls)]
    [InlineData(true, true, TlsMode.SqlServer)]
    public async Task RawTlsSupportsIndependentEndpoints(bool listenTls, bool remoteTls, TlsMode mode)
    {
        using var certificate = ListenTlsOptions.CreateSelfSigned();
        await using var rig = new TunnelRig();
        using var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
        var endpoint = await rig.AddAsync(new()
        {
            ListenPort = 0, RemotePort = ((IPEndPoint)destination.LocalEndpoint).Port,
            ListenTls = new() { Mode = listenTls ? mode : TlsMode.None, GenerateSelfSigned = listenTls },
            RemoteTls = new() { Mode = remoteTls ? mode : TlsMode.None, TrustServerCertificate = remoteTls }
        });
        var echo = Task.Run(async () =>
        {
            using var accepted = await destination.AcceptTcpClientAsync(rig.Token);
            using Stream stream = remoteTls ? await AcceptAsync(accepted, certificate, mode, rig.Token) : accepted.GetStream();
            var value = new byte[5]; await stream.ReadExactlyAsync(value, rig.Token); await stream.WriteAsync(value, rig.Token);
        }, rig.Token);
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        using Stream application = listenTls ? await ConnectAsync(client, mode, rig.Token) : client.GetStream();
        await application.WriteAsync("hello"u8.ToArray(), rig.Token);
        var result = new byte[5]; await application.ReadExactlyAsync(result, rig.Token);
        Assert.Equal("hello"u8.ToArray(), result);
        await echo;
    }

    [Theory]
    [InlineData(false, "localhost", false)]
    [InlineData(false, "wrong.example", false)]
    [InlineData(true, "wrong.example", true)]
    public async Task DestinationValidationRequiresExplicitBypass(bool trust, string targetHost, bool succeeds)
    {
        using var certificate = ListenTlsOptions.CreateSelfSigned();
        await using var rig = new TunnelRig();
        using var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
        var endpoint = await rig.AddAsync(new()
        {
            ListenPort = 0, RemotePort = ((IPEndPoint)destination.LocalEndpoint).Port,
            RemoteTls = new() { Mode = TlsMode.Tls, TargetHost = targetHost, TrustServerCertificate = trust }
        });
        var serve = Task.Run(async () =>
        {
            using var accepted = await destination.AcceptTcpClientAsync(rig.Token);
            try
            {
                using var ssl = await AcceptAsync(accepted, certificate, TlsMode.Tls, rig.Token);
                await ssl.WriteAsync(new byte[] { 42 }, rig.Token);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        }, rig.Token);
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        var data = new byte[1];
        if (succeeds) { await client.GetStream().ReadExactlyAsync(data, rig.Token); Assert.Equal(42, data[0]); }
        else
        {
            var error = await rig.FirstError.Task.WaitAsync(rig.Token);
            Assert.IsType<AuthenticationException>(error.Exception);
        }
        await serve;
    }

    [Fact]
    public async Task ListenerHandshakeHasDeadlineAndCancellation()
    {
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new()
        {
            ListenPort = 0, RemotePort = rig.EchoPort, HandshakeTimeoutMilliseconds = 100,
            ListenTls = new() { Mode = TlsMode.Tls, GenerateSelfSigned = true }
        });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        var error = await rig.FirstError.Task.WaitAsync(rig.Token);
        Assert.IsAssignableFrom<OperationCanceledException>(error.Exception);
    }


    [Theory]
    [InlineData(SslProtocols.Tls12, TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256)]
    [InlineData(SslProtocols.Tls13, TlsCipherSuite.TLS_AES_256_GCM_SHA384)]
    public async Task ProtocolAndCipherRestrictionsAreNegotiated(SslProtocols protocol, TlsCipherSuite cipher)
    {
        using var certificate = ListenTlsOptions.CreateSelfSigned();
        await using var rig = new TunnelRig();
        using var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
        var suites = OperatingSystem.IsLinux() ? new List<TlsCipherSuite> { cipher } : [];
        var endpoint = await rig.AddAsync(new()
        {
            ListenPort = 0, RemotePort = ((IPEndPoint)destination.LocalEndpoint).Port,
            ListenTls = new() { Mode = TlsMode.Tls, GenerateSelfSigned = true, Protocols = protocol, CipherSuites = suites },
            RemoteTls = new() { Mode = TlsMode.Tls, TrustServerCertificate = true, Protocols = protocol, CipherSuites = suites }
        });
        var serve = Task.Run(async () =>
        {
            using var accepted = await destination.AcceptTcpClientAsync(rig.Token);
            using var ssl = new SslStream(accepted.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, EnabledSslProtocols = protocol,
                CipherSuitesPolicy = OperatingSystem.IsLinux() ? new CipherSuitesPolicy(suites) : null
            }, rig.Token);
            Assert.Equal(protocol, ssl.SslProtocol);
            if (OperatingSystem.IsLinux()) Assert.Equal(cipher, ssl.NegotiatedCipherSuite);
            await ssl.WriteAsync(new byte[] { 42 }, rig.Token);
            await ssl.ShutdownAsync();
        }, rig.Token);
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        using var application = new SslStream(client.GetStream());
        await application.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", EnabledSslProtocols = protocol,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            CipherSuitesPolicy = OperatingSystem.IsLinux() ? new CipherSuitesPolicy(suites) : null
        }, rig.Token);
        Assert.Equal(protocol, application.SslProtocol);
        if (OperatingSystem.IsLinux()) Assert.Equal(cipher, application.NegotiatedCipherSuite);
        var bytes = new byte[1]; await application.ReadExactlyAsync(bytes, rig.Token);
        Assert.Equal(42, bytes[0]);
        await serve;
    }

    internal static async Task<SslStream> ConnectAsync(TcpClient client, TlsMode mode, CancellationToken token)
    {
        Stream network = client.GetStream();
        SqlWireStream? wire = null;
        if (mode == TlsMode.SqlServer)
        {
            // VERSION, ENCRYPTION, INSTOPT. A large instance field spans tunnel frames.
            var payload = new byte[6017];
            new byte[] { 0, 0, 16, 0, 6, 1, 0, 22, 0, 1, 2, 0, 23, 23, 106, 255 }.CopyTo(payload, 0);
            payload[16] = 16; payload[22] = 1;
            await WritePacketAsync(network, payload, 0x12, token);
            var response = await ReadPacketAsync(network, 0x04, token);
            Assert.Equal(1, response[17]);
            wire = new(network); network = wire;
        }
        var ssl = new SslStream(network, leaveInnerStreamOpen: true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12,
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }, token);
        if (wire is not null) wire.Handshake = false;
        return ssl;
    }

    internal static async Task<SslStream> AcceptAsync(TcpClient client, X509Certificate2 certificate, TlsMode mode, CancellationToken token)
    {
        Stream network = client.GetStream();
        SqlWireStream? wire = null;
        if (mode == TlsMode.SqlServer)
        {
            var request = await ReadPacketAsync(network, 0x12, token);
            Assert.Equal(6017, request.Length); Assert.Equal(1, request[22]);
            byte[] response = [0, 0, 11, 0, 6, 1, 0, 17, 0, 1, 255, 16, 0, 0, 0, 0, 0, 1];
            await WritePacketAsync(network, response, 0x04, token);
            wire = new(network); network = wire;
        }
        var ssl = new SslStream(network, leaveInnerStreamOpen: true);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 }, token);
        if (wire is not null) wire.Handshake = false;
        return ssl;
    }

    internal static async Task WritePacketAsync(Stream stream, byte[] payload, byte type, CancellationToken token)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            var length = Math.Min(1000, payload.Length - offset);
            byte[] header = [type, (byte)(offset + length == payload.Length ? 1 : 0), (byte)((length + 8) >> 8), (byte)(length + 8), 0, 0, 1, 0];
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(payload.AsMemory(offset, length), token);
            offset += length;
        }
    }

    internal static async Task<byte[]> ReadPacketAsync(Stream stream, byte type, CancellationToken token)
    {
        using var payload = new MemoryStream();
        var header = new byte[8];
        do
        {
            await stream.ReadExactlyAsync(header, token); Assert.Equal(type, header[0]);
            var bytes = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) - 8];
            await stream.ReadExactlyAsync(bytes, token); payload.Write(bytes);
        } while (header[1] == 0);
        return payload.ToArray();
    }

    // An independent test peer implementation, so production framing is not used on both ends.
    private sealed class SqlWireStream(Stream inner) : Stream
    {
        public bool Handshake { get; set; } = true;
        private int _remaining;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!Handshake) return await inner.ReadAsync(buffer, token);
            if (_remaining == 0)
            {
                byte[] header = new byte[8]; await inner.ReadExactlyAsync(header, token); Assert.Equal(0x12, header[0]);
                _remaining = (header[2] << 8 | header[3]) - 8;
            }
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], token);
            _remaining -= count; return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            Handshake ? new(WritePacketAsync(inner, buffer.ToArray(), 0x12, token)) : inner.WriteAsync(buffer, token);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
    }
}
