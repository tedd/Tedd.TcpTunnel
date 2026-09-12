using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Tedd.TcpTunnel.Tests;

public sealed class LibraryTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var codec in Enum.GetValues<Codec>())
            foreach (var encryption in Enum.GetValues<EncryptionAlgorithm>())
                yield return [codec, encryption, false];
        yield return [Codec.Brotli, EncryptionAlgorithm.AesGcm, true];
    }

    [Theory, MemberData(nameof(Profiles))]
    public async Task DirectClientInteroperatesWithForwardingServer(Codec codec, EncryptionAlgorithm algorithm, bool history)
    {
        await using var rig = new TunnelRig();
        var key = EncryptionOptions.GenerateKey();
        var server = new ForwardOptions
        {
            ListenPort = 0, RemotePort = rig.EchoPort, Mode = TunnelMode.Server, Compression = codec,
            CompressionHistory = history, BufferSize = 4096, Encryption = Encryption(algorithm, key, false)
        };
        var endpoint = await rig.AddAsync(server);
        await using var client = await TunnelClient.ConnectAsync("127.0.0.1", endpoint.Port,
            new() { Compression = codec, CompressionHistory = history, BufferSize = 1024, Encryption = Encryption(algorithm, key, true) }, rig.Token);
        await RoundTripAsync(client, rig.Token);
        Assert.Empty(rig.Errors);
    }

    [Theory, MemberData(nameof(Profiles))]
    public async Task DirectServerInteroperatesWithForwardingClient(Codec codec, EncryptionAlgorithm algorithm, bool history)
    {
        await using var rig = new TunnelRig();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var key = EncryptionOptions.GenerateKey();
        var endpoint = await rig.AddAsync(new()
        {
            ListenPort = 0, RemotePort = ((IPEndPoint)listener.LocalEndpoint).Port, Mode = TunnelMode.Client,
            Compression = codec, CompressionHistory = history, BufferSize = 1024, Encryption = Encryption(algorithm, key, true)
        });
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint, rig.Token);
        using var accepted = await listener.AcceptSocketAsync(rig.Token);
        await using var server = await TunnelServer.AcceptAsync(accepted,
            new() { Compression = codec, CompressionHistory = history, BufferSize = 4096, Encryption = Encryption(algorithm, key, false) }, rig.Token);
        var echo = EchoAsync(server, rig.Token);
        var input = new byte[100123]; new Random(11).NextBytes(input);
        var application = client.GetStream();
        var send = application.WriteAsync(input, rig.Token).AsTask();
        var output = new byte[input.Length];
        await application.ReadExactlyAsync(output, rig.Token);
        await send;
        client.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await application.ReadAsync(new byte[1], rig.Token));
        await echo;
        Assert.Equal(input, output);
        Assert.Empty(rig.Errors);
    }

    [Fact]
    public async Task DirectPairSupportsConcurrentConnectionsAndNoDestination()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            var tasks = new List<Task>();
            for (var i = 0; i < 8; i++) tasks.Add(ServeAsync(await listener.AcceptSocketAsync(stop.Token), stop.Token));
            await Task.WhenAll(tasks);
        }, stop.Token);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var client = await TunnelClient.ConnectAsync("localhost", port, cancellationToken: stop.Token);
            await RoundTripAsync(client, stop.Token);
        }));
        await serving;
    }

    [Fact]
    public async Task HalfCloseAllowsDelayedResponseAndStreamContracts()
    {
        await using var pair = await Pair.CreateAsync();
        Assert.True(pair.Client.CanRead); Assert.True(pair.Client.CanWrite); Assert.False(pair.Client.CanSeek);
        Assert.Throws<NotSupportedException>(() => pair.Client.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => pair.Client.SetLength(0));
        Assert.Throws<NotSupportedException>(() => pair.Client.Position = 0);
        Assert.Throws<NotSupportedException>(() => pair.Client.Length);
        Assert.Throws<NotSupportedException>(() => pair.Client.Position);
        Assert.Throws<ArgumentNullException>(() => pair.Client.Read(null!, 0, 0));
        Assert.Equal(0, await pair.Client.ReadAsync(Memory<byte>.Empty, pair.Token));
        await pair.Client.WriteAsync(ReadOnlyMemory<byte>.Empty, pair.Token);
        await pair.Client.WriteAsync(new byte[] { 4, 5 }, 0, 2, pair.Token);
        await pair.Client.FlushAsync(pair.Token);
        pair.Client.Flush();
        await pair.Client.CompleteWritesAsync(pair.Token);
        await pair.Client.CompleteWritesAsync(pair.Token);
        Assert.False(pair.Client.CanWrite);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pair.Client.WriteAsync(new byte[1], pair.Token));
        var input = new byte[2];
        await pair.Server.ReadExactlyAsync(input, pair.Token);
        Assert.Equal(new byte[] { 4, 5 }, input);
        Assert.Equal(0, await pair.Server.ReadAsync(new byte[1], 0, 1, pair.Token));
        pair.Server.Write(input, 0, 2);
        Assert.Equal(2, pair.Client.Read(input, 0, 2));
        await pair.Server.CompleteWritesAsync(pair.Token);
        Assert.Equal(0, await pair.Client.ReadAsync(input, pair.Token));
        await pair.Client.DisposeAsync();
        pair.Client.Dispose();
        Assert.False(pair.Client.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => _ = await pair.Client.ReadAsync(input, pair.Token));
    }

    [Fact]
    public async Task CancelledWireReadAbortsStreamButPreCancelledOperationDoesNot()
    {
        await using var pair = await Pair.CreateAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await pair.Client.ReadAsync(new byte[1], cancelled.Token));
        Assert.True(pair.Client.CanRead);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await pair.Client.ReadAsync(new byte[1], deadline.Token));
        Assert.False(pair.Client.CanRead);
        await Assert.ThrowsAsync<IOException>(async () => await pair.Client.WriteAsync(new byte[1], pair.Token));
    }

    [Fact]
    public async Task DisposingStreamUnblocksPendingRead()
    {
        await using var pair = await Pair.CreateAsync();
        var read = pair.Client.ReadAsync(new byte[1], pair.Token).AsTask();
        await pair.Client.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => read);
    }

    [Fact]
    public async Task HeartbeatsStayOutOfApplicationDataAndDoNotPreventIdleExpiry()
    {
        await using var pair = await Pair.CreateAsync(new() { HeartbeatMilliseconds = 10, IdleTimeoutMilliseconds = 150 });
        var read = pair.Client.ReadAsync(new byte[1], pair.Token).AsTask();
        await Assert.ThrowsAnyAsync<Exception>(() => read);
        Assert.False(pair.Client.CanRead);
        var failure = await Assert.ThrowsAsync<IOException>(async () => await pair.Client.WriteAsync(new byte[1], pair.Token));
        Assert.IsType<TimeoutException>(failure.InnerException);
    }

    [Theory]
    [InlineData(EncryptionAlgorithm.None)] [InlineData(EncryptionAlgorithm.AesGcm)]
    public async Task MissingFinIsNeverAnApplicationEof(EncryptionAlgorithm algorithm)
    {
        var key = EncryptionOptions.GenerateKey();
        await using var pair = await Pair.CreateAsync(new() { Encryption = Encryption(algorithm, key, true) },
            new() { Encryption = Encryption(algorithm, key, false) });
        await pair.Server.DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(async () => _ = await pair.Client.ReadAsync(new byte[1], pair.Token));
    }

    [Fact]
    public async Task ServerRejectsAclBeforeHandshakeAndClosesOwnedSocket()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
        using var socket = await listener.AcceptSocketAsync(stop.Token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => TunnelServer.AcceptAsync(socket,
            new() { AccessControl = new() { Deny = ["127.0.0.1"] } }, stop.Token));
        Assert.True(socket.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task ValidationDoesNotTakeSocketOwnership()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TunnelServer.AcceptAsync(socket, new() { BufferSize = 1 }, TestContext.Current.CancellationToken));
        Assert.False(socket.SafeHandle.IsClosed);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TunnelClient.ConnectAsync("localhost", 0, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => TunnelServer.AcceptAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandshakeTimeoutClosesOwnedSocket()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
        using var socket = await listener.AcceptSocketAsync(stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TunnelServer.AcceptAsync(socket,
            new() { HandshakeTimeoutMilliseconds = 50 }, stop.Token));
        Assert.True(socket.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task AuthenticationFailureNeverReturnsAnApplicationStream()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connecting = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            new() { Encryption = Encryption(EncryptionAlgorithm.AesGcm, EncryptionOptions.GenerateKey(), true) }, stop.Token);
        using var socket = await listener.AcceptSocketAsync(stop.Token);
        var accepting = TunnelServer.AcceptAsync(socket,
            new() { Encryption = Encryption(EncryptionAlgorithm.AesGcm, EncryptionOptions.GenerateKey(), false) }, stop.Token);
        await Assert.ThrowsAsync<CryptographicException>(() => connecting);
        await Assert.ThrowsAnyAsync<Exception>(() => accepting);
        Assert.True(socket.SafeHandle.IsClosed);
    }

    private static EncryptionOptions Encryption(EncryptionAlgorithm algorithm, string key, bool client) =>
        algorithm == EncryptionAlgorithm.None ? new() : client
            ? new() { Algorithm = algorithm, Key = key }
            : new() { Algorithm = algorithm, Keys = new() { ["default"] = key } };

    private static async Task RoundTripAsync(TunnelStream stream, CancellationToken token)
    {
        var input = new byte[100123]; new Random(7).NextBytes(input);
        var send = stream.WriteAsync(input, token).AsTask();
        var output = new byte[input.Length];
        await stream.ReadExactlyAsync(output, token);
        await send; Assert.Equal(input, output);
        await stream.CompleteWritesAsync(token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
    }

    private static async Task ServeAsync(Socket socket, CancellationToken token)
    {
        await using var stream = await TunnelServer.AcceptAsync(socket, cancellationToken: token);
        await EchoAsync(stream, token);
    }

    private static async Task EchoAsync(TunnelStream stream, CancellationToken token)
    {
        var bytes = new byte[2048];
        int count;
        while ((count = await stream.ReadAsync(bytes, token)) != 0)
            await stream.WriteAsync(bytes.AsMemory(0, count), token);
        await stream.CompleteWritesAsync(token);
    }

    private sealed class Pair(TunnelStream client, TunnelStream server, CancellationTokenSource stop) : IAsyncDisposable
    {
        public TunnelStream Client => client;
        public TunnelStream Server => server;
        public CancellationToken Token => stop.Token;
        public static async Task<Pair> CreateAsync(TunnelStreamOptions? clientOptions = null, TunnelStreamOptions? serverOptions = null)
        {
            var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, clientOptions, stop.Token);
            var socket = await listener.AcceptSocketAsync(stop.Token);
            var server = await TunnelServer.AcceptAsync(socket, serverOptions, stop.Token);
            return new(await client, server, stop);
        }
        public async ValueTask DisposeAsync()
        { await client.DisposeAsync(); await server.DisposeAsync(); stop.Dispose(); }
    }
}
