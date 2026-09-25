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

# Every *.test.mjs in the repo root, found by pattern rather than typed into a list — a file dropped
# here (or forgotten, as brain-gates.test.mjs and launch-mcp.test.mjs were) is run, not silently skipped.
# A file that imports node:test needs the `node --test` runner; every other file is a plain script that
# prints its own pass/fail line and exits non-zero on failure. Both groups are run below, and the total
# discovered must equal the total actually run, or the gate stops instead of quietly covering less.
$allTestFiles = Get-ChildItem "$PSScriptRoot/*.test.mjs" | Sort-Object Name
$nodeTestFiles = @($allTestFiles | Where-Object { Select-String -Path $_.FullName -Pattern "from 'node:test'" -Quiet })
$plainFiles = @($allTestFiles | Where-Object { $_.FullName -notin $nodeTestFiles.FullName })

foreach ($suite in $plainFiles) {
    Write-Host "-- $($suite.Name)" -ForegroundColor Cyan
    $out = & node "$($suite.FullName)" 2>&1
    $testExit = $LASTEXITCODE
    $line = ($out | Select-String 'passed|agreed' | Select-Object -Last 1)
    Write-Host "   $line"
    if ($testExit -ne 0 -or -not $line -or "$line" -match '[1-9]\d* (failed|diverged)') { $failed += $suite.Name }
}

Write-Host '-- ownership, edit-hook, registration, and workspace tool tests' -ForegroundColor Cyan
$out = & node --test --test-concurrency=1 @($nodeTestFiles | ForEach-Object { $_.FullName }) 2>&1
$testExit = $LASTEXITCODE
$line = $out | Select-String '^# pass ' | Select-Object -Last 1
Write-Host "   $line"
if ($testExit -ne 0 -or -not $line) { $failed += 'ownership, edit-hook, registration, and workspace tool tests' }

$ranCount = $plainFiles.Count + $nodeTestFiles.Count
Write-Host "-- test file coverage: $ranCount of $($allTestFiles.Count) *.test.mjs files run" -ForegroundColor Cyan
if ($ranCount -ne $allTestFiles.Count) { $failed += "test file coverage ($ranCount of $($allTestFiles.Count))" }

if ($failed.Count -gt 0) {
    Write-Host "`nRED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`nGREEN" -ForegroundColor Green
