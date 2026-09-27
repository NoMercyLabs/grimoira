# Rebuild everything and prove it still works. This is the actual repeated task: building one half
# and skipping the parse check or the ranking tests is how a broken hook or a silent scoring
# regression ships, since neither fails loudly at runtime.
#
#   ./build.ps1            build both binaries, parse hooks, run tests
#   ./build.ps1 -Quick     skip the tests
param([switch]$Quick)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host 'building Grimora.sln...' -ForegroundColor Cyan
# RESTRUCTURE.md slice 24 bullet 4: every feature's actual logic now lives in the solution's source
# projects; grimora.cs and mcp.cs (built below) are thin file-based hosts that reference them via
# `#:project` and are NOT part of Grimora.sln (a file-based app cannot be an sln project), so this alone
# would not produce bin-cli/grimora.exe or bin/mcp.dll — the two builds below still do that.
dotnet build "$root/Grimora.sln" | Select-String -Pattern 'error|warning NU19|-> '

Write-Host 'building CLI...' -ForegroundColor Cyan
# build-cli.ps1 publishes Grimora.Cli to bin-cli/ and keeps the grimora.cs build as bin-cli-old/ (sub-card 29e).
& "$root/build-cli.ps1" | Select-String -Pattern 'error|warning NU19|-> '

Write-Host 'building MCP server...' -ForegroundColor Cyan
# A live session keeps bin/mcp.dll open, and the copy then fails with MSB3027 after ten retries.
# The client respawns the server on demand, so stopping it here is cheaper than a confusing build error.
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'grimora.bin.mcp\.dll' } |
    ForEach-Object {
        Write-Host "  stopping MCP host pid $($_.ProcessId) (it holds bin/mcp.dll)" -ForegroundColor DarkGray
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
dotnet build "$root/mcp.cs" -c Release -o "$root/bin" | Select-String -Pattern 'error|warning NU19|-> '

Write-Host 'parsing hooks...' -ForegroundColor Cyan
$bad = 0
Get-ChildItem "$root/*.mjs" | ForEach-Object {
    node --check $_.FullName 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host "  FAIL $($_.Name)" -ForegroundColor Red; $bad++ }
}
if ($bad -gt 0) { throw "$bad hook(s) failed to parse" }
Write-Host "  all hooks parse" -ForegroundColor Green

if (-not $Quick) {
    Write-Host 'ranking tests...' -ForegroundColor Cyan
    node "$root/brain-lib.test.mjs"
    if ($LASTEXITCODE -ne 0) { throw 'ranking tests failed' }
}

Write-Host 'ready' -ForegroundColor Green
