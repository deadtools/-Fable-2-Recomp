"""Regression checks for v18.1 build guards (no game or Windows required).
These exercise parsing/error paths and verify script ordering, not cmd.exe itself.
"""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("preflight", ROOT / "tools/check_game_speed_toolchain.py")
PREFLIGHT = importlib.util.module_from_spec(spec)
spec.loader.exec_module(PREFLIGHT)


class BuildFixTests(unittest.TestCase):
    def test_explicit_stream_and_system_imports(self):
        source = (ROOT / "launcher/Fable2.Launcher/GameSpeedSettings.cs").read_text(encoding="utf-8")
        self.assertIn("using System.IO;", source)
        self.assertIn("using System;", source)
        self.assertIn("ContainsMarker(Stream input)", source)

    def test_no_implicit_import_compile_fixture(self):
        root = ET.parse(ROOT / "tests/Fable2.GameSpeed.CompileCheck/Fable2.GameSpeed.CompileCheck.csproj").getroot()
        self.assertEqual(root.findtext("PropertyGroup/ImplicitUsings"), "disable")
        self.assertEqual(root.findtext("PropertyGroup/TreatWarningsAsErrors"), "true")
        self.assertIn("GameSpeedSettings.cs", root.find("ItemGroup/Compile").attrib["Include"])

    def test_native_preflight_precedes_tests(self):
        source = (ROOT / "BUILD-GAME-SPEED-LOCAL.cmd").read_text(encoding="utf-8")
        self.assertLess(source.index("setup-native-build-env.cmd"), source.index("python -m unittest"))
        self.assertLess(source.index("python -m unittest"), source.index("prepare_game_speed_build.py"))
        self.assertIn('call "%~dp0tools\\setup-native-build-env.cmd"\nif errorlevel 1 goto :finish', source)
        self.assertNotIn("goto :toolchain_ready", source)

    def test_publish_and_deploy_guards_preserved(self):
        source = (ROOT / "BUILD-LAUNCHER-LOCAL.cmd").read_text(encoding="utf-8")
        self.assertLess(source.index("Fable2.GameSpeed.CompileCheck"), source.index("dotnet publish"))
        self.assertLess(source.index("dotnet publish"), source.index("deploy-launcher.ps1"))
        self.assertIn('if /i "%~1"=="--build-only" goto :finish', source)
        self.assertIn('if /i "%~1"=="--build-only" goto :exit_script', source)
        self.assertIn("set \"RESULT=1\"", source)
        self.assertIn("pause >nul", source)

    def test_scope_and_environment(self):
        source = (ROOT / "tools/setup-native-build-env.cmd").read_text(encoding="utf-8")
        commands = [line.strip().lower() for line in source.splitlines() if not line.lower().startswith("rem ")]
        self.assertNotIn("setlocal", commands)
        self.assertIn("-products *", source)
        self.assertIn("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", source)
        self.assertIn("-arch=x64 -host_arch=x64", source)
        self.assertIn("--emit-env", source)
        self.assertIn("game-speed-toolchain-selected.cmd", source)
        self.assertNotIn('set "FABLE2_CXX=clang++"', source)
        self.assertIn("CMake\\Ninja", source)
        self.assertIn("exit /b %ERRORLEVEL%", source)

    def test_cmd_files_use_crlf(self):
        for rel in ("BUILD-GAME-SPEED-LOCAL.cmd", "BUILD-LAUNCHER-LOCAL.cmd", "tools/setup-native-build-env.cmd"):
            data = (ROOT / rel).read_bytes()
            self.assertNotIn(b"\n", data.replace(b"\r\n", b""), rel)
            self.assertFalse(data.startswith(b"\xef\xbb\xbf"), rel)

    def test_detects_all_missing_tools(self):
        missing = PREFLIGHT.missing_tools(lambda name: None)
        self.assertEqual(len(missing), len(PREFLIGHT.TOOLS))
        self.assertTrue(any("cmake:" in text for text in missing))
        self.assertTrue(any("ninja:" in text for text in missing))

    def test_present_tools(self):
        self.assertEqual(PREFLIGHT.missing_tools(lambda name: "/tools/" + name), [])

    def test_version_parsing(self):
        self.assertEqual(PREFLIGHT.parse_version("clang version 21.1.2", r"clang version (\d+(?:\.\d+)+)"), (21, 1, 2))
        self.assertEqual(PREFLIGHT.parse_version("cmake version 3.31.4-msvc6", r"cmake version (\d+(?:\.\d+)+)"), (3, 31, 4))
        self.assertIsNone(PREFLIGHT.parse_version("invalid", r"clang version (\d+(?:\.\d+)+)"))

    def test_compile_failure_keeps_diagnostic(self):
        failed = subprocess.CompletedProcess([], 1, "compiler stdout", "missing Windows.h")
        with tempfile.TemporaryDirectory() as directory, patch.object(PREFLIGHT, "run_capture", return_value=failed) as run:
            with self.assertRaisesRegex(RuntimeError, "missing Windows.h"):
                PREFLIGHT.probe("clang++", Path(directory), [])
            self.assertEqual(run.call_count, 1)

    def test_runtime_probe_failure(self):
        results = [subprocess.CompletedProcess([], 0, "", ""), subprocess.CompletedProcess([], 1, "", "runtime unavailable")]
        with tempfile.TemporaryDirectory() as directory, patch.object(PREFLIGHT, "run_capture", side_effect=results):
            with self.assertRaisesRegex(RuntimeError, "runtime unavailable"):
                PREFLIGHT.probe("clang++", Path(directory), [])

    def test_probe_success(self):
        results = [subprocess.CompletedProcess([], 0, "", ""), subprocess.CompletedProcess([], 0, "x64 probe passed", "")]
        with tempfile.TemporaryDirectory() as directory, patch.object(PREFLIGHT, "run_capture", side_effect=results) as run:
            PREFLIGHT.probe("clang++", Path(directory), [])
            self.assertEqual(run.call_count, 2)
            self.assertIn("-fuse-ld=lld", run.call_args_list[0].args[0])

    def test_native_test_diagnostics_and_compiler(self):
        source = (ROOT / "tests/test_game_speed_v18.py").read_text(encoding="utf-8")
        self.assertIn('os.environ.get("FABLE2_CXX"', source)
        self.assertIn('result.stdout + result.stderr', source)
        self.assertIn('Compile failed:', source)


if __name__ == "__main__":
    unittest.main()
