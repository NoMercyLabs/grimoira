#!/usr/bin/env bash
# Bash twin of build-cli.ps1. Build the Grimora CLI.
# bin-cli/ is the published Grimora.Cli (assembly `grimora`), the thin client of the server's POST /cli.
#
# The old parity-test oracle build retired once every oracle-comparison test class was frozen to a golden
# (RESTRUCTURE.md slice 29f/33): a class replays its golden instead of running a second binary, and the
# handful of tests that still need a live pinned-commit build (InitFullTests, CliFlagCoverageGuardTests)
# build it themselves from git history, not from here.
#
# If a hook or a live session holds bin-cli/grimora.dll the copy will lock, so let any running call finish first.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Cleared first: dotnet publish never removes a file it no longer produces.
if [ -e "$ROOT/bin-cli" ]; then rm -rf "$ROOT/bin-cli"; fi

# PublishAot=false: the same choice as build-server.sh; AOT is its own decision, not this one.
dotnet publish "$ROOT/src/Grimora.Cli/Grimora.Cli.csproj" -c Release -o "$ROOT/bin-cli" -p:PublishAot=false
