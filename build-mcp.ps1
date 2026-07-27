# Rebuild the AITM MCP server to bin/ so Claude Code launches it as a prebuilt DLL — fast, reliable
# registration (~0.6s) instead of `dotnet run mcp.cs`, which cold-compiles + contends on a locked output
# binary at session start ("server slow to connect / still haven't registered"). Run after editing mcp.cs.
# If a live Claude session is holding bin/mcp.dll, stop it / restart Claude first or the copy will lock.
dotnet build "$PSScriptRoot/mcp.cs" -c Release -o "$PSScriptRoot/bin"
