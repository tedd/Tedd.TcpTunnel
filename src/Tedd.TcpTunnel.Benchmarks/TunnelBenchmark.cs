using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;

namespace Tedd.TcpTunnel.Benchmarks;

[MemoryDiagnoser]
public class TunnelBenchmark
{
    [Params(ExecutionMode.Async, ExecutionMode.Dedicated)] public ExecutionMode Execution { get; set; }
    [Params(1, 32)] public int Connections { get; set; }
    [Params(64, 65536)] public int PayloadSize { get; set; }
    private readonly CancellationTokenSource _stop = new();
    private TcpListener _echo = null!;
    private Task _echoTask = null!, _tunnelTask = null!;
    private TcpClient[] _clients = null!;
    private byte[][] _sent = null!, _received = null!;
    private readonly List<Task> _echoClients = [];

    [GlobalSetup]
    public async Task Setup()
    {
        _echo = new(IPAddress.Loopback, 0); _echo.Start(); _echoTask = EchoAsync();
        var listener = new Listener(new() { ListenPort = 0, RemotePort = ((IPEndPoint)_echo.LocalEndpoint).Port, Execution = Execution });
        _tunnelTask = listener.Start(_stop.Token); var endpoint = await listener.Ready;
        _clients = new TcpClient[Connections]; _sent = new byte[Connections][]; _received = new byte[Connections][];
        for (var i = 0; i < Connections; i++)
        {
            _clients[i] = new() { NoDelay = true }; await _clients[i].ConnectAsync(endpoint);
            _sent[i] = new byte[PayloadSize]; _received[i] = new byte[PayloadSize]; new Random(i).NextBytes(_sent[i]);
        }
    }
    private async Task EchoAsync()
    {
        try { while (true) _echoClients.Add(EchoClientAsync(await _echo.AcceptTcpClientAsync(_stop.Token))); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task EchoClientAsync(TcpClient client)
    {
        using (client)
        {
            var data = new byte[65536];
            try { int count; while ((count = await client.GetStream().ReadAsync(data, _stop.Token)) != 0) await client.GetStream().WriteAsync(data.AsMemory(0, count), _stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
    }
    [Benchmark]
    public Task RoundTripAllConnections() => Task.WhenAll(Enumerable.Range(0, Connections).Select(async i =>
    {
        var stream = _clients[i].GetStream(); await stream.WriteAsync(_sent[i]); await stream.ReadExactlyAsync(_received[i]);
    }));
    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var client in _clients) client.Dispose(); await _stop.CancelAsync();
        await _tunnelTask; await _echoTask; await Task.WhenAll(_echoClients); _echo.Stop(); _stop.Dispose();
    }
}
