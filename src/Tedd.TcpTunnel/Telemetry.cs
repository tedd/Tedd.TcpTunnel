namespace Tedd.TcpTunnel;

/// <summary>Payload totals excluding TCP, TLS, frame headers and authentication tags.</summary>
public sealed record TrafficTotals(long UncompressedBytes, long EncodedBytes, long CompressedBytes);

public sealed record ForwardTelemetry(string Name, string Mode, string ListenEndpoint, string RemoteEndpoint,
    int ActiveConnections, TrafficTotals Outbound, TrafficTotals Inbound);

/// <summary>Coherent counters updated once per successfully forwarded data block.</summary>
public sealed class TrafficCounter
{
    private readonly object _gate = new();
    private long _uncompressed, _encoded, _compressed;

    public void Add(int uncompressedBytes, int encodedBytes, bool compressed)
    {
        if (uncompressedBytes < 0 || encodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(uncompressedBytes));
        lock (_gate)
        {
            _uncompressed += uncompressedBytes;
            _encoded += encodedBytes;
            if (compressed) _compressed += encodedBytes;
        }
    }

    public TrafficTotals Snapshot()
    {
        lock (_gate) return new(_uncompressed, _encoded, _compressed);
    }
}
