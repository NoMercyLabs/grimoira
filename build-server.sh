#!/usr/bin/env bash
# Bash twin of build-server.ps1. Publish Grimora.Server to bin-server/, beside bin-cli/ (build-cli.sh) and bin/ (build-mcp.sh).
# SessionStartServerCheck.DefaultServerPath looks for <plugin>/bin-server/Grimora.Server(.exe).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Cleared first: dotnet publish never removes a file it no longer produces.
if [ -e "$ROOT/bin-server" ]; then rm -rf "$ROOT/bin-server"; fi

# PublishAot=false: the tool registry and Sqlite are not AOT-checked, and AOT is its own decision.
dotnet publish "$ROOT/src/Grimora.Server/Grimora.Server.csproj" -c Release -o "$ROOT/bin-server" -p:PublishAot=false
