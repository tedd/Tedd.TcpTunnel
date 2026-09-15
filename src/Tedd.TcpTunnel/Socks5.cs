using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tedd.TcpTunnel;

internal static class Socks5
{
    public static async Task<(string Host, int Port)> ReadTargetAsync(Socket socket, CancellationToken token)
    {
        var transport = new SocketTransport(socket, false, new SocketOptions());
        var buffer = new byte[260];
        await transport.ReadExactlyAsync(buffer.AsMemory(0, 2), token).ConfigureAwait(false);
        if (buffer[0] != 5 || buffer[1] == 0) throw new InvalidDataException("Expected a SOCKS5 greeting.");
        var methods = buffer[1];
        await transport.ReadExactlyAsync(buffer.AsMemory(0, methods), token).ConfigureAwait(false);
        var supported = buffer.AsSpan(0, methods).Contains((byte)0);
        await transport.SendAsync(new byte[] { 5, supported ? (byte)0 : (byte)255 }, token).ConfigureAwait(false);
        if (!supported) throw new InvalidDataException("SOCKS5 requires the no-authentication method.");
        await transport.ReadExactlyAsync(buffer.AsMemory(0, 4), token).ConfigureAwait(false);
        if (buffer[0] != 5 || buffer[2] != 0) throw new InvalidDataException("Malformed SOCKS5 request.");
        if (buffer[1] != 1)
        {
            await ReplyAsync(socket, 7, new(IPAddress.Any, 0), token).ConfigureAwait(false);
            throw new InvalidDataException("Only SOCKS5 CONNECT is supported.");
        }
        var kind = buffer[3];
        string host;
        if (kind is 1 or 4)
        {
            var length = kind == 1 ? 4 : 16;
            await transport.ReadExactlyAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
            host = new IPAddress(buffer.AsSpan(0, length)).ToString();
        }
        else if (kind == 3)
        {
            await transport.ReadExactlyAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);
            var length = buffer[0];
            if (length == 0) throw new InvalidDataException("Empty SOCKS5 hostname.");
            await transport.ReadExactlyAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
            if (buffer.AsSpan(0, length).ContainsAnyInRange((byte)128, byte.MaxValue) || buffer.AsSpan(0, length).Contains((byte)0))
                throw new InvalidDataException("SOCKS5 hostnames must use ASCII/Punycode.");
            host = Encoding.ASCII.GetString(buffer, 0, length);
        }
        else
        {
            await ReplyAsync(socket, 8, new(IPAddress.Any, 0), token).ConfigureAwait(false);
            throw new InvalidDataException("Unsupported SOCKS5 address type.");
        }
        await transport.ReadExactlyAsync(buffer.AsMemory(0, 2), token).ConfigureAwait(false);
        var port = BinaryPrimitives.ReadUInt16BigEndian(buffer);
        if (port == 0) throw new InvalidDataException("SOCKS5 port cannot be zero.");
        return (host, port);
    }

    public static ValueTask ReplyAsync(Socket socket, byte status, IPEndPoint endpoint, CancellationToken token)
    {
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        var bytes = address.GetAddressBytes();
        var reply = new byte[bytes.Length + 6];
        reply[0] = 5; reply[1] = status; reply[3] = bytes.Length == 4 ? (byte)1 : (byte)4;
        bytes.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(reply.Length - 2), (ushort)endpoint.Port);
        return new SocketTransport(socket, false, new SocketOptions()).SendAsync(reply, token);
    }
}
