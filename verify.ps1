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

    Write-Host '-- build Aitm.Server' -ForegroundColor Cyan
    $out = & "$PSScriptRoot/build-server.ps1" 2>&1
    $buildExit = $LASTEXITCODE
    $out | Select-Object -Last 1
    if ($buildExit -ne 0) { $failed += 'Aitm.Server build' }
}

Write-Host '-- code style (dotnet format style --verify-no-changes)' -ForegroundColor Cyan
# The style rules in the repo-root .editorconfig are errors in the build already; this also fails on a file
# `dotnet format` would rewrite. Fix a failure by running the same command without --verify-no-changes.
$out = & dotnet format style "$PSScriptRoot/Aitm.sln" --severity info --diagnostics IDE0005 IDE0008 IDE0028 IDE0090 IDE0300 IDE0301 IDE0305 IDE0370 --verify-no-changes 2>&1
$styleExit = $LASTEXITCODE
$out | Select-Object -Last 5
if ($styleExit -ne 0) { $failed += 'code style (dotnet format style)' }

Write-Host '-- Rider inspections (inspect.ps1)' -ForegroundColor Cyan
# Rider's own inspections, run headless from the pinned JetBrains tool against Aitm.sln.DotSettings.
$out = & "$PSScriptRoot/inspect.ps1" 2>&1
$inspectExit = $LASTEXITCODE
$out | Select-Object -Last 15
if ($inspectExit -ne 0) { $failed += 'Rider inspections (inspect.ps1)' }

Write-Host '-- dotnet test Aitm.sln' -ForegroundColor Cyan
# From slice 2 on (RESTRUCTURE.md section 4, "Exit check for every slice"): the new solution's own
# test projects, run alongside the Node suites below until phase 2 finishes moving every feature.
$out = & dotnet test "$PSScriptRoot/Aitm.sln" 2>&1
$dotnetTestExit = $LASTEXITCODE
$out | Select-String 'Passed!|Failed!' | Select-Object -Last 30
if ($dotnetTestExit -ne 0) { $failed += 'dotnet test Aitm.sln' }

Write-Host '-- hook registration' -ForegroundColor Cyan
$out = & node "$PSScriptRoot/hook-doctor.mjs" --project "$projectRoot" 2>&1
$hookExit = $LASTEXITCODE
$out | Select-Object -Last 1
if ($hookExit -ne 0) { $failed += 'hook registration' }

# `aitm selftest` is gone (RESTRUCTURE.md slice 24 bullet 1): its 63 checks now live in the C# test
# projects, verified by Aitm.Layout.Tests.SelfTestCoverageTests, which `dotnet test Aitm.sln` above
# already ran. `aitm selftest` itself now just prints "removed in 0.4" and exits 2 (cli-exit.test.mjs
# below covers that exit-code contract, the same way it covers every other dropped verb).

# Every *.test.mjs in the repo root, found by pattern rather than typed into a list — a file dropped
# here (or forgotten) is run, not silently skipped.
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
