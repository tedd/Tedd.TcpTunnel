using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Tedd.TcpTunnel;

public sealed class Listener
{
    private readonly ForwardOptions _options;
    private readonly Action<TunnelEvent>? _log;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly TaskCompletionSource<IPEndPoint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private long _nextId;
    public Task<IPEndPoint> Ready => _ready.Task;
    public int ActiveConnections => _connections.Count;

    public Listener(ForwardOptions options, Action<TunnelEvent>? log = null)
    { options.Validate(); _options = options; _log = log; }

    public async Task Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Listener can only be started once.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var limit = new SemaphoreSlim(_options.MaxConnections);
        Socket? listener = null;
        PcapWriter? capture = null;
        try
        {
            listener = new Socket(IPAddress.Parse(_options.ListenAddress).AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            capture = _options.Capture.Directory is null ? null : new PcapWriter(_options.Name, _options.Capture);
            if (listener.AddressFamily == AddressFamily.InterNetworkV6) listener.DualMode = _options.Socket.DualMode;
            if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = !_options.Socket.ReuseAddress;
            if (_options.Socket.ReuseAddress) listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Bind(new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.ListenPort));
            listener.Listen(_options.Backlog);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;
            _ready.TrySetResult(endpoint);
            Log("info", $"Listening on {endpoint} ({_options.Mode}, {_options.Execution}).");
            while (true)
            {
                await limit.WaitAsync(stop.Token).ConfigureAwait(false);
                Socket accepted;
                try { accepted = await listener.AcceptAsync(stop.Token).ConfigureAwait(false); }
                catch { limit.Release(); throw; }
                var id = Interlocked.Increment(ref _nextId);
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task ProcessTrackedAsync()
                {
                    await start.Task.ConfigureAwait(false);
                    try { await ProcessAsync(accepted, capture, stop.Token).ConfigureAwait(false); }
                    finally { limit.Release(); _connections.TryRemove(id, out var ignored); }
                }
                var task = ProcessTrackedAsync();
                _connections[id] = task;
                start.SetResult();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { _ready.TrySetCanceled(stop.Token); }
        catch (Exception ex) { _ready.TrySetException(ex); throw; }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            listener?.Dispose();
            try { await Task.WhenAll(_connections.Values).ConfigureAwait(false); }
            finally { capture?.Dispose(); }
        }
    }

    private async Task ProcessAsync(Socket accepted, PcapWriter? capture, CancellationToken token)
    {
        using (accepted)
        {
            var socksReady = false;
            try
            {
                SocketTuning.Apply(accepted, _options.Socket, message => Log("warning", message));
                var host = _options.RemoteHost;
                var port = _options.RemotePort;
                if (_options.Mode == TunnelMode.Socks5)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(_options.HandshakeTimeoutMilliseconds);
                    (host, port) = await Socks5.ReadTargetAsync(accepted, deadline.Token).ConfigureAwait(false);
                    socksReady = true;
                }
                using var remote = await Connector.ConnectAsync(host, port, _options.Retry, token,
                    (ex, attempt) => Log("warning", $"Connect attempt {attempt} to {host}:{port} failed.", ex)).ConfigureAwait(false);
                SocketTuning.Apply(remote, _options.Socket, message => Log("warning", message));
                if (socksReady) { await Socks5.ReplyAsync(accepted, 0, (IPEndPoint)remote.LocalEndPoint!, token).ConfigureAwait(false); socksReady = false; }
                await new TunnelConnection(accepted, remote, _options, capture).RunAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
            catch (Exception ex)
            {
                Log("error", "Connection terminated.", ex);
                if (socksReady)
                {
                    try { await Socks5.ReplyAsync(accepted, 5, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false); }
                    catch (Exception replyError) when (replyError is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        }
    }

    private void Log(string level, string message, Exception? exception = null) => _log?.Invoke(new(_options.Name, level, message, exception));
}

public sealed class TunnelHost
{
    public IReadOnlyList<Listener> Listeners { get; }
    public TunnelHost(TunnelOptions options, Action<TunnelEvent>? log = null)
    { options.Validate(); Listeners = options.Forwards.Select(f => new Listener(f, log)).ToArray(); }

    public async Task RunAsync(CancellationToken token = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task Run(Listener listener)
        {
            try { await listener.Start(stop.Token).ConfigureAwait(false); }
            finally { await stop.CancelAsync().ConfigureAwait(false); }
        }
        await Task.WhenAll(Listeners.Select(Run)).ConfigureAwait(false);
    }
}
