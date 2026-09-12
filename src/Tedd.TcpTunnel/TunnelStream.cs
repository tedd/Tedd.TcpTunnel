using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Tedd.TcpTunnel;

/// <summary>A duplex, non-seekable application stream carried over a TTN2 or authenticated TTN3 connection.</summary>
/// <remarks>Obtain instances from <see cref="TunnelClient.ConnectAsync"/> or <see cref="TunnelServer.AcceptAsync"/>.
/// One read and one write can run concurrently. Calls in the same direction are serialized.
/// Writes are immediately framed; Flush does not wait for peer acknowledgement.
/// Call <see cref="CompleteWritesAsync"/> to send an authenticated half-close while continuing to read.
/// Disposal aborts both directions. Cancellation during wire I/O also aborts the connection, since a partial
/// frame cannot safely be retried. A transport EOF without a protocol FIN throws instead of returning zero.</remarks>
public sealed class TunnelStream : Stream
{
    private readonly Socket _socket;
    private readonly ForwardOptions _options;
    private readonly TunnelSession _session;
    private readonly SocketTransport _transport;
    private readonly BlockCodec _encoder, _decoder;
    private readonly byte[] _send, _sendEncrypted, _receive, _receiveEncrypted, _decoded;
    private readonly byte[] _header = new byte[Protocol.HeaderSize];
    private readonly SemaphoreSlim _readGate = new(1), _writeGate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeLock = new();
    private readonly Task _maintenance;
    private readonly PcapWriter? _capture;
    private readonly Action<TunnelEvent>? _log;
    private readonly IPEndPoint _localEndpoint, _remoteEndpoint;
    private readonly long _started = Environment.TickCount64;
    private long _lastActivity = Environment.TickCount64, _lastSend = Environment.TickCount64;
    private uint _sendSequence, _receiveSequence;
    private int _offset, _count;
    private volatile bool _readClosed, _writeClosed, _disposed;
    private Exception? _failure;
    private Task? _cleanup;

    private TunnelStream(Socket socket, ForwardOptions options, TunnelSession session, Action<TunnelEvent>? log)
    {
        _socket = socket; _options = options; _session = session; _log = log;
        _localEndpoint = (IPEndPoint)socket.LocalEndPoint!;
        _remoteEndpoint = (IPEndPoint)socket.RemoteEndPoint!;
        _transport = new(socket, false, options.Socket);
        _send = new byte[Protocol.HeaderSize + BlockCodec.MaxEncodedLength(options.BufferSize)];
        _sendEncrypted = session.Send is null ? [] : new byte[_send.Length + FrameCipher.TagSize];
        _receive = new byte[BlockCodec.MaxEncodedLength(session.PeerFrame)];
        _receiveEncrypted = session.Receive is null ? [] : new byte[_receive.Length + FrameCipher.TagSize];
        _decoded = new byte[session.PeerFrame];
        _encoder = new(options);
        try
        {
            _decoder = new(options);
            try { _capture = options.Capture.Directory is null ? null : new PcapWriter(options.Name, options.Capture); }
            catch { _decoder.Dispose(); throw; }
        }
        catch { _encoder.Dispose(); throw; }
        _maintenance = Task.WhenAll(MaintainAsync(), WatchIdleAsync());
    }

    internal static async Task<TunnelStream> OpenAsync(Socket socket, ForwardOptions options,
        CancellationToken token, Action<TunnelEvent>? log)
    {
        TunnelSession? session = null;
        try
        {
            SocketTuning.Apply(socket, options.Socket, message => log?.Invoke(new(options.Name, "warning", message, Event: "socket-tuning")));
            session = await TunnelHandshake.NegotiateAsync(socket, options, token).ConfigureAwait(false);
            log?.Invoke(new(options.Name, "info", "Direct tunnel stream established.", Event: "connection-established",
                Source: socket.LocalEndPoint?.ToString(), Destination: socket.RemoteEndPoint?.ToString()));
            return new TunnelStream(socket, options, session, log);
        }
        catch { session?.Dispose(); socket.Dispose(); throw; }
    }

    /// <summary>True while the stream is open and has not failed. Reads return zero after the peer's FIN.</summary>
    public override bool CanRead => !_disposed && _failure is null;
    /// <summary>Always false.</summary>
    public override bool CanSeek => false;
    /// <summary>True until local write completion, failure or disposal.</summary>
    public override bool CanWrite => !_disposed && _failure is null && !_writeClosed;
    /// <summary>Not supported on a tunnel stream.</summary>
    public override long Length => throw new NotSupportedException();
    /// <summary>Not supported on a tunnel stream.</summary>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    /// <summary>Not supported on a tunnel stream.</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>Not supported on a tunnel stream.</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is { } failure) throw new IOException("The tunnel stream has failed and cannot be reused.", failure);
    }

    private void Abort(Exception? failure = null)
    {
        if (failure is not null && !_disposed) Interlocked.CompareExchange(ref _failure, failure, null);
        _stop.Cancel();
        _socket.Dispose();
    }

    /// <summary>Reads application bytes, consuming and authenticating complete frames internally.</summary>
    /// <returns>Bytes copied, or zero for an empty buffer or a validated peer FIN.</returns>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckOpen(); cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _readGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            while (_count == 0 && !_readClosed)
            {
                await _transport.ReadExactlyAsync(_header, stop.Token).ConfigureAwait(false);
                var (type, raw, wire) = Protocol.ReadHeader(_header, _session.PeerFrame);
                if (_session.Receive is { } cipher)
                {
                    await _transport.ReadExactlyAsync(_receiveEncrypted.AsMemory(0, wire + FrameCipher.TagSize), stop.Token).ConfigureAwait(false);
                    cipher.Decrypt(_header, _receiveEncrypted.AsSpan(0, wire + FrameCipher.TagSize), _receive.AsSpan(0, wire));
                }
                else await _transport.ReadExactlyAsync(_receive.AsMemory(0, wire), stop.Token).ConfigureAwait(false);
                if (type == FrameType.Fin) { _readClosed = true; break; }
                if (type == FrameType.Noop) continue;
                _decoder.Decode(_receive, wire, _decoded.AsSpan(0, raw));
                _capture?.Write(_remoteEndpoint, _localEndpoint, _decoded.AsSpan(0, raw), ref _receiveSequence);
                _offset = 0; _count = raw;
                Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            }
            var count = Math.Min(buffer.Length, _count);
            _decoded.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count; _count -= count;
            if (count > 0) Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            return count;
        }
        catch (Exception ex) { Abort(ex); throw; }
        finally { _readGate.Release(); }
    }

    /// <summary>Writes all bytes as one or more frames. Successful completion means the bytes reached the local transport.</summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckOpen(); cancellationToken.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _writeGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            if (_writeClosed) throw new InvalidOperationException("The tunnel write direction is complete.");
            try
            {
                while (!buffer.IsEmpty)
                {
                    var count = Math.Min(buffer.Length, _options.BufferSize);
                    var length = _encoder.Encode(buffer.Span[..count], _send, Protocol.HeaderSize);
                    await SendFrameAsync(FrameType.Data, count, length, stop.Token).ConfigureAwait(false);
                    _capture?.Write(_localEndpoint, _remoteEndpoint, buffer.Span[..count], ref _sendSequence);
                    Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
                    buffer = buffer[count..];
                }
            }
            catch (Exception ex) { Abort(ex); throw; }
        }
        finally { _writeGate.Release(); }
    }

    private async ValueTask SendFrameAsync(FrameType type, int raw, int wire, CancellationToken token)
    {
        Protocol.WriteHeader(_send, type, raw, wire);
        if (_session.Send is { } cipher)
        {
            _send.AsSpan(0, Protocol.HeaderSize).CopyTo(_sendEncrypted);
            cipher.Encrypt(_send.AsSpan(0, Protocol.HeaderSize), _send.AsSpan(Protocol.HeaderSize, wire), _sendEncrypted.AsSpan(Protocol.HeaderSize));
            await _transport.SendAsync(_sendEncrypted.AsMemory(0, Protocol.HeaderSize + wire + FrameCipher.TagSize), token).ConfigureAwait(false);
        }
        else await _transport.SendAsync(_send.AsMemory(0, Protocol.HeaderSize + wire), token).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastSend, Environment.TickCount64);
    }

    /// <summary>Sends a protocol FIN and shuts down TCP sending without closing the read direction. Repeated calls are harmless.</summary>
    /// <param name="cancellationToken">Cancels the operation; interruption of wire I/O aborts the connection.</param>
    public async ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
    {
        CheckOpen();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _writeGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            if (_writeClosed) return;
            await SendFrameAsync(FrameType.Fin, 0, 0, stop.Token).ConfigureAwait(false);
            _writeClosed = true;
            SocketTuning.ShutdownSend(_socket);
        }
        catch (Exception ex) { Abort(ex); throw; }
        finally { _writeGate.Release(); }
    }

    private async Task WatchIdleAsync()
    {
        if (_options.IdleTimeoutMilliseconds == 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(_options.IdleTimeoutMilliseconds, 1000)));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (_readClosed && _writeClosed) return;
                if (Environment.TickCount64 - Interlocked.Read(ref _lastActivity) >= _options.IdleTimeoutMilliseconds)
                { Abort(new TimeoutException("The tunnel application stream exceeded its idle timeout.")); return; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task MaintainAsync()
    {
        if (_options.HeartbeatMilliseconds == 0) return;
        var interval = _options.HeartbeatMilliseconds;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(interval, 1000)));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (_readClosed && _writeClosed) return;
                if (_options.HeartbeatMilliseconds == 0 || _writeClosed ||
                    Environment.TickCount64 - Interlocked.Read(ref _lastSend) < _options.HeartbeatMilliseconds) continue;
                // Data writes take precedence over optional heartbeats.
                if (!await _writeGate.WaitAsync(0, _stop.Token).ConfigureAwait(false)) continue;
                try
                {
                    if (!_writeClosed && Environment.TickCount64 - Interlocked.Read(ref _lastSend) >= _options.HeartbeatMilliseconds)
                        await SendFrameAsync(FrameType.Noop, 0, 0, _stop.Token).ConfigureAwait(false);
                }
                finally { _writeGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Abort(ex); }
    }

    /// <summary>Waits for the current writer. Writes are already unbuffered at the application-stream level.</summary>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        CheckOpen();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _writeGate.WaitAsync(stop.Token).ConfigureAwait(false);
        try { CheckOpen(); }
        finally { _writeGate.Release(); }
    }
    /// <inheritdoc/>
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    { ValidateBufferArguments(buffer, offset, count); return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult(); }
    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    { ValidateBufferArguments(buffer, offset, count); WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult(); }
    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { ValidateBufferArguments(buffer, offset, count); return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask(); }
    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { ValidateBufferArguments(buffer, offset, count); return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask(); }

    private Task BeginDispose()
    {
        lock (_disposeLock)
        {
            if (_cleanup is not null) return _cleanup;
            _disposed = true;
            Abort();
            return _cleanup = CleanupAsync();
        }
    }

    private async Task CleanupAsync()
    {
        await _maintenance.ConfigureAwait(false);
        await _readGate.WaitAsync().ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _session.Dispose(); _encoder.Dispose(); _decoder.Dispose(); _transport.Dispose();
            CryptographicOperations.ZeroMemory(_send); CryptographicOperations.ZeroMemory(_receive);
            CryptographicOperations.ZeroMemory(_decoded);
            _capture?.Dispose();
            _log?.Invoke(new(_options.Name, _failure is null ? "info" : "error", "Direct tunnel stream closed.", _failure,
                _failure is null ? "connection-closed" : "connection-failed", Source: _localEndpoint.ToString(),
                Destination: _remoteEndpoint.ToString(), DurationMilliseconds: Environment.TickCount64 - _started));
        }
        finally { _writeGate.Release(); _readGate.Release(); }
        // Keep the cancelled source and gates usable for operations racing with disposal.
    }

    /// <summary>Aborts the connection and waits for active I/O before releasing compression and encryption state.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) BeginDispose().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    /// <summary>Aborts the connection asynchronously. Use CompleteWritesAsync before disposal for a graceful half-close.</summary>
    public override async ValueTask DisposeAsync()
    {
        await BeginDispose().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
