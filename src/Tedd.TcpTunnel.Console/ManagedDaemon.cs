using System.Diagnostics;
using Tedd.TcpTunnel.Management;

namespace Tedd.TcpTunnel.Console;

internal static class ManagedDaemon
{
    internal static async Task RunAsync(Command command, Action<TunnelEvent> log, CancellationToken token)
    {
        var host = new TunnelHost(command.Options, log);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var instance = Guid.NewGuid();
        var revision = command.ConfigPath is null ? "" : ConfigurationFile.Read(command.ConfigPath).Revision;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ControlResponse> Handle(ControlRequest request, CancellationToken requestToken)
        {
            requestToken.ThrowIfCancellationRequested();
            var path = command.ConfigPath!;
            return Task.FromResult(request.Operation switch
            {
                "status" => new ControlResponse(true, Status: new(instance, Environment.ProcessId, command.Service == ServiceOperation.Run,
                    path, watch.Elapsed.TotalSeconds, started, revision, ConfigurationFile.Read(path).Revision != revision,
                    host.Listeners.Select(l => l.GetTelemetry()).ToArray())),
                "configuration" => new ControlResponse(true, Configuration: ConfigurationFile.Read(path)),
                "save" => new ControlResponse(true, Configuration: ConfigurationFile.Save(path,
                    request.Json ?? throw new ArgumentException("Configuration is required."), request.Revision)),
                "restart-connections" => RestartConnections(request.Forward),
                "stop" when command.Service != ServiceOperation.Run => Stop(),
                _ => new ControlResponse(false, "Unknown or unavailable control operation.")
            });
        }
        ControlResponse RestartConnections(string? name)
        {
            var listeners = host.Listeners.Where(l => name is null || l.GetTelemetry().Name == name).ToArray();
            if (listeners.Length == 0) return new(false, "Forward was not found.");
            foreach (var listener in listeners) listener.RestartConnections();
            return new(true);
        }
        ControlResponse Stop()
        {
            // Let the acknowledgement leave the pipe before cancellation tears it down.
            stop.CancelAfter(200);
            return new(true);
        }
        using var control = command.ConfigPath is null ? null : new ControlServer(
            command.Service == ServiceOperation.Run ? ControlProtocol.ServicePipe(command.ServiceName) : ControlProtocol.DaemonPipe(command.ConfigPath),
            command.Service == ServiceOperation.Run, Handle);
        var controlTask = control?.RunAsync(stop.Token) ?? Task.CompletedTask;
        var hostTask = host.RunAsync(stop.Token);
        try
        {
            if (control is null) await hostTask.ConfigureAwait(false);
            else
            {
                var completed = await Task.WhenAny(hostTask, controlTask).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(hostTask, controlTask).ConfigureAwait(false);
        }
    }
}
