# Tedd.TcpTunnel

A high-performance TCP tunneling architecture engineered for seamless client-server transmission with integrated data compression capabilities.

## Architectural Delineation

Tedd.TcpTunnel operates as a duplex transport intermediary designed to bind local listener endpoints to remote destinations.

### Established Framework Capabilities (Implemented)

* **Asynchronous Stream Transmission:** Leverages asynchronous TCP sockets for non-blocking IO.
* **Stream Compression:** Utilizes `LZ4Stream` from `K4os.Compression.LZ4.Streams` to dynamically compress outbound data and decompress inbound streams.
* **Memory Optimization:** Employs `ArrayPool<byte>` to minimize garbage collection pressure during stream buffer allocation.

### Planned Enhancements (Hypotheses)

* Implementation of dynamic configuration hot-reloading.
* Introduction of explicit multi-tenant endpoint isolation.
* Integration of a robust observability and metrics telemetry pipeline.

## Implementation Example

The following example demonstrates the programmatic instantiation of the `Listener` class using the modern .NET API surface.

```csharp
using System.Threading;
using System.Threading.Tasks;
using Tedd.TcpTunnel;

class Program
{
    static async Task Main(string[] args)
    {
        var settings = new TcpTunnelSettings
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 8080,
            RemoteHost = "192.168.1.10",
            RemotePort = 80,
            IsClient = true
        };

        var listener = new Listener(settings);
        using var cts = new CancellationTokenSource();

        await listener.Start(cts.Token);
    }
}
```
