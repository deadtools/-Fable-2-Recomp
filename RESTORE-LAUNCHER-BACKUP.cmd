@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
echo Restoring the previous working launcher (if a .previous.bak exists)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\restore-launcher-backup.ps1" -GameDirectory "D:\Fable2\Fable 2 1.5.0"
if errorlevel 1 (
    echo Restore failed. Your existing game files were not modified.
) else (
    echo Previous Fable2Launcher.exe restored successfully.
)
echo.
echo Нажмите любую кнопку чтобы завершить процесс
pause >nul
