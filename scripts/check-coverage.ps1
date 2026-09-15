param([string]$Directory = 'artifacts/coverage', [double]$MinimumLine = 97, [double]$MinimumCoreLine = 98, [double]$MinimumBranch = 83)
$ErrorActionPreference = 'Stop'
$files = Get-ChildItem -LiteralPath $Directory -Filter '*.xml' -Recurse
if (!$files) { throw "No Cobertura reports in $Directory" }
# Merge Windows/Linux hit counts by source file and line. No production source exclusions.
$lines = @{}
$core = @{}
$branches = @{}
foreach ($file in $files) {
    [xml]$report = Get-Content -LiteralPath $file.FullName
    foreach ($package in $report.coverage.packages.package) {
        foreach ($class in $package.classes.class) {
            $name = $class.filename.Replace('\', '/')
            foreach ($line in $class.lines.line) {
                $key = "$name`:$($line.number)"
                $hit = [int]$line.hits -gt 0
                $lines[$key] = $lines[$key] -or $hit
                if ($package.name -eq 'Tedd.TcpTunnel') { $core[$key] = $core[$key] -or $hit }
                if ($line.'condition-coverage' -match '\((\d+)/(\d+)\)') {
                    # Best observed branch coverage per source line; no optimistic cross-OS guessing.
                    $covered = [int]$Matches[1]; $total = [int]$Matches[2]
                    if (!$branches.ContainsKey($key) -or $covered -gt $branches[$key].Covered) { $branches[$key] = @{ Covered = $covered; Total = $total } }
                }
            }
        }
    }
}
function Rate($values) { if (!$values.Count) { throw 'Coverage report has no production lines.' }; return 100.0 * @($values.Values | Where-Object { $_ }).Count / $values.Count }
$lineRate = Rate $lines
$coreRate = Rate $core
$coveredBranches = ($branches.Values | Measure-Object -Property Covered -Sum).Sum
$totalBranches = ($branches.Values | Measure-Object -Property Total -Sum).Sum
$branchRate = if ($totalBranches) { 100.0 * $coveredBranches / $totalBranches } else { 100 }
'Line: {0:N2}% ({1} lines); core: {2:N2}%; branch: {3:N2}%' -f $lineRate, $lines.Count, $coreRate, $branchRate
if ($lineRate -lt $MinimumLine -or $coreRate -lt $MinimumCoreLine -or $branchRate -lt $MinimumBranch) {
    throw "Coverage is below thresholds: line $MinimumLine%, core $MinimumCoreLine%, branch $MinimumBranch%."
}
