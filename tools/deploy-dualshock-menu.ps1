# v17: patch the native MenuHighlight texture entry, not GameFace UVs.
# Shared C# 5 helper also used by the .NET 8 launcher. No launcher rebuild required.
param(
    [string]$GameRoot = 'D:\Fable2\Fable 2 1.5.0',
    [string]$Asset = '', # compatibility with older build wrappers; deliberately unused
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
try {
    $root = [System.IO.Path]::GetFullPath($GameRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Game directory not found: $root"
    }
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $sources = @(
        (Join-Path $projectRoot 'launcher\Fable2.Launcher\DualShockMenuBadgeArtwork.cs'),
        (Join-Path $projectRoot 'launcher\Fable2.Launcher\DualShockMenuBadgePatcher.cs')
    )
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Missing v17 patch source: $source. Extract the complete ZIP into the project folder."
        }
    }
    Add-Type -Path $sources -ErrorAction Stop
    $enabled = -not [bool]$Restore
    Write-Host "Actual target: Art\GUI\Frames\frames_elements.tex"
    Write-Host "Game folder: $root"
    $result = [Fable2Launcher.DualShockMenuBadgePatcher]::Apply($root, $enabled, $true)
    Write-Host $result -ForegroundColor Green
    Write-Host 'Native texture and header verified. HOLD/ABYX, other PS icons and game scripts were not changed.'
    Write-Host "Log: $(Join-Path $root 'launcher-dualshock-menu-v17.log')"
    exit 0
}
catch {
    Write-Host ('ERROR: MenuHighlight v17: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'Close the game before installation. Unknown texture revisions are not overwritten.'
    Write-Host 'Keep any .rollback files if the error mentions rollback failure.'
    exit 1
}
