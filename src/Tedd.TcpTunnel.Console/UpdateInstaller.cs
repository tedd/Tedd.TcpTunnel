using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tedd.TcpTunnel.Console;

internal sealed record UpdatePlan(int ParentId, long ParentStartTicks, string Target, string Staged, string Sha256);
internal sealed record UpdateRuntime(string BaseDirectory, string Executable, bool SingleFile, string TemporaryDirectory, Action<ProcessStartInfo> Launch)
{
    internal static UpdateRuntime Current => new(AppContext.BaseDirectory, Environment.ProcessPath ?? throw new IOException("Cannot find current executable."),
        UpdateInstaller.IsSingleFile, Path.GetTempPath(), info => { using var process = Process.Start(info) ?? throw new IOException("Unable to start update process."); });
}

internal static class UpdateInstaller
{
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location explicitly identifies the single-file distribution.")]
    internal static bool IsSingleFile => Assembly.GetExecutingAssembly().Location.Length == 0;

    public static async Task PrepareAsync(ReleaseClient client, AvailableRelease release, UpdateOptions options, CancellationToken token, UpdateRuntime? runtime = null)
    {
        runtime ??= UpdateRuntime.Current;
        var kind = options.InstallKind;
        if (kind == "auto")
        {
            var marker = Path.Combine(runtime.BaseDirectory, "install-kind.txt");
            kind = File.Exists(marker) ? (await File.ReadAllTextAsync(marker, token)).Trim() : "zip";
        }
        if (kind is not ("zip" or "msi" or "exe")) throw new InvalidDataException("Unrecognized install-kind.txt.");
        if (kind != "zip" && !OperatingSystem.IsWindows()) throw new InvalidOperationException("MSI/EXE installers require Windows.");
        if (kind == "zip" && !runtime.SingleFile) throw new InvalidOperationException("In-place portable updates require a published single-file distribution.");
        var target = runtime.Executable;
        var root = kind == "zip" ? Path.Combine(Path.GetDirectoryName(target)!, ".updates") : Path.Combine(runtime.TemporaryDirectory, "Tedd.TcpTunnel-updates");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update directory cannot be a symbolic link.");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var package = await client.DownloadAsync(release, kind, directory, token).ConfigureAwait(false);
        if (kind != "zip")
        {
            var start = new ProcessStartInfo(kind == "msi" ? "msiexec.exe" : package) { UseShellExecute = true };
            if (kind == "msi") { start.ArgumentList.Add("/i"); start.ArgumentList.Add(package); }
            runtime.Launch(start);
            System.Console.WriteLine("Verified installer started. Complete its installation prompts.");
            return;
        }
        var staged = Path.Combine(directory, Path.GetFileName(target));
        ExtractExecutable(package, staged);
        var helper = Path.Combine(directory, OperatingSystem.IsWindows() ? "updater.exe" : "updater");
        File.Copy(target, helper);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, File.GetUnixFileMode(target));
        using var self = Process.GetCurrentProcess();
        var plan = new UpdatePlan(Environment.ProcessId, self.StartTime.ToUniversalTime().Ticks, target, staged,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged))));
        var planPath = Path.Combine(directory, "plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), token).ConfigureAwait(false);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(planPath);
        runtime.Launch(info);
        System.Console.WriteLine($"Verified update staged. Close other tunnel processes if they hold the executable open. Result: {Path.Combine(directory, "result.txt")}");
    }

    internal static void ExtractExecutable(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var filename = OperatingSystem.IsWindows() ? "tcptunnel.exe" : "tcptunnel";
        if (zip.Entries.Count > 16) throw new InvalidDataException("Unexpected package contents.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in zip.Entries)
        {
            if (item.FullName != item.Name || item.Name.Length == 0 || item.Name.Contains(':') || item.Name.Contains('\\') || !names.Add(item.Name) ||
                ((item.ExternalAttributes >> 16) & 0xf000) == 0xa000 || item.Length > ReleaseClient.MaxDownloadBytes)
                throw new InvalidDataException("Unsafe ZIP entry.");
        }
        var entry = zip.GetEntry(filename) ?? throw new InvalidDataException("Package has no executable for this platform.");
        if (entry.Length == 0) throw new InvalidDataException("Empty executable.");
        entry.ExtractToFile(destination, overwrite: false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public static async Task<int> ApplyAsync(string planPath, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        try
        {
            var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath, token)) ?? throw new InvalidDataException("Invalid update plan.");
            ValidatePlan(plan, directory);
            try
            {
                using var parent = Process.GetProcessById(plan.ParentId);
                if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTicks)
                    await parent.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            }
            catch (ArgumentException) { /* Parent already exited. */ }
            for (var attempt = 0; ; attempt++)
            {
                try { ReplaceExecutable(plan); break; }
                catch (IOException) when (attempt < 29) { await Task.Delay(1000, token).ConfigureAwait(false); }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "Update installed. Previous executable retained as .previous. Restart your forwarding command.", token);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or TimeoutException or OperationCanceledException)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), $"Update failed: {ex.Message}", CancellationToken.None);
            return 1;
        }
    }

    internal static void ValidatePlan(UpdatePlan plan, string directory)
    {
        var target = Path.GetFullPath(plan.Target);
        var expectedRoot = Path.Combine(Path.GetDirectoryName(target)!, ".updates");
        if (Path.GetFileName(directory).Length != 32 || !Path.GetFileName(directory).All(char.IsAsciiHexDigit) ||
            !string.Equals(Path.GetDirectoryName(directory), expectedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Path.GetFullPath(plan.Staged) != Path.Combine(directory, Path.GetFileName(target)) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(expectedRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(plan.Staged) & FileAttributes.ReparsePoint) != 0 ||
            plan.Sha256.Length != 64 || !plan.Sha256.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Unsafe update plan.");
    }

    internal static void ReplaceExecutable(UpdatePlan plan)
    {
        using (var source = File.OpenRead(plan.Staged))
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), Convert.FromHexString(plan.Sha256))) throw new InvalidDataException("Staged executable checksum mismatch.");
        // Same-volume rename is atomic; the previous binary remains available for rollback.
        var backup = plan.Target + ".previous";
        File.Replace(plan.Staged, plan.Target, backup, ignoreMetadataErrors: true);
    }
}
