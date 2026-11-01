@echo off
rem Wrapper: passes all arguments to INSTALL-REXRUNTIME-INPUTFIX.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0INSTALL-REXRUNTIME-INPUTFIX.ps1" %*
exit /b %errorlevel%
