#!/usr/bin/env bash
# Bash twin of ship.ps1. verify -> commit -> push -> watch CI -> record what was learned, as one step.
#
#   ./ship.sh --message "fix(x): thing"
#   ./ship.sh --message-file msg.txt --term "what I learned" --fact "the durable version"
#   ./ship.sh --message "..." --skip-verify        only when verify just ran green
#
# Flags: --message --message-file --term --fact --category (default rule) --instance (default nomercy) --skip-verify --no-push
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

message=""; message_file=""; term=""; fact=""; category="rule"; instance="nomercy"; skip_verify=0; no_push=0
need() { [ "$1" -ge 2 ] || { echo "$2 needs a value" >&2; exit 2; }; }
while [ $# -gt 0 ]; do
  case "$1" in
    --message) need $# "$1"; message="$2"; shift 2 ;;
    --message-file) need $# "$1"; message_file="$2"; shift 2 ;;
    --term) need $# "$1"; term="$2"; shift 2 ;;
    --fact) need $# "$1"; fact="$2"; shift 2 ;;
    --category) need $# "$1"; category="$2"; shift 2 ;;
    --instance) need $# "$1"; instance="$2"; shift 2 ;;
    --skip-verify) skip_verify=1; shift ;;
    --no-push) no_push=1; shift ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

say() { printf '\033[%sm%s\033[0m\n' "$1" "$2"; }

if [ -z "$message" ] && [ -z "$message_file" ]; then echo 'need --message or --message-file' >&2; exit 1; fi
if [ -n "$term" ] && [ -z "$fact" ]; then echo '--term needs --fact' >&2; exit 1; fi

# HARD rule: know the branch before committing. Reported, never assumed.
branch="$(git rev-parse --abbrev-ref HEAD)"
say 36 "branch: $branch"

if [ -z "$(git status --porcelain)" ]; then say 33 'nothing to commit.'; exit 0; fi

if [ "$skip_verify" -eq 0 ]; then
  if ! "$ROOT/verify.sh"; then say 31 'verify RED - not committing.'; exit 1; fi
fi

# A message file written inside the repo gets swept up by `git add -A` and committed with the change.
# Move it to the temp folder first.
tmp_dir="${TMPDIR:-/tmp}"; tmp_dir="${tmp_dir%/}"
if [ -n "$message_file" ]; then
  msg_dir="$(cd "$(dirname "$message_file")" && pwd)"
  msg_full="$msg_dir/$(basename "$message_file")"
  case "$msg_full" in
    "$ROOT"/*)
      staged="$tmp_dir/ship-msg-$$.txt"
      cp "$msg_full" "$staged"
      rm -f "$msg_full"
      message_file="$staged"
      ;;
  esac
fi

git add -A
if [ -n "$message_file" ]; then
  git commit -q -F "$message_file" || { echo 'commit failed' >&2; exit 1; }
else
  git commit -q -m "$message" || { echo 'commit failed' >&2; exit 1; }
fi
sha="$(git rev-parse --short HEAD)"
say 32 "committed $sha"

# Clean up the message file here rather than leaving it to the caller.
case "$message_file" in
  "$tmp_dir"/*) rm -f "$message_file" ;;
esac

# The store write happens BEFORE the push wait, so a red CI or a dropped connection cannot lose it.
if [ -n "$fact" ]; then
  if [ -x "$ROOT/bin-cli/grimoira.exe" ]; then cli=("$ROOT/bin-cli/grimoira.exe")
  elif [ -x "$ROOT/bin-cli/grimoira" ]; then cli=("$ROOT/bin-cli/grimoira")
  else cli=(dotnet "$ROOT/bin-cli/grimoira.dll"); fi
  "${cli[@]}" add --instance "$instance" --term "$term" --value "$fact" \
    --category "$category" --provenance stated --source "$sha" | tail -n 1
fi

if [ "$no_push" -eq 1 ]; then say 33 'not pushing (--no-push).'; exit 0; fi

git push -q origin "$branch" || { echo 'push failed' >&2; exit 1; }
say 32 "pushed to $branch"

# LOCAL green is not CI green. Select the run BY SHA and wait for it to appear: `gh run list --limit 1`
# right after a push grabs the PREVIOUS commit's run.
run_id=""
for _ in $(seq 1 20); do
  run_id="$(gh run list --limit 10 --json databaseId,headSha --jq "[.[] | select(.headSha | startswith(\"$sha\"))][0].databaseId" 2>/dev/null || true)"
  if [ -n "$run_id" ] && [ "$run_id" != "null" ]; then break; fi
  run_id=""
  sleep 3
done
if [ -z "$run_id" ]; then say 33 "no CI run appeared for $sha."; exit 0; fi

say 36 "watching CI run $run_id..."
gh run watch "$run_id" --exit-status --compact >/dev/null 2>&1 || true
# Read the conclusion of THAT run, never whatever is newest by the time the watch returns.
conclusion="$(gh run view "$run_id" --json conclusion --jq '.conclusion' 2>/dev/null || true)"

if [ "$conclusion" = "success" ]; then say 32 "CI green on $sha"; exit 0; fi
say 31 "CI $conclusion on $sha - fix it, red CI is not someone else's problem"
gh run view "$run_id" --log-failed 2>/dev/null | head -n 40 || true
exit 1
