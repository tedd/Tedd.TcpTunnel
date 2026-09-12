using System.Net;
using System.Net.Sockets;

namespace Tedd.TcpTunnel;

/// <summary>Connects an application directly to a tunnel server without opening a local listener.</summary>
public static class TunnelClient
{
    /// <summary>Connects to a tunnel server and returns the negotiated, plaintext application stream.</summary>
    /// <param name="host">Tunnel server DNS name or IP literal.</param>
    /// <param name="port">Tunnel server TCP port, from 1 through 65535.</param>
    /// <param name="options">Protocol, encryption and connection settings; null uses defaults.</param>
    /// <param name="cancellationToken">Cancels connection and negotiation only. Use stream I/O tokens or disposal afterward.</param>
    /// <param name="log">Optional thread-safe event callback; must not throw.</param>
    /// <returns>An owned stream. Dispose it to close the TCP connection.</returns>
    /// <exception cref="ArgumentException">An endpoint or option is invalid.</exception>
    /// <exception cref="IOException">TCP connection attempts failed or the peer rejected the protocol.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Peer authentication failed.</exception>
    /// <exception cref="OperationCanceledException">Connection, negotiation, or its deadline was cancelled.</exception>
    public static async Task<TunnelStream> ConnectAsync(string host, int port, TunnelStreamOptions? options = null,
        CancellationToken cancellationToken = default, Action<TunnelEvent>? log = null)
    {
        var forward = (options ?? new()).ToForwardOptions(TunnelMode.Client, host, port);
        var socket = await Connector.ConnectAsync(host, port, forward.Retry, cancellationToken,
            (ex, attempt) => log?.Invoke(new(forward.Name, "warning", $"Connect attempt {attempt} failed.", ex, "connect-retry",
                Destination: $"{host}:{port}"))).ConfigureAwait(false);
        return await TunnelStream.OpenAsync(socket, forward, cancellationToken, log).ConfigureAwait(false);
    }
}

/// <summary>Accepts tunnel connections for application-owned services without opening a destination TCP connection.</summary>
public static class TunnelServer
{
    /// <summary>Negotiates an accepted TCP socket and returns a plaintext application stream.</summary>
    /// <param name="socket">A connected socket accepted by an application-owned TCP listener.</param>
    /// <param name="options">Protocol settings and source ACL; null uses defaults.</param>
    /// <param name="cancellationToken">Cancels negotiation only. Use stream I/O tokens or disposal afterward.</param>
    /// <param name="log">Optional thread-safe event callback; must not throw.</param>
    /// <returns>A stream owning the socket. No destination socket is created.</returns>
    /// <remarks>Ownership transfers after option validation, including when ACL checks or negotiation fail.
    /// The application owns its listener, acceptance loop, concurrency limit, and handler tasks.</remarks>
    /// <exception cref="ArgumentException">Options are invalid; the caller retains socket ownership.</exception>
    /// <exception cref="UnauthorizedAccessException">The source address is denied.</exception>
    /// <exception cref="IOException">The peer closed or supplied an incompatible protocol.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Peer authentication failed.</exception>
    /// <exception cref="OperationCanceledException">Negotiation or its deadline was cancelled.</exception>
    public static async Task<TunnelStream> AcceptAsync(Socket socket, TunnelStreamOptions? options = null,
        CancellationToken cancellationToken = default, Action<TunnelEvent>? log = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var forward = (options ?? new()).ToForwardOptions(TunnelMode.Server);
        try
        {
            if (socket.RemoteEndPoint is not IPEndPoint peer || !IpAccessControl.Create(forward.AccessControl).IsAllowed(peer.Address))
                throw new UnauthorizedAccessException("The tunnel source address is denied by ACL.");
        }
        catch { socket.Dispose(); throw; }
        return await TunnelStream.OpenAsync(socket, forward, cancellationToken, log).ConfigureAwait(false);
    }
}
