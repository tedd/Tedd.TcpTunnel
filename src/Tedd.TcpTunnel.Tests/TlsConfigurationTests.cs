using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Tedd.TcpTunnel.Console;

namespace Tedd.TcpTunnel.Tests;

public sealed class TlsConfigurationTests
{
    [Fact]
    public void TlsConfigurationRoundTripsAndCliOverridesJson()
    {
        using var directory = new TempDirectory();
        var file = Path.Combine(directory.Path, "tls.json");
        File.WriteAllText(file, """
            {"Forwards":[{"Mode":"Client","ListenTls":{"Mode":"SqlServer","CertificatePath":"cert.pfx"}}]}
            """);
        var command = Configuration.Parse(["--config", file, "--listen-tls:protocols", "Tls12"]);
        var tls = Assert.Single(command.Options.Forwards).ListenTls;
        Assert.Equal(TlsMode.SqlServer, tls.Mode);
        Assert.Equal(SslProtocols.Tls12, tls.Protocols);
        Assert.Equal(Path.Combine(directory.Path, "cert.pfx"), tls.CertificatePath);
        var json = JsonSerializer.Serialize(command.Options, Configuration.Json);
        JsonSerializer.Deserialize<TunnelOptions>(json, Configuration.Json)!.Validate();
        var remote = Configuration.Parse(["--mode", "Server", "--remote-tls:mode", "Tls",
            "--remote-tls:target-host", "sql.example", "--remote-tls:trust-server-certificate"]);
        Assert.True(remote.Options.Forwards[0].RemoteTls.TrustServerCertificate);
        Assert.Equal("sql.example", remote.Options.Forwards[0].RemoteTls.TargetHost);
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return [new ForwardOptions { ListenTls = null! }];
        yield return [new ForwardOptions { RemoteTls = null! }];
        yield return [new ForwardOptions { ListenTls = new() { Mode = TlsMode.Tls } }];
        yield return [new ForwardOptions { ListenTls = new() { Mode = (TlsMode)99 } }];
        yield return [new ForwardOptions { RemoteTls = new() { Protocols = SslProtocols.None } }];
        yield return [new ForwardOptions { RemoteTls = new() { Protocols = (SslProtocols)1 } }];
        yield return [new ForwardOptions { RemoteTls = new() { Mode = TlsMode.SqlServer, Protocols = SslProtocols.Tls13 } }];
        yield return [new ForwardOptions { RemoteTls = new() { CipherSuites = null! } }];
        yield return [new ForwardOptions { RemoteTls = new() { CipherSuites = [(TlsCipherSuite)0] } }];
        yield return [new ForwardOptions { RemoteTls = new() { TargetHost = "" } }];
        yield return [new ForwardOptions { RemoteTls = new() { Mode = TlsMode.SqlServer, CipherSuites = [TlsCipherSuite.TLS_AES_256_GCM_SHA384] } }];
        yield return [new ForwardOptions { RemoteTls = new() { Mode = TlsMode.SqlServerStrict, TrustServerCertificate = true } }];
        foreach (var name in new[] { "", "bad,name", new string('a', 254) })
            yield return [new ForwardOptions { ListenTls = new() { SelfSignedName = name } }];
        yield return [new ForwardOptions { ListenTls = new() { CertificatePath = "" } }];
        yield return [new ForwardOptions { ListenTls = new() { CertificateKeyPath = "" } }];
        yield return [new ForwardOptions { ListenTls = new() { CertificateKeyPath = "key.pem" } }];
        yield return [new ForwardOptions { ListenTls = new() { CertificatePath = "c.pfx", GenerateSelfSigned = true } }];
        yield return [new ForwardOptions { ListenTls = new() { CertificatePassword = "password", GenerateSelfSigned = true } }];
        yield return [new ForwardOptions { Mode = TunnelMode.Server, ListenTls = new() { Mode = TlsMode.Tls, GenerateSelfSigned = true } }];
        yield return [new ForwardOptions { Mode = TunnelMode.Client, RemoteTls = new() { Mode = TlsMode.Tls } }];
        yield return [new ForwardOptions { Mode = TunnelMode.Socks5, ListenTls = new() { Mode = TlsMode.Tls, GenerateSelfSigned = true } }];
        yield return [new ForwardOptions { Mode = TunnelMode.Socks5, RemoteTls = new() { Mode = TlsMode.Tls } }];
        yield return [new ForwardOptions { RemoteTls = new() { Mode = TlsMode.SqlServer } }];
        yield return [new ForwardOptions { ListenTls = new() { Mode = TlsMode.SqlServer, GenerateSelfSigned = true } }];
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void RejectsInvalidTlsConfiguration(ForwardOptions options) => Assert.ThrowsAny<ArgumentException>(options.Validate);

    [Fact]
    public void ExplicitCipherSuitesRespectPlatformSupport()
    {
        var tls = new RemoteTlsOptions { Mode = TlsMode.Tls, CipherSuites = [TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256] };
        if (OperatingSystem.IsLinux()) { tls.Validate(); Assert.NotNull(tls.CreateCipherPolicy()); }
        else Assert.Throws<PlatformNotSupportedException>(tls.Validate);
        tls.Mode = TlsMode.None; tls.Validate();
        tls.CipherSuites = []; Assert.Null(tls.CreateCipherPolicy());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("sql.example")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void GeneratedCertificateHasIdentityAndServerUsage(string name)
    {
        using var certificate = ListenTlsOptions.CreateSelfSigned(name);
        Assert.True(certificate.HasPrivateKey);
        Assert.True(certificate.MatchesHostname(name));
        Assert.True(certificate.NotAfter > DateTime.Now.AddMonths(11));
        Assert.False(Assert.Single(certificate.Extensions.OfType<X509BasicConstraintsExtension>()).CertificateAuthority);
        Assert.Contains(Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()).EnhancedKeyUsages.Cast<Oid>(),
            oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadsPfxAndPemWithPrivateKeys(bool encrypted)
    {
        using var directory = new TempDirectory();
        using var certificate = ListenTlsOptions.CreateSelfSigned();
        var pfx = Path.Combine(directory.Path, "certificate.pfx");
        var cert = Path.Combine(directory.Path, "certificate.pem");
        var key = Path.Combine(directory.Path, "key.pem");
        var password = encrypted ? "test-password" : null;
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        File.WriteAllText(cert, certificate.ExportCertificatePem());
        using var rsa = certificate.GetRSAPrivateKey()!;
        File.WriteAllText(key, encrypted
            ? rsa.ExportEncryptedPkcs8PrivateKeyPem(password!, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000))
            : rsa.ExportPkcs8PrivateKeyPem());
        using var loaded = new ListenTlsOptions { Mode = TlsMode.Tls, CertificatePath = pfx, CertificatePassword = password }.LoadCertificate();
        using var pem = new ListenTlsOptions { Mode = TlsMode.Tls, CertificatePath = cert, CertificateKeyPath = key, CertificatePassword = password }.LoadCertificate();
        Assert.True(loaded!.HasPrivateKey); Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
        Assert.True(pem!.HasPrivateKey); Assert.Equal(certificate.Thumbprint, pem.Thumbprint);
        if (encrypted) Assert.Throws<CryptographicException>(() => new ListenTlsOptions { Mode = TlsMode.Tls, CertificatePath = pfx }.LoadCertificate());
    }

    [Fact]
    public async Task CertificateCommandsValidateFilesAndRefuseOverwrite()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "client.pfx");
        Assert.Equal(0, await Program.Main(["--generate-certificate", path, "--listen-tls:self-signed-name", "sql.local",
            "--listen-tls:certificate-password", "test-password"]));
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, "test-password", X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(certificate.MatchesHostname("sql.local")); Assert.True(certificate.HasPrivateKey);
        var before = File.ReadAllBytes(path);
        Assert.Equal(1, await Program.Main(["--generate-certificate", path]));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(1, await Program.Main(["--generate-certificate"]));
        Assert.Equal(0, await Program.Main(["--check", "--listen-tls:mode", "Tls", "--listen-tls:certificate-path", path,
            "--listen-tls:certificate-password", "test-password"]));
        Assert.Equal(1, await Program.Main(["--check", "--listen-tls:mode", "Tls", "--listen-tls:certificate-path", path]));
        Assert.Equal(1, await Program.Main(["--check", "--listen-tls:mode", "Tls", "--listen-tls:certificate-path", path + ".missing"]));
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pfx));
        Assert.Throws<ArgumentException>(() => new ListenTlsOptions { Mode = TlsMode.Tls, CertificatePath = path }.LoadCertificate());
    }

    [Fact]
    public void TdsModeMismatchIsRejectedInTunnelHandshake()
    {
        var hello = new byte[Protocol.HelloSize];
        Protocol.WriteHello(hello, new() { Mode = TunnelMode.Client, ListenTls = new() { Mode = TlsMode.SqlServer } });
        Assert.Throws<InvalidDataException>(() => Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server }));
        Assert.Equal(65536, Protocol.ValidateHello(hello, new() { Mode = TunnelMode.Server, RemoteTls = new() { Mode = TlsMode.SqlServer } }));
    }
}
