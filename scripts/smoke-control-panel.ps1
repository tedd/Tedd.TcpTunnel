param([string]$Dotnet = 'dotnet', [string]$Output = 'artifacts/panel-smoke')
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'The MAUI UI smoke test requires Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($Output, $root)
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$manifest = Join-Path $destination 'app.manifest'
[IO.File]::WriteAllText($manifest, [IO.File]::ReadAllText((Join-Path $root 'src/Tedd.TcpTunnel.ControlPanel/Platforms/Windows/app.manifest')).Replace('requireAdministrator', 'asInvoker'))
& $Dotnet publish (Join-Path $root 'src/Tedd.TcpTunnel.ControlPanel') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:ControlPanelSmoke=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o (Join-Path $destination 'app') "-p:ApplicationManifest=$manifest" '-p:NuGetLockFilePath=packages.win-x64.lock.json' '-p:RestoreLockedMode=true'
if ($LASTEXITCODE) { throw 'UI smoke build failed.' }
$binary = Join-Path $destination 'app/Tedd.TcpTunnel.ControlPanel.exe'
$result = Join-Path $destination 'result.txt'
if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
$processInfo = [Diagnostics.ProcessStartInfo]::new($binary)
$processInfo.UseShellExecute = $false
$processInfo.CreateNoWindow = $true
$processInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$processInfo.ArgumentList.Add('--smoke-test')
$processInfo.ArgumentList.Add($destination)
$process = [Diagnostics.Process]::Start($processInfo)
try {
    if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'UI smoke test timed out.' }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result)) { throw "Control panel failed with exit code $($process.ExitCode)." }
    $text = Get-Content -LiteralPath $result -Raw
    if (!$text.StartsWith('PASS:')) { throw $text }
    if (!(Test-Path -LiteralPath (Join-Path $destination 'control-panel.png'))) { throw 'Screenshot was not captured.' }
    Write-Output $text
} finally { $process.Dispose() }
