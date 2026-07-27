# Rebuild everything and prove it still works. This is the actual repeated task: building one half
# and skipping the parse check or the ranking tests is how a broken hook or a silent scoring
# regression ships, since neither fails loudly at runtime.
#
#   ./build.ps1            build both binaries, parse hooks, run tests
#   ./build.ps1 -Quick     skip the tests
param([switch]$Quick)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host 'building CLI...' -ForegroundColor Cyan
dotnet build "$root/aitm.cs" -c Release -o "$root/bin-cli" | Select-String -Pattern 'error|warning NU19|-> '

Write-Host 'building MCP server...' -ForegroundColor Cyan
# A live session keeps bin/mcp.dll open, and the copy then fails with MSB3027 after ten retries.
# The client respawns the server on demand, so stopping it here is cheaper than a confusing build error.
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'aitm.bin.mcp\.dll' } |
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
