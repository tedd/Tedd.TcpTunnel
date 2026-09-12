using System.IO.Compression;

namespace Tedd.TcpTunnel;

/// <summary>Settings for an application stream connected directly to a tunnel peer.</summary>
/// <remarks>Configure before connecting and do not mutate nested settings while a connection is active.
/// Each write is sent immediately in frames of at most <see cref="BufferSize"/> bytes.
/// Listener batching, dedicated pump threads, and application endpoint TLS belong to <see cref="ForwardOptions"/>.</remarks>
public sealed class TunnelStreamOptions
{
    /// <summary>Connection name used in log events and capture filenames. Defaults to "stream".</summary>
    public string Name { get; set; } = "stream";
    /// <summary>Compression algorithm; both peers must agree. Defaults to no compression.</summary>
    public Codec Compression { get; set; }
    /// <summary>Compression level for LZ4, Deflate, GZip and ZLib. Defaults to Fastest.</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;
    /// <summary>Brotli quality, from 0 through 11. Defaults to 4.</summary>
    public int BrotliQuality { get; set; } = 4;
    /// <summary>Brotli window bits, from 10 through 24. Defaults to 20.</summary>
    public int BrotliWindow { get; set; } = 20;
    /// <summary>Zstandard level, from -5 through 22. Defaults to 3.</summary>
    public int ZstandardLevel { get; set; } = 3;
    /// <summary>Retains Brotli history per direction; both peers must agree. Defaults to false.</summary>
    public bool CompressionHistory { get; set; }
    /// <summary>Shared-key encryption. Clients use Key/KeyId; servers use Keys. Defaults to None.</summary>
    public EncryptionOptions Encryption { get; set; } = new();
    /// <summary>Maximum outgoing application frame size, 1 KiB through 1 MiB. Defaults to 64 KiB.</summary>
    public int BufferSize { get; set; } = 65536;
    /// <summary>Outgoing heartbeat interval in milliseconds. Zero disables it; defaults to 30000.</summary>
    public int HeartbeatMilliseconds { get; set; } = 30000;
    /// <summary>Application I/O idle limit in milliseconds. Zero disables it. Heartbeats do not reset it.</summary>
    public int IdleTimeoutMilliseconds { get; set; }
    /// <summary>Tunnel negotiation deadline in milliseconds. Defaults to 10000.</summary>
    public int HandshakeTimeoutMilliseconds { get; set; } = 10000;
    /// <summary>TCP tuning applied to the tunnel socket.</summary>
    public SocketOptions Socket { get; set; } = new();
    /// <summary>Client TCP connection attempts and backoff. Handshake or established-stream failures are never retried.</summary>
    public RetryOptions Retry { get; set; } = new();
    /// <summary>Server source-address allow/deny rules, checked before negotiation.</summary>
    public AccessControlOptions AccessControl { get; set; } = new();
    /// <summary>Optional plaintext PCAP capture. Each direct connection owns its capture writer.</summary>
    public CaptureOptions Capture { get; set; } = new();

    internal ForwardOptions ToForwardOptions(TunnelMode mode, string host = "127.0.0.1", int port = 80)
    {
        var result = new ForwardOptions
        {
            Name = Name, Mode = mode, RemoteHost = host, RemotePort = port,
            Compression = Compression, CompressionLevel = CompressionLevel,
            BrotliQuality = BrotliQuality, BrotliWindow = BrotliWindow, ZstandardLevel = ZstandardLevel,
            CompressionHistory = CompressionHistory, Encryption = Encryption, BufferSize = BufferSize,
            HeartbeatMilliseconds = HeartbeatMilliseconds, IdleTimeoutMilliseconds = IdleTimeoutMilliseconds,
            HandshakeTimeoutMilliseconds = HandshakeTimeoutMilliseconds, Socket = Socket, Retry = Retry,
            AccessControl = AccessControl, Capture = Capture
        };
        result.Validate();
        return result;
    }
}
