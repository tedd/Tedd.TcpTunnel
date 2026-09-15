using System.Buffers;
using System.IO.Compression;
using K4os.Compression.LZ4;

namespace Tedd.TcpTunnel;

// One instance per direction; never shared across connections or concurrent calls.
internal sealed class BlockCodec : IDisposable
{
    private readonly ForwardOptions _options;
    private BrotliEncoder _encoder;
    private BrotliDecoder _decoder;
    private readonly BufferStream _stream = new();
    private readonly ZstdSharp.Compressor? _zstdEncoder;
    private readonly ZstdSharp.Decompressor? _zstdDecoder;

    public BlockCodec(ForwardOptions options)
    {
        _options = options;
        if (options.Compression == Codec.Zstandard)
        {
            _zstdEncoder = new(options.ZstandardLevel);
            _zstdDecoder = new();
            _zstdDecoder.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 20);
        }
        if (options.Compression == Codec.Brotli && options.CompressionHistory)
            _encoder = new(options.BrotliQuality, options.BrotliWindow);
    }

    public static int MaxEncodedLength(int size) => checked(size * 2 + 1024);

    public int Encode(ReadOnlySpan<byte> source, byte[] target, int offset)
    {
        var destination = target.AsSpan(offset);
        switch (_options.Compression)
        {
            case Codec.None:
                source.CopyTo(destination); return source.Length;
            case Codec.Zstandard:
                return _zstdEncoder!.Wrap(source, destination);
            case Codec.Lz4:
                var n = LZ4Codec.Encode(source, destination, _options.CompressionLevel == CompressionLevel.SmallestSize ? LZ4Level.L12_MAX : LZ4Level.L00_FAST);
                if (n <= 0) throw new InvalidDataException("LZ4 output exceeds frame limit.");
                return n;
            case Codec.Brotli:
                if (!_options.CompressionHistory)
                {
                    if (!BrotliEncoder.TryCompress(source, destination, out var written, _options.BrotliQuality, _options.BrotliWindow))
                        throw new InvalidDataException("Brotli output exceeds frame limit.");
                    return written;
                }
                var status = _encoder.Compress(source, destination, out var consumed, out var encoded, false);
                if (consumed != source.Length || status is OperationStatus.InvalidData or OperationStatus.DestinationTooSmall)
                    throw new InvalidDataException("Brotli output exceeds frame limit.");
                status = _encoder.Flush(destination[encoded..], out var flushed);
                if (status != OperationStatus.Done) throw new InvalidDataException("Brotli flush exceeds frame limit.");
                return encoded + flushed;
            default:
                _stream.Reset(target, offset, target.Length - offset, writable: true);
                using (var compressor = CreateStream(_stream, true)) compressor.Write(source);
                return _stream.Count;
        }
    }

    public void Decode(byte[] source, int count, Span<byte> destination)
    {
        switch (_options.Compression)
        {
            case Codec.None:
                if (count != destination.Length) throw new InvalidDataException("Invalid uncompressed length.");
                source.AsSpan(0, count).CopyTo(destination); return;
            case Codec.Zstandard:
                if (_zstdDecoder!.Unwrap(source.AsSpan(0, count), destination) != destination.Length) throw new InvalidDataException("Invalid Zstandard frame.");
                return;
            case Codec.Lz4:
                if (LZ4Codec.Decode(source.AsSpan(0, count), destination) != destination.Length) throw new InvalidDataException("Invalid LZ4 frame.");
                return;
            case Codec.Brotli:
                if (!_options.CompressionHistory) _decoder = new();
                var status = _decoder.Decompress(source.AsSpan(0, count), destination, out var consumed, out var written);
                if (status == OperationStatus.InvalidData || consumed != count || written != destination.Length ||
                    (!_options.CompressionHistory && status != OperationStatus.Done) ||
                    (_options.CompressionHistory && status == OperationStatus.Done))
                    throw new InvalidDataException("Invalid Brotli frame.");
                if (!_options.CompressionHistory) _decoder.Dispose();
                return;
            default:
            {
                _stream.Reset(source, 0, count, writable: false);
                using var decoder = CreateStream(_stream, false);
                decoder.ReadExactly(destination);
                if (decoder.ReadByte() != -1) throw new InvalidDataException("Expanded frame exceeds declared length.");
                return;
            }
        }
    }

    private Stream CreateStream(Stream stream, bool compress) => (_options.Compression, compress) switch
    {
        (Codec.Deflate, true) => new DeflateStream(stream, _options.CompressionLevel, true),
        (Codec.Deflate, false) => new DeflateStream(stream, CompressionMode.Decompress, true),
        (Codec.GZip, true) => new GZipStream(stream, _options.CompressionLevel, true),
        (Codec.GZip, false) => new GZipStream(stream, CompressionMode.Decompress, true),
        (Codec.ZLib, true) => new ZLibStream(stream, _options.CompressionLevel, true),
        (Codec.ZLib, false) => new ZLibStream(stream, CompressionMode.Decompress, true),
        _ => throw new InvalidOperationException("Unsupported codec.")
    };

    public void Dispose() { _encoder.Dispose(); _decoder.Dispose(); _stream.Dispose(); _zstdEncoder?.Dispose(); _zstdDecoder?.Dispose(); }
}

internal sealed class BufferStream : Stream
{
    private byte[] _buffer = [];
    private int _offset, _length, _position;
    private bool _writable;
    public int Count => _position;
    public void Reset(byte[] buffer, int offset, int length, bool writable)
    { _buffer = buffer; _offset = offset; _length = length; _position = 0; _writable = writable; }
    public override bool CanRead => !_writable;
    public override bool CanSeek => false;
    public override bool CanWrite => _writable;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _length - _position);
        _buffer.AsSpan(_offset + _position, count).CopyTo(buffer); _position += count; return count;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_writable || buffer.Length > _length - _position) throw new InvalidDataException("Frame buffer exceeded.");
        buffer.CopyTo(_buffer.AsSpan(_offset + _position)); _position += buffer.Length;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
