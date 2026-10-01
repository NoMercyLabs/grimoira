#!/usr/bin/env bash
# Bash twin of inspect.ps1. Rider's own inspections, run headless. Every warning Rider shows must fail the build.
# The severities live in the checked-in Grimoira.sln.DotSettings; a rule set to ERROR there fails this script.
# Needs a restored solution (run `dotnet restore Grimoira.sln` first).
#
#   ./inspect.sh [--output FILE]     SARIF output file, default: $TMPDIR/grimoira-inspect.sarif
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

output="${TMPDIR:-/tmp}/grimoira-inspect.sarif"
while [ $# -gt 0 ]; do
  case "$1" in
    --output) [ $# -ge 2 ] || { echo '--output needs a value' >&2; exit 2; }; output="$2"; shift 2 ;;
    --output=*) output="${1#--output=}"; shift ;;
    -h|--help) sed -n '2,7p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

dotnet tool restore >/dev/null || { echo 'dotnet tool restore failed' >&2; exit 1; }
dotnet tool run jb inspectcode Grimoira.sln --output="$output" --format=Sarif --no-build >/dev/null || { echo 'jb inspectcode failed' >&2; exit 1; }

# The SARIF is read with dotnet-free tools only: python3/python is on every runner and on macOS.
py=python3; command -v python3 >/dev/null 2>&1 || py=python
count=$("$py" - "$output" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8-sig"))
errors = [r for r in data["runs"][0].get("results", []) if r.get("level") == "error"]
for r in errors:
    loc = r["locations"][0]["physicalLocation"]
    print("%s:%s: %s: %s" % (loc["artifactLocation"]["uri"], loc["region"]["startLine"], r["ruleId"], r["message"]["text"]), file=sys.stderr)
print(len(errors))
PY
)
echo "inspectcode: $count finding(s) at severity error"
if [ "$count" -gt 0 ]; then exit 1; fi
