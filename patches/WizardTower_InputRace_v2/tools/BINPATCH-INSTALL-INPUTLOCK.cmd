@echo off
rem Binary fix for the installed rexruntime.dll (no source tree needed).
rem 1) patches a COPY of <game>\rexruntime.dll (only build SHA256 1D8CBB87...5AEA)
rem 2) checks the result is the expected CBC1EECD...79DD
rem 3) installs it with INSTALL-REXRUNTIME-INPUTFIX (ABI gate, backup, SHA-256 log)
rem Usage: BINPATCH-INSTALL-INPUTLOCK.cmd [game dir, default D:\Fable2\Game]
rem Rollback: ROLLBACK-REXRUNTIME-INPUTFIX.cmd [-GameDir <game dir>]
setlocal
set "GAME=%~1"
if "%GAME%"=="" set "GAME=D:\Fable2\Game"
set "EXPECT=CBC1EECD67E7E2BE7370230E827D39A680C0DB927505E37EE977F18483D979DD"
set "OUT=%TEMP%\rexruntime_inputlock"
where python >nul 2>nul || (echo Error: python not on PATH. 1>&2 & exit /b 1)
if not exist "%OUT%" mkdir "%OUT%"
python -I "%~dp0binpatch_rexruntime_inputlock.py" "%GAME%\rexruntime.dll" "%OUT%\rexruntime.dll" || exit /b 1
powershell -NoProfile -Command "if ((Get-FileHash -Algorithm SHA256 -LiteralPath '%OUT%\rexruntime.dll').Hash -ne '%EXPECT%') { Write-Host 'Error: patched DLL hash differs from expected.'; exit 1 }" || exit /b 1
call "%~dp0INSTALL-REXRUNTIME-INPUTFIX.cmd" -NewDll "%OUT%\rexruntime.dll" -GameDir "%GAME%"
exit /b %errorlevel%
