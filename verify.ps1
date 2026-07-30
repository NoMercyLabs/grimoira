# Everything that has to be green before a commit, in the order that fails fastest.
# Written down because it had been re-typed by hand often enough that pattern-watch flagged it, and a
# procedure re-derived each time is a procedure with a step missing.
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$failed = @()

if (-not $SkipBuild) {
    Write-Host '-- build' -ForegroundColor Cyan
    & "$PSScriptRoot/build-cli.ps1" | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { $failed += 'build' }
}

Write-Host '-- selftest' -ForegroundColor Cyan
$out = & "$PSScriptRoot/bin-cli/aitm.exe" selftest --instance test 2>&1
$line = ($out | Select-String 'selftest:').ToString()
Write-Host "   $line"
if ($line -notmatch 'GREEN') { $failed += 'selftest' }

foreach ($suite in @('brain-lib.test.mjs', 'ranking-agreement.test.mjs', 'continue-guard.test.mjs',
                     'pattern-watch.test.mjs', 'proof-guard.test.mjs', 'synthesis-capture.test.mjs',
                     'mcp-stage.test.mjs')) {
    Write-Host "-- $suite" -ForegroundColor Cyan
    $out = & node "$PSScriptRoot/$suite" 2>&1
    $line = ($out | Select-String 'passed|agreed' | Select-Object -Last 1)
    Write-Host "   $line"
    if ("$line" -match '[1-9]\d* (failed|diverged)') { $failed += $suite }
}

if ($failed.Count -gt 0) {
    Write-Host "`nRED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`nGREEN" -ForegroundColor Green
