@echo off
rem Loads a rexruntime.dll OUTSIDE the game and calls InputSystem
rem GetState/GetCapabilities/SetState/GetKeystroke from 4 threads (tool mode,
rem NOP driver). Valid only for build 1D8CBB87...5AEA and its patched form.
rem Expected: original -> crash (0xC0000374/0xC0000005/0xC0000409/0xC000001D),
rem           patched  -> "OK" 5 of 5.
rem Usage: VERIFY-DLL-INPUTRACE.cmd <rexruntime.dll> [deps dir, default D:\Fable2\Game]
setlocal EnableDelayedExpansion
if "%~1"=="" (
  echo Usage: %~nx0 ^<rexruntime.dll^> [deps dir] 1>&2
  exit /b 2
)
set "DLL=%~f1"
set "DEPS=%~2"
if "%DEPS%"=="" set "DEPS=D:\Fable2\Game"
set "OUT=%TEMP%\inputrace_dlltest"
where clang++ >nul 2>nul || set "PATH=C:\Program Files\LLVM\bin;%PATH%"
if not exist "%OUT%" mkdir "%OUT%"
clang++ -std=c++20 -O2 "%~dp0inputfix_harness\dlltest.cpp" -o "%OUT%\dlltest.exe" || exit /b 1
"%OUT%\dlltest.exe" "%DLL%" "%DEPS%" 0 single || exit /b 1
for /l %%i in (1,1,5) do (
  "%OUT%\dlltest.exe" "%DLL%" "%DEPS%" 200000 >nul 2>nul
  cmd /c exit !errorlevel!
  echo run %%i: exit=0x!=ExitCode!
)
exit /b 0
