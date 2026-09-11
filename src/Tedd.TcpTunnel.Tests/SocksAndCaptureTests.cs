using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tedd.TcpTunnel.Tests;

public sealed class SocksAndCaptureTests
{
    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(4)]
    public async Task SocksConnectSupportsAllAddressTypes(int kind)
    {
        await using var rig = new TunnelRig();
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Socks5, ListenPort = 0 });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token); var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, rig.Token);
        var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, rig.Token); Assert.Equal(new byte[] { 5, 0 }, greeting);
        var host = kind == 1 ? IPAddress.Loopback.GetAddressBytes() : kind == 4 ? IPAddress.Loopback.MapToIPv6().GetAddressBytes() : Encoding.ASCII.GetBytes("localhost");
        var request = new byte[6 + host.Length + (kind == 3 ? 1 : 0)]; request[0] = 5; request[1] = 1; request[3] = (byte)kind;
        if (kind == 3) request[4] = (byte)host.Length;
        host.CopyTo(request, kind == 3 ? 5 : 4); BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(request.Length - 2), (ushort)rig.EchoPort);
        // Deliberately fragment the handshake into individual writes.
        foreach (var value in request) await stream.WriteAsync(new byte[] { value }, rig.Token);
        var reply = new byte[4]; await stream.ReadExactlyAsync(reply, rig.Token); Assert.Equal(0, reply[1]);
        await stream.ReadExactlyAsync(new byte[reply[3] == 1 ? 6 : 18], rig.Token);
        await stream.WriteAsync("SOCKS payload"u8.ToArray(), rig.Token);
        var output = new byte[13]; await stream.ReadExactlyAsync(output, rig.Token); Assert.Equal("SOCKS payload", Encoding.ASCII.GetString(output));
    }

    [Theory]
    [InlineData(2, 1, 7)] [InlineData(1, 9, 8)]
    public async Task SocksRejectsUnsupportedCommandOrAddress(int command, int address, int expected)
    {
        await using var rig = new TunnelRig(); var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Socks5, ListenPort = 0 });
        using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token); var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, rig.Token); await stream.ReadExactlyAsync(new byte[2], rig.Token);
        await stream.WriteAsync(new byte[] { 5, (byte)command, 0, (byte)address }, rig.Token);
        var reply = new byte[10]; await stream.ReadExactlyAsync(reply, rig.Token); Assert.Equal(expected, reply[1]);
    }

    [Theory]
    [InlineData(4, 1, 0)] [InlineData(5, 0, 0)] [InlineData(5, 1, 2)]
    public async Task SocksRejectsInvalidGreetingOrAuth(int version, int count, int method)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        var task = Socks5.ReadTargetAsync(accepted, TestContext.Current.CancellationToken);
        await client.GetStream().WriteAsync(new byte[] { (byte)version, (byte)count, (byte)method }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => task);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(false, 3)] [InlineData(true, 2)]
    public void PcapIsStandardBoundedAndHasValidChecksums(bool ipv6, int retention)
    {
        using var directory = new TempDirectory();
        string path;
        using (var writer = new PcapWriter("capture", new() { Directory = directory.Path, MaxFileBytes = 131072, RetainedFiles = retention }))
        {
            path = writer.FilePath; uint sequence = 0;
            var source = new IPEndPoint(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 1234);
            var destination = new IPEndPoint(ipv6 ? IPAddress.Parse("2001:db8::1") : IPAddress.Parse("10.0.0.2"), 5678);
            var payload = new byte[300001]; new Random(4).NextBytes(payload);
            writer.Write(source, destination, payload, ref sequence);
            Assert.Equal((uint)payload.Length, sequence);
        }
        Assert.InRange(Directory.GetFiles(directory.Path).Length, 1, retention);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xa1b2c3d4, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(101u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
        for (var offset = 24; offset < bytes.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8));
            Assert.Equal(length, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12)));
            var packet = bytes.AsSpan(offset + 16, length); var ip = ipv6 ? 40 : 20;
            Assert.Equal(ipv6 ? 6 : 4, packet[0] >> 4);
            if (!ipv6) Assert.Equal(0, PcapWriter.Checksum(packet[..20]));
            var pseudo = PcapWriter.Sum(ipv6 ? packet[8..40] : packet[12..20]) + 6u + (uint)(length - ip);
            Assert.Equal(0, PcapWriter.Checksum(packet[ip..], pseudo));
            Assert.Equal(1234, BinaryPrimitives.ReadUInt16BigEndian(packet[ip..]));
            offset += 16 + length;
        }
    }

    [Fact]
    public async Task ForwardingCanCapturePayload()
    {
        using var directory = new TempDirectory();
        await using (var rig = new TunnelRig())
        {
            var endpoint = await rig.AddAsync(new() { ListenPort = 0, RemotePort = rig.EchoPort, Capture = new() { Directory = directory.Path } });
            using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
            await client.GetStream().WriteAsync("payload"u8.ToArray(), rig.Token);
            await client.GetStream().ReadExactlyAsync(new byte[7], rig.Token);
        }
        var bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory.Path)));
        Assert.True(bytes.AsSpan().IndexOf("payload"u8) >= 0);
    }
}
