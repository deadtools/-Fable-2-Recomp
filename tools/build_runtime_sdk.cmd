@echo off
rem Matched Windows x64 runtime and D3D12 plugin. Optional second arg: Debug.
rem Run in a VS x64
rem developer shell with LLVM 20+, Ninja, CMake and Python on PATH.
setlocal
set "ROOT=%~dp0.."
set "SOURCE=%ROOT%\thirdparty\rexglue-sdk"
set "BUILD=%ROOT%\out\build\runtime-sdk"
set "STAGED=%ROOT%\out\tooling\runtime-sdk\win-amd64"
set "OFFICIAL_ARG=%~1"
set "OFFICIAL=%~1"
set "CONFIGURATION=%~2"
if not defined CONFIGURATION set "CONFIGURATION=Release"
if /i "%CONFIGURATION%"=="Debug" (
  set "CONFIGURATION=Debug"
  set "BUILD=%ROOT%\out\build\runtime-sdk-debug"
  set "STAGED=%ROOT%\out\tooling\runtime-sdk-debug\win-amd64"
) else if /i "%CONFIGURATION%"=="Release" (
  set "CONFIGURATION=Release"
) else (
  echo Error: runtime configuration must be Debug or Release. 1>&2
  exit /b 1
)
if not defined OFFICIAL set "OFFICIAL=%ROOT%\out\tooling\rexglue-sdk-0.10.0\win-amd64"
if not exist "%SOURCE%\CMakeLists.txt" (
  git -C "%ROOT%" submodule update --init thirdparty/rexglue-sdk || exit /b 1
)
rem Auto-fetch the official prebuilt SDK only when using the default location.
if not defined OFFICIAL_ARG if not exist "%OFFICIAL%\bin\rexglue.exe" (
  call "%~dp0setup_sdk.cmd" || exit /b 1
)
python "%~dp0prepare_runtime_sdk.py" "%SOURCE%" || exit /b 1
python "%~dp0prepare_game_speed_sdk.py" "%SOURCE%" || exit /b 1
rem -g: always emit ReXGlue PDBs (Release + -g keeps production codegen but
rem produces full-symbol PDBs the fable_2 build stages next to the exe).
set "NATIVE_CC=clang"
set "NATIVE_CXX=clang++"
set "NATIVE_RC=llvm-rc"
if defined FABLE2_TOOLCHAIN_READY (
  set "NATIVE_CC=%FABLE2_CC%"
  set "NATIVE_CXX=%FABLE2_CXX%"
  rem Normalize the CMake RC tool path before it is embedded in a generated
  rem CMakeRCCompiler.cmake. C:\Program Files\... would be parsed as \P.
  set "NATIVE_RC=%FABLE2_RC:\=/%"
  python "%~dp0check_game_speed_toolchain.py" --prepare-cache "%BUILD%" || exit /b 1
)
cmake -S "%SOURCE%" -B "%BUILD%" -G Ninja ^
  -DCMAKE_BUILD_TYPE=%CONFIGURATION% -DCMAKE_C_COMPILER="%NATIVE_CC%" -DCMAKE_CXX_COMPILER="%NATIVE_CXX%" -DCMAKE_RC_COMPILER:FILEPATH="%NATIVE_RC%" ^
  -DCMAKE_C_COMPILER_TARGET=x86_64-pc-windows-msvc -DCMAKE_CXX_COMPILER_TARGET=x86_64-pc-windows-msvc ^
  -DCMAKE_C_FLAGS="-march=x86-64-v2 -g" -DCMAKE_CXX_FLAGS="-march=x86-64-v2 -g" ^
  -DREXGLUE_USE_D3D12=ON -DREXGLUE_USE_VULKAN=OFF ^
  -DREXGLUE_ENABLE_TRACY=OFF -DREXGLUE_ENABLE_FIDELITYFX=OFF ^
  -DREXGLUE_BUILD_TESTS=OFF || exit /b 1
cmake --build "%BUILD%" --target rexruntime rexgpu-xenos || exit /b 1
python "%~dp0stage_renderer_sdk.py" "%OFFICIAL%" "%SOURCE%" "%BUILD%" "%STAGED%" --configuration %CONFIGURATION% || exit /b 1
exit /b 0
