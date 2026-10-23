@echo off
setlocal
chcp 65001 >nul
set "RESULT=0"
cd /d "%~dp0"
if errorlevel 1 (
  echo ERROR: Cannot open the launcher project directory.
  set "RESULT=1"
  goto :finish
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: Install the .NET 8 SDK, then run this script again.
  set "RESULT=1"
  goto :finish
)

rem Keep the game-speed source compile check introduced in v18.1.
echo Checking game-speed source without implicit usings...
dotnet build "tests\Fable2.GameSpeed.CompileCheck\Fable2.GameSpeed.CompileCheck.csproj" -c Release --nologo
if errorlevel 1 (
  echo ERROR: Game-speed source compilation failed. No installed files were changed.
  set "RESULT=1"
  goto :finish
)

echo Running launcher and config regression tests...
dotnet run --project "tests\Fable2.Launcher.ConfigTests" -c Release
if errorlevel 1 (
  echo ERROR: Launcher regression tests failed. Build stopped.
  set "RESULT=1"
  goto :finish
)

echo Building the launcher only, NOT fable_2.exe or GPU DLLs...
dotnet publish "launcher\Fable2.Launcher\Fable2.Launcher.csproj" -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "out\texture-diagnostics-launcher"
if errorlevel 1 (
  echo ERROR: Launcher publish failed. No installed files were changed.
  set "RESULT=1"
  goto :finish
)

set "LAUNCHER_BUILT=%~dp0out\texture-diagnostics-launcher\Fable2Launcher.exe"
if not exist "%LAUNCHER_BUILT%" (
  echo ERROR: Publish reported success, but Fable2Launcher.exe was not found.
  set "RESULT=1"
  goto :finish
)

:finish
echo.
if "%RESULT%"=="0" (
  echo ===============================================================================
  echo                     LAUNCHER 1.6.1 BUILD COMPLETE
  echo ===============================================================================
  echo ГОТОВО! СОБРАННЫЙ ФАЙЛ НАХОДИТСЯ ЗДЕСЬ:
  echo.
  echo    "%LAUNCHER_BUILT%"
  echo.
  echo ЧТО ДЕЛАТЬ: закройте запущенный лаунчер и вручную скопируйте
  echo Fable2Launcher.exe в папку установленной игры с заменой старого файла.
  echo.
  echo WHAT TO DO: close the launcher, copy Fable2Launcher.exe to your
  echo game installation folder and replace the old launcher file manually.
  echo.
  echo СКРИПТ НИЧЕГО НЕ КОПИРОВАЛ И НЕ МЕНЯЛ В ПАПКЕ ИГРЫ.
  echo NO FILES WERE DEPLOYED TO ANY GAME FOLDER.
  echo ===============================================================================
) else (
  echo ===============================================================================
  echo                         LAUNCHER BUILD FAILED
  echo ===============================================================================
  echo Проверьте ошибку выше. Файлы установленной игры не изменялись.
  echo Check the error above. No files were deployed to any game folder.
  echo ===============================================================================
)

rem Called from BUILD-GAME-SPEED-LOCAL.cmd with --build-only: never block its pipeline.
if /i "%~1"=="--build-only" goto :end

echo.
echo Нажмите любую кнопку чтобы завершить процесс
pause >nul
:end
endlocal & exit /b %RESULT%
