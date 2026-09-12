param(
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')][string]$Runtime,
    [string]$Version = '2.0.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$package = Join-Path $root "artifacts/packages/tcptunnel-$Version-$Runtime.zip"
$directory = Join-Path $root "artifacts/smoke/$Runtime/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $directory -Force | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($package, $directory)
$name = if ($IsWindows) { 'tcptunnel.exe' } else { 'tcptunnel' }
$binary = Join-Path $directory $name
if (!$IsWindows) { [System.IO.File]::SetUnixFileMode($binary, [System.IO.UnixFileMode]493) }
$actual = & $binary --version
if ($LASTEXITCODE -or $actual.Trim() -ne $Version) { throw "Unexpected package version: $actual" }
& $binary --config (Join-Path $directory 'tunnel.example.json') --check
if ($LASTEXITCODE) { throw 'Packaged configuration smoke test failed.' }
$generatedKey = & $binary --generate-key
if ($LASTEXITCODE -or $generatedKey.Trim().Length -ne 44 -or [Convert]::FromBase64String($generatedKey.Trim()).Length -ne 32) { throw 'Packaged key generation failed.' }
& $binary --mode Client --encryption:algorithm AesGcm --encryption:key $generatedKey.Trim() --check
if ($LASTEXITCODE) { throw 'Packaged encryption configuration failed.' }
$distributionFiles = @('LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.md', 'install-kind.txt')
if ($Runtime.StartsWith('win-')) { $distributionFiles += 'Tedd.TcpTunnel.ControlPanel.exe' }
foreach ($file in $distributionFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $directory $file))) { throw "Missing distribution file: $file" }
}
$originalHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
$stage = Join-Path $directory ".updates/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$staged = Join-Path $stage $name
$helper = Join-Path $stage $(if ($IsWindows) { 'updater.exe' } else { 'updater' })
Copy-Item -LiteralPath $binary -Destination $staged
Copy-Item -LiteralPath $binary -Destination $helper
if (!$IsWindows) { [System.IO.File]::SetUnixFileMode($helper, [System.IO.UnixFileMode]493) }
$plan = Join-Path $stage 'plan.json'
@{ ParentId = [int]::MaxValue; ParentStartTicks = 0; Target = $binary; Staged = $staged; Sha256 = $originalHash } |
    ConvertTo-Json | Set-Content -LiteralPath $plan
& $helper --apply-update $plan
if ($LASTEXITCODE) { throw "Packaged updater failed: $(Get-Content -LiteralPath (Join-Path $stage 'result.txt'))" }
foreach ($path in @($binary, "$binary.previous")) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $originalHash) { throw 'Replacement or retained backup differs.' }
}
if (Test-Path -LiteralPath $staged) { throw 'Staged executable was not moved into place.' }
& $binary --version
if ($LASTEXITCODE) { throw 'Updated executable failed to start.' }
Write-Output "Package and atomic update smoke tests passed: $Runtime"
