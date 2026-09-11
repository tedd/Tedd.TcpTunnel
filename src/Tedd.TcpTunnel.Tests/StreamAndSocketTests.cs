using System.Net;
using System.Net.Sockets;

namespace Tedd.TcpTunnel.Tests;

public sealed class StreamAndSocketTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(1024)] [InlineData(65536)]
    public async Task PooledCopyPreservesDataAndFlushes(int size)
    {
        var bytes = new byte[150000]; new Random(42).NextBytes(bytes);
        using var source = new MemoryStream(bytes); using var target = new FlushStream();
        await source.CopyToAsyncWithFlush(target, size, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, target.ToArray()); Assert.True(target.FlushCount > 0);
    }
    [Fact]
    public async Task CopyPropagatesErrorsAndCancellation()
    {
        using var source = new MemoryStream(new byte[100]); using var target = new MemoryStream();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CopyToAsyncWithFlush(target, 1024, stop.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ExtensionMethods.CopyToAsyncWithFlush(null!, target, 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => source.CopyToAsyncWithFlush(null!, 1, TestContext.Current.CancellationToken));
        target.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.CopyToAsyncWithFlush(target, 1, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task PlatformSocketTuningIsAppliedOrReportsFailure()
    {
        using var listener = new TcpListener(IPAddress.IPv6Any, 0); listener.Server.DualMode = true; listener.Start();
        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        var warnings = new List<string>();
        var options = new SocketOptions { NoDelay = false, SendBufferSize = 32768, ReceiveBufferSize = 32768, KeepAlive = false,
            LinuxQuickAck = true, LinuxUserTimeoutMilliseconds = 1000, LinuxCongestionControl = "cubic", WindowsLoopbackFastPath = true };
        SocketTuning.Apply(client, options, warnings.Add); SocketTuning.QuickAck(client, options);
        Assert.False(client.NoDelay); Assert.True(client.SendBufferSize >= 32768);
        if (OperatingSystem.IsLinux())
        {
            options.LinuxCongestionControl = "notavailable"; SocketTuning.Apply(client, options, warnings.Add);
            Assert.NotEmpty(warnings);
        }
        SocketTuning.ShutdownSend(client); client.Dispose(); SocketTuning.ShutdownSend(client);
    }
    [Fact]
    public async Task Ipv6ListenerAndReuseAddressAreSupported()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new Listener(new() { ListenAddress = "::1", ListenPort = 0, Socket = new() { DualMode = true, ReuseAddress = true } });
        var running = listener.Start(stop.Token); Assert.Equal(AddressFamily.InterNetworkV6, (await listener.Ready).AddressFamily);
        await stop.CancelAsync(); await running;
    }
    private sealed class FlushStream : MemoryStream
    {
        public int FlushCount { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken) { FlushCount++; return Task.CompletedTask; }
    }
}
