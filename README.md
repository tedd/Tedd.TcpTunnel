# Tedd.TcpTunnel

Tedd.TcpTunnel is a high-performance, asynchronous TCP tunneling and proxying framework engineered to establish transparent, bidirectional data streams with integrated payload compression.

## Architectural Overview

The framework's internal mechanics are designed around a highly optimized, allocation-minimized execution flow.

### 1. `Listener` Pipeline
The `Listener` serves as the primary ingress point, binding to a specified local endpoint. It manages concurrent connection processing by asynchronously accepting incoming TCP sockets and subsequently resolving and establishing connections to the designated remote host. Hostname resolution leverages `Dns.GetHostEntryAsync`, incorporating randomized IP selection for basic load distribution among resolved addresses.

### 2. `Connection` Management & Data Flow
Once a tunnel segment is established, execution transitions to the `Connection` component. This component orchestrates the bidirectional data transfer utilizing independent, concurrent asynchronous pipelines.

#### LZ4 Streaming Pipeline
To optimize bandwidth utilization, the data stream is processed through an integrated compression pipeline:
*   The payload is dynamically compressed and decompressed using `K4os.Compression.LZ4.Streams`.
*   The `IsClient` configuration parameter dictates the compression vector. In a standard client-server configuration, the client encodes (compresses) the outbound stream, and the server decodes (decompresses) the incoming stream, ensuring symmetric processing across the tunnel.

### 3. Memory Efficiency (`ArrayPool<byte>`)
To mitigate Garbage Collector (GC) pressure and maintain sub-millisecond execution times, the stream processing infrastructure eschews per-operation buffer allocations. Instead, `ExtensionMethods.CopyToAsyncWithFlush` leverages `System.Buffers.ArrayPool<byte>.Shared` to rent working memory buffers dynamically.

## Initialization Example

The following code exemplifies the initialization of a `Listener` operating in client mode, demonstrating the required configuration parameters.

```csharp
using System.Threading;
using System.Threading.Tasks;
using Tedd.TcpTunnel;

class Program
{
    static async Task Main()
    {
        // 1. Configure the tunneling parameters
        var settings = new TcpTunnelSettings
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 8080,
            RemoteHost = "example.com",
            RemotePort = 80,
            IsClient = true // Enables outbound stream compression
        };

        // 2. Instantiate the Listener
        var listener = new Listener(settings);
        using var cts = new CancellationTokenSource();

        // 3. Initiate the listening pipeline
        await listener.Start(cts.Token);
    }
}
```

## Roadmap & Future Enhancements

*   **Hypothesis:** Migrating from standard `Socket` interactions to `System.IO.Pipelines` will further reduce memory allocations and improve overall throughput by eliminating intermediate `NetworkStream` wrappers.
*   **Hypothesis:** Implementing configurable compression levels (Fastest vs. Optimal) will allow operators to fine-tune the CPU/Bandwidth trade-off based on deployment environment constraints.
