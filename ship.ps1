# verify -> commit -> push -> watch CI -> record what was learned, as one step.
#
# pattern-watch flagged this exact sequence at three repetitions, and it was right: run by hand it
# drifts. The step that goes missing is never the commit, it is the CI watch or the fact — the two that
# only pay off later, which is exactly why they are the ones worth making non-optional.
#
#   ./ship.ps1 -Message "fix(x): thing"
#   ./ship.ps1 -MessageFile msg.txt -Term "what I learned" -Fact "the durable version"
#   ./ship.ps1 -Message "..." -SkipVerify        only when verify just ran green
param(
    [string]$Message,
    [string]$MessageFile,
    [string]$Term,
    [string]$Fact,
    [string]$Category = 'rule',
    [string]$Instance = 'nomercy',
    [switch]$SkipVerify,
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Message -and -not $MessageFile) { Write-Error 'need -Message or -MessageFile'; exit 1 }
if ($Term -and -not $Fact) { Write-Error '-Term needs -Fact'; exit 1 }

# HARD rule: know the branch before committing. Reported, never assumed.
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
Write-Host "branch: $branch" -ForegroundColor Cyan

if (-not (git status --porcelain)) { Write-Host 'nothing to commit.' -ForegroundColor Yellow; exit 0 }

if (-not $SkipVerify) {
    & "$PSScriptRoot/verify.ps1"
    if ($LASTEXITCODE -ne 0) { Write-Host 'verify RED — not committing.' -ForegroundColor Red; exit 1 }
}

# A message file written inside the repo gets swept up by `git add -A` and committed alongside the
# change it describes. That happened on this script's own first commit.
if ($MessageFile) {
    $msgFull = (Resolve-Path $MessageFile).Path
    if ($msgFull.StartsWith($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $staged = Join-Path ([IO.Path]::GetTempPath()) "ship-msg-$PID.txt"
        Copy-Item $msgFull $staged -Force
        Remove-Item $msgFull -Force
        $MessageFile = $staged
    }
}

git add -A
if ($MessageFile) { git commit -q -F $MessageFile } else { git commit -q -m $Message }
if ($LASTEXITCODE -ne 0) { Write-Error 'commit failed'; exit 1 }
$sha = (git rev-parse --short HEAD).Trim()
Write-Host "committed $sha" -ForegroundColor Green

# The store write happens BEFORE the push wait, so a red CI or a dropped connection cannot lose it.
if ($Fact) {
    & "$PSScriptRoot/bin-cli/aitm.exe" add --instance $Instance --term $Term --value $Fact `
        --category $Category --provenance stated --source $sha | Select-Object -Last 1
}

if ($NoPush) { Write-Host 'not pushing (-NoPush).' -ForegroundColor Yellow; exit 0 }

git push -q origin $branch
if ($LASTEXITCODE -ne 0) { Write-Error 'push failed'; exit 1 }
Write-Host "pushed to $branch" -ForegroundColor Green

# LOCAL green is not CI green. Watching is the whole point of the last step, and it is the step that
# gets skipped when this is typed out by hand.
# Select the run BY SHA, and wait for it to appear. Taking `gh run list --limit 1` right after a push
# grabs the PREVIOUS commit's run, because GitHub has not registered the new one yet — the first use of
# this script watched an already-green run, then read a conclusion from a different run that had just
# started, got null, and reported red on a commit whose CI was fine.
$runId = $null
for ($i = 0; $i -lt 20; $i++) {
    $runId = (gh run list --limit 10 --json databaseId,headSha --jq "[.[] | select(.headSha | startswith(`"$sha`"))][0].databaseId" 2>$null)
    if ($runId) { break }
    Start-Sleep -Seconds 3
}
if (-not $runId) { Write-Host "no CI run appeared for $sha." -ForegroundColor Yellow; exit 0 }

Write-Host "watching CI run $runId…" -ForegroundColor Cyan
gh run watch $runId --exit-status --compact *> $null
# Read the conclusion of THAT run, never whatever is newest by the time the watch returns.
$conclusion = (gh run view $runId --json conclusion --jq '.conclusion' 2>$null)

if ($conclusion -eq 'success') {
    Write-Host "CI green on $sha" -ForegroundColor Green
    exit 0
}
Write-Host "CI $conclusion on $sha — fix it, red CI is not someone else's problem" -ForegroundColor Red
gh run view $runId --log-failed 2>$null | Select-Object -First 40
exit 1
