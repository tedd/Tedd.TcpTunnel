using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Tedd.TcpTunnel;

internal sealed class TunnelSession(int peerFrame, FrameCipher? send = null, FrameCipher? receive = null) : IDisposable
{
    public int PeerFrame { get; } = peerFrame;
    public FrameCipher? Send { get; } = send;
    public FrameCipher? Receive { get; } = receive;
    public void Dispose() { Send?.Dispose(); Receive?.Dispose(); }
}

internal static class TunnelHandshake
{
    // TTN3 transcript: client hello, server hello, client random + padded key ID, server random.
    internal const int ClientIdentitySize = 96;
    internal const int TranscriptSize = Protocol.HelloSize * 2 + ClientIdentitySize + 32;

    public static async Task<TunnelSession> NegotiateAsync(Socket socket, ForwardOptions options, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.HandshakeTimeoutMilliseconds);
        token = deadline.Token;
        using var transport = new SocketTransport(socket, false, options.Socket);
        var client = options.Mode == TunnelMode.Client;
        var transcript = new byte[TranscriptSize];
        var ownHello = transcript.AsMemory(client ? 0 : Protocol.HelloSize, Protocol.HelloSize);
        var peerHello = transcript.AsMemory(client ? Protocol.HelloSize : 0, Protocol.HelloSize);
        Protocol.WriteHello(ownHello.Span, options);
        await transport.SendAsync(ownHello, token).ConfigureAwait(false);
        await transport.ReadExactlyAsync(peerHello, token).ConfigureAwait(false);
        var peerFrame = Protocol.ValidateHello(peerHello.Span, options);
        if (options.Encryption.Algorithm == EncryptionAlgorithm.None) return new(peerFrame);

        var identity = transcript.AsMemory(Protocol.HelloSize * 2, ClientIdentitySize);
        var serverRandom = transcript.AsMemory(Protocol.HelloSize * 2 + ClientIdentitySize, 32);
        byte[] key;
        if (client)
        {
            RandomNumberGenerator.Fill(identity.Span[..32]);
            Encoding.ASCII.GetBytes(options.Encryption.KeyId, identity.Span[32..]);
            await transport.SendAsync(identity, token).ConfigureAwait(false);
            key = EncryptionOptions.DecodeKey(options.Encryption.Key);
        }
        else
        {
            await transport.ReadExactlyAsync(identity, token).ConfigureAwait(false);
            var id = ReadIdentity(identity.Span[32..]);
            // Use ordinal lookup even if a caller supplied a case-insensitive dictionary.
            var entry = options.Encryption.Keys!.FirstOrDefault(pair => string.Equals(pair.Key, id, StringComparison.Ordinal));
            if (entry.Value is null) throw new CryptographicException("Tunnel authentication failed.");
            key = EncryptionOptions.DecodeKey(entry.Value);
        }
        var secret = new byte[32];
        try
        {
            if (client) await transport.ReadExactlyAsync(serverRandom, token).ConfigureAwait(false);
            else
            {
                RandomNumberGenerator.Fill(serverRandom.Span);
                await transport.SendAsync(serverRandom, token).ConfigureAwait(false);
            }
            var hash = SHA256.HashData(transcript);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, key, secret, hash, "TTN3 session"u8);
            var serverProof = Proof(secret, hash, "TTN3 server proof"u8);
            var clientProof = Proof(secret, hash, "TTN3 client proof"u8);
            var received = new byte[32];
            if (client)
            {
                await transport.ReadExactlyAsync(received, token).ConfigureAwait(false);
                VerifyProof(serverProof, received);
                await transport.SendAsync(clientProof, token).ConfigureAwait(false);
            }
            else
            {
                await transport.SendAsync(serverProof, token).ConfigureAwait(false);
                await transport.ReadExactlyAsync(received, token).ConfigureAwait(false);
                VerifyProof(clientProof, received);
            }
            return CreateSession(options.Encryption.Algorithm, secret, client, peerFrame);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(secret); }
    }

    internal static string ReadIdentity(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end < 0) end = field.Length;
        if (field[end..].ContainsAnyExcept((byte)0) || field[..end].ContainsAnyInRange((byte)128, byte.MaxValue))
            throw new CryptographicException("Tunnel authentication failed.");
        var id = Encoding.ASCII.GetString(field[..end]);
        EncryptionOptions.ValidateId(id);
        return id;
    }

    internal static byte[] Proof(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> label)
    {
        Span<byte> key = stackalloc byte[32];
        try
        {
            HKDF.Expand(HashAlgorithmName.SHA256, secret, key, label);
            return HMACSHA256.HashData(key, hash);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static void VerifyProof(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> received)
    {
        if (!CryptographicOperations.FixedTimeEquals(expected, received)) throw new CryptographicException("Tunnel authentication failed.");
    }

    internal static TunnelSession CreateSession(EncryptionAlgorithm algorithm, ReadOnlySpan<byte> secret, bool client, int peerFrame)
    {
        Span<byte> c2s = stackalloc byte[32];
        Span<byte> s2c = stackalloc byte[32];
        try
        {
            HKDF.Expand(HashAlgorithmName.SHA256, secret, c2s, "TTN3 client to server"u8);
            HKDF.Expand(HashAlgorithmName.SHA256, secret, s2c, "TTN3 server to client"u8);
            return new(peerFrame, new(algorithm, client ? c2s : s2c), new(algorithm, client ? s2c : c2s));
        }
        finally { CryptographicOperations.ZeroMemory(c2s); CryptographicOperations.ZeroMemory(s2c); }
    }
}
