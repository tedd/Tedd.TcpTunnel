using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Tedd.TcpTunnel.Tests;

public sealed class EncryptionProtocolTests
{
    [Theory]
    [InlineData(EncryptionAlgorithm.ChaCha20Poly1305, "1819d4cd31a5c4d0b54e1f87a8172de2")]
    [InlineData(EncryptionAlgorithm.AesGcm, "e1b5e0e7b8e66146bf9accd951f91be9")]
    [InlineData(EncryptionAlgorithm.AesCcm, "3ba92a5be78558ff6500acc6bb5f3cd5")]
    public void EmptyControlKnownAnswerAndForgeryRejection(EncryptionAlgorithm algorithm, string expected)
    {
        if (!EncryptionOptions.IsSupported(algorithm)) return;
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var header = Convert.FromHexString("030000000000000000");
        using var sender = new FrameCipher(algorithm, secret);
        using var receiver = new FrameCipher(algorithm, secret);
        var tag = new byte[FrameCipher.TagSize]; sender.Encrypt(header, [], tag);
        Assert.Equal(Convert.FromHexString(expected), tag);
        receiver.Decrypt(header, tag, []);
        using var rejected = new FrameCipher(algorithm, secret);
        tag[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => rejected.Decrypt(header, tag, []));
    }
    [Fact]
    public async Task EncryptedIdleTimeoutCannotImitateAnAuthenticatedFin()
    {
        await using var rig = new TunnelRig();
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        var key = EncryptionOptions.GenerateKey();
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Server, ListenPort = 0,
            RemotePort = ((IPEndPoint)target.LocalEndpoint).Port, HeartbeatMilliseconds = 0, IdleTimeoutMilliseconds = 100,
            Encryption = EncryptionTests.Server(EncryptionAlgorithm.AesGcm, key) });
        using var peer = new TcpClient(); await peer.ConnectAsync(endpoint, rig.Token);
        using var session = await TunnelHandshake.NegotiateAsync(peer.Client, new() { Mode = TunnelMode.Client,
            Encryption = EncryptionTests.Client(EncryptionAlgorithm.AesGcm, key) }, rig.Token);
        using var destination = await target.AcceptTcpClientAsync(rig.Token);
        await Assert.ThrowsAsync<IOException>(async () => { _ = await destination.GetStream().ReadAsync(new byte[1], rig.Token); });
    }
    // Independent vectors: Python cryptography AEAD with HMAC-SHA256 HKDF expansion.
    [Theory]
    [InlineData(EncryptionAlgorithm.ChaCha20Poly1305, "ee4b60a2f58aa65434414d7e92448218e9b555", "e69b12e86c466dd56d609f7104b23fbd9a23b8")]
    [InlineData(EncryptionAlgorithm.AesGcm, "eab48bb3ef0dec8f718d56cd31e34d932d304b", "c25558e23157aa5c1a71205ebed2d7594b67f0")]
    [InlineData(EncryptionAlgorithm.AesCcm, "d4d08817f35c62639d2fb3b1fdebf83ed36914", "7a52d00bfd7ea8281a1929b021806997295305")]
    public void RecordAndRekeyKnownAnswers(EncryptionAlgorithm algorithm, string first, string rekeyed)
    {
        if (!EncryptionOptions.IsSupported(algorithm)) return;
        using var cipher = new FrameCipher(algorithm, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var header = Convert.FromHexString("010000000300000003");
        var output = new byte[19];
        cipher.Encrypt(header, new byte[] { 1, 2, 3 }, output);
        Assert.Equal(Convert.FromHexString(first), output);
        for (var i = 1; i <= FrameCipher.RekeyInterval; i++) cipher.Encrypt(header, new byte[] { 1, 2, 3 }, output);
        Assert.Equal(Convert.FromHexString(rekeyed), output);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClientProofBindsFreshServerRandomAndEntireTranscript(bool replay)
    {
        await using var rig = new TunnelRig();
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        var key = EncryptionOptions.GenerateKey();
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Server, ListenPort = 0,
            RemotePort = ((IPEndPoint)target.LocalEndpoint).Port, Encryption = EncryptionTests.Server(EncryptionAlgorithm.AesGcm, key) });
        byte[] oldProof;
        using (var original = new TcpClient())
        {
            await original.ConnectAsync(endpoint, rig.Token);
            var (secret, transcript) = await ReadChallenge(original.GetStream(), key, rig.Token);
            oldProof = TunnelHandshake.Proof(secret, SHA256.HashData(transcript), "TTN3 client proof"u8);
            CryptographicOperations.ZeroMemory(secret);
        }
        using var attacker = new TcpClient(); await attacker.ConnectAsync(endpoint, rig.Token);
        var stream = attacker.GetStream();
        var (currentSecret, currentTranscript) = await ReadChallenge(stream, key, rig.Token);
        var currentProof = TunnelHandshake.Proof(currentSecret, SHA256.HashData(currentTranscript), "TTN3 client proof"u8);
        Assert.NotEqual(oldProof, currentProof);
        if (!replay)
        {
            // A different, still valid buffer limit must invalidate the proof too.
            BinaryPrimitives.WriteInt32BigEndian(currentTranscript.AsSpan(8), 8192);
            oldProof = TunnelHandshake.Proof(currentSecret, SHA256.HashData(currentTranscript), "TTN3 client proof"u8);
        }
        CryptographicOperations.ZeroMemory(currentSecret);
        await stream.WriteAsync(oldProof, rig.Token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], rig.Token));
        Assert.False(target.Pending());
        Assert.Contains(rig.Errors, e => e.Exception is CryptographicException);
    }

    private static async Task<(byte[] Secret, byte[] Transcript)> ReadChallenge(NetworkStream stream, string key, CancellationToken token)
    {
        var transcript = new byte[TunnelHandshake.TranscriptSize];
        Protocol.WriteHello(transcript.AsSpan(0, Protocol.HelloSize), new() { Mode = TunnelMode.Client, BufferSize = 4096,
            Encryption = EncryptionTests.Client(EncryptionAlgorithm.AesGcm, key) });
        await stream.WriteAsync(transcript.AsMemory(0, Protocol.HelloSize), token);
        await stream.ReadExactlyAsync(transcript.AsMemory(Protocol.HelloSize, Protocol.HelloSize), token);
        var identity = transcript.AsMemory(Protocol.HelloSize * 2, TunnelHandshake.ClientIdentitySize);
        // Reuse the client random to ensure server freshness alone defeats this replay.
        Encoding.ASCII.GetBytes("laptop").CopyTo(identity.Slice(32));
        await stream.WriteAsync(identity, token);
        await stream.ReadExactlyAsync(transcript.AsMemory(transcript.Length - 32), token);
        var serverProof = new byte[32]; await stream.ReadExactlyAsync(serverProof, token);
        var hash = SHA256.HashData(transcript);
        var secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromBase64String(key), 32, hash, "TTN3 session"u8.ToArray());
        Assert.Equal(TunnelHandshake.Proof(secret, hash, "TTN3 server proof"u8), serverProof);
        return (secret, transcript);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ForgedOrTruncatedFramesNeverReachApplication(int attack)
    {
        await using var rig = new TunnelRig();
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        var key = EncryptionOptions.GenerateKey();
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Server, ListenPort = 0,
            RemotePort = ((IPEndPoint)target.LocalEndpoint).Port, Compression = Codec.Brotli, HeartbeatMilliseconds = 0,
            Encryption = EncryptionTests.Server(EncryptionAlgorithm.AesGcm, key) });
        using var attacker = new TcpClient(); await attacker.ConnectAsync(endpoint, rig.Token);
        using var session = await TunnelHandshake.NegotiateAsync(attacker.Client, new() { Mode = TunnelMode.Client, Compression = Codec.Brotli,
            Encryption = EncryptionTests.Client(EncryptionAlgorithm.AesGcm, key) }, rig.Token);
        using var destination = await target.AcceptTcpClientAsync(rig.Token);
        var stream = attacker.GetStream();
        var header = new byte[Protocol.HeaderSize];
        var control = attack is 1 or 2;
        Protocol.WriteHeader(header, control ? (attack == 1 ? FrameType.Fin : FrameType.Noop) : FrameType.Data, control ? 0 : 10, control ? 0 : 1);
        var encrypted = new byte[(control ? 0 : 1) + FrameCipher.TagSize];
        session.Send!.Encrypt(header, control ? [] : new byte[] { 255 }, encrypted);
        if (attack < 3) encrypted[^1] ^= 1;
        if (attack == 4) header[4] ^= 1;
        await stream.WriteAsync(header, rig.Token);
        await stream.WriteAsync(attack == 3 ? encrypted.AsMemory(0, encrypted.Length - 1) : encrypted, rig.Token);
        if (attack == 3) attacker.Client.Shutdown(SocketShutdown.Send);
        await Assert.ThrowsAsync<IOException>(async () => { _ = await destination.GetStream().ReadAsync(new byte[1], rig.Token); });
        var error = await rig.FirstError.Task.WaitAsync(rig.Token);
        Assert.True(attack == 3 ? error.Exception is EndOfStreamException : error.Exception is CryptographicException, error.Exception?.ToString());
    }

    [Fact]
    public async Task MissingAuthenticatedFinResetsAnApplicationAfterValidData()
    {
        await using var rig = new TunnelRig();
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        var key = EncryptionOptions.GenerateKey();
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Server, ListenPort = 0,
            RemotePort = ((IPEndPoint)target.LocalEndpoint).Port, HeartbeatMilliseconds = 0,
            Encryption = EncryptionTests.Server(EncryptionAlgorithm.AesGcm, key) });
        using var peer = new TcpClient(); await peer.ConnectAsync(endpoint, rig.Token);
        using var session = await TunnelHandshake.NegotiateAsync(peer.Client, new() { Mode = TunnelMode.Client,
            Encryption = EncryptionTests.Client(EncryptionAlgorithm.AesGcm, key) }, rig.Token);
        using var destination = await target.AcceptTcpClientAsync(rig.Token);
        var header = new byte[Protocol.HeaderSize];
        var prefix = "authenticated prefix"u8.ToArray();
        Protocol.WriteHeader(header, FrameType.Data, prefix.Length, prefix.Length);
        var record = new byte[prefix.Length + FrameCipher.TagSize]; session.Send!.Encrypt(header, prefix, record);
        var peerStream = peer.GetStream(); await peerStream.WriteAsync(header, rig.Token); await peerStream.WriteAsync(record, rig.Token);
        var application = destination.GetStream(); var output = new byte[prefix.Length];
        await application.ReadExactlyAsync(output, rig.Token); Assert.Equal(prefix, output);
        peer.Client.Shutdown(SocketShutdown.Send);
        await Assert.ThrowsAsync<IOException>(async () => { _ = await application.ReadAsync(new byte[1], rig.Token); });
        Assert.IsType<EndOfStreamException>((await rig.FirstError.Task.WaitAsync(rig.Token)).Exception);
    }
    [Fact]
    public async Task RestartRevokesOneClientAndClosesItsExistingSession()
    {
        await using var rig = new TunnelRig();
        var revoked = EncryptionOptions.GenerateKey(); var retained = EncryptionOptions.GenerateKey();
        var encryption = EncryptionTests.Server(EncryptionAlgorithm.AesGcm, revoked, "revoked");
        encryption.Keys!["retained"] = retained;
        var options = new ForwardOptions { Mode = TunnelMode.Server, ListenPort = 0, RemotePort = rig.EchoPort, Encryption = encryption };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(rig.Token);
        var server = new Listener(options);
        var running = server.Start(stop.Token); var endpoint = await server.Ready;
        using var existing = new TcpClient(); await existing.ConnectAsync(endpoint, rig.Token);
        using var session = await TunnelHandshake.NegotiateAsync(existing.Client, new() { Mode = TunnelMode.Client,
            Encryption = EncryptionTests.Client(EncryptionAlgorithm.AesGcm, revoked, "revoked") }, rig.Token);
        var stream = existing.GetStream();
        await stop.CancelAsync(); await running;
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1], rig.Token)); }
        catch (IOException) { /* Linux may report an immediate reset when the listener closes during startup. */ }
        encryption.Keys.Remove("revoked");
        var restarted = await rig.AddAsync(options);
        using var rejected = new TcpClient(); await rejected.ConnectAsync(restarted, rig.Token);
        await Assert.ThrowsAnyAsync<IOException>(() => TunnelHandshake.NegotiateAsync(rejected.Client, new() { Mode = TunnelMode.Client,
            Encryption = EncryptionTests.Client(EncryptionAlgorithm.AesGcm, revoked, "revoked") }, rig.Token));
        using var allowed = new TcpClient(); await allowed.ConnectAsync(restarted, rig.Token);
        using var retainedSession = await TunnelHandshake.NegotiateAsync(allowed.Client, new() { Mode = TunnelMode.Client,
            Encryption = EncryptionTests.Client(EncryptionAlgorithm.AesGcm, retained, "retained") }, rig.Token);
        var header = new byte[Protocol.HeaderSize]; Protocol.WriteHeader(header, FrameType.Fin, 0, 0);
        var tag = new byte[FrameCipher.TagSize]; retainedSession.Send!.Encrypt(header, [], tag);
        var allowedStream = allowed.GetStream(); await allowedStream.WriteAsync(header, rig.Token); await allowedStream.WriteAsync(tag, rig.Token);
        await allowedStream.ReadExactlyAsync(header, rig.Token); await allowedStream.ReadExactlyAsync(tag, rig.Token);
        retainedSession.Receive!.Decrypt(header, tag, []);
        Assert.Equal(FrameType.Fin, Protocol.ReadHeader(header, 65536).Type);
    }
}
