@echo off
rem Builds the patched rexruntime.dll / rexgpu-xenos.dll with the project's own
rem native script tools\build_runtime_sdk.cmd (NOT BUILD-LAUNCHER-LOCAL.cmd),
rem then builds and runs the input unit tests.
rem Usage (from "x64 Native Tools Command Prompt for VS"):
rem   BUILD-REXRUNTIME-INPUTFIX.cmd <full native source root>
rem Needs on PATH: clang/clang++ (LLVM 20+), ninja, cmake, python.
setlocal
if "%~1"=="" (
  echo Usage: %~nx0 ^<full native source root^> 1>&2
  exit /b 2
)
set "ROOT=%~f1"
set "SDK=%ROOT%\thirdparty\rexglue-sdk"
if not exist "%ROOT%\tools\build_runtime_sdk.cmd" (
  echo Error: %ROOT%\tools\build_runtime_sdk.cmd not found. 1>&2
  exit /b 1
)
for %%t in (clang++ ninja cmake python) do (
  where %%t >nul 2>nul || (echo Error: %%t not on PATH. 1>&2 & exit /b 1)
)
findstr /c:"state_mutex_" "%SDK%\src\input\input_system.cpp" >nul || (
  echo Error: patch not applied. Run APPLY-INPUTFIX-SOURCE.cmd first. 1>&2
  exit /b 1
)

echo === [1/3] Release runtime via tools\build_runtime_sdk.cmd
call "%ROOT%\tools\build_runtime_sdk.cmd" || exit /b 1

echo === [2/3] Unit tests (separate build dir, runtime build untouched)
set "TB=%ROOT%\out\build\runtime-sdk-inputfix-tests"
cmake -S "%SDK%" -B "%TB%" -G Ninja -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_C_COMPILER=clang -DCMAKE_CXX_COMPILER=clang++ -DCMAKE_RC_COMPILER:FILEPATH=llvm-rc ^
  -DCMAKE_C_COMPILER_TARGET=x86_64-pc-windows-msvc -DCMAKE_CXX_COMPILER_TARGET=x86_64-pc-windows-msvc ^
  -DCMAKE_C_FLAGS="-march=x86-64-v2 -g" -DCMAKE_CXX_FLAGS="-march=x86-64-v2 -g" ^
  -DREXGLUE_USE_D3D12=ON -DREXGLUE_USE_VULKAN=OFF -DREXGLUE_ENABLE_TRACY=OFF ^
  -DREXGLUE_ENABLE_FIDELITYFX=OFF -DREXGLUE_BUILD_TESTS=ON || exit /b 1
cmake --build "%TB%" || exit /b 1
ctest --test-dir "%TB%" --output-on-failure --no-tests=error || (
  echo Error: input unit tests failed. Do NOT install the DLL. 1>&2
  exit /b 1
)

echo === [3/3] Built files and SHA-256
for /r "%ROOT%\out\build\runtime-sdk" %%f in (rexruntime.dll rexgpu-xenos.dll) do (
  if exist "%%f" powershell -NoProfile -Command "'{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath '%%f').Hash, '%%f'"
)
echo Next: INSTALL-REXRUNTIME-INPUTFIX.cmd -NewDll ^<path to rexruntime.dll above^>
exit /b 0
