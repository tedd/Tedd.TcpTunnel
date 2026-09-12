using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tedd.TcpTunnel.Management;

public sealed record ControlRequest(string Operation, string? Json = null, string? Revision = null, string? Forward = null, int Version = 1);
public sealed record DaemonStatus(Guid InstanceId, int ProcessId, bool IsService, string ConfigPath, double SampleSeconds,
    DateTimeOffset StartedAt, string ActiveRevision, bool PendingConfiguration, IReadOnlyList<ForwardTelemetry> Forwards);
public sealed record ControlResponse(bool Success, string? Error = null, DaemonStatus? Status = null, ConfigurationDocument? Configuration = null);

public static class ControlProtocol
{
    public const int MaximumMessageBytes = 1024 * 1024;
    public static string ServicePipe(string name) => "Tedd.TcpTunnel.Service." + Hash(name.ToUpperInvariant());
    public static string DaemonPipe(string configPath) => "Tedd.TcpTunnel.Daemon." +
        Hash(OperatingSystem.IsWindows() ? Path.GetFullPath(configPath).ToUpperInvariant() : Path.GetFullPath(configPath));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ConfigurationFile.Json);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Control message exceeds 1 MiB.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid control message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, ConfigurationFile.Json) ?? throw new InvalidDataException("Empty control message.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    public static async Task<ControlResponse> SendAsync(string pipeName, ControlRequest request, CancellationToken token = default, int? expectedServerProcessId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(1500, timeout.Token).ConfigureAwait(false);
        // Authenticate the service process before sending configuration secrets.
        if (OperatingSystem.IsWindows() && expectedServerProcessId is { } expected &&
            (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expected))
            throw new UnauthorizedAccessException("The control pipe does not belong to the selected daemon process.");
        await WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        var response = await ReadAsync<ControlResponse>(pipe, timeout.Token).ConfigureAwait(false);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "The daemon rejected the request.");
        return response;
    }
}
