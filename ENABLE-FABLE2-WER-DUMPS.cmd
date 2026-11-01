@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "GAME=%~1"
if not defined GAME for %%D in ("%~dp0..\Game") do set "GAME=%%~fD"
set "KEY=HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\fable_2.exe"
set "DUMP_DIR=%GAME%\diagnostics\NativeCrashDumps"
set "MARKER=%~dp0FABLE2-WER-CREATED-BY-THIS-TOOL.txt"

echo Fable II crash dump capture (Windows WER HKLM)
echo Game: %GAME%
echo (pass the Game folder as the first argument to override the default)
echo Dump folder: %DUMP_DIR%
echo.
fltmc >nul 2>&1
if errorlevel 1 goto need_admin
if not exist "%GAME%\fable_2.exe" goto missing_game
if exist "%MARKER%" (
  reg query "%KEY%" /reg:64 >nul 2>&1
  if not errorlevel 1 goto already_ours
  del /q "%MARKER%" >nul 2>&1
)
reg query "%KEY%" /reg:64 >nul 2>&1
if not errorlevel 1 goto existing_key
if not exist "%DUMP_DIR%" (
  mkdir "%DUMP_DIR%"
  if errorlevel 1 goto fail_before
)
reg add "%KEY%" /reg:64 /v DumpFolder /t REG_EXPAND_SZ /d "%DUMP_DIR%" /f >nul || goto rollback
reg add "%KEY%" /reg:64 /v DumpType /t REG_DWORD /d 1 /f >nul || goto rollback
reg add "%KEY%" /reg:64 /v DumpCount /t REG_DWORD /d 3 /f >nul || goto rollback
>"%MARKER%" echo This tool created HKLM LocalDumps\fable_2.exe. Use DISABLE-FABLE2-WER-DUMPS.cmd to remove it.
if not exist "%MARKER%" goto marker_fail
echo.
echo SUCCESS. Now launch Fable II and reproduce the crash.
echo Check this folder for a .dmp file:
echo %DUMP_DIR%
echo The existing launcher ZIP may NOT include this external dump automatically.
reg query "%KEY%" /reg:64
goto done
:already_ours
echo Already set up by this tool. Current registry values:
reg query "%KEY%" /reg:64
goto done
:existing_key
echo ATTENTION: a WER per-app key already exists. It was NOT changed.
echo Please review the values instead of overwriting an existing debugger configuration.
reg query "%KEY%" /reg:64
goto done
:need_admin
echo ERROR: Run this CMD as administrator (right click, Run as administrator).
goto done
:missing_game
echo ERROR: Game EXE not found: %GAME%\fable_2.exe
goto done
:marker_fail
echo ERROR: Configured HKLM but could not create restore marker. Removing our newly created key.
:rollback
reg delete "%KEY%" /reg:64 /f >nul 2>&1
echo Setup failed. No pre-existing HKLM key was overwritten.
goto done
:fail_before
echo ERROR: Could not create dump directory.
:done
echo.
pause
endlocal
