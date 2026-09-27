# Rebuild the Grimora MCP server to bin/ so Claude Code launches it as a prebuilt DLL — fast, reliable
# registration (~0.6s) instead of `dotnet run mcp.cs`, which cold-compiles + contends on a locked output
# binary at session start ("server slow to connect / still haven't registered"). Run after editing mcp.cs.
# RESTRUCTURE.md slice 24 bullet 4: not a `dotnet build Grimora.sln` wrapper, for the same reason
# build-cli.ps1 isn't — mcp.cs is a file-based host, not an sln project, so only this build produces
# bin/mcp.dll (the callers in section 5: the NoMercy Claude/Codex MCP registration). Called today only by build.ps1 and verify.ps1.
dotnet build "$PSScriptRoot/mcp.cs" -c Release -o "$PSScriptRoot/bin"
