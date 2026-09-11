using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tedd.TcpTunnel.Console;

namespace Tedd.TcpTunnel.Tests;

public sealed class EncryptionTests
{
    public static IEnumerable<object[]> Algorithms() => Enum.GetValues<EncryptionAlgorithm>()
        .Where(a => a != EncryptionAlgorithm.None && EncryptionOptions.IsSupported(a)).Select(a => new object[] { a });

    internal static EncryptionOptions Client(EncryptionAlgorithm algorithm, string key, string id = "laptop") =>
        new() { Algorithm = algorithm, KeyId = id, Key = key };
    internal static EncryptionOptions Server(EncryptionAlgorithm algorithm, string key, string id = "laptop") =>
        new() { Algorithm = algorithm, Keys = new() { [id] = key } };

    [Fact]
    public async Task KeyGenerationAndCliConfigurationRoundTrip()
    {
        var key = EncryptionOptions.GenerateKey();
        Assert.Equal(44, key.Length); Assert.Equal(32, Convert.FromBase64String(key).Length);
        Assert.NotEqual(key, EncryptionOptions.GenerateKey());
        Assert.True(Configuration.Parse(["--generate-key"]).GenerateKey);
        Assert.Equal(0, await Program.Main(["--generate-key"]));
        var client = Configuration.Parse(["--mode=Client", "--encryption:algorithm=ChaCha20Poly1305", "--encryption:key-id=laptop", "--encryption:key=" + key]);
        Assert.Equal(key, client.Options.Forwards[0].Encryption.Key);
        var command = Configuration.Parse(["--mode=Server", "--encryption:algorithm=AesGcm", "--encryption:keys:Laptop=" + key, "--encryption:keys:laptop=" + key, "--encryption:keys:Laptop=" + EncryptionOptions.GenerateKey()]);
        Assert.Equal(2, command.Options.Forwards[0].Encryption.Keys!.Count);
        Assert.NotEqual(command.Options.Forwards[0].Encryption.Keys!["Laptop"], command.Options.Forwards[0].Encryption.Keys!["laptop"]);
        using var directory = new TempDirectory(); var path = Path.Combine(directory.Path, "keys.json");
        File.WriteAllText(path, JsonSerializer.Serialize(command.Options, Configuration.Json));
        var reloaded = Configuration.Parse(["--config", path]);
        Assert.Equal(key, reloaded.Options.Forwards[0].Encryption.Keys!["laptop"]);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("password")] [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB=")]
    public void InvalidSecretsAreRejectedWithoutDisclosure(string? key)
    {
        var ex = Assert.Throws<ArgumentException>(() => EncryptionOptions.DecodeKey(key));
        Assert.Contains("32 random bytes", ex.Message);
    }

    [Fact]
    public void OptionsRejectAmbiguousAndUnsafeCombinations()
    {
        var key = EncryptionOptions.GenerateKey();
        void Invalid(TunnelMode mode, EncryptionOptions encryption) => Assert.ThrowsAny<ArgumentException>(() => new ForwardOptions { Mode = mode, Encryption = encryption }.Validate());
        Invalid(TunnelMode.Raw, Client(EncryptionAlgorithm.AesGcm, key));
        Invalid(TunnelMode.Socks5, Client(EncryptionAlgorithm.AesGcm, key));
        Invalid(TunnelMode.Client, new() { Algorithm = (EncryptionAlgorithm)99 });
        Invalid(TunnelMode.Client, new() { Key = key });
        Invalid(TunnelMode.Server, new() { Keys = [] });
        Invalid(TunnelMode.Client, Server(EncryptionAlgorithm.AesGcm, key));
        Invalid(TunnelMode.Client, new() { Algorithm = EncryptionAlgorithm.AesGcm });
        Invalid(TunnelMode.Server, Client(EncryptionAlgorithm.AesGcm, key));
        Invalid(TunnelMode.Server, new() { Algorithm = EncryptionAlgorithm.AesGcm });
        Invalid(TunnelMode.Server, new() { Algorithm = EncryptionAlgorithm.AesGcm, Keys = [] });
        Invalid(TunnelMode.Server, Server(EncryptionAlgorithm.AesGcm, key, "bad/id"));
        Invalid(TunnelMode.Client, null!);
        foreach (var id in new[] { "", "a b", "é", new string('a', 65) }) Invalid(TunnelMode.Client, Client(EncryptionAlgorithm.AesGcm, key, id));
        new ForwardOptions { Mode = TunnelMode.Client, Encryption = Client(EncryptionAlgorithm.AesGcm, key, new string('a', 64)) }.Validate();
        Assert.False(EncryptionOptions.IsSupported((EncryptionAlgorithm)99));
        Assert.True(EncryptionOptions.IsSupported(EncryptionAlgorithm.None));
        Assert.Throws<ArgumentException>(() => new FrameCipher(EncryptionAlgorithm.None, new byte[32]));
        Assert.Throws<ArgumentException>(() => new FrameCipher((EncryptionAlgorithm)99, new byte[32]));
        foreach (var a in Enum.GetValues<EncryptionAlgorithm>().Where(a => !EncryptionOptions.IsSupported(a)))
            Assert.Throws<PlatformNotSupportedException>(() => new ForwardOptions { Mode = TunnelMode.Client, Encryption = Client(a, key) }.Validate());
    }

    [Theory, MemberData(nameof(Algorithms))]
    public void AuthenticatedRecordsRekeyAndPreserveControlFrames(EncryptionAlgorithm algorithm)
    {
        using var client = TunnelHandshake.CreateSession(algorithm, new byte[32], true, 1024);
        using var server = TunnelHandshake.CreateSession(algorithm, new byte[32], false, 1024);
        var header = new byte[Protocol.HeaderSize];
        var plaintext = Encoding.UTF8.GetBytes("compress this before encryption");
        var encrypted = new byte[plaintext.Length + FrameCipher.TagSize];
        var decrypted = new byte[plaintext.Length];
        for (var i = 0; i < FrameCipher.RekeyInterval * 2 + 1; i++)
        {
            Protocol.WriteHeader(header, FrameType.Data, plaintext.Length, plaintext.Length);
            client.Send!.Encrypt(header, plaintext, encrypted);
            Assert.False(encrypted.AsSpan(0, plaintext.Length).SequenceEqual(plaintext));
            server.Receive!.Decrypt(header, encrypted, decrypted); Assert.Equal(plaintext, decrypted);
            server.Send!.Encrypt(header, plaintext, encrypted);
            client.Receive!.Decrypt(header, encrypted, decrypted); Assert.Equal(plaintext, decrypted);
        }
        foreach (var type in new[] { FrameType.Noop, FrameType.Fin })
        {
            Protocol.WriteHeader(header, type, 0, 0);
            var control = new byte[FrameCipher.TagSize];
            client.Send!.Encrypt(header, [], control); server.Receive!.Decrypt(header, control, []);
        }
    }

    public static IEnumerable<object[]> Attacks() => Algorithms().SelectMany(a => Enumerable.Range(0, 6).Select(attack => new[] { a[0], attack }));

    [Theory, MemberData(nameof(Attacks))]
    public void TamperingReplayReorderingAndWrongDirectionFailClosed(EncryptionAlgorithm algorithm, int attack)
    {
        using var client = TunnelHandshake.CreateSession(algorithm, new byte[32], true, 1024);
        using var server = TunnelHandshake.CreateSession(algorithm, new byte[32], false, 1024);
        var header = new byte[Protocol.HeaderSize]; Protocol.WriteHeader(header, FrameType.Data, 3, 3);
        var record = new byte[3 + FrameCipher.TagSize]; client.Send!.Encrypt(header, new byte[] { 1, 2, 3 }, record);
        var output = new byte[3]; var receiver = server.Receive!;
        switch (attack)
        {
            case 0: header[1] ^= 1; break;
            case 1: record[0] ^= 1; break;
            case 2: record[^1] ^= 1; break;
            case 3: receiver.Decrypt(header, record, output); break;
            case 4: client.Send.Encrypt(header, new byte[] { 1, 2, 3 }, record); break;
            case 5: receiver = client.Receive!; break;
        }
        Assert.ThrowsAny<CryptographicException>(() => receiver.Decrypt(header, record, output));
        Assert.Equal(new byte[3], output);
        Assert.Throws<ObjectDisposedException>(() => receiver.Decrypt(header, record, output));
    }

    [Theory, MemberData(nameof(Algorithms))]
    public void MaximumRecordAndNonceExhaustion(EncryptionAlgorithm algorithm)
    {
        var input = RandomNumberGenerator.GetBytes(BlockCodec.MaxEncodedLength(1048576));
        using var sender = new FrameCipher(algorithm, new byte[32]);
        using var receiver = new FrameCipher(algorithm, new byte[32]);
        var ciphertext = new byte[input.Length + FrameCipher.TagSize]; var output = new byte[input.Length];
        sender.Encrypt([], input, ciphertext); receiver.Decrypt([], ciphertext, output); Assert.Equal(input, output);
        typeof(FrameCipher).GetField("_sequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, ulong.MaxValue);
        Assert.Throws<CryptographicException>(() => sender.Encrypt([], input, ciphertext));
    }

    [Fact]
    public void IdentityAndProofValidation()
    {
        Assert.Equal(new string('a', 64), TunnelHandshake.ReadIdentity(Enumerable.Repeat((byte)'a', 64).ToArray()));
        var field = new byte[64]; field[0] = (byte)'a'; field[2] = (byte)'b';
        Assert.Throws<CryptographicException>(() => TunnelHandshake.ReadIdentity(field));
        field[1] = 255; Assert.Throws<CryptographicException>(() => TunnelHandshake.ReadIdentity(field));
        Assert.Throws<ArgumentException>(() => TunnelHandshake.ReadIdentity(new byte[64]));
        Assert.Throws<CryptographicException>(() => TunnelHandshake.VerifyProof(new byte[32], new byte[31]));
        Assert.NotEqual(TunnelHandshake.Proof(new byte[32], new byte[32], "client"u8), TunnelHandshake.Proof(new byte[32], new byte[32], "server"u8));
    }

    [Theory]
    [InlineData(EncryptionAlgorithm.None)] [InlineData(EncryptionAlgorithm.AesGcm)] [InlineData(EncryptionAlgorithm.AesCcm)]
    public void AlgorithmMismatchCannotDowngrade(EncryptionAlgorithm peer)
    {
        var hello = new byte[Protocol.HelloSize]; Protocol.WriteHello(hello, new() { Mode = TunnelMode.Client, Encryption = new() { Algorithm = peer } });
        Assert.Throws<InvalidDataException>(() => Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server, Encryption = new() { Algorithm = EncryptionAlgorithm.ChaCha20Poly1305 } }));
        Protocol.WriteHello(hello, new() { Mode = TunnelMode.Client, Encryption = new() { Algorithm = EncryptionAlgorithm.ChaCha20Poly1305 } });
        Assert.Equal(65536, Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server, Encryption = new() { Algorithm = EncryptionAlgorithm.ChaCha20Poly1305 } }));
        hello[15] = 1;
        Assert.Throws<InvalidDataException>(() => Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server, Encryption = new() { Algorithm = EncryptionAlgorithm.ChaCha20Poly1305 } }));
    }

    public static IEnumerable<object[]> Modes() => Algorithms().SelectMany(a => Enum.GetValues<ExecutionMode>().SelectMany(mode =>
        Enum.GetValues<Codec>().Select(codec => new object[] { a[0], mode, codec })));

    [Theory, MemberData(nameof(Modes))]
    public async Task EncryptedCompressionConcurrentKeysAndHalfClose(EncryptionAlgorithm algorithm, ExecutionMode mode, Codec codec)
    {
        await using var rig = new TunnelRig();
        var first = EncryptionOptions.GenerateKey(); var second = EncryptionOptions.GenerateKey();
        var serverKeys = Server(algorithm, first); serverKeys.Keys!["desktop"] = second;
        var server = await rig.AddAsync(new() { Name = "server", Mode = TunnelMode.Server, ListenPort = 0, RemotePort = rig.EchoPort,
            Compression = codec, CompressionHistory = codec == Codec.Brotli, Encryption = serverKeys, Execution = mode, BufferSize = 8192, HeartbeatMilliseconds = 10 });
        var clients = new List<IPEndPoint>();
        foreach (var (id, key) in serverKeys.Keys)
            clients.Add(await rig.AddAsync(new() { Name = id, Mode = TunnelMode.Client, ListenPort = 0, RemotePort = server.Port,
                Compression = codec, CompressionHistory = codec == Codec.Brotli, Encryption = Client(algorithm, key, id), Execution = mode, BufferSize = 4096, HeartbeatMilliseconds = 10 }));
        await Task.WhenAll(clients.Select(async endpoint =>
        {
            using var client = new TcpClient(); await client.ConnectAsync(endpoint, rig.Token);
            var stream = client.GetStream();
            await Task.Delay(60, rig.Token); Assert.Equal(0, client.Available);
            var input = RandomNumberGenerator.GetBytes(200000); var output = new byte[input.Length];
            var send = Task.Run(async () => { await stream.WriteAsync(input, rig.Token); client.Client.Shutdown(SocketShutdown.Send); }, rig.Token);
            await stream.ReadExactlyAsync(output, rig.Token); await send;
            Assert.Equal(input, output); Assert.Equal(0, await stream.ReadAsync(new byte[1], rig.Token));
        }));
        Assert.Empty(rig.Errors);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WrongOrRevokedKeyNeverConnectsToDestination(bool revoked)
    {
        await using var rig = new TunnelRig();
        using var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
        var key = EncryptionOptions.GenerateKey();
        var server = await rig.AddAsync(new() { Mode = TunnelMode.Server, ListenPort = 0, RemotePort = ((IPEndPoint)destination.LocalEndpoint).Port,
            Encryption = Server(EncryptionAlgorithm.AesGcm, key, revoked ? "retained" : "laptop") });
        var endpoint = await rig.AddAsync(new() { Mode = TunnelMode.Client, ListenPort = 0, RemotePort = server.Port,
            Encryption = Client(EncryptionAlgorithm.AesGcm, revoked ? key : EncryptionOptions.GenerateKey()) });
        using var app = new TcpClient(); await app.ConnectAsync(endpoint, rig.Token);
        await Assert.ThrowsAsync<IOException>(async () => { _ = await app.GetStream().ReadAsync(new byte[1], rig.Token); });
        Assert.False(destination.Pending());
        Assert.Contains(rig.Errors, e => e.Exception is CryptographicException);
    }
}
