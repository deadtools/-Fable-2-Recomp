@echo off
setlocal
rem Bypass is for this PowerShell process only; no system policy is changed.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fable2-Diagnostic.ps1" -Mode Readback
set "RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %RESULT%
