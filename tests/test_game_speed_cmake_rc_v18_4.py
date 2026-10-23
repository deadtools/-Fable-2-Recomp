"""Regression tests for the Windows llvm-rc / CMake 4.5 path bug."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "game_speed_toolchain_v18_4", ROOT / "tools/check_game_speed_toolchain.py"
)
toolchain = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(toolchain)


class CMakeResourceCompilerPathTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="fable2-rc-path-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.selection = {
            "cc": "C:/Program Files/Visual Studio/Llvm/bin/clang.exe",
            "cxx": "C:/Program Files/Visual Studio/Llvm/bin/clang++.exe",
            "rc": "C:/Program Files/Visual Studio/Llvm/bin/llvm-rc.exe",
            "version": "22.1.3",
        }
        self.build = self.root / "out/build/runtime-sdk"
        self.build.mkdir(parents=True)
        (self.build / ".fable2-toolchain-v18_2.json").write_text(
            json.dumps(toolchain.cache_identity(self.selection)), encoding="utf-8"
        )
        self.generated = self.build / "CMakeFiles/4.5.0-rc1/CMakeRCCompiler.cmake"
        self.generated.parent.mkdir(parents=True)

    def test_invalid_generated_backslashes_cause_backup_and_fresh_cache(self):
        original = r'set(CMAKE_RC_COMPILER "C:\Program Files\Microsoft Visual Studio\18\VC\llvm-rc.exe")'
        self.generated.write_text(original + "\n", encoding="utf-8")
        backup = toolchain.prepare_cache(self.build, self.selection, root=self.root)
        self.assertIsNotNone(backup)
        self.assertTrue(backup.is_dir())
        self.assertEqual((backup / "CMakeFiles/4.5.0-rc1/CMakeRCCompiler.cmake").read_text(), original + "\n")
        self.assertFalse(self.generated.exists())
        self.assertTrue((self.build / ".fable2-toolchain-v18_2.json").exists())

    def test_forward_slashes_are_a_valid_cached_cmake_path(self):
        self.generated.write_text(
            'set(CMAKE_RC_COMPILER "C:/Program Files/LLVM/bin/llvm-rc.exe")\n', encoding="utf-8"
        )
        backup = toolchain.prepare_cache(self.build, self.selection, root=self.root)
        self.assertIsNone(backup)
        self.assertTrue(self.generated.exists())

    def test_escaped_backslashes_are_a_valid_cached_cmake_path(self):
        self.generated.write_text(
            r'set(CMAKE_RC_COMPILER "C:\\Program Files\\LLVM\\bin\\llvm-rc.exe")' + "\n", encoding="utf-8"
        )
        backup = toolchain.prepare_cache(self.build, self.selection, root=self.root)
        self.assertIsNone(backup)
        self.assertTrue(self.generated.exists())

    def test_rc_path_fix_applies_to_both_cmake_entrypoints(self):
        entrypoint = (ROOT / "build.cmd").read_text(encoding="utf-8")
        runtime = (ROOT / "tools/build_runtime_sdk.cmd").read_text(encoding="utf-8")
        self.assertIn('-DCMAKE_RC_COMPILER:FILEPATH="%FABLE2_RC:\\=/%"', entrypoint)
        self.assertIn('set "NATIVE_RC=%FABLE2_RC:\\=/%"', runtime)
        self.assertIn('-DCMAKE_RC_COMPILER:FILEPATH="%NATIVE_RC%"', runtime)

    @unittest.skipUnless(shutil.which("cmake"), "CMake not available")
    def test_cmake_parser_accepts_forward_slash_resource_compiler(self):
        cmake_file = self.root / "rc-valid.cmake"
        cmake_file.write_text(
            'set(CMAKE_RC_COMPILER "C:/Program Files/Microsoft Visual Studio/18/VC/llvm-rc.exe")\n'
            'if(NOT CMAKE_RC_COMPILER MATCHES "llvm-rc.exe$")\n'
            '  message(FATAL_ERROR "Bad resource compiler path")\n'
            'endif()\n', encoding="utf-8"
        )
        result = subprocess.run([shutil.which("cmake"), "-P", str(cmake_file)],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
