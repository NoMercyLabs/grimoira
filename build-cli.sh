#!/usr/bin/env bash
# Bash twin of build-cli.ps1. Build the Grimora CLI.
# bin-cli/ is the published Grimora.Cli (assembly `grimora`), the thin client of the server's POST /cli.
# The last grimora.cs build is kept beside it as bin-cli-old/ (the rollback and the oracle the parity tests compare against).
# If a hook or a live session holds bin-cli/grimora.dll the copy will lock, so let any running call finish first.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet build "$ROOT/grimora.cs" -c Release -o "$ROOT/bin-cli-old"

# Cleared first: dotnet publish never removes a file it no longer produces.
if [ -e "$ROOT/bin-cli" ]; then rm -rf "$ROOT/bin-cli"; fi

# PublishAot=false: the same choice as build-server.sh; AOT is its own decision, not this one.
dotnet publish "$ROOT/src/Grimora.Cli/Grimora.Cli.csproj" -c Release -o "$ROOT/bin-cli" -p:PublishAot=false
