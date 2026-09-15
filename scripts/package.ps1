param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')][string]$Runtime = 'win-x64',
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version = '2.0.0',
    [switch]$Installers,
    [switch]$UpdateLocks,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts/publish/$Runtime"
$packages = Join-Path $root 'artifacts/packages'
New-Item -ItemType Directory -Force -Path $output, $packages | Out-Null
& $Dotnet publish (Join-Path $root 'src/Tedd.TcpTunnel.Console') -c Release -r $Runtime --self-contained true '-p:PublishSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:Version=$Version" "-p:NuGetLockFilePath=packages.$Runtime.lock.json" "-p:RestoreLockedMode=$(!$UpdateLocks)" -o $output
if ($LASTEXITCODE) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.txt'), (Join-Path $root 'README.md'), (Join-Path $root 'tunnel.example.json') -Destination $output
Set-Content -LiteralPath (Join-Path $output 'install-kind.txt') -Value 'zip' -NoNewline
$executable = if ($Runtime.StartsWith('win-')) { 'tcptunnel.exe' } else { 'tcptunnel' }
if (!(Test-Path -LiteralPath (Join-Path $output $executable))) { throw 'Published executable is missing.' }
# Select the exact distribution contents; stale build output never enters the archive.
$zipPath = Join-Path $packages "tcptunnel-$Version-$Runtime.zip"
Add-Type -AssemblyName System.IO.Compression
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @($executable, 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.md', 'tunnel.example.json', 'install-kind.txt')) {
        $entry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $output $name), $name, [System.IO.Compression.CompressionLevel]::Optimal)
        if ($name -eq $executable -and $Runtime.StartsWith('linux-')) { $entry.ExternalAttributes = 0x81ed0000 }
    }
} finally { $zip.Dispose() }
if ($Installers) {
    if (!$IsWindows -or !$Runtime.StartsWith('win-')) { throw 'MSI and EXE packages must be built on Windows for a Windows runtime.' }
    $architecture = $Runtime.Split('-')[1]
    $msiVersion = $Version.Split('-')[0]
    & $Dotnet build (Join-Path $root 'installer/Package/Package.wixproj') -c Release "-p:InstallerPlatform=$architecture" "-p:BaseIntermediateOutputPath=obj/$architecture/" "-p:PackageVersion=$msiVersion" "-p:PublishDirectory=$output" "-p:OutputPath=$packages/"
    if ($LASTEXITCODE) { throw 'MSI build failed.' }
    $msi = Join-Path $packages "tcptunnel-$msiVersion-$Runtime.msi"
    & $Dotnet build (Join-Path $root 'installer/Bundle/Bundle.wixproj') -c Release "-p:InstallerPlatform=$architecture" "-p:BaseIntermediateOutputPath=obj/$architecture/" "-p:PackageVersion=$msiVersion" "-p:MsiPath=$msi" "-p:OutputPath=$packages/"
    if ($LASTEXITCODE) { throw 'EXE bundle build failed.' }
    if ($Version -ne $msiVersion) {
        Move-Item -LiteralPath $msi -Destination (Join-Path $packages "tcptunnel-$Version-$Runtime.msi")
        Move-Item -LiteralPath (Join-Path $packages "tcptunnel-$msiVersion-$Runtime-setup.exe") -Destination (Join-Path $packages "tcptunnel-$Version-$Runtime-setup.exe")
    }
}
Get-ChildItem -LiteralPath $packages -File | Where-Object { $_.Extension -in '.zip', '.msi', '.exe' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $packages 'SHA256SUMS') -Encoding utf8
Write-Output "Packages: $packages"
