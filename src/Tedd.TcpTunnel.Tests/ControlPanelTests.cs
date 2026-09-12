using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Tedd.TcpTunnel.Console;
using Tedd.TcpTunnel.Management;

namespace Tedd.TcpTunnel.Tests;

public sealed class ControlPanelTests
{
    [Fact]
    public void CountersAreCoherentUnderConcurrentUpdates()
    {
        var counter = new TrafficCounter();
        Parallel.For(0, 10000, i => counter.Add(100, i % 2 == 0 ? 40 : 100, i % 2 == 0));
        Assert.Equal(new TrafficTotals(1000000, 700000, 200000), counter.Snapshot());
        Assert.Throws<ArgumentOutOfRangeException>(() => counter.Add(-1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => counter.Add(0, -1, false));
    }

    [Theory]
    [InlineData(Codec.None, ExecutionMode.Async)]
    [InlineData(Codec.Brotli, ExecutionMode.Async)]
    [InlineData(Codec.Lz4, ExecutionMode.Dedicated)]
    public async Task TelemetryMeasuresBothDirectionsAndResetKeepsListener(Codec codec, ExecutionMode execution)
    {
        await using var rig = new TunnelRig();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(rig.Token);
        var port = rig.EchoPort;
        if (codec != Codec.None)
            port = (await rig.AddAsync(new() { Name = "server", Mode = TunnelMode.Server, ListenPort = 0, RemotePort = port, Compression = codec })).Port;
        var listener = new Listener(new() { Name = "client", Mode = codec == Codec.None ? TunnelMode.Raw : TunnelMode.Client,
            ListenPort = 0, RemotePort = port, Compression = codec, Execution = execution });
        var run = listener.Start(stop.Token);
        try
        {
            var endpoint = await listener.Ready;
            using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
            var data = new byte[32768]; Array.Fill<byte>(data, 42);
            await client.GetStream().WriteAsync(data, rig.Token);
            await client.GetStream().ReadExactlyAsync(new byte[data.Length], rig.Token);
            while (listener.GetTelemetry().Inbound.UncompressedBytes != data.Length) await Task.Delay(10, rig.Token);
            var telemetry = listener.GetTelemetry();
            Assert.Equal(data.Length, telemetry.Outbound.UncompressedBytes);
            Assert.Equal(1, telemetry.ActiveConnections);
            if (codec == Codec.None)
            {
                Assert.Equal(data.Length, telemetry.Outbound.EncodedBytes);
                Assert.Equal(0, telemetry.Inbound.CompressedBytes);
            }
            else
            {
                Assert.InRange(telemetry.Outbound.EncodedBytes, 1, data.Length - 1);
                Assert.Equal(telemetry.Outbound.EncodedBytes, telemetry.Outbound.CompressedBytes);
                Assert.InRange(telemetry.Inbound.EncodedBytes, 1, data.Length - 1);
            }
            listener.RestartConnections();
            while (listener.ActiveConnections != 0) await Task.Delay(10, rig.Token);
            using var next = new TcpClient(); await next.ConnectAsync(endpoint, rig.Token);
            await next.GetStream().WriteAsync(new byte[] { 7 }, rig.Token);
            var output = new byte[1]; await next.GetStream().ReadExactlyAsync(output, rig.Token);
            Assert.Equal(7, output[0]);
        }
        finally { await stop.CancelAsync(); await run; }
    }

    [Fact]
    public void SaveValidatesAndDetectsConflictingEdits()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var json = JsonSerializer.Serialize(new TunnelOptions { Forwards = [new()] }, ConfigurationFile.Json);
        var first = ConfigurationFile.Save(path, json, null);
        Assert.Equal(first, ConfigurationFile.Read(path));
        Assert.Throws<InvalidOperationException>(() => ConfigurationFile.Save(path, json, null));
        Assert.Throws<InvalidOperationException>(() => ConfigurationFile.Save(path, json, "old"));
        Assert.Throws<ArgumentException>(() => ConfigurationFile.Save(path, "{}", first.Revision));
        Assert.Throws<JsonException>(() => ConfigurationFile.Parse("{\"Forwards\":[{}],\"Typo\":true}"));
        Assert.Throws<ArgumentException>(() => ConfigurationFile.Parse("null"));
        var second = ConfigurationFile.Save(path, json + "\n", first.Revision);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(json + "\n", File.ReadAllText(path));
        File.Delete(path);
        Assert.Throws<InvalidOperationException>(() => ConfigurationFile.Save(path, json, second.Revision));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void RateHistoryUsesDaemonClockAndDoesNotBridgeRestarts()
    {
        var id = Guid.NewGuid();
        var history = new TelemetryHistory(2);
        DaemonStatus Sample(double seconds, long count, Guid? instance = null) => new(instance ?? id, 1, false, "config",
            seconds, DateTimeOffset.UnixEpoch, "rev", false, [new("a", "Client", "local", "remote", 1,
                new(count, count / 2, count / 4), new(count * 2, count, 0))]);
        history.Add(Sample(10, 100));
        Assert.Empty(history.For("a"));
        history.Add(Sample(12, 4000100));
        var sample = Assert.Single(history.For("a"));
        Assert.Equal(2, sample.Outbound.UncompressedMBps);
        Assert.Equal(1, sample.Outbound.EncodedMBps);
        Assert.Equal(0.5, sample.Outbound.CompressedMBps);
        Assert.Equal(0.5, sample.Outbound.UncompressedPayloadMBps);
        Assert.Equal(2, sample.Outbound.CompressionRatio);
        Assert.Null(new TrafficRate(0, 0, 0).CompressionRatio);
        history.Add(Sample(13, 4000100)); history.Add(Sample(14, 4000100));
        Assert.Equal(2, history.For("a").Count);
        history.Add(Sample(1, 0, Guid.NewGuid())); Assert.Empty(history.For("a"));
        history.Add(Sample(30, 0)); Assert.Empty(history.For("a"));
        history.Reset(); Assert.Empty(history.For("missing"));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1048577)]
    public async Task ProtocolRejectsInvalidMessageSizes(int length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlProtocol.ReadAsync<ControlRequest>(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProtocolBoundsWritesAndRejectsTruncatedInput()
    {
        await using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlProtocol.WriteAsync(stream, new string('x', ControlProtocol.MaximumMessageBytes), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EndOfStreamException>(() => ControlProtocol.ReadAsync<ControlRequest>(stream, TestContext.Current.CancellationToken));
        await ControlProtocol.WriteAsync<string?>(stream, null, TestContext.Current.CancellationToken); stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlProtocol.ReadAsync<ControlRequest>(stream, TestContext.Current.CancellationToken));
        Assert.Equal(ControlProtocol.ServicePipe("Abc"), ControlProtocol.ServicePipe("ABC"));
        Assert.NotEqual(ControlProtocol.ServicePipe("Abc"), ControlProtocol.ServicePipe("Def"));
        Assert.Equal(ControlProtocol.DaemonPipe("a.json"), ControlProtocol.DaemonPipe(Path.GetFullPath("a.json")));
    }

    [Fact]
    public async Task PipeHandlesReconnectsAndReportsInvalidOperations()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "tcptunnel-test-" + Guid.NewGuid().ToString("N");
        using var server = new ControlServer(name, false, (request, _) => request.Operation == "throw" ?
            throw new ArgumentException("Invalid settings") : Task.FromResult(new ControlResponse(true)));
        var serving = server.RunAsync(stop.Token);
        try
        {
            Assert.True((await ControlProtocol.SendAsync(name, new("status"), stop.Token)).Success);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ControlProtocol.SendAsync(name, new("throw"), stop.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ControlProtocol.SendAsync(name, new("status", Version: 99), stop.Token));
            using (var malformed = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await malformed.ConnectAsync(stop.Token);
                await malformed.WriteAsync(new byte[] { 0, 0, 0, 0 }, stop.Token);
                Assert.False((await ControlProtocol.ReadAsync<ControlResponse>(malformed, stop.Token)).Success);
            }
            Assert.True((await ControlProtocol.SendAsync(name, new("status"), stop.Token)).Success);
        }
        finally { await stop.CancelAsync(); await serving; }
    }

    [Fact]
    public async Task DaemonExposesConfigurationAndPendingRestartWithoutDisruptingTraffic()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "daemon.json");
        var options = new TunnelOptions { Forwards = [new() { ListenPort = 0 }], Update = new() { CheckOnStartup = false } };
        var original = ConfigurationFile.Save(path, JsonSerializer.Serialize(options, ConfigurationFile.Json), null);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = ManagedDaemon.RunAsync(Configuration.Parse(["--config", path]), _ => { }, stop.Token);
        var pipe = ControlProtocol.DaemonPipe(path);
        try
        {
            var before = (await ControlProtocol.SendAsync(pipe, new("status"), stop.Token)).Status!;
            Assert.False(before.IsService); Assert.False(before.PendingConfiguration); Assert.Single(before.Forwards);
            Assert.Equal(original, (await ControlProtocol.SendAsync(pipe, new("configuration"), stop.Token)).Configuration);
            await ControlProtocol.SendAsync(pipe, new("save", original.Json + "\n", original.Revision), stop.Token);
            Assert.True((await ControlProtocol.SendAsync(pipe, new("status"), stop.Token)).Status!.PendingConfiguration);
            await ControlProtocol.SendAsync(pipe, new("restart-connections"), stop.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ControlProtocol.SendAsync(pipe, new("restart-connections", Forward: "missing"), stop.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ControlProtocol.SendAsync(pipe, new("unknown"), stop.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ControlProtocol.SendAsync(pipe, new("save"), stop.Token));
            await ControlProtocol.SendAsync(pipe, new("stop"), stop.Token);
            await run;
        }
        finally { await stop.CancelAsync(); await run; }
    }

    [Fact]
    public void FirewallRulesAreScopedAndSkipLoopbackAndDynamicPorts()
    {
        var options = new TunnelOptions { Forwards = [
            new() { Name = "loopback" }, new() { Name = "v6", ListenAddress = "::1" },
            new() { Name = "dynamic", ListenAddress = "0.0.0.0", ListenPort = 0 },
            new() { Name = "lan", ListenAddress = "0.0.0.0", ListenPort = 9001 }] };
        var rule = Assert.Single(FirewallPlan.Build(options, "test", "tcptunnel.exe"));
        Assert.Equal("domain,private", rule.Profiles); Assert.Equal("LocalSubnet", rule.RemoteAddresses);
        Assert.Equal("any", rule.LocalAddress); Assert.Equal(9001, rule.Port);
        Assert.Contains("program=" + Path.GetFullPath("tcptunnel.exe"), rule.AddArguments);
        Assert.Contains("name=Tedd.TcpTunnel.test.lan", rule.DeleteArguments);
        Assert.Throws<ArgumentException>(() => FirewallPlan.Build(options, "bad name", "exe"));
        Assert.Throws<ArgumentException>(() => FirewallPlan.Build(options, "test", "exe", "bad"));
        Assert.Throws<ArgumentException>(() => FirewallPlan.Build(options, "test", "exe", remoteAddresses: "any & calc"));
        Assert.Equal("public", Assert.Single(FirewallPlan.Build(options, "test", "exe", "public", "192.0.2.0/24")).Profiles);
    }

    [Fact]
    public async Task PipeAuthenticatesServerBeforeSendingSecrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "tcptunnel-auth-" + Guid.NewGuid().ToString("N");
        var received = 0;
        using var server = new ControlServer(name, false, (_, _) => { Interlocked.Increment(ref received); return Task.FromResult(new ControlResponse(true)); });
        var serving = server.RunAsync(stop.Token);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ControlProtocol.SendAsync(name,
                new("save", "secret"), stop.Token, int.MaxValue));
            Assert.Equal(0, Volatile.Read(ref received));
            Assert.True((await ControlProtocol.SendAsync(name, new("status"), stop.Token, Environment.ProcessId)).Success);
            Assert.Equal(1, Volatile.Read(ref received));
        }
        finally { await stop.CancelAsync(); await serving; }
    }

    [Fact]
    public void AggregateHistoryCombinesCompressedAndPlainTraffic()
    {
        var history = new TelemetryHistory();
        var id = Guid.NewGuid();
        DaemonStatus Sample(int second, long bytes) => new(id, 1, false, "file", second, DateTimeOffset.UnixEpoch, "", false,
        [
            new("compressed", "Client", "", "", 1, new(bytes * 2, bytes, bytes), new(0, 0, 0)),
            new("plain", "Raw", "", "", 1, new(bytes, bytes, 0), new(0, 0, 0))
        ]);
        history.Add(Sample(0, 0));
        history.Add(Sample(1, 1000000));
        var total = Assert.Single(history.For(TelemetryHistory.AllForwards)).Outbound;
        Assert.Equal(3, total.UncompressedMBps);
        Assert.Equal(2, total.EncodedMBps);
        Assert.Equal(1, total.CompressedMBps);
        Assert.Equal(1, total.UncompressedPayloadMBps);
        Assert.Equal(1.5, total.CompressionRatio);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TcpTunnel-panel-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
