using System.Diagnostics;
using System.Text;

namespace Tedd.TcpTunnel.Console;

internal enum ServicePlatform { Unsupported, Windows, Linux }
internal sealed record ServiceProcessResult(int ExitCode, string StandardOutput = "", string StandardError = "");
internal sealed record ServiceRuntime(ServicePlatform Platform, string Executable, string UnitDirectory,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<ServiceProcessResult>> RunProcess)
{
    public static ServiceRuntime Current => new(
        OperatingSystem.IsWindows() ? ServicePlatform.Windows : OperatingSystem.IsLinux() ? ServicePlatform.Linux : ServicePlatform.Unsupported,
        Environment.ProcessPath ?? "", "/etc/systemd/system", ServiceManagement.RunProcessAsync);
}

internal static class ServiceManagement
{
    public static async Task InstallAsync(Command command, CancellationToken token, ServiceRuntime? runtime = null)
    {
        runtime ??= ServiceRuntime.Current;
        runtime = runtime with { Executable = ValidateExecutable(runtime.Executable) };
        var config = command.ConfigPath ?? throw new ArgumentException("A configuration file is required.");
        if (!File.Exists(config)) throw new FileNotFoundException("Configuration file not found.", config);
        if (runtime.Platform == ServicePlatform.Windows) await InstallWindowsAsync(command.ServiceName, runtime, config, token).ConfigureAwait(false);
        else if (runtime.Platform == ServicePlatform.Linux) await InstallSystemdAsync(command.ServiceName, runtime, config, token).ConfigureAwait(false);
        else throw new PlatformNotSupportedException("Service installation is supported on Windows and Linux.");
    }

    public static async Task UninstallAsync(string serviceName, CancellationToken token, ServiceRuntime? runtime = null)
    {
        runtime ??= ServiceRuntime.Current;
        if (runtime.Platform == ServicePlatform.Windows) await UninstallWindowsAsync(serviceName, runtime, token).ConfigureAwait(false);
        else if (runtime.Platform == ServicePlatform.Linux) await UninstallSystemdAsync(serviceName, runtime, token).ConfigureAwait(false);
        else throw new PlatformNotSupportedException("Service uninstallation is supported on Windows and Linux.");
    }

    internal static string BuildSystemdUnit(string serviceName, string executable, string config) => $"""
[Unit]
Description=Tedd.TcpTunnel ({serviceName})
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart={SystemdQuote(executable)} --service --service-name {SystemdQuote(serviceName)} --config {SystemdQuote(config)}
Restart=on-failure
RestartSec=2s

[Install]
WantedBy=multi-user.target
""";

    internal static string BuildWindowsCommandLine(string serviceName, string executable, string config) =>
        $"{WindowsQuote(executable)} --service --service-name {WindowsQuote(serviceName)} --config {WindowsQuote(config)}";

    private static async Task InstallWindowsAsync(string serviceName, ServiceRuntime runtime, string config, CancellationToken token)
    {
        var commandLine = BuildWindowsCommandLine(serviceName, runtime.Executable, config);
        await RunAsync(runtime, "sc.exe", ["create", serviceName, "binPath=", commandLine, "start=", "auto",
            "DisplayName=", $"Tedd.TcpTunnel ({serviceName})"], token).ConfigureAwait(false);
        await RunAsync(runtime, "sc.exe", ["description", serviceName, "TCP forwarding, compression and authenticated encryption"], token).ConfigureAwait(false);
        await RunAsync(runtime, "sc.exe", ["start", serviceName], token).ConfigureAwait(false);
        System.Console.WriteLine($"Installed and started Windows service {serviceName}.");
    }

    private static async Task UninstallWindowsAsync(string serviceName, ServiceRuntime runtime, CancellationToken token)
    {
        await RunAsync(runtime, "sc.exe", ["stop", serviceName], token, 1060, 1062).ConfigureAwait(false);
        await RunAsync(runtime, "sc.exe", ["delete", serviceName], token, 1060).ConfigureAwait(false);
        System.Console.WriteLine($"Stopped and uninstalled Windows service {serviceName}.");
    }

    private static async Task InstallSystemdAsync(string serviceName, ServiceRuntime runtime, string config, CancellationToken token)
    {
        var unitName = serviceName.ToLowerInvariant() + ".service";
        var unitDirectory = Path.GetFullPath(runtime.UnitDirectory);
        var unitPath = Path.GetFullPath(Path.Combine(unitDirectory, unitName));
        if (!string.Equals(Path.GetDirectoryName(unitPath), unitDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid systemd unit path.");
        await File.WriteAllTextAsync(unitPath, BuildSystemdUnit(serviceName, runtime.Executable, config), new UTF8Encoding(false), token).ConfigureAwait(false);
        await RunAsync(runtime, "systemctl", ["daemon-reload"], token).ConfigureAwait(false);
        await RunAsync(runtime, "systemctl", ["enable", "--now", unitName], token).ConfigureAwait(false);
        System.Console.WriteLine($"Installed and started systemd service {unitName}.");
    }

    private static async Task UninstallSystemdAsync(string serviceName, ServiceRuntime runtime, CancellationToken token)
    {
        var unitName = serviceName.ToLowerInvariant() + ".service";
        var unitDirectory = Path.GetFullPath(runtime.UnitDirectory);
        var unitPath = Path.GetFullPath(Path.Combine(unitDirectory, unitName));
        if (!string.Equals(Path.GetDirectoryName(unitPath), unitDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid systemd unit path.");
        await RunAsync(runtime, "systemctl", ["disable", "--now", unitName], token, 1, 5).ConfigureAwait(false);
        if (File.Exists(unitPath)) File.Delete(unitPath);
        await RunAsync(runtime, "systemctl", ["daemon-reload"], token).ConfigureAwait(false);
        await RunAsync(runtime, "systemctl", ["reset-failed", unitName], token, 1, 5).ConfigureAwait(false);
        System.Console.WriteLine($"Stopped and uninstalled systemd service {unitName}.");
    }

    internal static string ValidateExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Cannot determine the executable path.");
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Publish TcpTunnel as a self-contained executable before installing it as a service.");
        return Path.GetFullPath(path);
    }

    private static async Task RunAsync(ServiceRuntime runtime, string file, IReadOnlyList<string> arguments, CancellationToken token, params int[] allowedExitCodes)
    {
        var result = await runtime.RunProcess(file, arguments, token).ConfigureAwait(false);
        if (result.ExitCode != 0 && !allowedExitCodes.Contains(result.ExitCode))
            throw new InvalidOperationException($"{file} exited with code {result.ExitCode}: {(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim()}");
    }

    internal static async Task<ServiceProcessResult> RunProcessAsync(string file, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {file}.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static string SystemdQuote(string value)
    {
        if (value.Any(c => c is '\r' or '\n' or '\0')) throw new ArgumentException("Service arguments cannot contain control characters.");
        return '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal) + '"';
    }

    private static string WindowsQuote(string value)
    {
        if (value.Any(c => c is '\r' or '\n' or '\0')) throw new ArgumentException("Service arguments cannot contain control characters.");
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else { result.Append('\\', slashes).Append(character); }
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
