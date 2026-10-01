# Build the Grimoira CLI. RESTRUCTURE.md sub-card 29e: bin-cli/ is the published Grimoira.Cli (assembly `grimoira`), the
# thin client of the server's POST /cli; `hook`, `service` and `server uninstall-logon`
# run locally. Hooks, .mcp.json, brain-sweep and the scripts run bin-cli/grimoira(.exe|.dll).
#
# The old parity-test oracle build retired once every oracle-comparison test class was frozen to a golden
# (RESTRUCTURE.md slice 29f/33): a class replays its golden instead of running a second binary, and the
# handful of tests that still need a live pinned-commit build (InitFullTests, CliFlagCoverageGuardTests)
# build it themselves from git history, not from here.
#
# If a hook or a live session holds bin-cli/grimoira.dll the copy will lock, so let any running call finish first.
# Cleared first: dotnet publish never removes a file it no longer produces, so an old grimoira.cs DLL
# from before bin-cli/ became the thin client's own output would otherwise sit beside it forever.
if (Test-Path "$PSScriptRoot/bin-cli") { Remove-Item "$PSScriptRoot/bin-cli" -Recurse -Force }

# PublishAot=false: the same choice as build-server.ps1; AOT is its own decision, not this one.
dotnet publish "$PSScriptRoot/src/Grimoira.Cli/Grimoira.Cli.csproj" -c Release -o "$PSScriptRoot/bin-cli" -p:PublishAot=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
