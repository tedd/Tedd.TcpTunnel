using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Tedd.TcpTunnel.Tests;

public sealed class IntegrationTests
{
    public static IEnumerable<object[]> Modes()
    {
        foreach (var mode in Enum.GetValues<ExecutionMode>())
        {
            yield return [Codec.None, false, mode, false];
            foreach (var codec in Enum.GetValues<Codec>()) yield return [codec, true, mode, false];
            yield return [Codec.Brotli, true, mode, true];
        }
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task ConcurrentConnectionsPreserveBytesAndHalfClose(Codec codec, bool framed, ExecutionMode execution, bool history)
    {
        await using var rig = new TunnelRig();
        var options = new ForwardOptions { Name = "test", ListenPort = 0, RemotePort = rig.EchoPort, Compression = codec,
            Mode = framed ? TunnelMode.Server : TunnelMode.Raw, Execution = execution, BufferSize = 8192,
            CompressionHistory = history, HeartbeatMilliseconds = 25 };
        var endpoint = await rig.AddAsync(options);
        if (framed)
            endpoint = await rig.AddAsync(new ForwardOptions { Name = "client", Mode = TunnelMode.Client, ListenPort = 0,
                RemotePort = endpoint.Port, Compression = codec, CompressionHistory = history, Execution = execution,
                BufferSize = 4096, HeartbeatMilliseconds = 25 });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async seed =>
        {
            using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
            var stream = client.GetStream();
            var data = new byte[100000 + seed]; new Random(seed).NextBytes(data);
            var output = new byte[data.Length];
            var send = Task.Run(async () =>
            {
                await stream.WriteAsync(data, rig.Token);
                client.Client.Shutdown(SocketShutdown.Send);
            }, rig.Token);
            await stream.ReadExactlyAsync(output, rig.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], rig.Token));
            await send; Assert.Equal(data, output);
        }));
        Assert.Empty(rig.Errors);
    }

    [Theory]
    [InlineData(ExecutionMode.Async, false)] [InlineData(ExecutionMode.Dedicated, false)]
    [InlineData(ExecutionMode.Async, true)] [InlineData(ExecutionMode.Dedicated, true)]
    public async Task BatchingFlushesByDeadlineAndOnFullBuffer(ExecutionMode execution, bool framed)
    {
        await using var rig = new TunnelRig();
        var remote = rig.EchoPort;
        if (framed) remote = (await rig.AddAsync(new() { Name = "server", ListenPort = 0, Mode = TunnelMode.Server, RemotePort = remote, Compression = Codec.Brotli })).Port;
        var endpoint = await rig.AddAsync(new() { Name = "batch", ListenPort = 0, RemotePort = remote, Mode = framed ? TunnelMode.Client : TunnelMode.Raw,
            Compression = framed ? Codec.Brotli : Codec.None, BufferSize = 1024, BatchMilliseconds = 250, Execution = execution });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        var watch = Stopwatch.StartNew();
        await client.GetStream().WriteAsync(new byte[] { 7 }, rig.Token);
        var one = new byte[1]; await client.GetStream().ReadExactlyAsync(one, rig.Token);
        Assert.Equal(7, one[0]); Assert.InRange(watch.ElapsedMilliseconds, 100, 2500);
        watch.Restart();
        var input = new byte[1024]; new Random(4).NextBytes(input);
        await client.GetStream().WriteAsync(input, rig.Token);
        var output = new byte[1024]; await client.GetStream().ReadExactlyAsync(output, rig.Token);
        Assert.Equal(input, output); Assert.True(watch.ElapsedMilliseconds < 250, $"Full buffer waited {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task HeartbeatsNeverReachApplication()
    {
        await using var rig = new TunnelRig();
        var server = await rig.AddAsync(new() { Name = "server", ListenPort = 0, RemotePort = rig.EchoPort, Mode = TunnelMode.Server, HeartbeatMilliseconds = 20 });
        var endpoint = await rig.AddAsync(new() { Name = "client", ListenPort = 0, RemotePort = server.Port, Mode = TunnelMode.Client, HeartbeatMilliseconds = 20 });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        await Task.Delay(120, rig.Token);
        Assert.Equal(0, client.Available);
        await client.GetStream().WriteAsync(new byte[] { 42 }, rig.Token);
        var result = new byte[1]; await client.GetStream().ReadExactlyAsync(result, rig.Token); Assert.Equal(42, result[0]);
    }

    [Fact]
    public async Task RetryConnectsWhenDestinationBecomesAvailable()
    {
        using var held = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        held.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)held.LocalEndPoint!).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var attempts = 0;
        var connecting = Connector.ConnectAsync("127.0.0.1", port, new() { Attempts = 10, ConnectTimeoutMilliseconds = 50, InitialDelayMilliseconds = 50, MaxDelayMilliseconds = 100, Jitter = false }, deadline.Token,
            (_, _) => Interlocked.Increment(ref attempts));
        await Task.Delay(130, deadline.Token); held.Listen(1);
        using var connected = await connecting;
        using var accepted = await held.AcceptAsync(deadline.Token);
        Assert.True(attempts >= 1); Assert.True(connected.Connected);
    }

    [Fact]
    public async Task RetryExhaustionAndCancellationAreBounded()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var errors = 0;
        await Assert.ThrowsAsync<IOException>(() => Connector.ConnectAsync("127.0.0.1", port, new() { Attempts = 2, ConnectTimeoutMilliseconds = 50, InitialDelayMilliseconds = 1 }, TestContext.Current.CancellationToken, (_, _) => errors++));
        Assert.Equal(2, errors);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Connector.ConnectAsync("localhost", port, new(), stop.Token));
    }

    [Fact]
    public async Task IdleTimeoutClosesConnectionAndReleasesSlot()
    {
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new() { ListenPort = 0, RemotePort = rig.EchoPort, IdleTimeoutMilliseconds = 80, MaxConnections = 1 });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], rig.Token));
        using var next = new TcpClient(); await next.ConnectAsync(endpoint, rig.Token);
        await next.GetStream().WriteAsync(new byte[] { 8 }, rig.Token);
        var output = new byte[1]; await next.GetStream().ReadExactlyAsync(output, rig.Token); Assert.Equal(8, output[0]);
    }

    [Theory]
    [InlineData(ExecutionMode.Async)] [InlineData(ExecutionMode.Dedicated)]
    public async Task ActiveBatchDoesNotExpireAsIdle(ExecutionMode execution)
    {
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new() { ListenPort = 0, RemotePort = rig.EchoPort, Execution = execution,
            BufferSize = 1024, BatchMilliseconds = 5000, IdleTimeoutMilliseconds = 1000 });
        using var client = new TcpClient { NoDelay = true }; await client.ConnectAsync(endpoint, rig.Token);
        var input = new byte[1024]; new Random(12).NextBytes(input);
        for (var offset = 0; offset < input.Length; offset += 32)
        {
            await client.GetStream().WriteAsync(input.AsMemory(offset, 32), rig.Token);
            if (offset + 32 < input.Length) await Task.Delay(75, rig.Token);
        }
        var output = new byte[input.Length]; await client.GetStream().ReadExactlyAsync(output, rig.Token);
        Assert.Equal(input, output);
        Assert.Empty(rig.Errors);
    }

    [Fact]
    public async Task InvalidHandshakeIsIsolatedAndTimedOut()
    {
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Server, ListenPort = 0, RemotePort = rig.EchoPort, HandshakeTimeoutMilliseconds = 80 });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
        var hello = new byte[Protocol.HelloSize]; await client.GetStream().ReadExactlyAsync(hello, rig.Token);
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], rig.Token));
        using var second = new TcpClient(); await second.ConnectAsync(endpoint, rig.Token);
        await second.GetStream().ReadExactlyAsync(hello, rig.Token);
        await second.GetStream().WriteAsync(new byte[Protocol.HelloSize], rig.Token);
        Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], rig.Token));
    }

    [Fact]
    public async Task MultipleForwardsStartTogetherAndCancelCleanly()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = new TunnelHost(new() { Forwards = [new() { Name = "first", ListenPort = 0 }, new() { Name = "second", ListenPort = 0 }] });
        var running = host.RunAsync(stop.Token);
        var endpoints = await Task.WhenAll(host.Listeners.Select(l => l.Ready));
        Assert.NotEqual(endpoints[0].Port, endpoints[1].Port);
        await stop.CancelAsync(); await running;
        Assert.All(host.Listeners, l => Assert.Equal(0, l.ActiveConnections));
    }

    [Fact]
    public async Task BindFailureStopsHostAndReportsReadinessFailure()
    {
        using var held = new TcpListener(IPAddress.Loopback, 0); held.Start();
        var listener = new Listener(new() { ListenPort = ((IPEndPoint)held.LocalEndpoint).Port });
        await Assert.ThrowsAsync<SocketException>(() => listener.Start(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SocketException>(() => listener.Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.Start(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CaptureStartupFailureReportsReadinessFailure()
    {
        using var directory = new TempDirectory();
        var file = Path.Combine(directory.Path, "not-a-directory"); await File.WriteAllTextAsync(file, "occupied", TestContext.Current.CancellationToken);
        var listener = new Listener(new() { ListenPort = 0, Capture = new() { Directory = file } });
        await Assert.ThrowsAnyAsync<IOException>(() => listener.Start(TestContext.Current.CancellationToken));
        Assert.True(listener.Ready.IsCompleted, "Readiness must settle when capture initialization fails.");
        await Assert.ThrowsAnyAsync<IOException>(() => listener.Ready);
    }
}

internal sealed class TunnelRig : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
    private readonly List<Task> _listeners = [];
    private readonly List<Task> _echoClients = [];
    private readonly Task _echoLoop;
    public ConcurrentQueue<TunnelEvent> Errors { get; } = new();
    public TaskCompletionSource<TunnelEvent> FirstError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Token => _stop.Token;
    public int EchoPort => ((IPEndPoint)_echo.LocalEndpoint).Port;
    public TunnelRig() { _echo.Start(); _echoLoop = EchoLoopAsync(); }
    public async Task<IPEndPoint> AddAsync(ForwardOptions options)
    {
        var listener = new Listener(options, entry => { if (entry.Level == "error") { Errors.Enqueue(entry); FirstError.TrySetResult(entry); } });
        _listeners.Add(listener.Start(Token));
        return await listener.Ready.WaitAsync(Token);
    }
    private async Task EchoLoopAsync()
    {
        try { while (true) { var client = await _echo.AcceptTcpClientAsync(Token); _echoClients.Add(EchoAsync(client)); } }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
    }
    private async Task EchoAsync(TcpClient client)
    {
        using (client)
        {
            var data = new byte[8192];
            try
            {
                int count;
                while ((count = await client.GetStream().ReadAsync(data, Token)) != 0)
                    await client.GetStream().WriteAsync(data.AsMemory(0, count), Token);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_listeners); await _echoLoop; await Task.WhenAll(_echoClients);
        _echo.Stop(); _stop.Dispose();
    }
}
