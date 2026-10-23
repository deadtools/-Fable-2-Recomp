# Fable II Game Speed v18.7: install already built native files with verified backups.
# Designed for Windows PowerShell 5.1 and later. The game is NEVER force-closed.
param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [string]$GameDirectory = 'D:\Fable2\Fable 2 1.5.0'
)

$ErrorActionPreference = 'Stop'
$stage = $null
$backup = $null
$game = $null
$currentCommit = $null
$committed = New-Object 'System.Collections.Generic.List[string]'
$existed = @{}
$originalHashes = @{}

function Assert-GameClosed([string]$GameExe) {
    $candidates = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'fable_2.exe'")
    foreach ($process in $candidates) {
        if (-not $process.ExecutablePath) {
            throw 'A running fable_2.exe has no readable path. Close the game before installing.'
        }
        if ([string]::Equals([IO.Path]::GetFullPath($process.ExecutablePath),
                $GameExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Game is running (PID $($process.ProcessId)). Save and close it first."
        }
    }
}

try {
    $source = [IO.Path]::GetFullPath($SourceDirectory)
    $game = [IO.Path]::GetFullPath($GameDirectory)
    if (-not [IO.Directory]::Exists($source)) { throw "Build output directory not found: $source" }
    if (-not [IO.Directory]::Exists($game)) { throw "Game directory not found: $game" }
    if ([string]::Equals($source.TrimEnd('\'), $game.TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build and installed game directories must differ.'
    }

    foreach ($name in @('fable_2.exe', 'rexruntime.dll', 'rexgpu-xenos.dll', 'fable2_build.json')) {
        $required = [IO.Path]::GetFullPath((Join-Path $source $name))
        if (-not [IO.File]::Exists($required)) { throw "Missing native build output: $required" }
    }

    # Fail closed if the source executable does not include this feature.
    $gameExeSource = [IO.Path]::GetFullPath((Join-Path $source 'fable_2.exe'))
    $bytes = [IO.File]::ReadAllBytes($gameExeSource)
    if (-not [Text.Encoding]::ASCII.GetString($bytes).Contains('FABLE2_GAME_SPEED_V18_GUEST_CLOCK')) {
        throw 'Built fable_2.exe does not contain the game-speed v18 marker. No installation done.'
    }
    $bytes = $null
    $targetGameExe = [IO.Path]::GetFullPath((Join-Path $game 'fable_2.exe'))
    Assert-GameClosed $targetGameExe

    $files = @('fable_2.exe', 'fable2_build.json') + @(
        Get-ChildItem -LiteralPath $source -Filter '*.dll' -File |
            ForEach-Object { $_.Name }
    )
    $files = @($files | Sort-Object -Unique)
    $id = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
    $stage = [IO.Path]::GetFullPath((Join-Path $game ('.speed-v18-stage-' + $id)))
    $backup = [IO.Path]::GetFullPath((Join-Path $game ('backup-speed-v18-' + $id)))
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    [IO.Directory]::CreateDirectory($backup) | Out-Null

    $hashes = @{}
    foreach ($name in $files) {
        $from = [IO.Path]::GetFullPath((Join-Path $source $name))
        $staged = [IO.Path]::GetFullPath((Join-Path $stage $name))
        $target = [IO.Path]::GetFullPath((Join-Path $game $name))
        $backupFile = [IO.Path]::GetFullPath((Join-Path $backup $name))

        [IO.File]::Copy($from, $staged, $false)
        $hashes[$name] = (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $hashes[$name]) {
            throw "Staging SHA256 mismatch: $name"
        }
        $existed[$name] = [IO.File]::Exists($target)
        if ($existed[$name]) {
            [IO.File]::Copy($target, $backupFile, $false)
            $originalHashes[$name] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ((Get-FileHash -LiteralPath $backupFile -Algorithm SHA256).Hash -ne $originalHashes[$name]) {
                throw "Backup SHA256 mismatch: $name"
            }
        }
    }

    # Recheck immediately before touching native runtime files.
    Assert-GameClosed $targetGameExe
    foreach ($name in $files) {
        $currentCommit = $name
        $target = [IO.Path]::GetFullPath((Join-Path $game $name))
        $staged = [IO.Path]::GetFullPath((Join-Path $stage $name))
        Write-Host "Installing: $name"
        if ($existed[$name]) {
            # v18.6 incorrectly passed $null as the third Replace argument.
            # Supply a real absolute path for the atomic replacement backup.
            # The independent SHA-verified original also remains at $backup\$name.
            $atomicBackup = [IO.Path]::GetFullPath((Join-Path $backup ($name + '.atomic-backup')))
            if ([IO.File]::Exists($atomicBackup)) {
                throw "Unexpected backup file already exists: $atomicBackup"
            }
            try {
                [IO.File]::Replace($staged, $target, $atomicBackup, $true)
            }
            catch {
                throw "Atomic replacement failed for '$name'. Source='$staged', target='$target', backup='$atomicBackup'. $($_.Exception.Message)"
            }
        }
        else {
            [IO.File]::Move($staged, $target)
        }
        $committed.Add($name)
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hashes[$name]) {
            throw "Installed SHA256 mismatch: $name"
        }
        $currentCommit = $null
    }

    @{ version=18; installer='v18.7'; files=$files; sha256=$hashes; previous_existed=$existed } |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding UTF8

    # The SHA-verified pre-install copy remains; duplicate atomic backups can be removed.
    foreach ($name in $files) {
        $atomicBackup = Join-Path $backup ($name + '.atomic-backup')
        if ([IO.File]::Exists($atomicBackup)) {
            Remove-Item -LiteralPath $atomicBackup -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host 'Game-speed runtime installed and hashes verified.' -ForegroundColor Green
    Write-Host 'Original runtime backup: ' $backup
    Write-Host 'Game data, saves and settings were not modified.'
    exit 0
}
catch {
    $failure = $_.Exception.Message
    $rollbackFailed = $false
    $rollbackNames = New-Object 'System.Collections.Generic.List[string]'
    # Also check an in-progress File.Replace call, which may have modified the target
    # before reporting an error.
    if ($currentCommit) { $rollbackNames.Add($currentCommit) }
    for ($i = $committed.Count - 1; $i -ge 0; --$i) {
        if (-not $rollbackNames.Contains($committed[$i])) {
            $rollbackNames.Add($committed[$i])
        }
    }
    foreach ($name in $rollbackNames) {
        try {
            $target = [IO.Path]::GetFullPath((Join-Path $game $name))
            if ($existed[$name]) {
                $original = [IO.Path]::GetFullPath((Join-Path $backup $name))
                if (-not [IO.File]::Exists($original)) {
                    throw "Original backup file is missing: $original"
                }
                [IO.File]::Copy($original, $target, $true)
                if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $originalHashes[$name]) {
                    throw "Rollback SHA256 mismatch: $name"
                }
            }
            elseif ([IO.File]::Exists($target)) {
                Remove-Item -LiteralPath $target -Force -ErrorAction Stop
            }
            Write-Host "Rolled back: $name"
        }
        catch {
            $rollbackFailed = $true
            Write-Warning "Rollback failed for $name. See original backup folder: $backup. $($_.Exception.Message)"
        }
    }
    Write-Host "Game-speed installation FAILED: $failure" -ForegroundColor Red
    if ($backup -and [IO.Directory]::Exists($backup)) {
        Write-Host "Backup folder: $backup"
    }
    if ($rollbackFailed) {
        Write-Host 'WARNING: One or more rollback operations failed. Restore from the backup before playing.' -ForegroundColor Red
    }
    exit 1
}
finally {
    if ($stage -and [IO.Directory]::Exists($stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
