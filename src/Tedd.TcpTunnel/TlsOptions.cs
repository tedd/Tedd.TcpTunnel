using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Tedd.TcpTunnel;

public enum TlsMode { None, Tls, SqlServer, SqlServerStrict }

public class TlsOptions
{
    public TlsMode Mode { get; set; }
    public SslProtocols Protocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;
    public List<TlsCipherSuite> CipherSuites { get; set; } = [];

    public virtual void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("Unknown TLS mode.");
        if (Protocols == SslProtocols.None || (Protocols & ~(SslProtocols.Tls12 | SslProtocols.Tls13)) != 0)
            throw new ArgumentException("TLS protocols must be Tls12, Tls13, or both.");
        if (Mode == TlsMode.SqlServer && (Protocols & SslProtocols.Tls12) == 0)
            throw new ArgumentException("SQL Server TDS 7.x requires TLS 1.2. Use SqlServerStrict mode for TDS 8.0.");
        if (CipherSuites is null || CipherSuites.Any(suite => !SupportedSuites.Contains(suite)))
            throw new ArgumentException("TLS cipher suites must use ECDHE with AES-GCM or ChaCha20-Poly1305, or their TLS 1.3 equivalents.");
        if (CipherSuites.Count > 0 && !CipherSuites.Any(suite =>
            (EnabledProtocols & (IsTls13Suite(suite) ? SslProtocols.Tls13 : SslProtocols.Tls12)) != 0))
            throw new ArgumentException("No configured TLS cipher suite matches the enabled protocols.");
        if (Mode != TlsMode.None && CipherSuites.Count > 0 && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Explicit TLS cipher suites require Linux. On Windows, configure Schannel cipher suites through OS policy.");
    }

    private static bool IsTls13Suite(TlsCipherSuite suite) => suite is TlsCipherSuite.TLS_AES_128_GCM_SHA256
        or TlsCipherSuite.TLS_AES_256_GCM_SHA384 or TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256;

    private static readonly HashSet<TlsCipherSuite> SupportedSuites =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256, TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256, TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256, TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256
    ];

    // Schannel needs a temporary persisted key container; disposing the certificate
    // removes it. OpenSSL can keep the private key entirely in memory.
    internal static X509KeyStorageFlags KeyStorage => OperatingSystem.IsWindows()
        ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    internal SslProtocols EnabledProtocols => Mode == TlsMode.SqlServer ? SslProtocols.Tls12 : Protocols;
    internal CipherSuitesPolicy? CreateCipherPolicy() => CipherSuites.Count > 0 && OperatingSystem.IsLinux()
        ? new CipherSuitesPolicy(CipherSuites) : null;
}

public sealed class ListenTlsOptions : TlsOptions
{
    public string? CertificatePath { get; set; }
    public string? CertificateKeyPath { get; set; }
    public string? CertificatePassword { get; set; }
    public bool GenerateSelfSigned { get; set; }
    public string SelfSignedName { get; set; } = "localhost";

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(SelfSignedName) || SelfSignedName.Length > 253 ||
            SelfSignedName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not ':'))
            throw new ArgumentException("SelfSignedName must be a DNS name or IP address.");
        if (CertificatePath is not null && string.IsNullOrWhiteSpace(CertificatePath) ||
            CertificateKeyPath is not null && string.IsNullOrWhiteSpace(CertificateKeyPath))
            throw new ArgumentException("Certificate paths cannot be empty.");
        if (GenerateSelfSigned && (CertificatePath is not null || CertificateKeyPath is not null || CertificatePassword is not null))
            throw new ArgumentException("Choose a certificate file or GenerateSelfSigned, not both.");
        if (CertificateKeyPath is not null && CertificatePath is null)
            throw new ArgumentException("CertificateKeyPath requires CertificatePath.");
        if (Mode != TlsMode.None && !GenerateSelfSigned && CertificatePath is null)
            throw new ArgumentException("Listener TLS requires CertificatePath or GenerateSelfSigned=true.");
    }

    public X509Certificate2? LoadCertificate()
    {
        Validate();
        if (Mode == TlsMode.None) return null;
        if (GenerateSelfSigned) return CreateSelfSigned(SelfSignedName);
        X509Certificate2 certificate;
        if (CertificateKeyPath is null)
            certificate = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath!, CertificatePassword, KeyStorage);
        else
        {
            using var pem = CertificatePassword is null
                ? X509Certificate2.CreateFromPemFile(CertificatePath!, CertificateKeyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(CertificatePath!, CertificatePassword, CertificateKeyPath);
            certificate = ImportPrivateCertificate(pem);
        }
        if (certificate.HasPrivateKey) return certificate;
        certificate.Dispose();
        throw new ArgumentException("The listener certificate must contain a private key.");
    }

    public static X509Certificate2 CreateSelfSigned(string name = "localhost")
    {
        new ListenTlsOptions { SelfSignedName = name }.Validate();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(name, out var address)) san.AddIpAddress(address); else san.AddDnsName(name);
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        { san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback); }
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return ImportPrivateCertificate(certificate);
    }

    private static X509Certificate2 ImportPrivateCertificate(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, KeyStorage | X509KeyStorageFlags.Exportable); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}

public sealed class RemoteTlsOptions : TlsOptions
{
    public string? TargetHost { get; set; }
    public bool TrustServerCertificate { get; set; }

    public override void Validate()
    {
        base.Validate();
        if (Mode == TlsMode.SqlServerStrict && TrustServerCertificate)
            throw new ArgumentException("SqlServerStrict requires destination certificate validation.");
        if (TargetHost is not null && string.IsNullOrWhiteSpace(TargetHost)) throw new ArgumentException("TLS TargetHost cannot be empty.");
    }
}
