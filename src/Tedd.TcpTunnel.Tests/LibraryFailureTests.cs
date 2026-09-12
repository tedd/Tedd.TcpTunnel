using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Tedd.TcpTunnel.Tests;

public sealed class LibraryFailureTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MalformedAndForgedRecordsAbortTheApplicationStream(bool encrypted)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var key = EncryptionOptions.GenerateKey();
        var clientOptions = new TunnelStreamOptions
        {
            Encryption = encrypted ? new() { Algorithm = EncryptionAlgorithm.AesGcm, Key = key } : new()
        };
        var serverOptions = new ForwardOptions
        {
            Mode = TunnelMode.Server,
            Encryption = encrypted ? new() { Algorithm = EncryptionAlgorithm.AesGcm, Keys = new() { ["default"] = key } } : new()
        };
        var connecting = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, clientOptions, deadline.Token);
        using var socket = await listener.AcceptSocketAsync(deadline.Token);
        using var session = await TunnelHandshake.NegotiateAsync(socket, serverOptions, deadline.Token);
        await using var client = await connecting;
        var bytes = new byte[Protocol.HeaderSize + 1 + (encrypted ? FrameCipher.TagSize : 0)];
        if (encrypted)
        {
            Protocol.WriteHeader(bytes, FrameType.Data, 1, 1);
            session.Send!.Encrypt(bytes.AsSpan(0, Protocol.HeaderSize), new byte[] { 42 }, bytes.AsSpan(Protocol.HeaderSize));
            bytes[^1] ^= 1;
        }
        else Protocol.WriteHeader(bytes, (FrameType)255, 0, 0);
        using var transport = new SocketTransport(socket, false, new());
        await transport.SendAsync(bytes, deadline.Token);
        if (encrypted)
            await Assert.ThrowsAnyAsync<CryptographicException>(async () => _ = await client.ReadAsync(new byte[1], deadline.Token));
        else
            await Assert.ThrowsAsync<InvalidDataException>(async () => _ = await client.ReadAsync(new byte[1], deadline.Token));
        Assert.False(client.CanRead);
    }

    [Fact]
    public async Task CaptureContainsPlaintextAndLifecycleHasEndpointsAndDuration()
    {
        using var directory = new TempDirectory();
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Server, ListenPort = 0, RemotePort = rig.EchoPort });
        var events = new ConcurrentQueue<TunnelEvent>();
        await using (var stream = await TunnelClient.ConnectAsync("127.0.0.1", endpoint.Port,
            new() { Capture = new() { Directory = directory.Path } }, rig.Token, events.Enqueue))
        {
            var payload = "direct stream capture"u8.ToArray();
            await stream.WriteAsync(payload, rig.Token);
            var response = new byte[payload.Length];
            await stream.ReadExactlyAsync(response, rig.Token);
            Assert.Equal(payload, response);
            await stream.CompleteWritesAsync(rig.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], rig.Token));
        }
        var capture = File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory.Path, "*.pcap")));
        Assert.True(capture.AsSpan().IndexOf("direct stream capture"u8) >= 0);
        Assert.Contains(events, e => e.Event == "connection-established" && e.Source is not null && e.Destination is not null);
        Assert.Contains(events, e => e.Event == "connection-closed" && e.DurationMilliseconds >= 0);
    }

    [Fact]
    public async Task CaptureOpenFailureDisposesNegotiatedSocket()
    {
        using var directory = new TempDirectory();
        var file = Path.Combine(directory.Path, "file"); File.WriteAllText(file, "not a directory");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connecting = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken: deadline.Token);
        using var socket = await listener.AcceptSocketAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<IOException>(() => TunnelServer.AcceptAsync(socket,
            new() { Capture = new() { Directory = file } }, deadline.Token));
        await using var client = await connecting;
        Assert.True(socket.SafeHandle.IsClosed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BrokenTransportFailsWritesAndFin(bool fin)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connecting = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken: deadline.Token);
        using var socket = await listener.AcceptSocketAsync(deadline.Token);
        await using var server = await TunnelServer.AcceptAsync(socket, cancellationToken: deadline.Token);
        await using var client = await connecting;
        socket.Dispose();
        if (fin) await Assert.ThrowsAnyAsync<Exception>(async () => await server.CompleteWritesAsync(deadline.Token));
        else await Assert.ThrowsAnyAsync<Exception>(async () => await server.WriteAsync(new byte[1], deadline.Token));
        Assert.False(server.CanWrite);
    }

    [Fact]
    public async Task HeartbeatTransportFailureIsObservedAndDisposed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connecting = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken: deadline.Token);
        using var socket = await listener.AcceptSocketAsync(deadline.Token);
        var events = new ConcurrentQueue<TunnelEvent>();
        await using var server = await TunnelServer.AcceptAsync(socket, new() { HeartbeatMilliseconds = 10 }, deadline.Token, events.Enqueue);
        await using var client = await connecting;
        socket.Dispose();
        while (server.CanWrite) await Task.Delay(10, deadline.Token);
        await server.DisposeAsync();
        Assert.Contains(events, e => e.Event == "connection-failed" && e.Exception is not null);
    }

    [Fact]
    public async Task IdleDeadlineInterruptsBlockedWriter()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connecting = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            new() { IdleTimeoutMilliseconds = 100, HeartbeatMilliseconds = 10, Socket = new() { SendBufferSize = 1024 } }, deadline.Token);
        using var socket = await listener.AcceptSocketAsync(deadline.Token);
        socket.ReceiveBufferSize = 1024;
        using var session = await TunnelHandshake.NegotiateAsync(socket, new() { Mode = TunnelMode.Server }, deadline.Token);
        await using var client = await connecting;
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.WriteAsync(new byte[8 * 1024 * 1024], deadline.Token));
        Assert.False(deadline.IsCancellationRequested);
        var error = await Assert.ThrowsAsync<IOException>(async () => await client.FlushAsync(deadline.Token));
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task ClientRetryFailureReportsAttempts()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var events = new List<TunnelEvent>();
        await Assert.ThrowsAsync<IOException>(() => TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)socket.LocalEndPoint!).Port,
            new() { Retry = new() { Attempts = 2, InitialDelayMilliseconds = 0 } }, TestContext.Current.CancellationToken, events.Add));
        Assert.Equal(2, events.Count(e => e.Event == "connect-retry"));
    }
}
