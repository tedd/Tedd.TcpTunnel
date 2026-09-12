using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Tedd.TcpTunnel;

internal sealed class TlsEndpoints : IDisposable
{
    public SslStream? Local { get; private set; }
    public SslStream? Remote { get; private set; }

    internal static async Task<TlsEndpoints> CreateAsync(Socket local, Socket remote, ForwardOptions options,
        X509Certificate2? certificate, TunnelSession? session, BlockCodec? encoder, BlockCodec? decoder,
        Action<string>? log, CancellationToken token)
    {
        var endpoints = new TlsEndpoints();
        if (options.ListenTls.Mode == TlsMode.None && options.RemoteTls.Mode == TlsMode.None) return endpoints;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.HandshakeTimeoutMilliseconds);
        token = deadline.Token;
        try
        {
            if (options.ListenTls.Mode == TlsMode.SqlServer || options.RemoteTls.Mode == TlsMode.SqlServer)
            {
                using var localNetwork = new NetworkStream(local, ownsSocket: false);
                using var remoteNetwork = new NetworkStream(remote, ownsSocket: false);
                using var tunnel = session is null ? null : new TunnelSetupStream(
                    options.Mode == TunnelMode.Client ? remote : local, options, session, encoder!, decoder!);
                Stream fromClient = options.Mode == TunnelMode.Server ? tunnel! : localNetwork;
                Stream toServer = options.Mode == TunnelMode.Client ? tunnel! : remoteNetwork;
                var request = await TdsPrelogin.ReadAsync(fromClient, 0x12, token).ConfigureAwait(false);
                TdsPrelogin.RequireEncryption(request, response: false);
                await TdsPrelogin.WriteAsync(toServer, request, 0x12, token).ConfigureAwait(false);
                var response = await TdsPrelogin.ReadAsync(toServer, 0x04, token).ConfigureAwait(false);
                TdsPrelogin.RequireEncryption(response, response: true);
                await TdsPrelogin.WriteAsync(fromClient, response, 0x04, token).ConfigureAwait(false);
                tunnel?.EnsureConsumed();
            }
            if (options.ListenTls.Mode != TlsMode.None)
            {
                var (ssl, tds) = CreateStream(local, options.ListenTls.Mode);
                endpoints.Local = ssl;
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = options.ListenTls.EnabledProtocols,
                    CipherSuitesPolicy = options.ListenTls.CreateCipherPolicy(),
                    AllowRenegotiation = false,
                    // TDS 8.0 peers negotiate the tds/8.0 ALPN identifier.
                    ApplicationProtocols = options.ListenTls.Mode == TlsMode.SqlServerStrict ? [new("tds/8.0")] : null
                }, token).ConfigureAwait(false);
                tds?.CompleteHandshake();
                RequireTdsAlpn(ssl, options.ListenTls.Mode);
                log?.Invoke($"Listener TLS established: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}.");
            }
            if (options.RemoteTls.Mode != TlsMode.None)
            {
                var (ssl, tds) = CreateStream(remote, options.RemoteTls.Mode);
                endpoints.Remote = ssl;
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = options.RemoteTls.TargetHost ?? options.RemoteHost,
                    EnabledSslProtocols = options.RemoteTls.EnabledProtocols,
                    CipherSuitesPolicy = options.RemoteTls.CreateCipherPolicy(),
                    AllowRenegotiation = false,
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                    RemoteCertificateValidationCallback = options.RemoteTls.TrustServerCertificate
                        ? static (_, certificate, _, _) => certificate is not null : null,
                    ApplicationProtocols = options.RemoteTls.Mode == TlsMode.SqlServerStrict ? [new("tds/8.0")] : null
                }, token).ConfigureAwait(false);
                tds?.CompleteHandshake();
                RequireTdsAlpn(ssl, options.RemoteTls.Mode);
                log?.Invoke($"Destination TLS established: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}.");
            }
            return endpoints;
        }
        catch { endpoints.Dispose(); throw; }
    }

    internal static void RequireTdsAlpn(SslStream ssl, TlsMode mode)
    {
        if (mode == TlsMode.SqlServerStrict && ssl.NegotiatedApplicationProtocol != new SslApplicationProtocol("tds/8.0"))
            throw new AuthenticationException("SqlServerStrict requires TDS 8.0 ALPN negotiation.");
    }

    private static (SslStream Ssl, TdsHandshakeStream? Tds) CreateStream(Socket socket, TlsMode mode)
    {
        Stream network = new NetworkStream(socket, ownsSocket: false);
        var tds = mode == TlsMode.SqlServer ? new TdsHandshakeStream(network) : null;
        return (new SslStream(tds ?? network, leaveInnerStreamOpen: false), tds);
    }

    public void Dispose() { Local?.Dispose(); Remote?.Dispose(); }
}
