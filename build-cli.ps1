# Rebuild the AITM CLI to bin-cli/ so the hooks invoke a prebuilt binary instead of cold-compiling
# aitm.cs on every call. Run after editing aitm.cs. If a hook or a live session is holding
# bin-cli/aitm.dll the copy will lock, so let any running index finish first.
#
# RESTRUCTURE.md slice 24 bullet 4: not a `dotnet build Aitm.sln` wrapper. aitm.cs is a file-based app
# (its logic dispatches into Aitm.sln's projects via `#:project`, RESTRUCTURE.md section 0 rule 3) and
# cannot itself be an sln project, so `dotnet build Aitm.sln` alone never produces bin-cli/aitm.exe —
# the callers this script's output serves (`.claude/skills/brain-sweep/sweep.sh`, section 5 caller 5;
# the plugin's own bin-cli/aitm.exe path) still need this exact build. Called today only by build.ps1
# and verify.ps1 (searched: no external caller under C:/Projects/NoMercy/.claude invokes this script by
# name — they only read the exe path it produces).
dotnet build "$PSScriptRoot/aitm.cs" -c Release -o "$PSScriptRoot/bin-cli"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# RESTRUCTURE.md slice 29 part 1: the published CLI, beside bin-cli/ and never over it (brain-sweep and
# the hooks still run bin-cli/aitm.exe). PublishAot=false: a file-based app publishes as Native AOT by
# default, and the tool registry and Sqlite are not AOT-checked; AOT is its own decision, not this one.
dotnet publish "$PSScriptRoot/aitm.cs" -c Release -o "$PSScriptRoot/bin-cli-next" -p:PublishAot=false
