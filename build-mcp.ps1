# Rebuild the AITM MCP server to bin/ so Claude Code launches it as a prebuilt DLL — fast, reliable
# registration (~0.6s) instead of `dotnet run mcp.cs`, which cold-compiles + contends on a locked output
# binary at session start ("server slow to connect / still haven't registered"). Run after editing mcp.cs.
# Sessions launch the server via launch-mcp.mjs, which runs from a per-session SHADOW COPY of bin/, so a
# running session no longer locks bin/mcp.dll and this rebuild works any time. (Sessions started before
# that launcher change still hold the old dll directly — restart them to pick up a rebuild.)
dotnet build "$PSScriptRoot/mcp.cs" -c Release -o "$PSScriptRoot/bin"
