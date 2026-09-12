# Tedd.TcpTunnel application API

`Tedd.TcpTunnel` contains both client and server APIs in one NuGet package. It targets
**.NET 11**; the repository pins the .NET 11 RC1 SDK in `global.json`.

```sh
dotnet add package Tedd.TcpTunnel
```

The package contains the transport assembly, IntelliSense XML documentation, this guide,
and the LGPL-2.1 license. LZ4 and Zstandard dependencies are restored transitively.
It does not start a process, register a service, or check for updates when referenced.

## Choose an integration

| Application requirement | API | Socket behavior |
| --- | --- | --- |
| Embed the forwarding program | `TunnelHost` or `Listener` with `ForwardOptions` | Opens configured listeners and destination connections |
| Connect directly from application code | `TunnelClient.ConnectAsync` | Opens one connection to a tunnel server; no local listener |
| Handle traffic directly inside a server | `TunnelServer.AcceptAsync` | Uses your accepted socket; no destination connection |

Either direct endpoint interoperates with a matching program endpoint. A direct client
can reach an ordinary TCP service through a Server-mode forward. A Client-mode forward
can reach an embedded direct server. Two direct endpoints can communicate without
application-side TCP sockets. Each connection has its own compression and encryption state.

All public types are in the `Tedd.TcpTunnel` namespace. Configure options before starting
and leave them, including nested collections, unchanged while they are in use.

## Embed the forwarding program

This example hosts both roles in one process. The client listens on port 9000, reaches
the tunnel server on port 9001, and the server connects to a database on port 5432.
In separate applications, put the relevant single forward in each host.

```csharp
using Tedd.TcpTunnel;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

var host = new TunnelHost(new TunnelOptions
{
    Forwards =
    [
        new ForwardOptions
        {
            Name = "server", Mode = TunnelMode.Server,
            ListenAddress = "127.0.0.1", ListenPort = 9001,
            RemoteHost = "127.0.0.1", RemotePort = 5432,
            Compression = Codec.Lz4
        },
        new ForwardOptions
        {
            Name = "client", Mode = TunnelMode.Client,
            ListenPort = 9000, RemoteHost = "127.0.0.1", RemotePort = 9001,
            Compression = Codec.Lz4
        }
    ]
}, e => Console.Error.WriteLine($"{e.Forward}: {e.Level}: {e.Message}"));

Task running = host.RunAsync(stop.Token);
try
{
    foreach (var listener in host.Listeners)
        Console.WriteLine(await listener.Ready.WaitAsync(stop.Token));
    await running;
}
finally
{
    await stop.CancelAsync();
    await running;
}
```

Use `new Listener(forwardOptions, log)` and `Start(token)` for one forward. `Start` and
`RunAsync` run for the lifetime of the service; do not await them before awaiting `Ready`.
`Ready` returns the bound `IPEndPoint`, including the OS-selected port when `ListenPort=0`.
`ActiveConnections` reports the current per-listener count. A listener can start only once.

Cancellation stops acceptance, closes active connections, and waits for handlers to finish.
Expected shutdown cancellation is absorbed by `Listener.Start`. Startup failures fault both
`Ready` and the running task. A failed listener stops the entire host. Individual connection
failures are logged and isolated. Always observe the running task and cancel it during cleanup.

`ForwardOptions` is the same configuration used by the executable: Raw, Client, Server and
SOCKS5 modes, ACLs, retry/backoff, bounded batching, connection limits, socket tuning,
heartbeats, idle deadlines, capture, compression, encryption, and Async/Dedicated execution.
`TunnelHost` uses `TunnelOptions.Forwards`; logging is delivered through your callback.
The `Logging` and `Update` properties configure the executable's adapters and do not cause
library file logging or update checks. Your application owns logging, configuration loading,
process lifetime, and service registration.

## Direct client stream

This client connects to a direct echo server such as the example below. It creates no
local listener. The same API works with a Server-mode forward configured with LZ4.

```csharp
using System.Text;
using Tedd.TcpTunnel;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var stream = await TunnelClient.ConnectAsync(
    "127.0.0.1", 9001,
    new TunnelStreamOptions { Compression = Codec.Lz4 },
    deadline.Token);

byte[] request = Encoding.UTF8.GetBytes("Hello through the tunnel");
await stream.WriteAsync(request, deadline.Token);
await stream.CompleteWritesAsync(deadline.Token);

byte[] reply = new byte[request.Length];
await stream.ReadExactlyAsync(reply, deadline.Token);
if (await stream.ReadAsync(new byte[1], deadline.Token) != 0)
    throw new IOException("Unexpected response bytes.");
Console.WriteLine(Encoding.UTF8.GetString(reply));
```

`ConnectAsync(host, port, options, cancellationToken, log)` validates options, applies
`Retry` to TCP connection attempts, tunes the socket, and negotiates the tunnel. The
returned stream owns the socket. Its cancellation token applies to connection setup and
negotiation only. Pass cancellation tokens to subsequent I/O and dispose the stream when done.

The peer must use the opposite role and matching compression, history and encryption
settings. Buffer size and compression quality may differ. Handshake failures and established
connections are never retried or replayed. Reconnect explicitly at the application level.

## Direct server stream

This complete example accepts one connection and echoes its plaintext bytes without opening
an outgoing TCP connection. The application owns the TCP listener.

```csharp
using System.Net;
using System.Net.Sockets;
using Tedd.TcpTunnel;

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var listener = new TcpListener(IPAddress.Loopback, 9001);
listener.Start();

using var accepted = await listener.AcceptSocketAsync(deadline.Token);
await using var stream = await TunnelServer.AcceptAsync(
    accepted,
    new TunnelStreamOptions
    {
        Compression = Codec.Lz4,
        AccessControl = new() { Allow = ["127.0.0.0/8", "::1"] }
    },
    deadline.Token);

byte[] buffer = new byte[8192];
int count;
while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
    await stream.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
await stream.CompleteWritesAsync(deadline.Token);
```

`AcceptAsync(socket, options, cancellationToken, log)` takes ownership of the accepted
socket after option validation. ACL checks run before the handshake. A denied source or
failed handshake closes the socket and returns no application stream. Invalid options
leave ownership with the caller. The surrounding `using` above covers either outcome.

For a long-running service, accept connections repeatedly, limit concurrency, give every
connection its own handler and stream, isolate handler failures, and await handlers during
shutdown. The [runnable library sample](https://github.com/tedd/Tedd.TcpTunnel/tree/main/samples/LibraryDemo)
demonstrates a bounded concurrent server, a direct client, and embedded forwarding.

You can pass the returned object anywhere a duplex `System.IO.Stream` is accepted. The
bytes are application payload, with tunnel framing, compression and encryption handled
internally. Application protocols still need their own message boundaries; TCP writes do
not imply corresponding reads.

## Encryption

Generate a random shared key using `EncryptionOptions.GenerateKey()` and deliver it through
your application's secret distribution mechanism. Never use a password as a tunnel key.
Set these options on the client and server respectively; select the same algorithm:

```csharp
string key = Environment.GetEnvironmentVariable("TUNNEL_KEY")
    ?? throw new InvalidOperationException("Set TUNNEL_KEY to a generated Base64 key.");

var clientOptions = new TunnelStreamOptions
{
    Compression = Codec.Lz4,
    Encryption = new()
    {
        Algorithm = EncryptionAlgorithm.AesGcm, KeyId = "my-app", Key = key
    }
};
var serverOptions = new TunnelStreamOptions
{
    Compression = Codec.Lz4,
    Encryption = new()
    {
        Algorithm = EncryptionAlgorithm.AesGcm,
        Keys = new() { ["my-app"] = key }
    }
};
```

`None` uses TTN2. `AesGcm`, `AesCcm`, and `ChaCha20Poly1305` use authenticated TTN3,
including authenticated heartbeat and FIN records. Availability depends on the OS/runtime.
The shared-key protocol does not provide forward secrecy. Compression leaks information
through lengths; use `Compression=None` if attacker-controlled text shares a context with
secrets. PCAP capture contains plaintext application data. See the repository's
[encryption guide](https://github.com/tedd/Tedd.TcpTunnel#encrypt-a-link) for key rotation and protocol details.

## Stream contract and lifecycle

| Operation | Contract |
| --- | --- |
| `ReadAsync` / `Read` | Returns any available application bytes, up to the supplied buffer length. Use `ReadExactlyAsync` when length is known. |
| Empty read | Returns zero immediately without consuming a frame or waiting for FIN. |
| Peer FIN | Nonempty reads return zero after all buffered application bytes are consumed. Your write direction remains open. |
| `WriteAsync` / `Write` | Sends all supplied bytes immediately, split into frames bounded by `BufferSize`. Does not wait for remote delivery confirmation. |
| `FlushAsync` / `Flush` | Waits for the current writer; there is no additional application write buffer. |
| `CompleteWritesAsync` | Waits for earlier writes, sends FIN, and closes TCP sending. Idempotent. Continue reading the response afterward. |
| Write after completion | Throws `InvalidOperationException`; the read direction remains usable. |
| `Dispose` / `DisposeAsync` | Aborts both directions and releases codec/cipher state after active I/O finishes. It does not send FIN. |
| Seek, Length, Position, SetLength | Throws `NotSupportedException`. |

Use one concurrent reader and one concurrent writer. Same-direction operations are
serialized; ordering between independently scheduled calls is unspecified. Avoid holding
application locks while synchronously waiting for network I/O. The sync methods block the
caller; stream mode does not create dedicated forwarding threads.

A pre-cancelled I/O call, or cancellation while waiting for a direction's lock, leaves the
connection intact. Once wire I/O has started, cancellation aborts the stream because the
peer may have received or sent part of a frame. Dispose and reconnect rather than retrying
that operation on the same stream. Disposal interrupts pending operations.

Heartbeats are generated automatically and consumed during reads without becoming payload.
The idle timer measures application reads/writes, including reads of buffered payload;
heartbeats do not count. Applications should keep reading while expecting peer data.

## Options reference

`TunnelStreamOptions` deliberately contains connection settings only. Socket acceptance,
concurrency limits and process lifetime belong to the embedding application. For socket
forwarding with batching and dedicated pump threads, use `ForwardOptions` and `Listener`.

| Property | Default | Meaning |
| --- | --- | --- |
| `Name` | `stream` | 1–100 ASCII letters, digits, hyphens or underscores; used in logs/capture. |
| `Compression` | `None` | None, Brotli, Deflate, GZip, ZLib, Lz4, Zstandard. |
| `CompressionLevel` | `Fastest` | Controls LZ4 and Deflate/GZip/ZLib codecs. |
| `BrotliQuality`, `BrotliWindow` | `4`, `20` | Quality 0–11; window 10–24. |
| `ZstandardLevel` | `3` | -5 through 22. |
| `CompressionHistory` | `false` | Per-direction Brotli history; requires Brotli and matching peers. |
| `Encryption` | `Algorithm=None` | Client Key/KeyId or server Keys allowlist. |
| `BufferSize` | `65536` | Maximum outgoing raw frame bytes, 1024–1048576. Incoming frames use the peer's negotiated limit. |
| `HeartbeatMilliseconds` | `30000` | Outgoing idle heartbeat interval, 0–3600000; zero disables. |
| `IdleTimeoutMilliseconds` | `0` | Maximum application inactivity; zero disables. |
| `HandshakeTimeoutMilliseconds` | `10000` | Negotiation deadline, 1–300000. |
| `Retry` | 3 attempts | Client TCP setup only; attempt timeout 10000 ms, initial delay 200 ms, cap 5000 ms, jitter enabled. |
| `Socket` | NoDelay/KeepAlive enabled | Same TCP tuning as ForwardOptions; listener-only options do not affect an accepted/connected socket. |
| `AccessControl` | Empty allow/deny lists | Server source filtering. Deny takes precedence; empty Allow permits non-denied sources. |
| `Capture` | Directory=null | Optional rotating plaintext PCAP; 64 MiB/file and 4 retained files per direct connection. |

For full forwarding, `ForwardOptions.Validate()` and `TunnelOptions.Validate()` can check
configuration without opening sockets. Direct APIs validate `TunnelStreamOptions` before
connection setup. The repository [configuration reference](https://github.com/tedd/Tedd.TcpTunnel#configuration-and-command-line)
also describes socket tuning, listener limits and forwarding behavior.

## Errors and diagnostics

| Failure | Result |
| --- | --- |
| Invalid option or endpoint | `ArgumentException`, including its out-of-range/null subclasses. |
| Unsupported cipher on the platform | `PlatformNotSupportedException`. |
| Exhausted client TCP attempts | `IOException` with the last connection error as inner exception. |
| Source ACL denial | `UnauthorizedAccessException` from server acceptance. |
| Protocol mismatch or malformed frame | `InvalidDataException` (an IOException). |
| Authentication failure / altered encrypted record | `CryptographicException`; the stream becomes unusable. |
| Transport EOF without FIN | `EndOfStreamException` or another transport exception; never a clean application EOF. |
| Cancellation or handshake deadline | `OperationCanceledException`; concurrent socket shutdown may produce an I/O/socket/disposal error. |
| Idle expiry | Connection abort; subsequent I/O throws `IOException` with `TimeoutException` as inner exception. |
| I/O after disposal | `ObjectDisposedException`. |

Direct APIs propagate failures to the caller. After an I/O failure, later operations throw
`IOException` carrying the original failure. The optional `Action<TunnelEvent>` callback
receives connection establishment/closure, client retries, and socket tuning diagnostics.
Pass a thread-safe callback that does not throw. Closure events include duration and the
failure, if any, when the stream is disposed. Do not serialize secret options into logs.

## Build and publish the package

```sh
dotnet pack src/Tedd.TcpTunnel/Tedd.TcpTunnel.csproj -c Release -o artifacts/nuget
```

The NuGet workflow builds and tests the library, builds the runnable example, and creates
`.nupkg` and `.snupkg` artifacts. Publishing a GitHub Release from a `vMAJOR.MINOR.PATCH`
tag (optionally with a prerelease suffix) publishes that version to NuGet.org. The release
version must match `Directory.Build.props`. Draft releases do not publish packages.

Repository maintainers must configure the GitHub Actions secret `NUGET_API_KEY` with
NuGet permission to push `Tedd.TcpTunnel`. The workflow fails explicitly if the secret is
missing. For local publishing, set `NUGET_API_KEY` in the shell environment and run:

```sh
dotnet nuget push artifacts/nuget/Tedd.TcpTunnel.2.1.0.nupkg --source https://api.nuget.org/v3/index.json --skip-duplicate
```

The adjacent symbol package is pushed alongside the library. NuGet validates and indexes
uploads before they appear in search. Package versions are immutable; increment the version
for a subsequent release. See [NuGet publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package).
