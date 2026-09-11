using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace Tedd.TcpTunnel;

internal sealed class TunnelConnection(Socket local, Socket remote, ForwardOptions options, PcapWriter? capture, TunnelSession? session)
{
    private long _lastActivity = Environment.TickCount64;

    public async Task RunAsync(CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = stop.Token.Register(() => { local.Dispose(); remote.Dispose(); });
        var framed = options.Mode is TunnelMode.Client or TunnelMode.Server;
        var tunnel = options.Mode == TunnelMode.Server ? local : remote;
        var plain = options.Mode == TunnelMode.Server ? remote : local;
        var peerFrame = session?.PeerFrame ?? options.BufferSize;
        var sync = options.Execution == ExecutionMode.Dedicated;
        Task Start(Func<Task> pump) => sync ? Task.Factory.StartNew(() => pump().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default) : pump();
        async Task Guard(Func<Task> pump)
        {
            try { await pump().ConfigureAwait(false); }
            catch { await stop.CancelAsync().ConfigureAwait(false); throw; }
        }
        var watchdog = WatchIdleAsync(stop);
        try
        {
            await Task.WhenAll(
                Start(() => Guard(() => SendAsync(plain, tunnel, framed, sync, stop.Token, session?.Send))),
                Start(() => Guard(() => framed ? ReceiveFramesAsync(tunnel, plain, peerFrame, sync, stop.Token, session?.Receive) :
                    SendAsync(tunnel, plain, false, sync, stop.Token)))).ConfigureAwait(false);
        }
        finally { await stop.CancelAsync().ConfigureAwait(false); await watchdog.ConfigureAwait(false); }
    }

    private async Task WatchIdleAsync(CancellationTokenSource stop)
    {
        if (options.IdleTimeoutMilliseconds == 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(options.IdleTimeoutMilliseconds, 1000)));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                if (Environment.TickCount64 - Interlocked.Read(ref _lastActivity) >= options.IdleTimeoutMilliseconds)
                { await stop.CancelAsync().ConfigureAwait(false); return; }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void Activity() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    private async Task SendAsync(Socket source, Socket destination, bool framed, bool sync, CancellationToken token, FrameCipher? cipher = null)
    {
        using var reader = new SocketTransport(source, sync, options.Socket);
        using var writer = new SocketTransport(destination, sync, options.Socket);
        var buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        var encoded = framed ? ArrayPool<byte>.Shared.Rent(BlockCodec.MaxEncodedLength(options.BufferSize) + Protocol.HeaderSize) : null;
        var encrypted = cipher is null ? null : ArrayPool<byte>.Shared.Rent(BlockCodec.MaxEncodedLength(options.BufferSize) + Protocol.HeaderSize + FrameCipher.TagSize);
        using var codec = framed ? new BlockCodec(options) : null;
        async ValueTask SendFrameAsync(int length)
        {
            if (cipher is null) await writer.SendAsync(encoded!.AsMemory(0, Protocol.HeaderSize + length), token).ConfigureAwait(false);
            else
            {
                encoded!.AsSpan(0, Protocol.HeaderSize).CopyTo(encrypted);
                cipher.Encrypt(encoded.AsSpan(0, Protocol.HeaderSize), encoded.AsSpan(Protocol.HeaderSize, length), encrypted.AsSpan(Protocol.HeaderSize));
                await writer.SendAsync(encrypted.AsMemory(0, Protocol.HeaderSize + length + FrameCipher.TagSize), token).ConfigureAwait(false);
            }
        }
        var sourceEndpoint = (IPEndPoint)source.RemoteEndPoint!;
        var destinationEndpoint = (IPEndPoint)destination.RemoteEndPoint!;
        uint sequence = 0;
        try
        {
            while (true)
            {
                var read = await reader.ReceiveAsync(buffer.AsMemory(0, options.BufferSize), framed ? options.HeartbeatMilliseconds : 0, token).ConfigureAwait(false);
                if (read < 0)
                {
                    Protocol.WriteHeader(encoded!, FrameType.Noop, 0, 0);
                    await SendFrameAsync(0).ConfigureAwait(false);
                    continue;
                }
                if (read == 0) break;
                Activity();
                var deadline = Environment.TickCount64 + options.BatchMilliseconds;
                while (read < options.BufferSize && options.BatchMilliseconds > 0)
                {
                    var remaining = (int)(deadline - Environment.TickCount64);
                    if (remaining <= 0) break;
                    var more = await reader.ReceiveAsync(buffer.AsMemory(read, options.BufferSize - read), remaining, token).ConfigureAwait(false);
                    if (more <= 0) break;
                    read += more;
                    Activity();
                }
                capture?.Write(sourceEndpoint, destinationEndpoint, buffer.AsSpan(0, read), ref sequence);
                if (framed)
                {
                    var length = codec!.Encode(buffer.AsSpan(0, read), encoded!, Protocol.HeaderSize);
                    Protocol.WriteHeader(encoded!, FrameType.Data, read, length);
                    await SendFrameAsync(length).ConfigureAwait(false);
                }
                else await writer.SendAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (framed)
            {
                Protocol.WriteHeader(encoded!, FrameType.Fin, 0, 0);
                await SendFrameAsync(0).ConfigureAwait(false);
            }
            SocketTuning.ShutdownSend(destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: cipher is not null);
            if (encoded is not null) ArrayPool<byte>.Shared.Return(encoded, clearArray: cipher is not null);
            if (encrypted is not null) ArrayPool<byte>.Shared.Return(encrypted);
        }
    }

    private async Task ReceiveFramesAsync(Socket source, Socket destination, int maxFrame, bool sync, CancellationToken token, FrameCipher? cipher)
    {
        using var reader = new SocketTransport(source, sync, options.Socket);
        using var writer = new SocketTransport(destination, sync, options.Socket);
        var encoded = ArrayPool<byte>.Shared.Rent(BlockCodec.MaxEncodedLength(maxFrame));
        var encrypted = cipher is null ? null : ArrayPool<byte>.Shared.Rent(BlockCodec.MaxEncodedLength(maxFrame) + FrameCipher.TagSize);
        var decoded = ArrayPool<byte>.Shared.Rent(maxFrame);
        var header = new byte[Protocol.HeaderSize];
        using var codec = new BlockCodec(options);
        var sourceEndpoint = (IPEndPoint)source.RemoteEndPoint!;
        var destinationEndpoint = (IPEndPoint)destination.RemoteEndPoint!;
        uint sequence = 0;
        try
        {
            while (true)
            {
                await reader.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var (type, raw, wire) = Protocol.ReadHeader(header, maxFrame);
                if (cipher is not null)
                {
                    await reader.ReadExactlyAsync(encrypted.AsMemory(0, wire + FrameCipher.TagSize), token).ConfigureAwait(false);
                    cipher.Decrypt(header, encrypted.AsSpan(0, wire + FrameCipher.TagSize), encoded.AsSpan(0, wire));
                }
                if (type == FrameType.Fin) { SocketTuning.ShutdownSend(destination); return; }
                if (type == FrameType.Noop) continue;
                if (cipher is null) await reader.ReadExactlyAsync(encoded.AsMemory(0, wire), token).ConfigureAwait(false);
                codec.Decode(encoded, wire, decoded.AsSpan(0, raw));
                Activity();
                capture?.Write(sourceEndpoint, destinationEndpoint, decoded.AsSpan(0, raw), ref sequence);
                await writer.SendAsync(decoded.AsMemory(0, raw), token).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encoded, clearArray: cipher is not null);
            ArrayPool<byte>.Shared.Return(decoded, clearArray: cipher is not null);
            if (encrypted is not null) ArrayPool<byte>.Shared.Return(encrypted);
        }
    }
}
