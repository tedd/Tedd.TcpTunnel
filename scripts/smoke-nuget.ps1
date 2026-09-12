param([string]$PackageDirectory = 'artifacts/nuget')
$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$package = @(Get-ChildItem -LiteralPath $packageRoot -Filter 'Tedd.TcpTunnel.*.nupkg')
if ($package.Count -ne 1) { throw 'Expected exactly one Tedd.TcpTunnel .nupkg.' }
$archive = [IO.Compression.ZipFile]::OpenRead($package[0].FullName)
try {
    foreach ($entry in @('lib/net11.0/Tedd.TcpTunnel.dll', 'lib/net11.0/Tedd.TcpTunnel.xml', 'README.md', 'LICENSE')) {
        if (!$archive.GetEntry($entry)) { throw "Package is missing $entry" }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('Tedd.TcpTunnel.nuspec').Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = $nuspec.package.metadata.version
} finally { $archive.Dispose() }
$consumer = Join-Path $packageRoot ('consumer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $consumer | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="Tedd.TcpTunnel" Version="$version" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')
@'
using System.Net;
using System.Net.Sockets;
using Tedd.TcpTunnel;

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var key = EncryptionOptions.GenerateKey();
var connect = TunnelClient.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
    new() { Compression = Codec.Lz4, Encryption = new() { Algorithm = EncryptionAlgorithm.AesGcm, Key = key } }, stop.Token);
using var socket = await listener.AcceptSocketAsync(stop.Token);
await using var server = await TunnelServer.AcceptAsync(socket,
    new() { Compression = Codec.Lz4, Encryption = new() { Algorithm = EncryptionAlgorithm.AesGcm, Keys = new() { ["default"] = key } } }, stop.Token);
await using var client = await connect;
await client.WriteAsync("package smoke test"u8.ToArray(), stop.Token);
await client.CompleteWritesAsync(stop.Token);
var bytes = new byte[18];
await server.ReadExactlyAsync(bytes, stop.Token);
if (!bytes.AsSpan().SequenceEqual("package smoke test"u8)) throw new IOException("Payload mismatch.");
if (await server.ReadAsync(new byte[1], stop.Token) != 0) throw new IOException("Missing client FIN.");
await server.WriteAsync(bytes, stop.Token);
await server.CompleteWritesAsync(stop.Token);
await client.ReadExactlyAsync(bytes, stop.Token);
if (!bytes.AsSpan().SequenceEqual("package smoke test"u8)) throw new IOException("Response mismatch.");
if (await client.ReadAsync(new byte[1], stop.Token) != 0) throw new IOException("Missing server FIN.");
Console.WriteLine("NuGet consumer: encrypted compressed duplex stream and half-close passed.");
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs')
# Isolate the package cache so a previous build with the same version cannot satisfy the test.
$escapedSource = [Security.SecurityElement]::Escape($packageRoot)
@"
<configuration>
  <packageSources>
    <clear />
    <add key="local-package" value="$escapedSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-package"><package pattern="Tedd.TcpTunnel" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'NuGet.Config')
dotnet restore (Join-Path $consumer 'Consumer.csproj') --configfile (Join-Path $consumer 'NuGet.Config') --packages (Join-Path $consumer 'packages')
if ($LASTEXITCODE -ne 0) { throw 'Package consumer restore failed.' }
dotnet run --project (Join-Path $consumer 'Consumer.csproj') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Package consumer smoke test failed.' }
