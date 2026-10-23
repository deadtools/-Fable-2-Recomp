# Deploy only the finished launcher executable. Never touch game data or saves.
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$temporaryFile = $null
try {
    $sourceFull = [System.IO.Path]::GetFullPath($Source)
    $destinationFull = [System.IO.Path]::GetFullPath($Destination)
    $destinationDirectory = [System.IO.Path]::GetDirectoryName($destinationFull)

    if (-not [System.IO.File]::Exists($sourceFull)) {
        throw "Published launcher does not exist: $sourceFull"
    }
    if (-not [System.IO.Directory]::Exists($destinationDirectory)) {
        throw "Game directory does not exist: $destinationDirectory"
    }
    if ([string]::Equals($sourceFull, $destinationFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source and destination are the same file.'
    }

    # Stop ONLY the exact installed launcher. Do not kill other executables
    # also named Fable2Launcher.exe in unrelated directories.
    $running = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'Fable2Launcher.exe'" |
        Where-Object {
            $_.ExecutablePath -and
            [string]::Equals(
                [System.IO.Path]::GetFullPath($_.ExecutablePath),
                $destinationFull,
                [System.StringComparison]::OrdinalIgnoreCase)
        })
    foreach ($process in $running) {
        Write-Host "Stopping installed launcher PID $($process.ProcessId)..."
        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
    }
    foreach ($process in $running) {
        $pidToWait = [int]$process.ProcessId
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ((Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) -and
               [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        if (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {
            throw "Launcher PID $pidToWait did not exit. Existing executable was not replaced."
        }
    }

    # Stage a complete, hash-verified EXE before replacing the installed one.
    $temporaryFile = Join-Path $destinationDirectory ('.Fable2Launcher-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [System.IO.File]::Copy($sourceFull, $temporaryFile, $false)
    $builtHash = (Get-FileHash -LiteralPath $sourceFull -Algorithm SHA256).Hash
    $stagedHash = (Get-FileHash -LiteralPath $temporaryFile -Algorithm SHA256).Hash
    if ($builtHash -ne $stagedHash) {
        throw 'Staged launcher failed SHA256 verification. Existing executable was not replaced.'
    }

    if ([System.IO.File]::Exists($destinationFull)) {
        $backupPath = $destinationFull + '.previous.bak'
        [System.IO.File]::Replace($temporaryFile, $destinationFull, $backupPath, $true)
    }
    else {
        [System.IO.File]::Move($temporaryFile, $destinationFull)
    }
    $temporaryFile = $null

    $installedHash = (Get-FileHash -LiteralPath $destinationFull -Algorithm SHA256).Hash
    if ($installedHash -ne $builtHash) {
        throw 'Installed launcher SHA256 check failed. Restore Fable2Launcher.exe.previous.bak.'
    }
    Write-Host "Launcher updated: $destinationFull"
    exit 0
}
catch {
    Write-Error ("Launcher deployment FAILED: " + $_.Exception.Message)
    exit 1
}
finally {
    if ($temporaryFile -and [System.IO.File]::Exists($temporaryFile)) {
        Remove-Item -LiteralPath $temporaryFile -Force -ErrorAction SilentlyContinue
    }
}
