# Installs ChatComplete into a LocalExile checkout for development:
#   <repo>/LocalExile/Mods/Binaries/chatcomplete/ (gitignored; the Gateway hot-reloads it).
# The release zip has the same layout: the mod folder holds the script, its settings
# and bin/NativeSuggest.dll.
param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$target = Join-Path $Repo "LocalExile\Mods\Binaries\chatcomplete"
$dll = Join-Path $PSScriptRoot "native\build\Release\NativeSuggest.dll"
$bin = Join-Path $target "bin"

New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $PSScriptRoot "mods\chatcomplete\*") $target -Force
if (-not (Test-Path $dll)) {
    Write-Warning "The DLL is not built yet: cmake --build native/build --config Release --target nativesuggest"
    Write-Host "Installed $target (without the DLL)"
    return
}
try {
    Copy-Item $dll $bin -Force
    Write-Host "Installed $target (with NativeSuggest.dll)"
} catch {
    Write-Warning "Could not replace bin\NativeSuggest.dll (the client is probably running): $($_.Exception.Message)"
    Write-Host "Installed $target (kept the existing DLL)"
}
