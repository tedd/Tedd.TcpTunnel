using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tedd.TcpTunnel.ControlPanel;

internal sealed record ServiceState(bool Installed, string State, bool Automatic, string? Executable, string? ConfigPath, int ProcessId = 0);
internal static class WindowsOperations
{
    public static ServiceState ReadService(string name)
    {
        ValidateName(name);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        if (key is null) return new(false, "Not installed", false, null, null);
        var command = key.GetValue("ImagePath") as string ?? throw new InvalidOperationException("The service has no executable.");
        var args = SplitCommandLine(Environment.ExpandEnvironmentVariables(command));
        if (args.Length == 0 || !string.Equals(Path.GetFileName(args[0]), "tcptunnel.exe", StringComparison.OrdinalIgnoreCase) || !args.Contains("--service"))
            throw new InvalidOperationException("The selected service is not a TcpTunnel service.");
        var index = Array.IndexOf(args, "--config");
        if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("The service has no configuration path.");
        var manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception();
        try
        {
            var service = OpenService(manager, name, 4);
            if (service == 0) throw new Win32Exception();
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var state, (uint)Marshal.SizeOf<NativeStatus>(), out _)) throw new Win32Exception();
                var text = state.CurrentState switch { 1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", 7 => "Paused", _ => "Pending" };
                return new(true, text, key.GetValue("Start") is int start && start == 2, args[0], Path.GetFullPath(args[index + 1]), (int)state.ProcessId);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static async Task SetServiceAsync(string name, string action)
    {
        ValidateName(name);
        var before = ReadService(name);
        if (!before.Installed) throw new InvalidOperationException("Install the service first.");
        if (action is "stop" or "restart" && before.State != "Stopped")
        {
            if (before.State == "Starting") await WaitAsync(name, "Running");
            if (before.State != "Stopping") await RunAsync(SystemTool("sc.exe"), ["stop", name]);
            await WaitAsync(name, "Stopped");
        }
        if (action is "start" or "restart")
        {
            var current = ReadService(name);
            if (current.State == "Stopping") await WaitAsync(name, "Stopped");
            if (current.State != "Running" && current.State != "Starting") await RunAsync(SystemTool("sc.exe"), ["start", name]);
            await WaitAsync(name, "Running");
        }
    }

    public static async Task SetStartupAsync(string name, bool automatic)
    {
        if (!ReadService(name).Installed) throw new InvalidOperationException("Install the service first.");
        await RunAsync(SystemTool("sc.exe"), ["config", name, "start=", automatic ? "auto" : "demand"]);
    }

    public static async Task WaitAsync(string name, string state)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (ReadService(name).State == state) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("The service did not reach " + state + " within 30 seconds.");
    }

    public static string SystemTool(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    public static async Task<string> RunAsync(string file, string[] arguments)
    {
        var (code, text) = await RunResultAsync(file, arguments);
        if (code != 0) throw new InvalidOperationException(Path.GetFileName(file) + ": " + text.Trim());
        return text;
    }

    public static async Task ApplyFirewallRuleAsync(Tedd.TcpTunnel.Management.FirewallRule rule)
    {
        var netsh = SystemTool("netsh.exe");
        var (code, _) = await RunResultAsync(netsh, ["advfirewall", "firewall", "show", "rule", "name=" + rule.Name]);
        if (code == 1) await RunAsync(netsh, rule.AddArguments.ToArray());
        else if (code == 0)
        {
            var update = new List<string> { "advfirewall", "firewall", "set", "rule", "name=" + rule.Name, "program=" + rule.Program, "new" };
            update.AddRange(rule.AddArguments.Skip(5));
            await RunAsync(netsh, update.ToArray());
        }
        else throw new InvalidOperationException("Unable to inspect Windows Firewall rules.");
    }

    private static async Task<(int Code, string Text)> RunResultAsync(string file, string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + file);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await process.WaitForExitAsync(timeout.Token);
        var text = await output + await error;
        return (process.ExitCode, text);
    }

    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(name[0]) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("Use 1–64 letters, digits, periods, hyphens or underscores for the service name.");
    }

    private static string[] SplitCommandLine(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == 0) throw new Win32Exception();
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus { public uint ServiceType, CurrentState, Accepted, Win32Exit, SpecificExit, CheckPoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(nint service, int level, out NativeStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
}
