using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Tedd.TcpTunnel;

/// <summary>Hosts one TCP forward using the same transport behavior as the executable.</summary>
/// <remarks>Configure options before construction. Call Start once, await Ready for the bound endpoint,
/// and cancel and await Start during application shutdown. Connection failures are isolated and logged.</remarks>
public sealed class Listener
{
    private readonly ForwardOptions _options;
    private readonly Action<TunnelEvent>? _log;
    private readonly IpAccessControl _accessControl;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly TaskCompletionSource<IPEndPoint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private long _nextId;
    private readonly TrafficCounter _outbound = new();
    private readonly TrafficCounter _inbound = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _connectionStops = new();

    public ForwardTelemetry GetTelemetry() => new(_options.Name, _options.Mode.ToString(),
        Ready.IsCompletedSuccessfully ? Ready.Result.ToString() : $"{_options.ListenAddress}:{_options.ListenPort}",
        $"{_options.RemoteHost}:{_options.RemotePort}", ActiveConnections, _outbound.Snapshot(), _inbound.Snapshot());

    /// <summary>Disconnects current sessions; listeners stay bound and clients may reconnect.</summary>
    public void RestartConnections()
    {
        foreach (var stop in _connectionStops.Values)
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    /// <summary>Completes with the bound endpoint after startup, or faults/cancels if startup fails.</summary>
    public Task<IPEndPoint> Ready => _ready.Task;
    /// <summary>Current number of tracked connections, including connections negotiating or connecting.</summary>
    public int ActiveConnections => _connections.Count;

    /// <summary>Validates and configures one forward. The optional event callback must be thread-safe and must not throw.</summary>
    public Listener(ForwardOptions options, Action<TunnelEvent>? log = null)
    { options.Validate(); _options = options; _log = log; _accessControl = IpAccessControl.Create(options.AccessControl); }

    /// <summary>Runs acceptance and forwarding until cancellation. May only be called once.</summary>
    /// <remarks>Start returns the service lifetime task. Await Ready separately for startup. Cancellation closes
    /// active connections and waits for cleanup; expected shutdown cancellation is absorbed.</remarks>
    public async Task Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Listener can only be started once.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var limit = new SemaphoreSlim(_options.MaxConnections);
        Socket? listener = null;
        PcapWriter? capture = null;
        X509Certificate2? certificate = null;
        try
        {
            certificate = _options.ListenTls.LoadCertificate();
            listener = new Socket(IPAddress.Parse(_options.ListenAddress).AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            capture = _options.Capture.Directory is null ? null : new PcapWriter(_options.Name, _options.Capture);
            if (listener.AddressFamily == AddressFamily.InterNetworkV6) listener.DualMode = _options.Socket.DualMode;
            if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = !_options.Socket.ReuseAddress;
            if (_options.Socket.ReuseAddress) listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Bind(new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.ListenPort));
            listener.Listen(_options.Backlog);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;
            _ready.TrySetResult(endpoint);
            Log("info", "listener-started", $"Listening on {endpoint} ({_options.Mode}, {_options.Execution}).");
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
                    using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    _connectionStops[id] = connectionStop;
                    await start.Task.ConfigureAwait(false);
                    try { await ProcessAsync(id, accepted, capture, certificate, connectionStop.Token).ConfigureAwait(false); }
                    finally { _connectionStops.TryRemove(id, out _); limit.Release(); _connections.TryRemove(id, out _); }
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
            finally { capture?.Dispose(); certificate?.Dispose(); }
        }
    }

    private async Task ProcessAsync(long id, Socket accepted, PcapWriter? capture, X509Certificate2? certificate, CancellationToken token)
    {
        using (accepted)
        {
            var started = Environment.TickCount64;
            var source = (accepted.RemoteEndPoint as IPEndPoint)?.ToString();
            string? destination = null;
            var socksReady = false;
            try
            {
                Log("info", "connection-attempt", $"Connection attempt from {source ?? "unknown endpoint"}.", id: id, source: source);
                if (accepted.RemoteEndPoint is not IPEndPoint peer || !_accessControl.IsAllowed(peer.Address))
                {
                    Log("warning", "connection-denied", $"Connection from {source ?? "unknown endpoint"} denied by ACL.", id: id, source: source);
                    return;
                }
                SocketTuning.Apply(accepted, _options.Socket, message => Log("warning", "socket-tuning", message, id: id, source: source));
                var host = _options.RemoteHost;
                var port = _options.RemotePort;
                if (_options.Mode == TunnelMode.Socks5)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(_options.HandshakeTimeoutMilliseconds);
                    (host, port) = await Socks5.ReadTargetAsync(accepted, deadline.Token).ConfigureAwait(false);
                    socksReady = true;
                    Log("debug", "socks-target", $"SOCKS target is {host}:{port}.", id: id, source: source, destination: $"{host}:{port}");
                }
                destination = $"{host}:{port}";
                using var serverSession = _options.Mode == TunnelMode.Server
                    ? await TunnelHandshake.NegotiateAsync(accepted, _options, token).ConfigureAwait(false) : null;
                if (serverSession is not null) Log("debug", "handshake-completed", "Incoming tunnel handshake completed.", id: id, source: source, destination: destination);
                Log("debug", "connect-started", $"Connecting to {destination}.", id: id, source: source, destination: destination);
                using var remote = await Connector.ConnectAsync(host, port, _options.Retry, token,
                    (ex, attempt) => Log("warning", "connect-retry", $"Connect attempt {attempt} to {destination} failed.", ex, id, source, destination)).ConfigureAwait(false);
                destination = remote.RemoteEndPoint?.ToString() ?? destination;
                SocketTuning.Apply(remote, _options.Socket, message => Log("warning", "socket-tuning", message, id: id, source: source, destination: destination));
                if (socksReady) { await Socks5.ReplyAsync(accepted, 0, (IPEndPoint)remote.LocalEndPoint!, token).ConfigureAwait(false); socksReady = false; }
                using var clientSession = _options.Mode == TunnelMode.Client
                    ? await TunnelHandshake.NegotiateAsync(remote, _options, token).ConfigureAwait(false) : null;
                if (clientSession is not null) Log("debug", "handshake-completed", "Outgoing tunnel handshake completed.", id: id, source: source, destination: destination);
                Log("info", "connection-established", $"Connection established from {source} to {destination}.", id: id, source: source, destination: destination);
                await new TunnelConnection(accepted, remote, _options, capture, serverSession ?? clientSession, certificate,
                    message => Log("debug", "tls-established", message, id: id, source: source, destination: destination),
                    _outbound, _inbound).RunAsync(token).ConfigureAwait(false);
                Log("info", "connection-closed", "Connection closed.", id: id, source: source, destination: destination,
                    duration: Environment.TickCount64 - started);
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
                Log("debug", "connection-cancelled", "Connection cancelled during shutdown.", ex, id, source, destination,
                    Environment.TickCount64 - started);
            }
            catch (Exception ex)
            {
                if (_options.Mode == TunnelMode.Client && _options.Encryption.Algorithm != EncryptionAlgorithm.None)
                    SocketTuning.ResetOnClose(accepted);
                Log("error", "connection-failed", "Connection terminated.", ex, id, source, destination,
                    Environment.TickCount64 - started);
                if (socksReady)
                {
                    try { await Socks5.ReplyAsync(accepted, 5, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false); }
                    catch (Exception replyError) when (replyError is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        }
    }

    private void Log(string level, string eventName, string message, Exception? exception = null, long? id = null,
        string? source = null, string? destination = null, long? duration = null) =>
        _log?.Invoke(new(_options.Name, level, message, exception, eventName, id, source, destination, duration));
}

/// <summary>Hosts multiple forwarding listeners with coordinated startup failure and shutdown.</summary>
public sealed class TunnelHost
{
    /// <summary>Configured listeners in forward order; use their Ready tasks to observe startup.</summary>
    public IReadOnlyList<Listener> Listeners { get; }
    /// <summary>Validates the options and creates a listener for each forward. Logging is delivered to the supplied callback.</summary>
    public TunnelHost(TunnelOptions options, Action<TunnelEvent>? log = null)
    { options.Validate(); Listeners = options.Forwards.Select(f => new Listener(f, log)).ToArray(); }

    /// <summary>Runs all listeners until cancellation or a listener failure, then awaits coordinated cleanup.</summary>
    /// <remarks>Does not perform update checks, configure log sinks, or register an operating system service.</remarks>
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
