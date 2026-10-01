#!/usr/bin/env bash
# An install only updates when the plugin version changes: `claude plugin update` compares versions,
# not commits. 2026-09-30: 31 master commits, the compaction fix among them, never reached an install
# because the version stayed 1.0.0. So a change to a shipped file must bump the version, and the
# three places that carry it must agree.
#
#   .github/scripts/check-plugin-version.sh <base-ref>
set -euo pipefail

base="${1:?usage: check-plugin-version.sh <base-ref>}"

version_of() {
  sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' | head -n 1
}

new=$(version_of < .claude-plugin/plugin.json)
market=$(version_of < .claude-plugin/marketplace.json)
old=$(git show "$base:.claude-plugin/plugin.json" | version_of)

if [ "$new" != "$market" ]; then
  echo "::error::plugin.json says $new, marketplace.json says $market"
  exit 1
fi

if ! grep -q "Version = \"$new\"" src/Grimoira.Cli/Tools/McpBridge.cs; then
  echo "::error::src/Grimoira.Cli/Tools/McpBridge.cs does not report version $new"
  exit 1
fi

shipped=$(git diff --name-only "$base" HEAD | grep -vE '^(docs/|tests/|\.github/|README\.md$|LICENSE$|NOTICE$)' || true)

if [ -n "$shipped" ] && [ "$old" = "$new" ]; then
  echo "::error::shipped files changed and the plugin version is still $new, so no install gets this change. Bump .claude-plugin/plugin.json, .claude-plugin/marketplace.json and McpBridge.cs. Changed:"
  echo "$shipped"
  exit 1
fi

echo "plugin version $old -> $new; shipped files changed: $(printf '%s' "$shipped" | grep -c . || true)"
