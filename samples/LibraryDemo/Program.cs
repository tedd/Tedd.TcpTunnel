using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tedd.TcpTunnel;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    switch (args.FirstOrDefault())
    {
        case "server": await RunServerAsync(stop.Token); break;
        case "client": await RunClientAsync(stop.Token); break;
        case "forward-client":
            await RunForwardAsync(TunnelMode.Client, 9000, 9001, stop.Token); break;
        case "forward-server":
            await RunForwardAsync(TunnelMode.Server, 9001, 9002, stop.Token); break;
        default:
            Console.WriteLine("Usage: LibraryDemo server|client|forward-client|forward-server");
            Console.WriteLine("Direct server: localhost:9001; client sends one echo request.");
            Console.WriteLine("Forwarding: client localhost:9000 -> tunnel :9001 -> destination :9002.");
            Console.WriteLine("Set TUNNEL_KEY to the same generated Base64 key in both processes for AES-GCM.");
            break;
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

static EncryptionOptions Encryption(bool client)
{
    var key = Environment.GetEnvironmentVariable("TUNNEL_KEY");
    return key is null ? new() : client
        ? new() { Algorithm = EncryptionAlgorithm.AesGcm, KeyId = "demo", Key = key }
        : new() { Algorithm = EncryptionAlgorithm.AesGcm, Keys = new() { ["demo"] = key } };
}

static async Task RunClientAsync(CancellationToken token)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
    deadline.CancelAfter(TimeSpan.FromSeconds(30));
    await using var stream = await TunnelClient.ConnectAsync("127.0.0.1", 9001,
        new() { Compression = Codec.Lz4, Encryption = Encryption(true) }, deadline.Token);
    var request = Encoding.UTF8.GetBytes("Hello through the tunnel");
    await stream.WriteAsync(request, deadline.Token);
    await stream.CompleteWritesAsync(deadline.Token);
    var response = new byte[request.Length];
    await stream.ReadExactlyAsync(response, deadline.Token);
    if (await stream.ReadAsync(new byte[1], deadline.Token) != 0) throw new IOException("Unexpected response bytes.");
    Console.WriteLine(Encoding.UTF8.GetString(response));
}

static async Task RunServerAsync(CancellationToken token)
{
    using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
    token = shutdown.Token;
    using var listener = new TcpListener(IPAddress.Loopback, 9001);
    using var limit = new SemaphoreSlim(64);
    var handlers = new ConcurrentDictionary<long, Task>();
    long nextId = 0;
    listener.Start();
    Console.WriteLine($"Listening on {listener.LocalEndpoint}");
    try
    {
        while (true)
        {
            await limit.WaitAsync(token);
            Socket accepted;
            try { accepted = await listener.AcceptSocketAsync(token); }
            catch { limit.Release(); throw; }
            var id = ++nextId;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task HandleAsync()
            {
                await start.Task;
                // This using also covers option validation failures before ownership transfer.
                using (accepted)
                {
                    try
                    {
                        await using var stream = await TunnelServer.AcceptAsync(accepted,
                            new() { Compression = Codec.Lz4, Encryption = Encryption(false) }, token);
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await stream.ReadAsync(buffer, token)) != 0)
                            await stream.WriteAsync(buffer.AsMemory(0, count), token);
                        await stream.CompleteWritesAsync(token);
                    }
                    catch (Exception) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { Console.Error.WriteLine($"Connection {id}: {ex.Message}"); }
                    finally { limit.Release(); handlers.TryRemove(id, out _); }
                }
            }
            handlers[id] = HandleAsync();
            start.SetResult();
        }
    }
    finally { await shutdown.CancelAsync(); listener.Stop(); await Task.WhenAll(handlers.Values); }
}

static async Task RunForwardAsync(TunnelMode mode, int listenPort, int remotePort, CancellationToken token)
{
    var listener = new Listener(new()
    {
        Mode = mode, ListenPort = listenPort, RemotePort = remotePort,
        Compression = Codec.Lz4, Encryption = Encryption(mode == TunnelMode.Client)
    }, e => Console.Error.WriteLine($"{e.Level}: {e.Message}"));
    var running = listener.Start(token);
    Console.WriteLine(await listener.Ready);
    await running;
}
