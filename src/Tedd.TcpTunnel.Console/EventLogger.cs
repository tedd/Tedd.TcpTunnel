using System.Text.Json;

namespace Tedd.TcpTunnel.Console;

internal sealed class EventLogger : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(Configuration.Json) { WriteIndented = false };
    private readonly LoggingOptions _options;
    private readonly bool _service;
    private readonly object _sync = new();
    private readonly StreamWriter? _file;

    public EventLogger(LoggingOptions options, bool service, string? configPath)
    {
        _options = options;
        _service = service;
        var path = options.File;
        if (path is null && service && OperatingSystem.IsWindows())
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Tedd.TcpTunnel", "tcptunnel.log");
        if (path is null) return;
        if (!Path.IsPathRooted(path) && configPath is not null)
            path = Path.Combine(Path.GetDirectoryName(configPath)!, path);
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Write(TunnelEvent entry)
    {
        if (!Enabled(entry.Level)) return;
        var json = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            forward = entry.Forward,
            level = entry.Level,
            @event = entry.Event,
            connectionId = entry.ConnectionId,
            source = entry.Source,
            destination = entry.Destination,
            durationMilliseconds = entry.DurationMilliseconds,
            message = entry.Message,
            error = entry.Exception?.Message,
            exception = _options.Level == TunnelLogLevel.Debug ? entry.Exception?.ToString() : null
        }, Json);
        lock (_sync)
        {
            if (_options.Console && (!_service || !OperatingSystem.IsWindows())) System.Console.Error.WriteLine(json);
            _file?.WriteLine(json);
        }
    }

    private bool Enabled(string level)
    {
        var eventLevel = level switch
        {
            "error" => TunnelLogLevel.Error,
            "warning" => TunnelLogLevel.Warning,
            "debug" => TunnelLogLevel.Debug,
            _ => TunnelLogLevel.Information
        };
        return eventLevel <= _options.Level;
    }

    public void Dispose() => _file?.Dispose();
}
