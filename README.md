# Tedd.TcpTunnel

Tedd.TcpTunnel is a high-performance, strictly CLI-based TCP forwarding and network protocol translation utility designed to construct encrypted and compressed (LZ4) bidirectional tunnels between clients and servers.

> **Architectural Note:** This framework operates entirely at the network transport layer. It intrinsically **does not** utilize User Interface (UI) framework architectures, meaning constructs such as hierarchical data binding or routed events are entirely absent by design. This structural choice ensures maximal computational throughput and minimal memory overhead.

## Architectural Paradigms

The underlying architecture relies on a deterministic execution flow centered around asynchronous socket operations.

### Implemented Facts

*   **Listener Execution:** The `Listener` class binds to a designated `IPAddress` and `ListenPort` to accept incoming `Socket` connections asynchronously (`AcceptAsync`).
*   **Connection Routing:** Upon establishing an incoming connection, a subsequent TCP connection is initiated to the designated `RemoteHost` and `RemotePort`.
*   **Bidirectional Transport & Compression:** Each session spawns a background `Connection` task. The `Connection` orchestrates simultaneous, bidirectional byte transfer between the local and remote sockets.
*   **Stream Processing:** The framework employs `NetworkStream` combined with `LZ4Stream.Encode` and `LZ4Stream.Decode` to achieve transparent payload compression and decompression dynamically over the wire. `CopyToAsyncWithFlush` is leveraged for robust data transference between streams.

### Planned Hypotheses (Future Scope)

*   Integration of TLS/SSL payload encryption encapsulating the LZ4 data streams.
*   Advanced telemetry and connection metric logging to monitor bandwidth utilization in real-time.
*   Dynamic configuration reloading without requiring TCP listener restart.

## Implementation Integration

The following verified example demonstrates the integration of the modern API surface utilizing C# asynchronous constructs.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Tedd.TcpTunnel;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Construct the tunnel configuration.
        var settings = new TcpTunnelSettings
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 8080,
            RemoteHost = "127.0.0.1", // Target server
            RemotePort = 80,
            IsClient = true // Specifies whether this instance acts as the client side of the tunnel.
        };

        // 2. Instantiate the Listener with the defined parameters.
        var listener = new Listener(settings);

        // 3. Initiate the asynchronous listening operation.
        // A cancellation token is supplied to allow for graceful termination.
        using var cts = new CancellationTokenSource();

        Console.WriteLine("TCP Tunnel operational. Press Ctrl+C to terminate.");
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await listener.Start(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Tunnel execution gracefully terminated.");
        }
    }
}
```
