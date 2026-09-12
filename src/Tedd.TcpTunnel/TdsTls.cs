using System.Buffers.Binary;
using System.Net.Sockets;

namespace Tedd.TcpTunnel;

// TDS 7.x wraps only TLS negotiation in PRELOGIN packets. After negotiation,
// TLS records carry the complete TDS byte stream without the outer packet header.
internal sealed class TdsHandshakeStream(Stream inner) : AsyncDuplexStream
{
    private int _remaining;
    private bool _complete;
    public void CompleteHandshake()
    {
        if (_remaining != 0) throw new InvalidDataException("Unread TDS TLS handshake bytes.");
        _complete = true;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_complete) return await inner.ReadAsync(buffer, token).ConfigureAwait(false);
        if (_remaining == 0)
        {
            var header = new byte[8];
            await inner.ReadExactlyAsync(header, token).ConfigureAwait(false);
            _remaining = TdsPrelogin.PayloadLength(header, 0x12);
        }
        var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], token).ConfigureAwait(false);
        if (count == 0) throw new EndOfStreamException("Truncated TDS TLS handshake.");
        _remaining -= count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
        _complete ? inner.WriteAsync(buffer, token) : TdsPrelogin.WriteAsync(inner, buffer, 0x12, token);

    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

internal static class TdsPrelogin
{
    internal const int MaxPayload = 65535;

    internal static int PayloadLength(ReadOnlySpan<byte> header, byte type)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]) - 8;
        if (header[0] != type || (header[1] & ~1) != 0 || length <= 0 || header[7] != 0)
            throw new InvalidDataException("Invalid TDS PRELOGIN packet.");
        return length;
    }

    internal static async Task<byte[]> ReadAsync(Stream stream, byte type, CancellationToken token)
    {
        using var payload = new MemoryStream();
        var header = new byte[8];
        do
        {
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var length = PayloadLength(header, type);
            if (payload.Length + length > MaxPayload) throw new InvalidDataException("TDS PRELOGIN exceeds 65535 bytes.");
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            payload.Write(bytes);
        } while ((header[1] & 1) == 0);
        return payload.ToArray();
    }

    internal static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, byte type, CancellationToken token)
    {
        byte packetId = 1;
        while (!payload.IsEmpty)
        {
            var length = Math.Min(payload.Length, 4088);
            var packet = new byte[length + 8];
            packet[0] = type;
            packet[1] = (byte)(length == payload.Length ? 1 : 0);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            packet[6] = packetId++;
            payload.Span[..length].CopyTo(packet.AsSpan(8));
            await stream.WriteAsync(packet, token).ConfigureAwait(false);
            payload = payload[length..];
        }
    }

    internal static void RequireEncryption(Span<byte> payload, bool response)
    {
        if (payload.IsEmpty || payload[0] != 0) throw new InvalidDataException("TDS PRELOGIN must start with VERSION.");
        var fields = new List<(byte Token, int Offset, int Length)>();
        var seen = new HashSet<byte>();
        int index = 0;
        while (index < payload.Length && payload[index] != 0xff)
        {
            if (index + 5 > payload.Length || !seen.Add(payload[index])) throw new InvalidDataException("Invalid TDS PRELOGIN option table.");
            fields.Add((payload[index], BinaryPrimitives.ReadUInt16BigEndian(payload[(index + 1)..]),
                BinaryPrimitives.ReadUInt16BigEndian(payload[(index + 3)..])));
            index += 5;
        }
        if (index == payload.Length) throw new InvalidDataException("TDS PRELOGIN terminator is missing.");
        var end = index + 1;
        foreach (var field in fields.OrderBy(field => field.Offset))
        {
            if (field.Offset < end || field.Offset + field.Length > payload.Length)
                throw new InvalidDataException("Invalid or overlapping TDS PRELOGIN option range.");
            end = field.Offset + field.Length;
        }
        var version = fields.Single(field => field.Token == 0);
        var encryption = fields.SingleOrDefault(field => field.Token == 1);
        if (version.Length != 6 || encryption.Length != 1) throw new InvalidDataException("TDS PRELOGIN requires VERSION and ENCRYPTION.");
        var value = payload[encryption.Offset];
        if (response ? value is not (1 or 3) : value is not (0 or 1 or 3))
            throw new InvalidDataException("SQL Server termination requires full-session TLS; unsupported encryption negotiation.");
        // Preserve all other fields (MARS, instance, authentication and trace metadata).
        payload[encryption.Offset] = 1;
    }
}

// Used only for the bounded PRELOGIN exchange; codec history and authenticated
// record sequence numbers continue in the normal forwarding pumps.
internal sealed class TunnelSetupStream(Socket socket, ForwardOptions options, TunnelSession session,
    BlockCodec encoder, BlockCodec decoder) : AsyncDuplexStream
{
    private readonly SocketTransport _transport = new(socket, false, options.Socket);
    private byte[] _pending = [];
    private int _offset;

    public void EnsureConsumed()
    {
        if (_offset != _pending.Length) throw new InvalidDataException("Unexpected data after TDS PRELOGIN.");
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.IsEmpty) return 0;
        while (_offset == _pending.Length)
        {
            var header = new byte[Protocol.HeaderSize];
            await _transport.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var (type, raw, wire) = Protocol.ReadHeader(header, session.PeerFrame);
            var bytes = new byte[wire + (session.Receive is null ? 0 : FrameCipher.TagSize)];
            await _transport.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var encoded = new byte[wire];
            if (session.Receive is null) bytes.CopyTo(encoded, 0); else session.Receive.Decrypt(header, bytes, encoded);
            if (type == FrameType.Noop) continue;
            if (type != FrameType.Data) throw new EndOfStreamException("Tunnel closed during TDS PRELOGIN.");
            _pending = new byte[raw]; _offset = 0;
            decoder.Decode(encoded, wire, _pending);
        }
        var count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(buffer.Length, options.BufferSize);
            var encoded = new byte[Protocol.HeaderSize + BlockCodec.MaxEncodedLength(count)];
            var length = encoder.Encode(buffer.Span[..count], encoded, Protocol.HeaderSize);
            Protocol.WriteHeader(encoded, FrameType.Data, count, length);
            if (session.Send is null) await _transport.SendAsync(encoded.AsMemory(0, Protocol.HeaderSize + length), token).ConfigureAwait(false);
            else
            {
                var record = new byte[Protocol.HeaderSize + length + FrameCipher.TagSize];
                encoded.AsSpan(0, Protocol.HeaderSize).CopyTo(record);
                session.Send.Encrypt(record.AsSpan(0, Protocol.HeaderSize), encoded.AsSpan(Protocol.HeaderSize, length), record.AsSpan(Protocol.HeaderSize));
                await _transport.SendAsync(record, token).ConfigureAwait(false);
            }
            buffer = buffer[count..];
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _transport.Dispose(); base.Dispose(disposing); }
}

internal abstract class AsyncDuplexStream : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
