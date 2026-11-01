@echo off
rem Wrapper: passes all arguments to ROLLBACK-REXRUNTIME-INPUTFIX.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ROLLBACK-REXRUNTIME-INPUTFIX.ps1" %*
exit /b %errorlevel%
