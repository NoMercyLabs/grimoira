#!/usr/bin/env bash
# Bash twin of verify.ps1. Everything that has to be green before a commit, in the order that fails fastest.
#
#   ./verify.sh [--skip-build] [--project DIR]
set -euo pipefail
START_DIR="$PWD"
cd "$(dirname "${BASH_SOURCE[0]}")"
ROOT="$(pwd)"

skip_build=0
project="${CLAUDE_PROJECT_DIR:-$START_DIR}"
while [ $# -gt 0 ]; do
  case "$1" in
    --skip-build) skip_build=1; shift ;;
    --project) [ $# -ge 2 ] || { echo '--project needs a value' >&2; exit 2; }; project="$2"; shift 2 ;;
    --project=*) project="${1#--project=}"; shift ;;
    -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
# --project wins, then CLAUDE_PROJECT_DIR, then the directory the script was started from.

failed=()
step() { printf '\033[36m%s\033[0m\n' "$1"; }

# run <label> <tail-lines> <command...>: print the last lines of the output, record the label on a non-zero exit.
run() {
  local label="$1" lines="$2" out rc
  shift 2
  if out=$("$@" 2>&1); then rc=0; else rc=$?; fi
  printf '%s\n' "$out" | tail -n "$lines"
  if [ "$rc" -ne 0 ]; then failed+=("$label"); fi
}

if [ "$skip_build" -eq 0 ]; then
  step '-- build CLI'
  run 'CLI build' 1 "$ROOT/build-cli.sh"
  step '-- build tool server'
  run 'tool server build' 1 "$ROOT/build-mcp.sh"
  step '-- build Grimoira.Server'
  run 'Grimoira.Server build' 1 "$ROOT/build-server.sh"
fi

step '-- code style (dotnet format style --verify-no-changes)'
run 'code style (dotnet format style)' 5 dotnet format style "$ROOT/Grimoira.sln" --severity info --diagnostics IDE0005 IDE0008 IDE0028 IDE0090 IDE0300 IDE0301 IDE0305 IDE0370 --verify-no-changes

step '-- Rider inspections (inspect.sh)'
run 'Rider inspections (inspect.sh)' 15 "$ROOT/inspect.sh"

step '-- dotnet test Grimoira.sln'
if out=$(dotnet test "$ROOT/Grimoira.sln" 2>&1); then rc=0; else rc=$?; fi
printf '%s\n' "$out" | { grep -E 'Passed!|Failed!' || true; } | tail -n 30
if [ "$rc" -ne 0 ]; then failed+=('dotnet test Grimoira.sln'); fi

step '-- hook registration'
run 'hook registration' 1 dotnet "$ROOT/bin-cli/grimoira.dll" hooks-doctor --project "$project"

if [ "${#failed[@]}" -gt 0 ]; then
  list=""
  for f in "${failed[@]}"; do list="${list:+$list, }$f"; done
  printf '\n\033[31mRED: %s\033[0m\n' "$list"
  exit 1
fi
printf '\n\033[32mGREEN\033[0m\n'
