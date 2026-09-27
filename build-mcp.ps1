# Rebuild the Grimora MCP server to bin/ so Claude Code launches it as a prebuilt DLL — fast, reliable
# registration (~0.6s) instead of `dotnet run mcp.cs`, which cold-compiles + contends on a locked output
# binary at session start ("server slow to connect / still haven't registered"). Run after editing mcp.cs.
# Sessions launch the server via run-mcp.mjs, which runs from a per-session SHADOW COPY of bin/, so a
# running session no longer locks bin/mcp.dll and this rebuild works any time. (Sessions started before
# that launcher change still hold the old dll directly — restart them to pick up a rebuild.)
# RESTRUCTURE.md slice 24 bullet 4: not a `dotnet build Grimora.sln` wrapper, for the same reason
# build-cli.ps1 isn't — mcp.cs is a file-based host, not an sln project, so only this build produces
# bin/mcp.dll (the callers in section 5: the NoMercy Claude/Codex MCP registration, `.mcp.json`'s `node`
# entry via run-mcp.mjs). Called today only by build.ps1 and verify.ps1.
dotnet build "$PSScriptRoot/mcp.cs" -c Release -o "$PSScriptRoot/bin"
# Stamp the build with the source hash, so run-mcp.mjs knows this bin/ matches mcp.cs.
if ($LASTEXITCODE -eq 0) {
    Push-Location $PSScriptRoot
    node --input-type=module -e "import { writeStamp } from './build-stamp.mjs'; writeStamp('bin', 'mcp.cs')"
    Pop-Location
}
