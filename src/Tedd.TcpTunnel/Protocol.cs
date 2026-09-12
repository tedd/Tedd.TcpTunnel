using System.Buffers.Binary;

namespace Tedd.TcpTunnel;

internal enum FrameType : byte { Data = 1, Noop = 2, Fin = 3 }

internal static class Protocol
{
    public const int HeaderSize = 9;
    public const int HelloSize = 16;

    public static void WriteHello(Span<byte> hello, ForwardOptions options)
    {
        hello.Clear();
        (options.Encryption.Algorithm == EncryptionAlgorithm.None ? "TTN2"u8 : "TTN3"u8).CopyTo(hello);
        hello[4] = options.Encryption.Algorithm == EncryptionAlgorithm.None ? (byte)2 : (byte)3;
        hello[5] = (byte)options.Compression;
        hello[6] = options.CompressionHistory ? (byte)1 : (byte)0;
        hello[7] = (byte)options.Mode;
        BinaryPrimitives.WriteInt32BigEndian(hello[8..], options.BufferSize);
        hello[12] = (byte)options.Encryption.Algorithm;
        hello[13] = UsesTdsPrelogin(options) ? (byte)1 : (byte)0;
    }

    public static int ValidateHello(ReadOnlySpan<byte> hello, ForwardOptions options)
    {
        if (hello.Length != HelloSize || !hello[..4].SequenceEqual(options.Encryption.Algorithm == EncryptionAlgorithm.None ? "TTN2"u8 : "TTN3"u8) || hello[4] != (options.Encryption.Algorithm == EncryptionAlgorithm.None ? 2 : 3) ||
            hello[5] != (byte)options.Compression || hello[6] != (options.CompressionHistory ? 1 : 0) ||
            hello[7] != (byte)(options.Mode == TunnelMode.Client ? TunnelMode.Server : TunnelMode.Client) ||
            hello[12] != (byte)options.Encryption.Algorithm || hello[13] != (UsesTdsPrelogin(options) ? 1 : 0) || hello[14..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Incompatible tunnel protocol, role, compression, history, encryption or SQL Server TLS setting.");
        var size = BinaryPrimitives.ReadInt32BigEndian(hello[8..]);
        if (size is < 1024 or > 1048576) throw new InvalidDataException("Invalid peer frame limit.");
        return size;
    }

    private static bool UsesTdsPrelogin(ForwardOptions options) =>
        (options.Mode == TunnelMode.Client ? options.ListenTls.Mode : options.RemoteTls.Mode) == TlsMode.SqlServer;

    public static void WriteHeader(Span<byte> header, FrameType type, int rawLength, int wireLength)
    {
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], rawLength);
        BinaryPrimitives.WriteInt32BigEndian(header[5..], wireLength);
    }

    public static (FrameType Type, int RawLength, int WireLength) ReadHeader(ReadOnlySpan<byte> header, int maxFrame)
    {
        var type = (FrameType)header[0];
        var raw = BinaryPrimitives.ReadInt32BigEndian(header[1..]);
        var wire = BinaryPrimitives.ReadInt32BigEndian(header[5..]);
        if (type == FrameType.Data)
        {
            if (raw < 1 || raw > maxFrame || wire < 1 || wire > BlockCodec.MaxEncodedLength(maxFrame))
                throw new InvalidDataException("Frame exceeds negotiated limits.");
        }
        else if (type is not (FrameType.Noop or FrameType.Fin) || raw != 0 || wire != 0)
            throw new InvalidDataException("Invalid control frame.");
        return (type, raw, wire);
    }
}
