using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Tedd.TcpTunnel;

public sealed record TunnelEvent(
    string Forward,
    string Level,
    string Message,
    Exception? Exception = null,
    string Event = "message",
    long? ConnectionId = null,
    string? Source = null,
    string? Destination = null,
    long? DurationMilliseconds = null);

internal static partial class SocketTuning
{
    public static void Apply(Socket socket, SocketOptions options, Action<string>? unsupported = null)
    {
        socket.NoDelay = options.NoDelay;
        if (options.SendBufferSize > 0) socket.SendBufferSize = options.SendBufferSize;
        if (options.ReceiveBufferSize > 0) socket.ReceiveBufferSize = options.ReceiveBufferSize;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
        if (options.KeepAlive)
        {
            Try(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, options.KeepAliveSeconds), unsupported);
            Try(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, options.KeepAliveIntervalSeconds), unsupported);
            Try(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, options.KeepAliveRetryCount), unsupported);
        }
        if (OperatingSystem.IsLinux())
        {
            if (options.LinuxUserTimeoutMilliseconds > 0) Try(() => SetLinuxInt(socket, 18, options.LinuxUserTimeoutMilliseconds), unsupported);
            if (options.LinuxQuickAck) Try(() => SetLinuxInt(socket, 12, 1), unsupported);
            if (options.LinuxCongestionControl is { } algorithm)
                Try(() => SetLinuxBytes(socket, 13, Encoding.ASCII.GetBytes(algorithm + '\0')), unsupported);
        }
        if (OperatingSystem.IsWindows() && options.WindowsLoopbackFastPath &&
            socket.LocalEndPoint is IPEndPoint local && socket.RemoteEndPoint is IPEndPoint remote &&
            IPAddress.IsLoopback(local.Address) && IPAddress.IsLoopback(remote.Address))
            Try(() => socket.IOControl(unchecked((int)0x98000010), BitConverter.GetBytes(1), null), unsupported);
    }

    public static void QuickAck(Socket socket, SocketOptions options)
    {
        if (OperatingSystem.IsLinux() && options.LinuxQuickAck)
        {
            try { SetLinuxInt(socket, 12, 1); }
            catch (Win32Exception) { /* The initial setup reports unsupported tuning once. */ }
        }
    }

    private static void Try(Action operation, Action<string>? unsupported)
    {
        try { operation(); }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or Win32Exception)
        { unsupported?.Invoke($"Optional socket tuning unavailable: {ex.Message}"); }
    }

    private static void SetLinuxInt(Socket socket, int option, int value)
    {
        if (SetSocketOption(socket.SafeHandle, 6, option, ref value, sizeof(int)) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static void SetLinuxBytes(Socket socket, int option, byte[] value)
    {
        if (SetSocketOptionBytes(socket.SafeHandle, 6, option, value, (uint)value.Length) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static partial int SetSocketOption(SafeSocketHandle socket, int level, int name, ref int value, uint length);
    [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static partial int SetSocketOptionBytes(SafeSocketHandle socket, int level, int name, byte[] value, uint length);

    public static void ResetOnClose(Socket socket)
    {
        try { socket.LingerState = new LingerOption(true, 0); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    public static void ShutdownSend(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Send); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }
}

internal sealed class SocketTransport(Socket socket, bool synchronous, SocketOptions options) : IDisposable
{
    private CancellationTokenSource? _receiveTimeout;
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, int timeoutMilliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int count;
        if (synchronous)
        {
            if (timeoutMilliseconds > 0 && !socket.Poll(TimeSpan.FromMilliseconds(timeoutMilliseconds), SelectMode.SelectRead)) return -1;
            count = socket.Receive(buffer.Span, SocketFlags.None);
        }
        else if (timeoutMilliseconds > 0)
        {
            // A cancelled timed receive is awaited before this buffer is reused.
            if (_receiveTimeout is null || _receiveTimeout.IsCancellationRequested)
            {
                _receiveTimeout?.Dispose();
                _receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            _receiveTimeout.CancelAfter(timeoutMilliseconds);
            try { count = await socket.ReceiveAsync(buffer, SocketFlags.None, _receiveTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return -1; }
            finally { _receiveTimeout.CancelAfter(Timeout.Infinite); }
        }
        else count = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
        SocketTuning.QuickAck(socket, options);
        return count;
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            var count = await ReceiveAsync(buffer, 0, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Peer closed in the middle of a tunnel message.");
            buffer = buffer[count..];
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            var sent = synchronous ? socket.Send(buffer.Span, SocketFlags.None) :
                await socket.SendAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            if (sent == 0) throw new IOException("Peer stopped accepting data.");
            buffer = buffer[sent..];
        }
    }
    public void Dispose() => _receiveTimeout?.Dispose();
}

internal static class Connector
{
    public static async Task<Socket> ConnectAsync(string host, int port, RetryOptions retry, CancellationToken token, Action<Exception, int>? failed = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(retry.ConnectTimeoutMilliseconds);
                // Hostname overload allows the runtime to select and race address families.
                await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                token.ThrowIfCancellationRequested();
                failed?.Invoke(ex, attempt + 1);
                if (attempt + 1 >= retry.Attempts) throw new IOException($"Unable to connect to {host}:{port} after {retry.Attempts} attempts.", ex);
                await Task.Delay(retry.Delay(attempt, Random.Shared.NextDouble()), token).ConfigureAwait(false);
            }
            catch { socket.Dispose(); throw; }
        }
    }
}
