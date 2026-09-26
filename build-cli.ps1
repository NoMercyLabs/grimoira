# Build the AITM CLI. RESTRUCTURE.md sub-card 29e: bin-cli/ is the published Aitm.Cli (assembly `aitm`), the
# thin client of the server's POST /cli; `hook`, `service` and `server uninstall-logon`
# run locally. Hooks, .mcp.json, brain-sweep and the scripts run bin-cli/aitm(.exe|.dll).
#
# The last aitm.cs build is kept beside it as bin-cli-old/ until slice 32 signs off: it is the rollback
# (copy bin-cli-old/ over bin-cli/) and the oracle the parity tests compare against. aitm.cs is a file-based
# app (RESTRUCTURE.md section 0 rule 3), so `dotnet build Aitm.sln` never produces it.
#
# If a hook or a live session holds bin-cli/aitm.dll the copy will lock, so let any running call finish first.
dotnet build "$PSScriptRoot/aitm.cs" -c Release -o "$PSScriptRoot/bin-cli-old"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Cleared first: dotnet publish never removes a file it no longer produces, so an old aitm.cs DLL
# from before bin-cli/ became the thin client's own output would otherwise sit beside it forever.
if (Test-Path "$PSScriptRoot/bin-cli") { Remove-Item "$PSScriptRoot/bin-cli" -Recurse -Force }

# PublishAot=false: the same choice as build-server.ps1; AOT is its own decision, not this one.
dotnet publish "$PSScriptRoot/src/Aitm.Cli/Aitm.Cli.csproj" -c Release -o "$PSScriptRoot/bin-cli" -p:PublishAot=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
