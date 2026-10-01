# Publish Grimoira.Server to bin-server/, beside bin-cli/ (build-cli.ps1) and bin/ (build-mcp.ps1).
# RESTRUCTURE.md sub-card 29e: `SessionStartServerCheck.DefaultServerPath` looks for
# `<plugin>/bin-server/Grimoira.Server(.exe)`, the sibling of the CLI's bin-cli/. Until this script runs,
# that path does not exist and the SessionStart check starts nothing (it fails open, so a session is
# never blocked; see SessionStartServerCheck.cs).
#
# Cleared first: dotnet publish never removes a file it no longer produces, so a stale earlier
# build's output would otherwise sit here forever (the same reasoning as build-cli.ps1's bin-cli/).
if (Test-Path "$PSScriptRoot/bin-server") { Remove-Item "$PSScriptRoot/bin-server" -Recurse -Force }

# Mirrors build-cli.ps1's bin-cli publish line: PublishAot=false, because the tool registry and
# Sqlite are not AOT-checked, and AOT is its own decision, not this one.
dotnet publish "$PSScriptRoot/src/Grimoira.Server/Grimoira.Server.csproj" -c Release -o "$PSScriptRoot/bin-server" -p:PublishAot=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
