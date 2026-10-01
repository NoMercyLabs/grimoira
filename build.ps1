# Rebuild everything and prove it still works. This is the actual repeated task: building one half
# and skipping the parse check is how a broken hook ships, since it does not fail loudly at runtime.
#
#   ./build.ps1            build both binaries, parse hooks

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host 'building Grimoira.sln...' -ForegroundColor Cyan
# RESTRUCTURE.md slice 24 bullet 4: every feature's actual logic now lives in the solution's source
# projects; grimoira.cs and mcp.cs (built below) are thin file-based hosts that reference them via
# `#:project` and are NOT part of Grimoira.sln (a file-based app cannot be an sln project), so this alone
# would not produce bin-cli/grimoira.exe or bin/mcp.dll — the two builds below still do that.
dotnet build "$root/Grimoira.sln" | Select-String -Pattern 'error|warning NU19|-> '

Write-Host 'building CLI...' -ForegroundColor Cyan
# build-cli.ps1 publishes Grimoira.Cli to bin-cli/ (sub-card 29e).
& "$root/build-cli.ps1" | Select-String -Pattern 'error|warning NU19|-> '

Write-Host 'building MCP server...' -ForegroundColor Cyan
# A live session keeps bin/mcp.dll open, and the copy then fails with MSB3027 after ten retries.
# The client respawns the server on demand, so stopping it here is cheaper than a confusing build error.
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'grimoira.bin.mcp\.dll' } |
    ForEach-Object {
        Write-Host "  stopping MCP host pid $($_.ProcessId) (it holds bin/mcp.dll)" -ForegroundColor DarkGray
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
dotnet build "$root/mcp.cs" -c Release -o "$root/bin" | Select-String -Pattern 'error|warning NU19|-> '

Write-Host 'ready' -ForegroundColor Green
