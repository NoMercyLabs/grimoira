# Publish Aitm.Server to bin-server/, beside bin-cli/ (build-cli.ps1) and bin/ (build-mcp.ps1).
# RESTRUCTURE.md sub-card 29e: `SessionStartServerCheck.DefaultServerPath` looks for
# `<plugin>/bin-server/Aitm.Server(.exe)`, the sibling of the CLI's bin-cli/. Until this script runs,
# that path does not exist and the SessionStart check starts nothing (it fails open, so a session is
# never blocked; see SessionStartServerCheck.cs).
#
# Mirrors build-cli.ps1's bin-cli-next publish line: PublishAot=false, because the tool registry and
# Sqlite are not AOT-checked, and AOT is its own decision, not this one.
dotnet publish "$PSScriptRoot/src/Aitm.Server/Aitm.Server.csproj" -c Release -o "$PSScriptRoot/bin-server" -p:PublishAot=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
