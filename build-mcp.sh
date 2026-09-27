#!/usr/bin/env bash
# Bash twin of build-mcp.ps1. Rebuild the Grimora MCP server to bin/ so Claude Code launches it as a prebuilt DLL.
# Run after editing mcp.cs. Called today only by build.sh and verify.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet build "$ROOT/mcp.cs" -c Release -o "$ROOT/bin"
