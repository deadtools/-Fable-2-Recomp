@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "KEY=HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\fable_2.exe"
set "MARKER=%~dp0FABLE2-WER-CREATED-BY-THIS-TOOL.txt"
echo Disable WER dumps created by this helper.
echo.
fltmc >nul 2>&1
if errorlevel 1 goto need_admin
if not exist "%MARKER%" goto missing_marker
reg query "%KEY%" /reg:64 >nul 2>&1
if errorlevel 1 goto already_removed
echo The key below will be removed. Dumps already captured will NOT be deleted.
reg query "%KEY%" /reg:64
echo.
choice /C YN /M "Remove this per-app WER key"
if errorlevel 2 goto done
if not errorlevel 1 goto done
:remove_key
reg delete "%KEY%" /reg:64 /f || goto failed
del /q "%MARKER%" >nul 2>&1
echo SUCCESS. WER settings for fable_2.exe removed.
goto done
:already_removed
del /q "%MARKER%" >nul 2>&1
echo Settings were already removed. Marker removed.
goto done
:missing_marker
echo The marker is missing. For safety this tool will not modify registry keys it did not create.
goto done
:need_admin
echo ERROR: Run this CMD as administrator.
goto done
:failed
echo ERROR: Registry deletion failed. The marker was preserved.
:done
echo.
pause
endlocal
