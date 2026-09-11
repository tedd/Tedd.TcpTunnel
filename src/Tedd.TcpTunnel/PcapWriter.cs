using System.Buffers.Binary;
using System.Net;

namespace Tedd.TcpTunnel;

// Classic PCAP, LINKTYPE_RAW (101), reconstructed IP/TCP segments. No raw-socket privileges.
internal sealed class PcapWriter : IDisposable
{
    private readonly string _path;
    private readonly CaptureOptions _options;
    private readonly Lock _gate = new();
    private readonly byte[] _buffer = new byte[65536];
    private FileStream _file;

    public PcapWriter(string forward, CaptureOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.Directory!);
        _path = Path.Combine(options.Directory!, $"{forward}-{Environment.ProcessId}-{Guid.NewGuid():N}.pcap");
        _file = Open();
    }
    internal string FilePath => _path;

    private FileStream Open()
    {
        var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
        Span<byte> header = stackalloc byte[24]; header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xa1b2c3d4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 65535);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 101);
        file.Write(header); return file;
    }

    public void Write(IPEndPoint source, IPEndPoint destination, ReadOnlySpan<byte> payload, ref uint sequence)
    {
        lock (_gate)
        {
            while (!payload.IsEmpty)
            {
                var size = Math.Min(payload.Length, 60000);
                var packet = _buffer.AsSpan(16);
                var ipv4 = (source.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || source.Address.IsIPv4MappedToIPv6) &&
                           (destination.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || destination.Address.IsIPv4MappedToIPv6);
                var ipSize = ipv4 ? 20 : 40;
                packet[..(ipSize + 20)].Clear();
                var tcp = packet[ipSize..];
                BinaryPrimitives.WriteUInt16BigEndian(tcp, (ushort)source.Port);
                BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], (ushort)destination.Port);
                BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
                tcp[12] = 0x50; tcp[13] = 0x08; // synthetic PSH, sequence space starts at zero
                BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);
                payload[..size].CopyTo(tcp[20..]);
                uint pseudo;
                if (ipv4)
                {
                    packet[0] = 0x45; packet[8] = 64; packet[9] = 6;
                    BinaryPrimitives.WriteUInt16BigEndian(packet[2..], (ushort)(40 + size));
                    source.Address.MapToIPv4().TryWriteBytes(packet[12..16], out _);
                    destination.Address.MapToIPv4().TryWriteBytes(packet[16..20], out _);
                    BinaryPrimitives.WriteUInt16BigEndian(packet[10..], Checksum(packet[..20]));
                    pseudo = Sum(packet[12..20]) + 6u + (uint)(20 + size);
                }
                else
                {
                    packet[0] = 0x60; packet[6] = 6; packet[7] = 64;
                    BinaryPrimitives.WriteUInt16BigEndian(packet[4..], (ushort)(20 + size));
                    source.Address.MapToIPv6().TryWriteBytes(packet[8..24], out _);
                    destination.Address.MapToIPv6().TryWriteBytes(packet[24..40], out _);
                    pseudo = Sum(packet[8..40]) + 6u + (uint)(20 + size);
                }
                BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], Checksum(tcp[..(20 + size)], pseudo));
                var length = ipSize + 20 + size;
                var time = DateTimeOffset.UtcNow;
                BinaryPrimitives.WriteUInt32LittleEndian(_buffer, (uint)time.ToUnixTimeSeconds());
                BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(4), (uint)((time.Ticks % TimeSpan.TicksPerSecond) / 10));
                BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(8), (uint)length);
                BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(12), (uint)length);
                if (_file.Position + length + 16 > _options.MaxFileBytes) Rotate();
                _file.Write(_buffer.AsSpan(0, length + 16));
                sequence += (uint)size; payload = payload[size..];
            }
        }
    }

    internal static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        while (data.Length >= 2) { sum += BinaryPrimitives.ReadUInt16BigEndian(data); data = data[2..]; }
        if (!data.IsEmpty) sum += (uint)data[0] << 8;
        return sum;
    }
    internal static ushort Checksum(ReadOnlySpan<byte> data, uint initial = 0)
    {
        var sum = initial + Sum(data);
        while ((sum >> 16) != 0) sum = (sum & 65535) + (sum >> 16);
        return (ushort)~sum;
    }
    private void Rotate()
    {
        _file.Dispose();
        if (_options.RetainedFiles == 1) File.Delete(_path);
        else
        {
            File.Delete($"{_path}.{_options.RetainedFiles - 1}");
            for (var i = _options.RetainedFiles - 2; i >= 1; i--)
                if (File.Exists($"{_path}.{i}")) File.Move($"{_path}.{i}", $"{_path}.{i + 1}");
            File.Move(_path, $"{_path}.1");
        }
        _file = Open();
    }
    public void Dispose() { lock (_gate) _file.Dispose(); }
}
