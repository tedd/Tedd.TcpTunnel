using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tedd.TcpTunnel.Console;

internal sealed record ApplicationServices(Func<HttpClient> CreateHttpClient, Func<bool> ConfirmUpdate, UpdateRuntime? UpdateRuntime = null);

internal static class Program
{
    internal static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    public static Task<int> Main(string[] args) => RunAsync(args);

    internal static async Task<int> RunAsync(string[] args, ApplicationServices? services = null, CancellationToken token = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stop.Cancel(); };
        System.Console.CancelKeyPress += cancelHandler;
        using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        try
        {
            if (args is ["--apply-update", var plan]) return await UpdateInstaller.ApplyAsync(plan, stop.Token).ConfigureAwait(false);
            var command = Configuration.Parse(args);
            if (command.Help) { System.Console.WriteLine(Configuration.HelpText); return 0; }
            if (command.Version) { System.Console.WriteLine(Version); return 0; }
            if (command.WriteConfig is { } file) { await File.WriteAllTextAsync(file, JsonSerializer.Serialize(command.Options, Configuration.Json), stop.Token); return 0; }
            if (command.Check) { System.Console.WriteLine("Configuration is valid."); return 0; }
            using var http = services?.CreateHttpClient() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var updater = new ReleaseClient(http, command.Options.Update);
            if (command.CheckUpdate || command.UpdateNow)
            {
                var release = await updater.CheckAsync(Version, stop.Token).ConfigureAwait(false);
                if (release is null) { System.Console.WriteLine("No newer compatible release is available."); return 0; }
                System.Console.WriteLine($"Version {release.Version} is available: {release.Page}");
                if (!command.UpdateNow) return 0;
                if (!command.Yes)
                {
                    if (!(services?.ConfirmUpdate() ?? ConfirmUpdate())) return 0;
                }
                await UpdateInstaller.PrepareAsync(updater, release, command.Options.Update, stop.Token, services?.UpdateRuntime).ConfigureAwait(false);
                return 0;
            }
            var monitor = MonitorUpdatesAsync(updater, command.Options.Update, stop.Token);
            try
            {
                await new TunnelHost(command.Options, entry => System.Console.Error.WriteLine(JsonSerializer.Serialize(new
                { timestamp = DateTimeOffset.UtcNow, forward = entry.Forward, level = entry.Level, message = entry.Message, error = entry.Exception?.Message })))
                    .RunAsync(stop.Token).ConfigureAwait(false);
            }
            finally { await stop.CancelAsync().ConfigureAwait(false); await monitor.ConfigureAwait(false); }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or InvalidDataException or System.Net.Sockets.SocketException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or NotSupportedException)
        { System.Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
        finally { System.Console.CancelKeyPress -= cancelHandler; }
    }

    internal static bool ConfirmUpdate(TextReader? input = null, TextWriter? output = null)
    {
        if (input is null && System.Console.IsInputRedirected) throw new ArgumentException("Use --yes to apply an update without an interactive terminal.");
        (output ?? System.Console.Out).Write("Download and install this update? [y/N] ");
        return string.Equals((input ?? System.Console.In).ReadLine(), "y", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task MonitorUpdatesAsync(ReleaseClient updater, UpdateOptions options, CancellationToken token)
    {
        if (!options.CheckOnStartup) return;
        string? offered = null;
        try
        {
            while (true)
            {
                try
                {
                    var release = await updater.CheckAsync(Version, token).ConfigureAwait(false);
                    if (release is not null && offered != release.Version)
                    {
                        offered = release.Version;
                        System.Console.Error.WriteLine($"Update {release.Version} available. Run tcptunnel --update-now to review and install: {release.Page}");
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                { System.Console.Error.WriteLine($"Update check unavailable: {ex.Message}"); }
                await Task.Delay(TimeSpan.FromHours(options.CheckIntervalHours), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
