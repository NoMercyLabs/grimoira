#!/usr/bin/env bash
# Bash twin of build.ps1. Rebuild everything and prove it still works.
#
#   ./build.sh            build both binaries, parse hooks
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FILTER='error|warning NU19|-> '

# grep exits 1 on no match; that is not a failure here, the build's own exit code is.
show() { grep -E "$FILTER" || true; }

printf '\033[36mbuilding Grimora.sln...\033[0m\n'
dotnet build "$ROOT/Grimora.sln" | show

printf '\033[36mbuilding CLI...\033[0m\n'
"$ROOT/build-cli.sh" | show

printf '\033[36mbuilding MCP server...\033[0m\n'
# build.ps1 stops any dotnet host that holds bin/mcp.dll. This twin does not: it never kills a process
# it did not start. If a live session holds bin/mcp.dll the build below fails with MSB3027; end that
# session (the client respawns the server on demand) and run this script again.
dotnet build "$ROOT/mcp.cs" -c Release -o "$ROOT/bin" | show

printf '\033[32mready\033[0m\n'
