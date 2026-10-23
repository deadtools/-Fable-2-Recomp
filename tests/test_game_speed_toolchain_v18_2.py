"""v18.2 selection/cache regression tests. Synthetic Windows paths and process
results are used: this suite does NOT claim to run cmd.exe or Windows binaries.
The separate v18 native tests compile and execute actual policy/config C++ code.
"""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("tc_v18_2", ROOT / "tools/check_game_speed_toolchain.py")
TC = importlib.util.module_from_spec(spec)
spec.loader.exec_module(TC)


def fake_llvm(directory):
    directory.mkdir(parents=True, exist_ok=True)
    for name in TC.LLVM_TOOLS:
        (directory / name).write_bytes(b"unit fixture only; not an executable")
    return directory / "clang++.exe"


def selection(root, version="22.1.0"):
    return {"cc": str(root / "clang.exe"), "cxx": str(root / "clang++.exe"),
            "rc": str(root / "llvm-rc.exe"), "lld": str(root / "lld-link.exe"),
            "version": version, "vctools_version": "14.51.36231"}


def version_result(version):
    return subprocess.CompletedProcess([], 0, "clang version " + version + "\n", "")


class ToolchainV182Tests(unittest.TestCase):
    def test_discovery_prefers_visual_studio_over_old_standalone(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            vs = fake_llvm(root / "VS/VC/Tools/Llvm/x64/bin")
            old = fake_llvm(root / "ProgramFiles/LLVM/bin")
            env = {"FABLE2_VSROOT": str(root / "VS"), "ProgramFiles": str(root / "ProgramFiles"), "PATH": ""}
            self.assertEqual(TC.discover_llvm(env), [vs.resolve(), old.resolve()])

    def test_discovery_deduplicates_and_respects_custom_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            exe = fake_llvm(root / "custom/bin")
            env = {"FABLE2_LLVM_DIR": str(root / "custom"), "PATH": str(exe.parent), "FABLE2_CXX": str(exe)}
            self.assertEqual(TC.discover_llvm(env), [exe.resolve()])

    def test_empty_path_does_not_search_current_working_directory(self):
        self.assertEqual(TC.discover_llvm({"PATH": ""}), [])

    def test_old_llvm_is_rejected_without_compiling_and_next_one_tested(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); old = fake_llvm(root / "old"); new = fake_llvm(root / "new")
            versions = [version_result("17.0.6"), version_result("22.1.0"), version_result("22.1.0")]
            with patch.object(TC, "run_capture", side_effect=versions), patch.object(TC, "probe") as probe:
                result = TC.choose_llvm([old, new], [])
                self.assertEqual(result["cxx"], str(new))
                probe.assert_called_once()
                self.assertEqual(probe.call_args.args[0], str(new))

    def test_version_alone_is_insufficient_cpp23_probe_failure_tries_next(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); a = fake_llvm(root / "a"); b = fake_llvm(root / "b")
            log = []
            with patch.object(TC, "run_capture", return_value=version_result("22.1.0")), \
                 patch.object(TC, "probe", side_effect=[RuntimeError("missing builtin in MSVC"), None]):
                result = TC.choose_llvm([a, b], log)
            self.assertEqual(result["cxx"], str(b))
            self.assertIn("missing builtin in MSVC", "\n".join(log))

    def test_incomplete_toolchain_is_not_mixed_with_another_llvm(self):
        with tempfile.TemporaryDirectory() as temp:
            exe = fake_llvm(Path(temp) / "llvm")
            (exe.parent / "lld-link.exe").unlink()
            with patch.object(TC, "run_capture") as run:
                with self.assertRaisesRegex(RuntimeError, "No installed LLVM passed"):
                    TC.choose_llvm([exe], [])
                run.assert_not_called()

    def test_mismatched_c_and_cpp_versions_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            exe = fake_llvm(Path(temp))
            with patch.object(TC, "run_capture", side_effect=[version_result("22.1.0"), version_result("20.1.8")]), \
                 patch.object(TC, "probe") as probe:
                with self.assertRaises(RuntimeError): TC.choose_llvm([exe], [])
                probe.assert_not_called()

    def test_no_candidates_produces_specific_install_guidance(self):
        with self.assertRaisesRegex(RuntimeError, r"C\+\+ Clang tools for Windows"):
            TC.choose_llvm([], [])

    def test_probe_uses_cpp23_toml_format_windows_target_and_same_directory(self):
        results = [subprocess.CompletedProcess([], 0, "", ""), subprocess.CompletedProcess([], 0, "probe passed", "")]
        with tempfile.TemporaryDirectory() as temp, patch.object(TC, "run_capture", side_effect=results) as run:
            exe = Path(temp) / "llvm/clang++.exe"
            TC.probe(str(exe), Path(temp), [])
            args = run.call_args_list[0].args[0]
            self.assertIn("-std=c++23", args)
            self.assertIn("--target=x86_64-pc-windows-msvc", args)
            self.assertIn("-fuse-ld=lld", args)
            self.assertIn("-Werror", args)
            self.assertTrue(run.call_args_list[0].kwargs["env"]["PATH"].startswith(str(exe.parent) + os.pathsep))
            content = (Path(temp) / "probe.cpp").read_text()
            self.assertIn("<format>", content)
            self.assertIn("toml::parse", content)
            self.assertIn("std::is_pointer_interconvertible_base_of_v", content)

    def test_environment_file_pins_absolute_compiler_paths_with_crlf(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); data = selection(root / "LLVM with spaces/bin")
            dest = root / "selected.cmd"
            TC.write_selection(data, dest, root)
            text = dest.read_text()
            self.assertIn('set "FABLE2_CXX=' + data["cxx"] + '"', text)
            self.assertIn('set "LLVM=', text)
            self.assertIn('set "FABLE2_TOOLCHAIN_READY=18.2"', text)
            self.assertIn(';%PATH%"', text)
            self.assertNotIn(b"\n", dest.read_bytes().replace(b"\r\n", b""))
            self.assertEqual(json.loads((root / "out/game-speed-toolchain-selection.json").read_text()), data)

    def test_batch_escapes_literal_percent_and_rejects_quote_or_newline(self):
        self.assertEqual(TC.cmd_assignment("NAME", "abc%def"), 'set "NAME=abc%%def"')
        for value in ['broken"path', "one\ntwo", "a\rb", "a\0b"]:
            with self.assertRaises(ValueError): TC.cmd_assignment("NAME", value)

    def test_legacy_cmake_cache_archived_not_deleted(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); output = root / "out/build/win-amd64-release"
            output.mkdir(parents=True); (output / "CMakeCache.txt").write_text("old compiler")
            backup = TC.prepare_cache(output, selection(root), root)
            self.assertIsNotNone(backup)
            self.assertEqual((backup / "CMakeCache.txt").read_text(), "old compiler")
            self.assertFalse((output / "CMakeCache.txt").exists())
            self.assertTrue((output / ".fable2-toolchain-v18_2.json").is_file())

    def test_same_toolchain_retains_incremental_output(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); output = root / "out/build/runtime-sdk"; data = selection(root)
            self.assertIsNone(TC.prepare_cache(output, data, root))
            (output / "object.obj").write_bytes(b"compiled output")
            self.assertIsNone(TC.prepare_cache(output, data, root))
            self.assertEqual((output / "object.obj").read_bytes(), b"compiled output")

    def test_changed_tool_version_archives_objects_even_same_path(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); output = root / "out/build/runtime-sdk"
            TC.prepare_cache(output, selection(root, "20.1.8"), root)
            (output / "object.obj").write_bytes(b"compiled with old version")
            backup = TC.prepare_cache(output, selection(root, "22.1.0"), root)
            self.assertEqual((backup / "object.obj").read_bytes(), b"compiled with old version")
            self.assertFalse((output / "object.obj").exists())

    def test_changed_msvc_toolset_also_invalidates_cache(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); output = root / "out/build/runtime-sdk"; data = selection(root)
            TC.prepare_cache(output, data, root)
            data["vctools_version"] = "14.52.0"
            self.assertIsNotNone(TC.prepare_cache(output, data, root))

    def test_cmake_cache_modified_outside_wrapper_is_detected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); output = root / "out/build/runtime-sdk"; data = selection(root)
            TC.prepare_cache(output, data, root)
            (output / "CMakeCache.txt").write_text("CMAKE_CXX_COMPILER:FILEPATH=" + str(root / "other/clang++.exe"))
            self.assertIsNotNone(TC.prepare_cache(output, data, root))

    def test_cache_guard_cannot_touch_game_or_project_root(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for output in (root, root / "out/build", root / "game", root / "out/build/../../saves"):
                with self.assertRaises(ValueError): TC.prepare_cache(output, selection(root), root)
                self.assertFalse((output / ".fable2-toolchain-v18_2.json").exists())

    def test_scripts_wire_selection_into_both_cmake_builds(self):
        main = (ROOT / "BUILD-GAME-SPEED-LOCAL.cmd").read_text(encoding="utf-8")
        self.assertIn("build v18.2", main)
        self.assertIn("setlocal DisableDelayedExpansion", main)
        self.assertLess(main.index("setup-native-build-env.cmd"), main.index("python -m unittest"))
        setup = (ROOT / "tools/setup-native-build-env.cmd").read_text(encoding="utf-8")
        self.assertNotIn('set "PATH=%ProgramFiles%\\LLVM\\bin;', setup)
        self.assertIn("--emit-env", setup)
        for rel in ("build.cmd", "tools/build_runtime_sdk.cmd"):
            text = (ROOT / rel).read_text(encoding="utf-8")
            self.assertIn("--prepare-cache", text)
            self.assertIn("%FABLE2_CXX%", text)
            self.assertIn("CMAKE_CXX_COMPILER_TARGET=x86_64-pc-windows-msvc", text)
            self.assertNotIn("rmdir /s /q", text[text.index("--prepare-cache"):])

    def test_no_fake_builtins_no_disabled_stl_checks_or_skipped_native_tests(self):
        source = (ROOT / "tools/check_game_speed_toolchain.py").read_text()
        self.assertNotIn("#define __builtin_verbose_trap", source)
        self.assertNotIn("_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH", source)
        self.assertNotIn("-Wno-error", source)
        native = (ROOT / "tests/test_game_speed_v18.py").read_text()
        self.assertIn("*native_flags()", native)
        self.assertIn("Selected FABLE2_CXX is missing", native)
        main = (ROOT / "BUILD-GAME-SPEED-LOCAL.cmd").read_text(encoding="utf-8")
        self.assertIn("-p test_game_speed_v18.py", main)


if __name__ == "__main__":
    unittest.main()
