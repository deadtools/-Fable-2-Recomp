@echo off
rem Called inside BUILD-GAME-SPEED-LOCAL.cmd's SETLOCAL scope.
rem Intentionally no SETLOCAL here: INCLUDE, LIB and PATH must reach the caller.
set "FABLE2_VSROOT="
if defined VSINSTALLDIR if exist "%VSINSTALLDIR%\Common7\Tools\VsDevCmd.bat" set "FABLE2_VSROOT=%VSINSTALLDIR%"
if defined FABLE2_VSROOT goto :activate_vs
set "FABLE2_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%FABLE2_VSWHERE%" set "FABLE2_VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%FABLE2_VSWHERE%" goto :missing_vs
for /f "usebackq delims=" %%I in (`"%FABLE2_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "FABLE2_VSROOT=%%I"
if not defined FABLE2_VSROOT goto :missing_vs
:activate_vs
if not exist "%FABLE2_VSROOT%\Common7\Tools\VsDevCmd.bat" goto :missing_vs
echo Initializing Visual Studio C++ x64 environment...
call "%FABLE2_VSROOT%\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 goto :broken_vs
if not defined VCToolsInstallDir goto :broken_vs
if not defined INCLUDE goto :broken_vs
if not defined LIB goto :broken_vs
where cl.exe >nul 2>nul
if errorlevel 1 goto :broken_vs

rem v18.2 does NOT prefer Program Files/LLVM blindly. The Python preflight
rem tries complete installations and compiles C++23 against this MSVC/SDK.
rem CMake and Ninja ship with the VS 'C++ CMake tools for Windows' component.
where cmake >nul 2>nul
if not errorlevel 1 goto :cmake_ready
if exist "%FABLE2_VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" set "PATH=%FABLE2_VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%PATH%"
:cmake_ready
where ninja >nul 2>nul
if not errorlevel 1 goto :ninja_ready
if exist "%FABLE2_VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe" set "PATH=%FABLE2_VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;%PATH%"
:ninja_ready
where git >nul 2>nul
if not errorlevel 1 goto :git_ready
if exist "%ProgramFiles%\Git\cmd\git.exe" set "PATH=%ProgramFiles%\Git\cmd;%PATH%"
:git_ready
rem A Microsoft Store alias may satisfy WHERE but cannot run Python.
python -c "import sys; assert sys.version_info >= (3, 10)" >nul 2>nul
if not errorlevel 1 goto :python_ready
py -3 -c "import sys; assert sys.version_info >= (3, 10)" >nul 2>nul
if errorlevel 1 goto :missing_python
set "FABLE2_PYTHON="
for /f "usebackq delims=" %%I in (`py -3 -c "import sys; print(sys.executable)"`) do set "FABLE2_PYTHON=%%I"
if not defined FABLE2_PYTHON goto :missing_python
for %%I in ("%FABLE2_PYTHON%") do set "PATH=%%~dpI;%PATH%"
python -c "import sys; assert sys.version_info >= (3, 10)" >nul 2>nul
if errorlevel 1 goto :missing_python
:python_ready
rem Select actual absolute paths. Do not run tests with an unvalidated compiler.
set "FABLE2_TOOLCHAIN_READY="
python "%~dp0check_game_speed_toolchain.py" --emit-env "%~dp0..\out\game-speed-toolchain-selected.cmd"
if errorlevel 1 exit /b 1
if not exist "%~dp0..\out\game-speed-toolchain-selected.cmd" exit /b 1
call "%~dp0..\out\game-speed-toolchain-selected.cmd"
exit /b %ERRORLEVEL%

:missing_vs
echo.
echo ERROR: Visual Studio C++ Build Tools were not found.
echo LLVM is installed separately; it still needs MSVC headers and Windows SDK.
echo Open Visual Studio Installer and install Desktop development with C++.
echo Include MSVC x64/x86, Windows 10 or 11 SDK, and C++ CMake tools for Windows.
echo See BUILD_FIX_V18_2_README_RU.txt. No game files were changed.
exit /b 1
:broken_vs
echo.
echo ERROR: Visual Studio was found, but its C++ x64 environment is incomplete.
echo Modify it in Visual Studio Installer: MSVC x64/x86 and Windows SDK are required.
echo Do not install only Visual Studio Code. See BUILD_FIX_V18_2_README_RU.txt.
exit /b 1
:missing_python
echo.
echo ERROR: A working Python 3.10+ installation was not found.
echo Reopen the command window after installing Python and enabling its PATH option.
echo See BUILD_FIX_V18_2_README_RU.txt. No game files were changed.
exit /b 1
