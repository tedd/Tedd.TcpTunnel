using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Tedd.TcpTunnel.Management;

public sealed class ControlServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Func<ControlRequest, CancellationToken, Task<ControlResponse>> _handle;

    public ControlServer(string name, bool service, Func<ControlRequest, CancellationToken, Task<ControlResponse>> handle)
    {
        _handle = handle;
        if (OperatingSystem.IsWindows() && service)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            _pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        }
        else _pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var accepted = false;
            try
            {
                await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                accepted = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                ControlResponse response;
                try
                {
                    var request = await ControlProtocol.ReadAsync<ControlRequest>(_pipe, deadline.Token).ConfigureAwait(false);
                    response = request.Version == 1 ? await _handle(request, deadline.Token).ConfigureAwait(false) :
                        new(false, "Unsupported control protocol version.");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
                { response = new(false, ex.Message); }
                await ControlProtocol.WriteAsync(_pipe, response, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { }
            finally { if (accepted) _pipe.Disconnect(); }
        }
    }

    public void Dispose() => _pipe.Dispose();
}
