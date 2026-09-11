using System.Text.Json;
using Tedd.TcpTunnel.Console;

namespace Tedd.TcpTunnel.Tests;

public sealed class ServiceAndLoggingTests
{
    [Fact]
    public void ServiceDefinitionsQuotePathsAndRetainConfiguration()
    {
        var unit = ServiceManagement.BuildSystemdUnit("sql-tunnel", "/opt/tcp tunnel/tcptunnel", "/etc/tcp tunnel.json");
        Assert.Contains("ExecStart=\"/opt/tcp tunnel/tcptunnel\" --service --service-name \"sql-tunnel\" --config \"/etc/tcp tunnel.json\"", unit);
        Assert.Contains("Restart=on-failure", unit);
        var windows = ServiceManagement.BuildWindowsCommandLine("sql-tunnel", @"C:\Program Files\TcpTunnel\tcptunnel.exe", @"C:\ProgramData\TcpTunnel\tunnel.json");
        Assert.Equal("\"C:\\Program Files\\TcpTunnel\\tcptunnel.exe\" --service --service-name \"sql-tunnel\" --config \"C:\\ProgramData\\TcpTunnel\\tunnel.json\"", windows);
    }

    [Fact]
    public async Task WindowsServiceInstallationAndRemovalUseScWithStableArguments()
    {
        using var directory = new TempDirectory(); var config = Path.Combine(directory.Path, "tunnel.json");
        File.WriteAllText(config, """{"Forwards":[{}]}""");
        var calls = new List<(string File, string[] Arguments)>();
        Task<ServiceProcessResult> Run(string file, IReadOnlyList<string> arguments, CancellationToken token)
        {
            calls.Add((file, arguments.ToArray()));
            var code = arguments[0] == "stop" ? 1062 : arguments[0] == "delete" ? 1060 : 0;
            return Task.FromResult(new ServiceProcessResult(code));
        }
        var executable = Path.Combine(directory.Path, "tcptunnel.exe");
        var runtime = new ServiceRuntime(ServicePlatform.Windows, executable, directory.Path, Run);
        var command = Configuration.Parse(["--install-service", "--service-name", "sql-tunnel", "--config", config]);
        await ServiceManagement.InstallAsync(command, TestContext.Current.CancellationToken, runtime);
        await ServiceManagement.UninstallAsync("sql-tunnel", TestContext.Current.CancellationToken, runtime);
        Assert.Equal(["create", "description", "start", "stop", "delete"], calls.Select(call => call.Arguments[0]));
        Assert.Contains("--service", calls[0].Arguments[3]);
        Assert.All(calls, call => Assert.Equal("sc.exe", call.File));
    }

    [Fact]
    public async Task SystemdInstallationWritesEnablesAndRemovesUnit()
    {
        using var directory = new TempDirectory(); var config = Path.Combine(directory.Path, "tunnel.json");
        File.WriteAllText(config, """{"Forwards":[{}]}""");
        var calls = new List<string>();
        Task<ServiceProcessResult> Run(string file, IReadOnlyList<string> arguments, CancellationToken token)
        {
            calls.Add(string.Join(' ', arguments));
            var code = arguments[0] is "disable" or "reset-failed" ? 1 : 0;
            return Task.FromResult(new ServiceProcessResult(code));
        }
        var runtime = new ServiceRuntime(ServicePlatform.Linux, Path.Combine(directory.Path, "tcptunnel"), directory.Path, Run);
        var command = Configuration.Parse(["--install-service", "--service-name", "sql-tunnel", "--config", config]);
        await ServiceManagement.InstallAsync(command, TestContext.Current.CancellationToken, runtime);
        var unit = Path.Combine(directory.Path, "sql-tunnel.service");
        Assert.True(File.Exists(unit)); Assert.Contains("network-online.target", File.ReadAllText(unit));
        await ServiceManagement.UninstallAsync("sql-tunnel", TestContext.Current.CancellationToken, runtime);
        Assert.False(File.Exists(unit));
        Assert.Equal(["daemon-reload", "enable --now sql-tunnel.service", "disable --now sql-tunnel.service", "daemon-reload", "reset-failed sql-tunnel.service"], calls);
    }

    [Fact]
    public async Task ServiceFailuresAreExplicitAndProcessExecutionCapturesOutput()
    {
        using var directory = new TempDirectory(); var config = Path.Combine(directory.Path, "tunnel.json");
        File.WriteAllText(config, """{"Forwards":[{}]}""");
        var command = Configuration.Parse(["--install-service", "--config", config]);
        var failed = new ServiceRuntime(ServicePlatform.Windows, "tcptunnel.exe", directory.Path,
            (_, _, _) => Task.FromResult(new ServiceProcessResult(5, "stdout", "access denied")));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceManagement.InstallAsync(command, TestContext.Current.CancellationToken, failed));
        Assert.Contains("access denied", exception.Message);
        var stdoutFailure = failed with { RunProcess = (_, _, _) => Task.FromResult(new ServiceProcessResult(9, "failed", "")) };
        exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceManagement.InstallAsync(command, TestContext.Current.CancellationToken, stdoutFailure));
        Assert.Contains("failed", exception.Message);
        var unsupported = failed with { Platform = ServicePlatform.Unsupported };
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => ServiceManagement.InstallAsync(command, TestContext.Current.CancellationToken, unsupported));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => ServiceManagement.UninstallAsync("test", TestContext.Current.CancellationToken, unsupported));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ServiceManagement.InstallAsync(command with { ConfigPath = Path.Combine(directory.Path, "missing") }, TestContext.Current.CancellationToken, failed));
        var invalidUnit = failed with { Platform = ServicePlatform.Linux };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceManagement.InstallAsync(command with { ServiceName = "../escape" }, TestContext.Current.CancellationToken, invalidUnit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceManagement.UninstallAsync("../escape", TestContext.Current.CancellationToken, invalidUnit));
        Assert.Throws<InvalidOperationException>(() => ServiceManagement.ValidateExecutable(null));
        Assert.Throws<InvalidOperationException>(() => ServiceManagement.ValidateExecutable("dotnet.exe"));
        Assert.Equal(Path.GetFullPath("tcptunnel.exe"), ServiceManagement.ValidateExecutable("tcptunnel.exe"));

        var result = OperatingSystem.IsWindows()
            ? await ServiceManagement.RunProcessAsync("cmd.exe", ["/d", "/c", "echo output"], TestContext.Current.CancellationToken)
            : await ServiceManagement.RunProcessAsync("/bin/sh", ["-c", "printf output"], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode); Assert.Contains("output", result.StandardOutput);
        Assert.NotEqual(ServicePlatform.Unsupported, ServiceRuntime.Current.Platform);
    }

    [Fact]
    public void WindowsServiceLifecycleReportsStopsFailuresAndRegistrationFailure()
    {
        var stopped = WindowsService.ExerciseLifecycle(stop: true, failure: false, registrationFails: false);
        Assert.Equal(1u, stopped.State); Assert.Equal(0, stopped.ExitCode); Assert.True(stopped.Cancelled);
        var failed = WindowsService.ExerciseLifecycle(stop: false, failure: true, registrationFails: false);
        Assert.Equal(1u, failed.State); Assert.Equal(1, failed.ExitCode); Assert.False(failed.Cancelled);
        var registration = WindowsService.ExerciseLifecycle(stop: false, failure: false, registrationFails: true);
        Assert.Equal(0u, registration.State);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                WindowsService.SetDispatcherResultForTest(true);
                Assert.Equal(0, WindowsService.Run("test", _ => Task.FromResult(0)));
                WindowsService.SetDispatcherResultForTest(false);
                Assert.Throws<System.ComponentModel.Win32Exception>(() => WindowsService.Run("test", _ => Task.FromResult(0)));
                WindowsService.SetDispatcherResultForTest(null);
                Assert.Throws<System.ComponentModel.Win32Exception>(() => WindowsService.Run("test", _ => Task.FromResult(0)));
            }
            finally { WindowsService.SetDispatcherResultForTest(null); }
        }
        else Assert.Throws<PlatformNotSupportedException>(() => WindowsService.Run("test", _ => Task.FromResult(0)));
    }

    [Fact]
    public void LoggerFiltersDebugAndWritesStructuredConnectionFields()
    {
        using var directory = new TempDirectory(); var file = Path.Combine(directory.Path, "events.jsonl");
        using (var logger = new EventLogger(new() { File = file, Console = false }, false, null))
        {
            logger.Write(new("sql", "debug", "hidden", Event: "connect-started", ConnectionId: 1));
            logger.Write(new("sql", "info", "connected", Event: "connection-established", ConnectionId: 2,
                Source: "127.0.0.1:1", Destination: "[::1]:1433", DurationMilliseconds: 4));
        }
        var lines = File.ReadAllLines(file); Assert.Single(lines);
        using var json = JsonDocument.Parse(lines[0]);
        Assert.Equal("connection-established", json.RootElement.GetProperty("event").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("connectionId").GetInt64());
        Assert.Equal("[::1]:1433", json.RootElement.GetProperty("destination").GetString());
    }

    [Fact]
    public void DebugLoggerIncludesExceptionDetailsAndCanWriteConsoleOnly()
    {
        using var directory = new TempDirectory(); var config = Path.Combine(directory.Path, "tunnel.json");
        var relative = "debug.jsonl";
        using (var logger = new EventLogger(new() { Level = TunnelLogLevel.Debug, Console = false, File = relative }, false, config))
            logger.Write(new("sql", "error", "failed", new IOException("disk"), Event: "connection-failed"));
        var text = File.ReadAllText(Path.Combine(directory.Path, relative));
        Assert.Contains("System.IO.IOException", text);
        using var console = new EventLogger(new() { Console = true }, false, null);
        console.Write(new("sql", "info", "service event"));
        using var disabled = new EventLogger(new() { Console = false }, false, null);
        disabled.Write(new("sql", "info", "discarded"));
    }

    [Fact]
    public async Task ProgramRoutesWindowsServiceModeThroughTheDispatcher()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory(); var config = Path.Combine(directory.Path, "tunnel.json");
        File.WriteAllText(config, """{"Forwards":[{}],"Update":{"CheckOnStartup":false}}""");
        try
        {
            WindowsService.SetDispatcherResultForTest(true);
            Assert.Equal(0, await Program.Main(["--service", "--config", config]));
            WindowsService.SetDispatcherResultForTest(false);
            Assert.Equal(1, await Program.Main(["--service", "--config", config]));
        }
        finally { WindowsService.SetDispatcherResultForTest(null); }
    }
}
