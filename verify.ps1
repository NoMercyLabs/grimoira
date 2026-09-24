# Everything that has to be green before a commit, in the order that fails fastest.
# Written down because it had been re-typed by hand often enough that pattern-watch flagged it, and a
# procedure re-derived each time is a procedure with a step missing.
param([switch]$SkipBuild, [string]$Project)

$ErrorActionPreference = 'Stop'
$projectRoot = if ($Project) { $Project } elseif ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
Set-Location $PSScriptRoot
$failed = @()

if (-not $SkipBuild) {
    Write-Host '-- build CLI' -ForegroundColor Cyan
    $out = & "$PSScriptRoot/build-cli.ps1" 2>&1
    $buildExit = $LASTEXITCODE
    $out | Select-Object -Last 1
    if ($buildExit -ne 0) { $failed += 'CLI build' }

    Write-Host '-- build tool server' -ForegroundColor Cyan
    $out = & "$PSScriptRoot/build-mcp.ps1" 2>&1
    $buildExit = $LASTEXITCODE
    $out | Select-Object -Last 1
    if ($buildExit -ne 0) { $failed += 'tool server build' }
}

Write-Host '-- hook registration' -ForegroundColor Cyan
$out = & node "$PSScriptRoot/hook-doctor.mjs" --project "$projectRoot" 2>&1
$hookExit = $LASTEXITCODE
$out | Select-Object -Last 1
if ($hookExit -ne 0) { $failed += 'hook registration' }

Write-Host '-- selftest' -ForegroundColor Cyan
$out = & "$PSScriptRoot/bin-cli/aitm.exe" selftest --instance test 2>&1
$selftestExit = $LASTEXITCODE
$line = $out | Select-String 'selftest:' | Select-Object -Last 1
Write-Host "   $line"
if ($selftestExit -ne 0 -or "$line" -notmatch 'GREEN') { $failed += 'selftest' }

foreach ($suite in @('brain-lib.test.mjs', 'ranking-agreement.test.mjs', 'deferral-shape.test.mjs',
                     'continue-guard.test.mjs', 'pattern-watch.test.mjs', 'proof-guard.test.mjs',
                     'synthesis-capture.test.mjs', 'mcp-stage.test.mjs', 'mcp-graph.test.mjs', 'blast-radius.test.mjs',
                     'population-guard.test.mjs', 'idp-impersonate.test.mjs')) {
    Write-Host "-- $suite" -ForegroundColor Cyan
    $out = & node "$PSScriptRoot/$suite" 2>&1
    $testExit = $LASTEXITCODE
    $line = ($out | Select-String 'passed|agreed' | Select-Object -Last 1)
    Write-Host "   $line"
    if ($testExit -ne 0 -or -not $line -or "$line" -match '[1-9]\d* (failed|diverged)') { $failed += $suite }
}

Write-Host '-- ownership, edit-hook, registration, and workspace tool tests' -ForegroundColor Cyan
$out = & node --test --test-concurrency=1 "$PSScriptRoot/process-owner.test.mjs" "$PSScriptRoot/index-on-edit.test.mjs" "$PSScriptRoot/hook-doctor.test.mjs" "$PSScriptRoot/workspace-tools.test.mjs" 2>&1
$testExit = $LASTEXITCODE
$line = $out | Select-String '^# pass ' | Select-Object -Last 1
Write-Host "   $line"
if ($testExit -ne 0 -or -not $line) { $failed += 'ownership, edit-hook, registration, and workspace tool tests' }

if ($failed.Count -gt 0) {
    Write-Host "`nRED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`nGREEN" -ForegroundColor Green
