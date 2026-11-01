@echo off
rem Standalone check of the InputSystem race, no full source tree needed.
rem Compiles the ORIGINAL and the PATCHED input_system.cpp with stub headers
rem (tools\inputfix_harness\stub) and the real input headers, then runs the
rem concurrency test 5 times each.
rem Usage: RUN-INPUTFIX-HARNESS.cmd <rexglue-sdk dir with ORIGINAL include\ and src\input\>
rem Expected: ORIGINAL -> exit 0xC0000374 / crash, PATCHED -> all PASS.
setlocal EnableDelayedExpansion
if "%~1"=="" (
  echo Usage: %~nx0 ^<original rexglue-sdk dir^> 1>&2
  exit /b 2
)
set "ORIG=%~f1"
set "PAY=%~dp0.."
set "H=%~dp0inputfix_harness"
set "OUT=%TEMP%\inputfix_harness_out"
where clang++ >nul 2>nul || set "PATH=C:\Program Files\LLVM\bin;%PATH%"
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%\orig_inc\rex\input" "%OUT%\fix_inc\rex\input"
copy /y "%ORIG%\include\rex\input\input_system.h" "%OUT%\orig_inc\rex\input\" >nul || exit /b 1
copy /y "%PAY%\thirdparty\rexglue-sdk\include\rex\input\input_system.h" "%OUT%\fix_inc\rex\input\" >nul || exit /b 1
set "COMMON=-std=c++20 -O2 -I"%ORIG%\include" -I"%H%\stub" "%ORIG%\src\input\device_assignment.cpp" "%ORIG%\src\input\state_merge.cpp" "%PAY%\thirdparty\rexglue-sdk\tests\unit\input\input_assignment_test.cpp" "%H%\main.cpp""
clang++ -I"%OUT%\orig_inc" "%ORIG%\src\input\input_system.cpp" %COMMON% -o "%OUT%\harness_orig.exe" || exit /b 1
clang++ -I"%OUT%\fix_inc" "%PAY%\thirdparty\rexglue-sdk\src\input\input_system.cpp" %COMMON% -o "%OUT%\harness_fix.exe" || exit /b 1
echo === PATCHED: all tests
"%OUT%\harness_fix.exe" || (echo PATCHED FAILED & exit /b 1)
for %%v in (orig fix) do (
  for /l %%i in (1,1,5) do (
    "%OUT%\harness_%%v.exe" Concurrent >nul 2>nul
    cmd /c exit !errorlevel!
    echo %%v run %%i: exit=0x!=ExitCode!
  )
)
exit /b 0
