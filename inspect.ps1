# Rider's own inspections, run headless. Rule (2026-09-27): every warning Rider shows must fail the build.
# The severities live in the checked-in Grimoira.sln.DotSettings; a rule set to ERROR there fails this script.
# Needs a restored solution (run `dotnet restore Grimoira.sln` first); costs about 1.5 minutes and 2.8 GB of nuget cache.
param([string]$Output = (Join-Path ([System.IO.Path]::GetTempPath()) 'grimoira-inspect.sarif'))

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
& dotnet tool run jb inspectcode Grimoira.sln --output=$Output --format=Sarif --no-build | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'jb inspectcode failed' }

$results = (Get-Content $Output -Raw | ConvertFrom-Json).runs[0].results | Where-Object { $_.level -eq 'error' }
foreach ($r in $results) {
    $loc = $r.locations[0].physicalLocation
    Write-Host ("{0}:{1}: {2}: {3}" -f $loc.artifactLocation.uri, $loc.region.startLine, $r.ruleId, $r.message.text)
}
Write-Host ("inspectcode: {0} finding(s) at severity error" -f @($results).Count)
if (@($results).Count -gt 0) { exit 1 }
