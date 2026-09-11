using System.IO.Compression;
using System.Net;

namespace Tedd.TcpTunnel;

public enum TunnelMode { Raw, Client, Server, Socks5 }
public enum Codec { None, Brotli, Deflate, GZip, ZLib, Lz4, Zstandard }
public enum ExecutionMode { Async, Dedicated }
public enum TunnelLogLevel { Error, Warning, Information, Debug }

public sealed class TunnelOptions
{
    public List<ForwardOptions> Forwards { get; set; } = [];
    public UpdateOptions Update { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();

    public void Validate()
    {
        if (Forwards is null || Forwards.Count == 0) throw new ArgumentException("Configure at least one forward.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var forward in Forwards)
        {
            if (forward is null) throw new ArgumentException("A forward cannot be null.");
            forward.Validate();
            if (!names.Add(forward.Name)) throw new ArgumentException($"Duplicate forward name: {forward.Name}");
        }
        if (Update is null) throw new ArgumentException("Update options cannot be null.");
        Update.Validate();
        if (Logging is null) throw new ArgumentException("Logging options cannot be null.");
        Logging.Validate();
    }
}

public sealed class ForwardOptions
{
    public string Name { get; set; } = "default";
    public TunnelMode Mode { get; set; } = TunnelMode.Raw;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 8080;
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 80;
    public Codec Compression { get; set; } = Codec.None;
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;
    public int BrotliQuality { get; set; } = 4;
    public int BrotliWindow { get; set; } = 20;
    public int ZstandardLevel { get; set; } = 3;
    public bool CompressionHistory { get; set; }
    public EncryptionOptions Encryption { get; set; } = new();
    public int BufferSize { get; set; } = 65536;
    public int BatchMilliseconds { get; set; }
    public int HeartbeatMilliseconds { get; set; } = 30000;
    public int IdleTimeoutMilliseconds { get; set; }
    public int HandshakeTimeoutMilliseconds { get; set; } = 10000;
    public int MaxConnections { get; set; } = 1024;
    public int Backlog { get; set; } = 512;
    public ExecutionMode Execution { get; set; } = ExecutionMode.Async;
    public bool AllowRemoteSocks { get; set; }
    public RetryOptions Retry { get; set; } = new();
    public SocketOptions Socket { get; set; } = new();
    public CaptureOptions Capture { get; set; } = new();
    public AccessControlOptions AccessControl { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Forward names must contain 1–100 ASCII letters, digits, hyphens or underscores.");
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Compression) || !Enum.IsDefined(Execution) || !Enum.IsDefined(CompressionLevel))
            throw new ArgumentException("Unknown mode, compression, level or execution option.");
        if (!IPAddress.TryParse(ListenAddress, out var address)) throw new ArgumentException("ListenAddress must be an IP literal.");
        Range(ListenPort, 0, 65535, nameof(ListenPort));
        Range(RemotePort, 1, 65535, nameof(RemotePort));
        if (string.IsNullOrWhiteSpace(RemoteHost)) throw new ArgumentException("RemoteHost is required.");
        Range(BufferSize, 1024, 1048576, nameof(BufferSize));
        Range(BatchMilliseconds, 0, 10000, nameof(BatchMilliseconds));
        Range(HeartbeatMilliseconds, 0, 3600000, nameof(HeartbeatMilliseconds));
        Range(IdleTimeoutMilliseconds, 0, int.MaxValue, nameof(IdleTimeoutMilliseconds));
        Range(HandshakeTimeoutMilliseconds, 1, 300000, nameof(HandshakeTimeoutMilliseconds));
        Range(MaxConnections, 1, 1000000, nameof(MaxConnections));
        Range(Backlog, 1, 65535, nameof(Backlog));
        Range(BrotliQuality, 0, 11, nameof(BrotliQuality));
        Range(BrotliWindow, 10, 24, nameof(BrotliWindow));
        Range(ZstandardLevel, -5, 22, nameof(ZstandardLevel));
        if (CompressionHistory && Compression != Codec.Brotli) throw new ArgumentException("CompressionHistory requires Brotli.");
        if (Mode is TunnelMode.Raw or TunnelMode.Socks5 && Compression != Codec.None)
            throw new ArgumentException("Compression requires a client/server tunnel pair.");
        if (Mode == TunnelMode.Socks5 && !AllowRemoteSocks && !IPAddress.IsLoopback(address))
            throw new ArgumentException("A non-loopback SOCKS listener requires AllowRemoteSocks=true.");
        if (Retry is null || Socket is null || Capture is null || Encryption is null || AccessControl is null) throw new ArgumentException("Nested forward options cannot be null.");
        Retry.Validate(); Socket.Validate(); Capture.Validate(); Encryption.Validate(Mode); AccessControl.Validate();
    }

    internal static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(name, $"Must be between {min} and {max}.");
    }
}

public sealed class RetryOptions
{
    public int Attempts { get; set; } = 3;
    public int ConnectTimeoutMilliseconds { get; set; } = 10000;
    public int InitialDelayMilliseconds { get; set; } = 200;
    public int MaxDelayMilliseconds { get; set; } = 5000;
    public bool Jitter { get; set; } = true;
    internal void Validate()
    {
        ForwardOptions.Range(Attempts, 1, 100, nameof(Attempts));
        ForwardOptions.Range(ConnectTimeoutMilliseconds, 1, 300000, nameof(ConnectTimeoutMilliseconds));
        ForwardOptions.Range(InitialDelayMilliseconds, 0, 300000, nameof(InitialDelayMilliseconds));
        ForwardOptions.Range(MaxDelayMilliseconds, InitialDelayMilliseconds, 300000, nameof(MaxDelayMilliseconds));
    }
    internal TimeSpan Delay(int failure, double random) => TimeSpan.FromMilliseconds(
        Math.Min(MaxDelayMilliseconds, InitialDelayMilliseconds * Math.Pow(2, Math.Min(failure, 30))) * (Jitter ? 0.5 + random * 0.5 : 1));
}

public sealed class SocketOptions
{
    public bool NoDelay { get; set; } = true;
    public bool KeepAlive { get; set; } = true;
    public int KeepAliveSeconds { get; set; } = 60;
    public int KeepAliveIntervalSeconds { get; set; } = 10;
    public int KeepAliveRetryCount { get; set; } = 5;
    public int SendBufferSize { get; set; }
    public int ReceiveBufferSize { get; set; }
    public bool DualMode { get; set; } = true;
    public bool ReuseAddress { get; set; }
    public bool LinuxQuickAck { get; set; }
    public int LinuxUserTimeoutMilliseconds { get; set; }
    public string? LinuxCongestionControl { get; set; }
    public bool WindowsLoopbackFastPath { get; set; }
    internal void Validate()
    {
        ForwardOptions.Range(KeepAliveSeconds, 1, 32767, nameof(KeepAliveSeconds));
        ForwardOptions.Range(KeepAliveIntervalSeconds, 1, 32767, nameof(KeepAliveIntervalSeconds));
        ForwardOptions.Range(KeepAliveRetryCount, 1, 127, nameof(KeepAliveRetryCount));
        ForwardOptions.Range(SendBufferSize, 0, int.MaxValue, nameof(SendBufferSize));
        ForwardOptions.Range(ReceiveBufferSize, 0, int.MaxValue, nameof(ReceiveBufferSize));
        ForwardOptions.Range(LinuxUserTimeoutMilliseconds, 0, int.MaxValue, nameof(LinuxUserTimeoutMilliseconds));
        if (LinuxCongestionControl is not null && (LinuxCongestionControl.Length is < 1 or > 32 || !LinuxCongestionControl.All(char.IsAsciiLetterOrDigit)))
            throw new ArgumentException("Invalid Linux congestion-control name.");
    }
}

public sealed class AccessControlOptions
{
    public List<string> Allow { get; set; } = [];
    public List<string> Deny { get; set; } = [];
    internal void Validate()
    {
        if (Allow is null || Deny is null) throw new ArgumentException("ACL allow and deny lists cannot be null.");
        _ = IpAccessControl.Create(this);
    }
}

public sealed class LoggingOptions
{
    public TunnelLogLevel Level { get; set; } = TunnelLogLevel.Information;
    public bool Console { get; set; } = true;
    public string? File { get; set; }
    internal void Validate()
    {
        if (!Enum.IsDefined(Level)) throw new ArgumentException("Unknown logging level.");
        if (File is not null && string.IsNullOrWhiteSpace(File)) throw new ArgumentException("Logging file cannot be empty.");
    }
}

public sealed class CaptureOptions
{
    public string? Directory { get; set; }
    public long MaxFileBytes { get; set; } = 64 * 1024 * 1024;
    public int RetainedFiles { get; set; } = 4;
    internal void Validate()
    {
        if (Directory is not null && string.IsNullOrWhiteSpace(Directory)) throw new ArgumentException("Capture directory cannot be empty.");
        if (MaxFileBytes < 131072) throw new ArgumentOutOfRangeException(nameof(MaxFileBytes));
        ForwardOptions.Range(RetainedFiles, 1, 1000, nameof(RetainedFiles));
    }
}

public sealed class UpdateOptions
{
    public bool CheckOnStartup { get; set; } = true;
    public int CheckIntervalHours { get; set; } = 24;
    public string Repository { get; set; } = "tedd/Tedd.TcpTunnel";
    public bool IncludePrerelease { get; set; }
    public string InstallKind { get; set; } = "auto";
    public void Validate()
    {
        ForwardOptions.Range(CheckIntervalHours, 1, 8760, nameof(CheckIntervalHours));
        var parts = Repository?.Split('/');
        if (parts is not { Length: 2 } || parts.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Repository must be owner/name.");
        if (InstallKind is not ("auto" or "zip" or "msi" or "exe")) throw new ArgumentException("InstallKind must be auto, zip, msi or exe.");
    }
}
