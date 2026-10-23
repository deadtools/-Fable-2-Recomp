param([string]$GameDirectory = 'D:\Fable2\Fable 2 1.5.0')
$ErrorActionPreference = 'Stop'
$target = Join-Path $GameDirectory 'Fable2Launcher.exe'
$backup = $target + '.previous.bak'
if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
    throw ('A previous launcher backup was not found: ' + $backup)
}
$fullTarget = [System.IO.Path]::GetFullPath($target)
$running = @(Get-CimInstance Win32_Process -Filter "Name='Fable2Launcher.exe'" -ErrorAction Stop |
    Where-Object { $_.ExecutablePath -and
        ([System.IO.Path]::GetFullPath($_.ExecutablePath) -ieq $fullTarget) })
foreach ($p in $running) {
    Write-Host ('Stopping installed launcher PID ' + $p.ProcessId)
    Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
}
foreach ($p in $running) {
    for ($i = 0; $i -lt 50; $i++) {
        if (-not (Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 100
    }
    if (Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue) {
        throw ('Launcher PID ' + $p.ProcessId + ' did not exit')
    }
}
Copy-Item -LiteralPath $backup -Destination $target -Force
Write-Host ('Restored previous launcher: ' + $target) -ForegroundColor Green
Write-Host 'No game, DLL or configuration files were changed.'
