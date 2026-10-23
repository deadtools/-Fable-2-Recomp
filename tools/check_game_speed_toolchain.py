"""v18.2: select and test one compatible Windows LLVM/MSVC toolchain.

No downloads, software installations or edits to the installed game. Each LLVM
candidate must compile AND execute a C++23 probe using the active MSVC/SDK and
the project's TOML headers. A selected absolute path is shared by tests and CMake.
Full diagnostics: out/game-speed-toolchain.log. Old CMake output is renamed,
not deleted, when its recorded toolchain differs or has never been recorded.
"""
from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
REVISION = "18.2"
# The MSVC 14.51 STL was validated upstream with 20.1.8. A newer installed
# MSVC STL may need newer LLVM still; the compile probe is the final authority.
MIN_CLANG = (20, 1, 8)
TOOLS = (
    ("git", "Git for Windows"),
    ("cmake", "CMake 3.25 or newer / C++ CMake tools for Windows"),
    ("ninja", "Ninja / C++ CMake tools for Windows"),
    ("dotnet", ".NET 8 SDK"),
)
LLVM_TOOLS = ("clang.exe", "clang++.exe", "lld-link.exe", "llvm-rc.exe")
PROBE = r"""#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <filesystem>
#include <format>
#include <iostream>
#include <memory>
#include <string>
#include <type_traits>
#include <vector>
#include <toml++/toml.hpp>
static_assert(sizeof(void*) == 8, "A 64-bit toolchain is required");
static_assert(std::is_same_v<std::remove_cvref_t<const int&>, int>);
struct Base { int member; };
struct Derived : Base {};
#if defined(__cpp_lib_is_pointer_interconvertible)
static_assert(std::is_pointer_interconvertible_base_of_v<Base, Derived>);
#endif
int main() {
    const auto config = toml::parse("[gameplay]\ngame_speed=0.5\n");
    const auto speed = config["gameplay"]["game_speed"].value_or(1.0);
    const auto number = std::make_unique<double>(speed);
    const std::vector<std::string> values{"native", "toolchain"};
    const auto path = std::filesystem::current_path();
    const auto text = std::format("game_speed={:.1f}", *number);
    if (path.empty() || values.size() != 2 || GetCurrentProcessId() == 0 ||
        text != "game_speed=0.5") return 1;
    std::cout << "C++23 / MSVC STL / Windows SDK / TOML / std::format / x64 passed\n";
    return 0;
}
"""


def parse_version(text: str, pattern: str) -> tuple[int, ...] | None:
    match = re.search(pattern, text, flags=re.IGNORECASE)
    return tuple(int(part) for part in match.group(1).split(".")) if match else None


def missing_tools(which=shutil.which) -> list[str]:
    return [f"{name}: install {hint}" for name, hint in TOOLS if not which(name)]


def run_capture(args: list[str], log: list[str], timeout: int = 45,
                env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    log.append("COMMAND: " + subprocess.list2cmdline(args))
    result = subprocess.run(args, capture_output=True, text=True, errors="replace",
                            timeout=timeout, env=env)
    log.extend([result.stdout, result.stderr, f"Exit: {result.returncode}"])
    return result


def discover_llvm(env: dict[str, str] | None = None) -> list[Path]:
    """Prefer the active VS installation, but actually try every discovered LLVM.
    An optional FABLE2_LLVM_DIR selects a custom bin or LLVM install directory.
    No fixed preference for Program Files/LLVM (the v18.1 regression).
    """
    env = os.environ if env is None else env
    directories: list[Path] = []
    explicit = env.get("FABLE2_LLVM_DIR", "")
    if explicit:
        custom = Path(explicit.strip('"'))
        directories.extend([custom, custom / "bin"])
    vs = env.get("FABLE2_VSROOT") or env.get("VSINSTALLDIR")
    if vs:
        directories.extend([Path(vs) / "VC/Tools/Llvm/x64/bin",
                            Path(vs) / "VC/Tools/Llvm/bin"])
    selected = env.get("FABLE2_CXX", "")
    if selected and Path(selected).is_absolute():
        directories.append(Path(selected).parent)
    for key in ("ProgramW6432", "ProgramFiles", "ProgramFiles(x86)"):
        if env.get(key):
            directories.append(Path(env[key]) / "LLVM/bin")
    for value in env.get("PATH", "").split(os.pathsep):
        if value.strip().strip('"'):
            directories.append(Path(value.strip().strip('"')))
    result: list[Path] = []
    seen: set[str] = set()
    for directory in directories:
        path = (directory / "clang++.exe").resolve()
        key = str(path).casefold()
        if key not in seen and path.is_file():
            result.append(path)
            seen.add(key)
    return result


def probe(compiler: str, directory: Path, log: list[str]) -> None:
    source, binary = directory / "probe.cpp", directory / "probe.exe"
    source.write_text(PROBE, encoding="utf-8")
    env = dict(os.environ)
    env["PATH"] = str(Path(compiler).parent) + os.pathsep + env.get("PATH", "")
    args = [compiler, "--target=x86_64-pc-windows-msvc", "-std=c++23",
            "-Wall", "-Wextra", "-Werror", "-fno-color-diagnostics", "-fuse-ld=lld",
            "-I", str(ROOT / "thirdparty/tomlplusplus/include"),
            str(source), "-o", str(binary)]
    result = run_capture(args, log, 90, env=env)
    if result.returncode:
        raise RuntimeError("C++23 compile/link check failed:\n" + result.stdout + result.stderr)
    result = run_capture([str(binary)], log, 20, env=env)
    if result.returncode:
        raise RuntimeError("The compiled x64 check could not run:\n" + result.stdout + result.stderr)
    print(result.stdout.strip())


def choose_llvm(candidates: list[Path], log: list[str]) -> dict[str, str]:
    for compiler in candidates:
        print("Checking LLVM: " + str(compiler), flush=True)
        try:
            missing = [name for name in LLVM_TOOLS if not (compiler.parent / name).is_file()]
            if missing:
                raise RuntimeError("Incomplete LLVM installation, missing: " + ", ".join(missing))
            result = run_capture([str(compiler), "--version"], log)
            version = parse_version(result.stdout, r"clang version (\d+(?:\.\d+)+)")
            if result.returncode or version is None or version < MIN_CLANG:
                actual = ".".join(map(str, version)) if version else "unknown"
                raise RuntimeError(f"LLVM {actual} is too old; minimum for this build is 20.1.8")
            c_version = run_capture([str(compiler.parent / "clang.exe"), "--version"], log)
            if c_version.returncode or parse_version(c_version.stdout, r"clang version (\d+(?:\.\d+)+)") != version:
                raise RuntimeError("clang and clang++ in this directory have different versions")
            with tempfile.TemporaryDirectory(prefix="fable2-cxx23-") as directory:
                probe(str(compiler), Path(directory), log)
            print("Selected compatible LLVM " + ".".join(map(str, version)), flush=True)
            return {"cc": str(compiler.parent / "clang.exe"), "cxx": str(compiler),
                    "rc": str(compiler.parent / "llvm-rc.exe"),
                    "lld": str(compiler.parent / "lld-link.exe"),
                    "version": ".".join(map(str, version)), "version_banner": result.stdout.strip(),
                    "vctools": os.environ.get("VCToolsInstallDir", ""),
                    "vctools_version": os.environ.get("VCToolsVersion", ""),
                    "sdk": os.environ.get("WindowsSdkDir", ""),
                    "sdk_version": os.environ.get("WindowsSDKVersion", ""),
                    "include": os.environ.get("INCLUDE", ""), "lib": os.environ.get("LIB", "")}
        except (OSError, RuntimeError, subprocess.SubprocessError) as error:
            detail = str(compiler) + ": " + str(error)
            log.append("REJECTED: " + detail)
            # Do not swamp cmd.exe with hundreds of errors from each wrong STL.
            print("  Rejected: " + str(error).splitlines()[0] + "; details in toolchain log.", flush=True)
    reason = "No installed LLVM passed the C++23/MSVC compatibility check."
    if not candidates:
        reason = "No LLVM compiler was found."
    raise RuntimeError(reason + "\nIn Visual Studio Installer, modify this Visual Studio installation.\n"
                       "Under Desktop development with C++, add/update 'C++ Clang tools for Windows'.\n"
                       "Alternatively update the standalone LLVM distribution. Do not reinstall Python or Git.\n"
                       "Then rerun BUILD-GAME-SPEED-LOCAL.cmd. No game files were changed.")


def cmd_assignment(name: str, value: str) -> str:
    if any(char in value for char in ('"', '\r', '\n', '\x00')):
        raise ValueError("Unsafe path in toolchain environment: " + name)
    # The caller disables delayed expansion; double '%' for a literal batch value.
    return f'set "{name}={value.replace("%", "%%")}"'


def write_selection(selection: dict[str, str], output: Path, root: Path = ROOT) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    metadata = root / "out/game-speed-toolchain-selection.json"
    metadata.parent.mkdir(parents=True, exist_ok=True)
    metadata.write_text(json.dumps(selection, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    bindir = str(Path(selection["cxx"]).parent)
    lines = ["@echo off", "rem Generated by the successful v18.2 C++23 preflight. Not a user setting."]
    for name, value in {"FABLE2_CC": selection["cc"], "FABLE2_CXX": selection["cxx"],
                        "FABLE2_RC": selection["rc"], "FABLE2_LLVM_BIN": bindir,
                        "LLVM": bindir, "CC": selection["cc"], "CXX": selection["cxx"],
                        "FABLE2_TOOLCHAIN_READY": REVISION}.items():
        lines.append(cmd_assignment(name, value))
    # PATH is deliberately expanded by cmd.exe, not captured/rewritten from Python.
    lines.append('set "PATH=' + bindir.replace('%', '%%') + ';%PATH%"')
    lines.append("exit /b 0")
    output.write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-8"))


def cache_identity(selection: dict[str, str]) -> dict[str, str]:
    content = json.dumps(selection, sort_keys=True, ensure_ascii=True)
    return {"revision": REVISION, "fingerprint": hashlib.sha256(content.encode()).hexdigest(),
            "cxx": selection["cxx"], "cc": selection["cc"], "rc": selection["rc"],
            "version": selection["version"]}


def prepare_cache(directory: Path, selection: dict[str, str], root: Path = ROOT) -> Path | None:
    """Never reuse unproven objects/cache with another compiler. Keep the old tree."""
    directory, allowed = directory.resolve(), (root / "out/build").resolve()
    if directory == allowed or allowed not in directory.parents:
        raise ValueError("Refusing to alter anything outside a project out/build subdirectory")
    identity = cache_identity(selection)
    marker = directory / ".fable2-toolchain-v18_2.json"
    previous = None
    if marker.is_file():
        try:
            previous = json.loads(marker.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            pass
    cache_matches = True
    cache = directory / "CMakeCache.txt"
    if cache.is_file():
        values = {}
        for line in cache.read_text(encoding="utf-8", errors="replace").splitlines():
            match = re.match(r"^(CMAKE_(?:C|CXX|RC)_COMPILER):[^=]*=(.*)$", line)
            if match:
                values[match.group(1)] = match.group(2)
        for key, selected in (("CMAKE_C_COMPILER", "cc"), ("CMAKE_CXX_COMPILER", "cxx"),
                              ("CMAKE_RC_COMPILER", "rc")):
            if key in values and Path(values[key]).resolve() != Path(selection[selected]).resolve():
                cache_matches = False
    # CMake 4.5.0-rc1 may write a Windows backslash tool path into the
    # generated CMakeRCCompiler.cmake without escaping it. On the next run
    # that file is included before the compiler can be reconfigured, so
    # simply passing a corrected -DCMAKE_RC_COMPILER is not sufficient.
    # Preserve the affected build directory and configure afresh instead.
    invalid_rc_cache = False
    for compiler_file in directory.glob("CMakeFiles/*/CMakeRCCompiler.cmake"):
        contents = compiler_file.read_text(encoding="utf-8", errors="replace")
        if re.search(r'(?m)^\s*set\(CMAKE_RC_COMPILER\s+"[A-Za-z]:\\(?!\\)', contents):
            invalid_rc_cache = True
            break
    backed_up = None
    if directory.exists() and any(directory.iterdir()) and \
            (previous != identity or not cache_matches or invalid_rc_cache):
        if invalid_rc_cache:
            print("Found invalid CMakeRCCompiler.cmake (unescaped Windows path). "
                  "Preserving old cache and configuring a clean RC cache.", flush=True)
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        backed_up = directory.with_name(directory.name + ".before-toolchain-v18_2-" + stamp)
        directory.rename(backed_up)
        print("Preserved old CMake output: " + str(backed_up))
    directory.mkdir(parents=True, exist_ok=True)
    marker.write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")
    return backed_up


def check_other_tools(log: list[str]) -> None:
    problems = missing_tools()
    for name in ("VCToolsInstallDir", "INCLUDE", "LIB"):
        value = os.environ.get(name, "")
        log.append(f"{name}={value}")
        if not value:
            problems.append(name + " is not set: activate the Visual Studio C++ x64 environment")
    if problems:
        raise RuntimeError("Missing build prerequisites:\n  " + "\n  ".join(problems))
    result = run_capture([shutil.which("cmake"), "--version"], log)
    version = parse_version(result.stdout, r"cmake version (\d+(?:\.\d+)+)")
    if result.returncode or version is None or version < (3, 25):
        raise RuntimeError("CMake 3.25 or newer is required.")
    sdk = run_capture([shutil.which("dotnet"), "--list-sdks"], log)
    if sdk.returncode or not re.search(r"^(?:[89]|[1-9]\d+)\.\d+\.\d+", sdk.stdout, re.MULTILINE):
        raise RuntimeError("Install .NET 8 SDK; a runtime alone is insufficient.")
    runtime = run_capture([shutil.which("dotnet"), "--list-runtimes"], log)
    if runtime.returncode or not re.search(r"^Microsoft\.NETCore\.App 8\.", runtime.stdout, re.MULTILINE):
        raise RuntimeError(".NET 8 runtime is required for net8.0 tests. Install .NET 8 SDK.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--emit-env", type=Path, default=ROOT / "out/game-speed-toolchain-selected.cmd")
    parser.add_argument("--prepare-cache", type=Path)
    args = parser.parse_args()
    log = ["Fable II game-speed toolchain preflight v18.2", f"Python: {sys.version}"]
    cache_mode = args.prepare_cache is not None
    try:
        if os.name != "nt":
            raise RuntimeError("Run this preflight on Windows via BUILD-GAME-SPEED-LOCAL.cmd.")
        if cache_mode:
            selection = json.loads((ROOT / "out/game-speed-toolchain-selection.json").read_text(encoding="utf-8"))
            if os.environ.get("FABLE2_TOOLCHAIN_READY") != REVISION or \
               Path(os.environ.get("FABLE2_CXX", "")).resolve() != Path(selection["cxx"]).resolve():
                raise RuntimeError("Toolchain selection is stale. Rerun BUILD-GAME-SPEED-LOCAL.cmd.")
            prepare_cache(args.prepare_cache, selection)
            return 0
        # Remove stale success files before probing. Never let an earlier selection
        # authorize a failed run or leave a misleading 'selected' compiler behind.
        args.emit_env.unlink(missing_ok=True)
        (ROOT / "out/game-speed-toolchain-selection.json").unlink(missing_ok=True)
        print("Fable II toolchain preflight v18.2 - checking actual C++23 compatibility", flush=True)
        if sys.version_info < (3, 10):
            raise RuntimeError("Python 3.10 or newer is required.")
        check_other_tools(log)
        selection = choose_llvm(discover_llvm(), log)
        write_selection(selection, args.emit_env)
        print("Native build prerequisites verified. Starting project tests next.", flush=True)
        return 0
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        message = "Preflight failed: " + str(error)
        log.append(message)
        print(message, file=sys.stderr)
        print("Installed game files were NOT changed. See BUILD_FIX_V18_2_README_RU.txt.", file=sys.stderr)
        return 1
    finally:
        if not cache_mode:
            target = ROOT / "out/game-speed-toolchain.log"
            try:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("\n".join(log), encoding="utf-8")
                print("Toolchain log: " + str(target))
            except OSError as error:
                print(f"Could not save toolchain log: {error}", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
