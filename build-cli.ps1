# Rebuild the AITM CLI to bin-cli/ so the hooks invoke a prebuilt binary instead of cold-compiling
# aitm.cs on every call. Run after editing aitm.cs. If a hook or a live session is holding
# bin-cli/aitm.dll the copy will lock, so let any running index finish first.
dotnet build "$PSScriptRoot/aitm.cs" -c Release -o "$PSScriptRoot/bin-cli"
