@echo off
rem Wrapper: passes all arguments to APPLY-INPUTFIX-SOURCE.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0APPLY-INPUTFIX-SOURCE.ps1" %*
exit /b %errorlevel%
