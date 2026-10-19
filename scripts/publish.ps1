# Builds a self-contained, single-file OpenMessages.exe in publish\win-x64 (and win-arm64).
# Usage: scripts\publish.ps1 [-Config path\to\firebase-config.json]
param([string]$Config = "firebase-config.json")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

foreach ($rid in "win-x64", "win-arm64") {
    dotnet publish src/OpenMessages.Windows -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish/$rid"
    if (Test-Path $Config) { Copy-Item $Config "publish/$rid/firebase-config.json" }
}
Write-Host "Published to publish\"
