@echo off
setlocal
chcp 65001 >nul
set "RESULT=0"
cd /d "%~dp0"
if errorlevel 1 (
  echo Cannot open the launcher project directory.
  set "RESULT=1"
  goto :finish
)
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install the .NET 8 SDK, then run this script again.
  set "RESULT=1"
  goto :finish
)
rem Console tests enable System.IO implicitly; WPF does not. Check imports independently.
echo Checking game-speed source without implicit usings...
dotnet build "tests\Fable2.GameSpeed.CompileCheck\Fable2.GameSpeed.CompileCheck.csproj" -c Release --nologo
if errorlevel 1 (
  echo Game-speed source compilation failed. Installed launcher was NOT changed.
  set "RESULT=1"
  goto :finish
)
echo Running launcher and config regression tests...
dotnet run --project "tests\Fable2.Launcher.ConfigTests" -c Release
if errorlevel 1 (
  echo Launcher regression tests failed. Build stopped.
  set "RESULT=1"
  goto :finish
)

echo Building the launcher only, NOT fable_2.exe or GPU DLLs...
dotnet publish "launcher\Fable2.Launcher\Fable2.Launcher.csproj" -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "out\texture-diagnostics-launcher"
if errorlevel 1 (
  echo Launcher build failed. The installed executable was not touched.
  set "RESULT=1"
  goto :finish
)

if /i "%~1"=="--build-only" goto :finish

set "LAUNCHER_BUILT=%CD%\out\texture-diagnostics-launcher\Fable2Launcher.exe"
set "LAUNCHER_INSTALLED=D:\Fable2\Fable 2 1.5.0\Fable2Launcher.exe"
echo Installing new launcher into the game folder...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\deploy-launcher.ps1" -Source "%LAUNCHER_BUILT%" -Destination "%LAUNCHER_INSTALLED%"
if errorlevel 1 (
  echo Launcher was built, but auto-install failed. Check deployment error and the optional .previous.bak file.
  echo Built EXE: "%LAUNCHER_BUILT%"
  set "RESULT=1"
  goto :finish
)
echo Launcher built and installed. Patching the native MenuHighlight badge (v17, frames_elements.tex)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\deploy-dualshock-menu.ps1" -GameRoot "D:\Fable2\Fable 2 1.5.0"
if errorlevel 1 (
  echo The launcher was updated but the menu data patch was not installed.
  echo See the error above. Unsupported texture entries were NOT overwritten.
  set "RESULT=1"
  goto :finish
)
echo Done: launcher built and installed; MenuHighlight A-to-PS-cross patch verified/applied.

:finish
if /i "%~1"=="--build-only" goto :exit_script
echo.
echo Нажмите любую кнопку чтобы завершить процесс
pause >nul
:exit_script
endlocal & exit /b %RESULT%
