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

    Write-Host '-- build Grimora.Server' -ForegroundColor Cyan
    $out = & "$PSScriptRoot/build-server.ps1" 2>&1
    $buildExit = $LASTEXITCODE
    $out | Select-Object -Last 1
    if ($buildExit -ne 0) { $failed += 'Grimora.Server build' }
}

Write-Host '-- code style (dotnet format style --verify-no-changes)' -ForegroundColor Cyan
# The style rules in the repo-root .editorconfig are errors in the build already; this also fails on a file
# `dotnet format` would rewrite. Fix a failure by running the same command without --verify-no-changes.
$out = & dotnet format style "$PSScriptRoot/Grimora.sln" --severity info --diagnostics IDE0005 IDE0008 IDE0028 IDE0090 IDE0300 IDE0301 IDE0305 IDE0370 --verify-no-changes 2>&1
$styleExit = $LASTEXITCODE
$out | Select-Object -Last 5
if ($styleExit -ne 0) { $failed += 'code style (dotnet format style)' }

Write-Host '-- Rider inspections (inspect.ps1)' -ForegroundColor Cyan
# Rider's own inspections, run headless from the pinned JetBrains tool against Grimora.sln.DotSettings.
$out = & "$PSScriptRoot/inspect.ps1" 2>&1
$inspectExit = $LASTEXITCODE
$out | Select-Object -Last 15
if ($inspectExit -ne 0) { $failed += 'Rider inspections (inspect.ps1)' }

Write-Host '-- dotnet test Grimora.sln' -ForegroundColor Cyan
# From slice 2 on (RESTRUCTURE.md section 4, "Exit check for every slice"): the new solution's own
# test projects, run alongside the Node suites below until phase 2 finishes moving every feature.
$out = & dotnet test "$PSScriptRoot/Grimora.sln" 2>&1
$dotnetTestExit = $LASTEXITCODE
$out | Select-String 'Passed!|Failed!' | Select-Object -Last 30
if ($dotnetTestExit -ne 0) { $failed += 'dotnet test Grimora.sln' }

Write-Host '-- hook registration' -ForegroundColor Cyan
$out = & dotnet "$PSScriptRoot/bin-cli/grimora.dll" hooks-doctor --project "$projectRoot" 2>&1
$hookExit = $LASTEXITCODE
$out | Select-Object -Last 1
if ($hookExit -ne 0) { $failed += 'hook registration' }

# `grimora selftest` is gone (RESTRUCTURE.md slice 24 bullet 1): its 63 checks now live in the C# test
# projects, verified by Grimora.Layout.Tests.SelfTestCoverageTests, which `dotnet test Grimora.sln` above
# already ran. `grimora selftest` itself now just prints "removed in 0.4" and exits 2 (the cli-exit test
# below covers that exit-code contract, the same way it covers every other dropped verb).

if ($failed.Count -gt 0) {
    Write-Host "`nRED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`nGREEN" -ForegroundColor Green
