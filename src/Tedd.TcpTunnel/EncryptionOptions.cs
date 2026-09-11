using System.Security.Cryptography;

namespace Tedd.TcpTunnel;

public enum EncryptionAlgorithm { None = 0, ChaCha20Poly1305 = 1, AesGcm = 2, AesCcm = 3 }

public sealed class EncryptionOptions
{
    public EncryptionAlgorithm Algorithm { get; set; }
    public string KeyId { get; set; } = "default";
    public string? Key { get; set; }
    public Dictionary<string, string>? Keys { get; set; }

    public static string GenerateKey()
    {
        Span<byte> key = stackalloc byte[32];
        try { RandomNumberGenerator.Fill(key); return Convert.ToBase64String(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static bool IsSupported(EncryptionAlgorithm algorithm) => algorithm switch
    {
        EncryptionAlgorithm.None => true,
        EncryptionAlgorithm.ChaCha20Poly1305 => ChaCha20Poly1305.IsSupported,
        EncryptionAlgorithm.AesGcm => AesGcm.IsSupported,
        EncryptionAlgorithm.AesCcm => AesCcm.IsSupported,
        _ => false
    };

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Encryption key IDs require 1–64 ASCII letters, digits, hyphens or underscores.");
    }

    internal static byte[] DecodeKey(string? value)
    {
        Span<byte> bytes = stackalloc byte[32];
        try
        {
            if (value is null || value.Length != 44 || !Convert.TryFromBase64String(value, bytes, out var count) || count != 32 ||
                Convert.ToBase64String(bytes) != value)
                throw new ArgumentException("Encryption keys must be canonical Base64 encoding of 32 random bytes. Use --generate-key.");
            return bytes.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void Validate(TunnelMode mode)
    {
        if (!Enum.IsDefined(Algorithm)) throw new ArgumentException("Unknown encryption algorithm.");
        ValidateId(KeyId);
        if (Algorithm == EncryptionAlgorithm.None)
        {
            if (Key is not null || Keys is not null) throw new ArgumentException("Select an encryption algorithm when configuring keys.");
            return;
        }
        if (mode is not (TunnelMode.Client or TunnelMode.Server)) throw new ArgumentException("Encryption requires a client/server tunnel pair.");
        if (!IsSupported(Algorithm)) throw new PlatformNotSupportedException("The selected encryption algorithm is unavailable on this platform.");
        if (mode == TunnelMode.Client)
        {
            if (Keys is not null) throw new ArgumentException("Clients use Encryption.Key and KeyId; servers use Encryption.Keys.");
            CryptographicOperations.ZeroMemory(DecodeKey(Key));
        }
        else
        {
            if (Key is not null || Keys is null || Keys.Count is < 1 or > 4096)
                throw new ArgumentException("Servers require Encryption.Keys with 1–4096 client entries and no Encryption.Key.");
            foreach (var (id, value) in Keys)
            {
                ValidateId(id);
                CryptographicOperations.ZeroMemory(DecodeKey(value));
            }
        }
    }
}
