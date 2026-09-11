# Tedd.TcpTunnel

TCP forwarding for Windows and Linux, with configurable compression, bounded batching,
SOCKS5 CONNECT, multiple forwarding setups, and concurrent connections.

[Website](https://tedd.no/Tedd.TcpTunnel/) ·
[Downloads](https://github.com/tedd/Tedd.TcpTunnel/releases) ·
[Build artifacts](https://github.com/tedd/Tedd.TcpTunnel/actions/workflows/build.yml?query=branch%3Adeploy)

## Install

| Platform | Architectures | Packages |
| --- | --- | --- |
| Windows | x64, ARM64 | MSI, EXE setup, portable ZIP |
| Linux (glibc) | x64, ARM64 | Portable ZIP |

The following commands resolve the current published release and select x64 or ARM64
automatically.

### Windows

PowerShell:

```powershell
$ErrorActionPreference = 'Stop'
$arch = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { throw 'TcpTunnel supports Windows x64 and ARM64.' }
}
$release = Invoke-RestMethod 'https://api.github.com/repos/tedd/Tedd.TcpTunnel/releases/latest'
$asset = $release.assets | Where-Object name -Like "*-win-$arch-setup.exe" | Select-Object -First 1
if (!$asset) { throw "No Windows $arch installer is present in the latest release." }
$installer = Join-Path $env:TEMP $asset.name
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installer
Start-Process -FilePath $installer -Wait
Remove-Item -LiteralPath $installer
```

Command Prompt:

```bat
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $arch=if($env:PROCESSOR_ARCHITECTURE -eq 'ARM64'){'arm64'}else{'x64'}; $release=Invoke-RestMethod 'https://api.github.com/repos/tedd/Tedd.TcpTunnel/releases/latest'; $asset=$release.assets | Where-Object name -Like ('*-win-'+$arch+'-setup.exe') | Select-Object -First 1; if(!$asset){throw 'No compatible Windows installer is present in the latest release.'}; $installer=Join-Path $env:TEMP $asset.name; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installer; Start-Process -FilePath $installer -Wait; Remove-Item -LiteralPath $installer"
```

Bash (Git Bash):

```bash
case "$(uname -m)" in
  x86_64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "TcpTunnel supports Windows x64 and ARM64." >&2; exit 1 ;;
esac
release_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' https://github.com/tedd/Tedd.TcpTunnel/releases/latest)"
version="${release_url##*/v}"
installer="$(mktemp --suffix=.exe)"
curl -fL "https://github.com/tedd/Tedd.TcpTunnel/releases/download/v$version/tcptunnel-$version-win-$arch-setup.exe" -o "$installer" &&
  "$installer" &&
  rm -f "$installer"
```

The EXE setup installs the MSI, registers upgrades and uninstallation, and adds the
installation directory to the system PATH. Open a new terminal after installation.

### Linux

Bash:

```bash
case "$(uname -m)" in
  x86_64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "TcpTunnel supports Linux x64 and ARM64." >&2; exit 1 ;;
esac
release_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' https://github.com/tedd/Tedd.TcpTunnel/releases/latest)"
version="${release_url##*/v}"
archive="$(mktemp --suffix=.zip)"
install_root="${XDG_DATA_HOME:-$HOME/.local/share}/tcptunnel"
bin_dir="$HOME/.local/bin"
curl -fL "https://github.com/tedd/Tedd.TcpTunnel/releases/download/v$version/tcptunnel-$version-linux-$arch.zip" -o "$archive" &&
  mkdir -p "$install_root" "$bin_dir" &&
  unzip -oq "$archive" -d "$install_root" &&
  chmod +x "$install_root/tcptunnel" &&
  ln -sfn "$install_root/tcptunnel" "$bin_dir/tcptunnel" &&
  rm -f "$archive"
```

PowerShell 7:

```powershell
$ErrorActionPreference = 'Stop'
$arch = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { throw 'TcpTunnel supports Linux x64 and ARM64.' }
}
$release = Invoke-RestMethod 'https://api.github.com/repos/tedd/Tedd.TcpTunnel/releases/latest'
$asset = $release.assets | Where-Object name -Like "*-linux-$arch.zip" | Select-Object -First 1
if (!$asset) { throw "No Linux $arch package is present in the latest release." }
$archive = Join-Path ([IO.Path]::GetTempPath()) $asset.name
$installRoot = Join-Path $HOME '.local/share/tcptunnel'
$binDir = Join-Path $HOME '.local/bin'
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive
New-Item -ItemType Directory -Force -Path $installRoot, $binDir | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $installRoot -Force
& chmod +x (Join-Path $installRoot 'tcptunnel')
& ln -sfn (Join-Path $installRoot 'tcptunnel') (Join-Path $binDir 'tcptunnel')
Remove-Item -LiteralPath $archive
```

The Linux commands require `curl` and `unzip`, install under `~/.local/share/tcptunnel`,
and link the executable into `~/.local/bin`. Ensure `~/.local/bin` is on `PATH`.

Packages are self-contained: a separate .NET installation is unnecessary. The portable
application is a single executable, accompanied by documentation, a license, an example
configuration, and an installation marker. Native runtime components may extract on first run.

Windows packages are unsigned unless the release maintainer signs them.

To run a portable Linux package without installing it, extract the ZIP and run:

```sh
chmod +x tcptunnel
./tcptunnel --config tunnel.example.json --check
./tcptunnel --config tunnel.example.json
```

The application targets **.NET 11 RC1**, a prerelease runtime. GitHub Actions builds are
available before a release is published; downloading those artifacts requires a GitHub account.

## Forward a connection

```sh
tcptunnel --forward web --mode Raw --listen-port 8080 --remote-host 192.0.2.10 --remote-port 80
```

Connect your application to `127.0.0.1:8080`. Each accepted connection opens its own
connection to the destination. Raw mode works with ordinary TCP services.

TcpTunnel forwards and compresses bytes; it does **not encrypt or authenticate** them.
Use trusted networks, a VPN, or an application protocol with its own security. Listeners
bind to loopback by default. Changing the bind address exposes that interface.

## Compress a link

Run a tunnel server near the destination and a client near the application. The examples
below are equivalent.

### Bash

Server:

```bash
tcptunnel --forward server --mode Server \
  --listen-address 0.0.0.0 --listen-port 9001 \
  --remote-host 127.0.0.1 --remote-port 5432 \
  --compression Brotli --compression-history true
```

Client:

```bash
tcptunnel --forward client --mode Client \
  --listen-address 127.0.0.1 --listen-port 9000 \
  --remote-host tunnel.example --remote-port 9001 \
  --compression Brotli --compression-history true
```

### PowerShell

Server:

```powershell
tcptunnel --forward server --mode Server `
  --listen-address 0.0.0.0 --listen-port 9001 `
  --remote-host 127.0.0.1 --remote-port 5432 `
  --compression Brotli --compression-history true
```

Client:

```powershell
tcptunnel --forward client --mode Client `
  --listen-address 127.0.0.1 --listen-port 9000 `
  --remote-host tunnel.example --remote-port 9001 `
  --compression Brotli --compression-history true
```

### Command Prompt

Server:

```bat
tcptunnel --forward server --mode Server ^
  --listen-address 0.0.0.0 --listen-port 9001 ^
  --remote-host 127.0.0.1 --remote-port 5432 ^
  --compression Brotli --compression-history true
```

Client:

```bat
tcptunnel --forward client --mode Client ^
  --listen-address 127.0.0.1 --listen-port 9000 ^
  --remote-host tunnel.example --remote-port 9001 ^
  --compression Brotli --compression-history true
```

Connect the application to `127.0.0.1:9000`. Both directions use compression. Each application
connection has a separate TCP tunnel and compression context. The pair must use matching
compression and history settings, with one Client and one Server. Buffer sizes and quality
may differ. TTN2 framing validates the protocol, roles, codec, history flag, and frame lengths.
Both peers must support TTN2; it does not interoperate with the archived experimental transport.

### Compression choices

| `Compression` | Intended use | Controls |
| --- | --- | --- |
| `None` | Avoid compression CPU work | Batching and socket controls |
| `Lz4` | Fast block compression | `CompressionLevel`; SmallestSize selects LZ4 HC |
| `Brotli` | Compression ratio and repeated content | `BrotliQuality` 0–11, `BrotliWindow` 10–24 |
| `Zstandard` | General-purpose speed/ratio trade-off | `ZstandardLevel` -5–22 |
| `Deflate` | Raw DEFLATE blocks | `CompressionLevel` |
| `GZip` | GZip blocks | `CompressionLevel` |
| `ZLib` | ZLib blocks | `CompressionLevel` |

`CompressionLevel` accepts `Fastest`, `Optimal`, `SmallestSize`, and `NoCompression`.
Brotli and Zstandard use their dedicated numeric controls. Brotli, Deflate, GZip and ZLib
use `System.IO.Compression`; LZ4 uses K4os, and Zstandard uses ZstdSharp.

`CompressionHistory=true` is supported with Brotli. The encoder retains state across frames,
reusing content in its sliding window. It does not train a persistent model, share dictionaries
between connections, or retain history after disconnect. Other codecs use independent blocks.
Higher quality and larger windows consume more CPU and memory. Already compressed or encrypted
traffic generally has little to gain.

### Latency and batching

`BufferSize` is the maximum application-data frame size: 1 KiB–1 MiB, default 64 KiB.
`BatchMilliseconds=0` forwards each read immediately. A positive value collects bytes until
the buffer fills or the deadline expires. The deadline starts with the first bytes and does
not restart for subsequent reads. OS scheduling and backpressure can extend delivery time.

Batching also works without compression. `Socket.NoDelay=true` disables Nagle by default.
Nagle and application batching are separate controls; enabling both can add delay.

Example client settings to benchmark with your traffic:

| Priority | Settings |
| --- | --- |
| Low batching latency | `--compression Lz4 --batch-milliseconds 0` |
| Larger transfers | `--compression Zstandard --buffer-size 262144 --batch-milliseconds 2` |
| Higher compression | `--compression Brotli --brotli-quality 9 --brotli-window 22 --compression-history true --batch-milliseconds 10` |

Run a matching Server for each compressed client.

## Multiple forwarding setups

```json
{
  "Forwards": [
    { "Name": "database", "Mode": "Raw", "ListenPort": 5433,
      "RemoteHost": "database.example", "RemotePort": 5432 },
    { "Name": "proxy", "Mode": "Socks5", "ListenPort": 1080, "MaxConnections": 256 }
  ],
  "Update": { "CheckOnStartup": true, "Repository": "tedd/Tedd.TcpTunnel" }
}
```

```sh
tcptunnel --config tunnel.json
```

The same setup can be expressed entirely on the command line:

```sh
tcptunnel --forward database --mode Raw --listen-port 5433 --remote-host database.example --remote-port 5432 --forward proxy --mode Socks5 --listen-port 1080 --max-connections 256
```

`--forward NAME` selects an existing setup or adds one. Subsequent short options apply to it.
Forwards have independent settings and connection limits. A listener startup failure stops
the instance; an individual connection failure is logged and isolated.

### SOCKS5

`Mode=Socks5` provides SOCKS5 CONNECT with IPv4, IPv6, and domain-name targets and the
no-authentication method. BIND and UDP ASSOCIATE are not supported. SOCKS mode is an
uncompressed local proxy; it does not negotiate dynamic targets through a Client/Server pair.
Non-loopback binding requires `AllowRemoteSocks=true` and appropriate network access controls.

## Configuration and command line

Every JSON field is available on the CLI. CLI values override the file. Nested fields use
`:` or `.`, and names accept PascalCase, camelCase, or hyphenation.

```sh
tcptunnel --config tunnel.json --forwards:0:socket:no-delay false
tcptunnel --config tunnel.json --forward database --retry:attempts 5
tcptunnel --config tunnel.json --update:check-on-startup false
tcptunnel --write-config tunnel.full.json
tcptunnel --help
```

`--write-config` writes all effective values and exits. `--help` includes every option and
default. Unknown JSON properties and CLI options, invalid enums, out-of-range values,
duplicate names, and incompatible combinations are rejected. Booleans accept `true`/`false`;
a boolean flag without a value means `true`. Use `null` to clear optional strings. JSON
supports comments and trailing commas.

| Command | Behavior |
| --- | --- |
| `--config PATH` | Load JSON |
| `--write-config PATH` | Write effective configuration and exit |
| `--check` | Validate without opening sockets |
| `--help` / `-h` | Usage and complete option template |
| `--version` | Application version |
| `--check-update` | Check GitHub and exit |
| `--update-now` | Offer and apply an update |
| `--update-now --yes` | Apply without an interactive prompt |

### Connections, retries, and threading

- `MaxConnections` defaults to 1024 per forward; `Backlog` defaults to 512.
- `Retry.Attempts` includes the first attempt. Connections have a timeout and capped
  exponential backoff, with optional jitter.
- Retries occur **before forwarding starts**. Established streams are not reconnected or
  replayed, which could duplicate application operations. Applications must reconnect.
- `HandshakeTimeoutMilliseconds` limits tunnel and SOCKS negotiation.
- `IdleTimeoutMilliseconds=0` disables application-data idle expiry. Heartbeats do not count
  as application activity.
- Half-closes preserve the other direction until it finishes. Cancellation closes active
  connections. Ctrl+C and Linux SIGTERM stop the instance.
- `Execution=Async` uses asynchronous sockets and is the default for many connections.
- `Execution=Dedicated` uses two OS threads per connection with blocking I/O. It can suit
  a small number of busy connections. Set an appropriate connection limit and benchmark both.

### Keepalive and socket controls

`HeartbeatMilliseconds` sends TTN2 no-op frames on idle outgoing tunnel directions. Peers
consume them without delivering them to applications. Zero disables them. Raw and SOCKS
connections use TCP keepalive instead.

`Socket` exposes `NoDelay`, `KeepAlive`, `KeepAliveSeconds`, `KeepAliveIntervalSeconds`,
`KeepAliveRetryCount`, `SendBufferSize`, `ReceiveBufferSize`, `DualMode`, and `ReuseAddress`.
Zero buffer sizes preserve OS defaults. IPv6 `DualMode` applies to listening sockets.

Linux exposes `LinuxQuickAck`, `LinuxUserTimeoutMilliseconds`, and `LinuxCongestionControl`
(for example, `cubic`, or `bbr` when available). Quick ACK is rearmed after receives. Windows
exposes optional `WindowsLoopbackFastPath` for loopback connections. Optional tuning failures
produce warnings; kernel availability and permissions determine which settings apply.

### Logging and packet capture

Operational events are JSON lines on stderr. Capture is opt-in:

```sh
tcptunnel --config tunnel.json --forward database --capture:directory captures --capture:max-file-bytes 67108864 --capture:retained-files 4
```

Capture writes classic **PCAP / LINKTYPE_RAW (101)**, readable by Wireshark and tcpdump.
It records plaintext application data in reconstructed IPv4/IPv6 TCP segments, with valid
checksums and synthetic per-direction sequence numbers. It is a stream-level capture, not a
record of original packet boundaries, handshakes, retransmissions, or physical-interface timing.

Files rotate at `MaxFileBytes`; `RetainedFiles` bounds a forward's files within the current
process run. Runs have unique prefixes. Retain/remove older runs according to your storage
policy. Captures can contain credentials and other payloads. Capture write failures terminate
the affected connection and appear in operational logs.

## Updates

Startup and periodic checks query the configured GitHub repository. Set
`Update.CheckOnStartup=false` to disable automatic checks. `CheckIntervalHours` controls
the interval, and `IncludePrerelease` controls the release channel.

An available version is offered in the log. Run `tcptunnel --update-now` to review and install.
The updater requires an exact platform asset and its SHA-256 entry in `SHA256SUMS`.
Repository control and HTTPS are the trust boundary; checksums detect corruption but do not
replace independent code signing.

Portable updates stage a verified executable under `.updates`, wait for the invoking process
to exit, and replace the executable in place. The previous binary remains as `.previous`.
Configuration files remain in place. Close other copies if they hold the binary open. The
helper writes `result.txt`; restart your normal forwarding command after success. Failed
replacement leaves the current executable intact. Staging directories can be removed after
confirming the update.

MSI-installed copies launch the verified MSI. EXE setup installs the same MSI, so automatic
selection uses the MSI upgrade path. `Update.InstallKind` can select `msi`, `exe`, or `zip`;
`auto` reads `install-kind.txt`. Installer upgrades use the installer and may require
elevation. In-place ZIP updates require a published single-file build.

## Build, test, benchmark, and release

Install the SDK pinned in `global.json`: **11.0.100-rc.1.26425.128**.

```sh
dotnet restore src/Tedd.TcpTunnel.sln --locked-mode
dotnet build src/Tedd.TcpTunnel.sln -c Release --no-restore
dotnet test --project src/Tedd.TcpTunnel.Tests/Tedd.TcpTunnel.Tests.csproj -c Release --no-progress --coverlet --coverlet-output-format cobertura --results-directory artifacts/coverage
```

Tests use xUnit and Microsoft Testing Platform. They exercise real loopback sockets, both
execution modes, all codecs, malformed input, batching, concurrent connections, half-closes,
cancellation, retries, SOCKS, captures, and updater failures. The CI gate merges Windows/Linux
reports without excluding production source: at least 97% line coverage overall, 98% in the
transport library, and 83% branch coverage.

BenchmarkDotNet includes archived/current/Pipes copies, codec round trips, Brotli history,
and TCP round trips across connection counts, payload sizes, and threading modes:

```sh
dotnet run --project src/Tedd.TcpTunnel.Benchmarks -c Release -- --filter '*StreamCopyBenchmark*' --job short
dotnet run --project src/Tedd.TcpTunnel.Benchmarks -c Release -- --filter '*CompressionBenchmark*' '*BrotliHistoryBenchmark*' --job short
dotnet run --project src/Tedd.TcpTunnel.Benchmarks -c Release -- --filter '*TunnelBenchmark*' --job short
```

The end-to-end throughput suite starts a destination sink plus a TcpTunnel client and server,
then validates and times complete transfers across the loopback path. On Windows it selects
large DLLs from `System32`; Brotli profiles repeat the largest file on one connection to compare
history reuse against the same history-disabled sequence.

```powershell
dotnet run --project src/Tedd.TcpTunnel.Benchmarks -c Release -- --throughput --target-mib 64 --warmups 1 --iterations 3 --output benchmarks.md --json-output website/benchmarks.json
```

The command records the best and median application-data throughput for 17 codec profiles in
[benchmarks.md](benchmarks.md) and the website data file. Supply repeated `--file PATH` options
to replace the Windows corpus. The manually dispatched benchmark workflow can run either this
suite or the BenchmarkDotNet microbenchmarks.

Buffers are pooled. Hot paths use spans/memory, `ValueTask`, and runtime/library vectorized
copy and compression implementations. Connection setup, async scheduling, expired timers,
some codecs, and logging still allocate. Zero allocation for the complete service and
universal throughput improvements are not guaranteed. Benchmark representative workloads.

PowerShell 7 packaging commands:

```powershell
./scripts/package.ps1 -Runtime win-x64 -Installers
./scripts/package.ps1 -Runtime win-arm64 -Installers
./scripts/package.ps1 -Runtime linux-x64
./scripts/package.ps1 -Runtime linux-arm64
```

Outputs are under `artifacts/packages`. Installers use WiX 5.0.2; WiX 6/7 have additional
maintenance-fee terms and are not upgraded automatically. MSI versions use the numeric
release version; publish increasing numeric versions for reliable installer upgrade ordering.
CI extracts each native-platform ZIP, starts the executable, validates the bundled example,
and performs a real atomic update with backup verification. Windows ARM64 packages are
cross-compiled; running their installers requires a Windows ARM64 machine.

The `deploy` branch builds/tests on Windows/Linux, packages four runtime identifiers, and
publishes `website/` through GitHub Pages. Enable Pages with **GitHub Actions** as its source.
Version tags (for example, `v2.0.0`) produce a **draft GitHub Release** with every package and
combined checksums. Review/sign artifacts and publish the draft to expose them to the website
and updater. Benchmarks are manually dispatchable. CI artifacts have 90-day retention;
published release assets remain versioned on GitHub.

Original source and historical benchmark reports are in `archive/`. The archive project
is used only for benchmark comparisons.

## License

[GNU Lesser General Public License v2.1](LICENSE).
