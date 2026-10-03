#!/usr/bin/env bash
# NoMercyLabs/skills and this repository each publish a marketplace named nomercylabs, and both must
# list the same plugins at the same versions. Whichever one a user adds, the install list is the same.
#
#   .github/scripts/check-marketplace-sync.sh [path-to-other-marketplace.json]
set -euo pipefail

other="${1:-}"
if [ -z "$other" ]; then
  other=$(mktemp)
  trap 'rm -f "$other"' EXIT
  curl -fsSL https://raw.githubusercontent.com/NoMercyLabs/skills/main/.claude-plugin/marketplace.json -o "$other"
fi

python3 - .claude-plugin/marketplace.json "$other" <<'PY'
import json, sys

def plugins(path):
    with open(path, encoding="utf-8") as f:
        return {p["name"]: p.get("version") for p in json.load(f)["plugins"]}

mine, theirs = plugins(sys.argv[1]), plugins(sys.argv[2])
errors = []
for name in sorted(mine.keys() - theirs.keys()):
    errors.append(f"{name} is listed here but missing in NoMercyLabs/skills")
for name in sorted(theirs.keys() - mine.keys()):
    errors.append(f"{name} is listed in NoMercyLabs/skills but missing here")
for name in sorted(mine.keys() & theirs.keys()):
    if mine[name] != theirs[name]:
        errors.append(f"{name} is {mine[name]} here and {theirs[name]} in NoMercyLabs/skills")

for e in errors:
    print(f"::error::{e}")
if errors:
    sys.exit(1)
print(f"marketplace lists match: {', '.join(sorted(mine))}")
PY
