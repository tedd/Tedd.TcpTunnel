using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tedd.TcpTunnel;

// One instance per direction. Sequence numbers are implicit on the ordered TCP stream.
internal sealed class FrameCipher : IDisposable
{
    public const int TagSize = 16;
    // At the maximum encoded frame size, each key protects at most about 2 GiB.
    internal const int RekeyInterval = 1024;
    private readonly EncryptionAlgorithm _algorithm;
    private readonly byte[] _secret;
    private AesGcm? _gcm;
    private AesCcm? _ccm;
    private ChaCha20Poly1305? _chacha;
    private ulong _sequence;
    private bool _disposed;

    internal FrameCipher(EncryptionAlgorithm algorithm, ReadOnlySpan<byte> secret)
    {
        if (algorithm == EncryptionAlgorithm.None || !EncryptionOptions.IsSupported(algorithm))
            throw new ArgumentException("An available authenticated encryption algorithm is required.");
        _algorithm = algorithm;
        _secret = secret.ToArray();
    }

    private void Prepare(Span<byte> nonce)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sequence == ulong.MaxValue) throw new CryptographicException("Encrypted stream sequence exhausted.");
        if (_sequence % RekeyInterval == 0)
        {
            _gcm?.Dispose(); _ccm?.Dispose(); _chacha?.Dispose();
            Span<byte> key = stackalloc byte[32];
            Span<byte> next = stackalloc byte[32];
            try
            {
                HKDF.Expand(HashAlgorithmName.SHA256, _secret, key, "TTN3 record key"u8);
                HKDF.Expand(HashAlgorithmName.SHA256, _secret, next, "TTN3 next traffic secret"u8);
                next.CopyTo(_secret);
                switch (_algorithm)
                {
                    case EncryptionAlgorithm.AesGcm: _gcm = new(key, TagSize); break;
                    case EncryptionAlgorithm.AesCcm: _ccm = new(key); break;
                    case EncryptionAlgorithm.ChaCha20Poly1305: _chacha = new(key); break;
                }
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(next); }
        }
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sequence);
    }

    internal void Encrypt(ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext, Span<byte> output)
    {
        Span<byte> nonce = stackalloc byte[12];
        Prepare(nonce);
        var ciphertext = output[..plaintext.Length];
        var tag = output.Slice(plaintext.Length, TagSize);
        if (_gcm is not null) _gcm.Encrypt(nonce, plaintext, ciphertext, tag, header);
        else if (_ccm is not null)
        {
            // OpenSSL distinguishes a null input/output pointer from an empty message.
            // Back empty spans with storage so CCM processes and authenticates controls.
            Span<byte> empty = stackalloc byte[1];
            _ccm.Encrypt(nonce, plaintext.IsEmpty ? empty[..0] : plaintext,
                ciphertext.IsEmpty ? empty[..0] : ciphertext, tag, header);
        }
        else _chacha!.Encrypt(nonce, plaintext, ciphertext, tag, header);
        _sequence++;
    }

    internal void Decrypt(ReadOnlySpan<byte> header, ReadOnlySpan<byte> input, Span<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        Prepare(nonce);
        try
        {
            var ciphertext = input[..plaintext.Length];
            var tag = input.Slice(plaintext.Length, TagSize);
            if (_gcm is not null) _gcm.Decrypt(nonce, ciphertext, tag, plaintext, header);
            else if (_ccm is not null)
            {
                Span<byte> empty = stackalloc byte[1];
                _ccm.Decrypt(nonce, ciphertext.IsEmpty ? empty[..0] : ciphertext, tag,
                    plaintext.IsEmpty ? empty[..0] : plaintext, header);
            }
            else _chacha!.Decrypt(nonce, ciphertext, tag, plaintext, header);
            _sequence++;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            Dispose(); // Never permit continued use after a failed authentication.
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _gcm?.Dispose(); _ccm?.Dispose(); _chacha?.Dispose();
        CryptographicOperations.ZeroMemory(_secret);
    }
}
