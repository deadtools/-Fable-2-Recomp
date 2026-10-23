"""Regression tests for safe, idempotent ReXGlue debug-export reconciliation.

Runs against an isolated mock SDK checkout; never reads or modifies an actual
installed SDK, and never invokes git clean/reset.
"""
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools" / "prepare_runtime_sdk.py"
PATCH = ROOT / "thirdparty" / "rexglue-sdk-debug-exports.patch"

spec = importlib.util.spec_from_file_location("prepare_runtime_sdk", SCRIPT)
pr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pr)

SPEED_EXPORTS = [
    "?set_guest_time_scalar@Clock@chrono@rex@@SAXN@Z",
    "?guest_time_scalar@Clock@chrono@rex@@SANXZ",
    "?guest_tick_ratio@Clock@chrono@rex@@SA?AU?$pair@_K_K@std@@XZ",
    "?FLAGS_clock_no_scaling_storage_@@YAAEA_NXZ",
]


def command(*args, cwd=None):
    return subprocess.run(args, cwd=cwd, capture_output=True, text=True, check=True)


def original_def():
    return "\n".join([
        "LIBRARY rexruntime",
        "EXPORTS",
        "??0AchievementToastDialog@ui@rex@@QEAA@PEAVImGuiDrawer@12@PEAVImmediateDrawer@12@PEAVRuntime@2@@Z",
        "??0AchievementsOverlayDialog@ui@rex@@QEAA@PEAVImGuiDrawer@12@PEAVImmediateDrawer@12@PEAVRuntime@2@PEAVAchievementManager@system@2@@Z",
        *["?FakeExport%03d@@YAXXZ" % i for i in range(469)],
        "vmaDestroyAllocator",
        "vmaDestroyImage",
        "vmaGetAllocationInfo",
    ]) + "\n"


class PrepareRuntimeSDKTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.sdk = Path(self.tmp.name) / "sdk"
        path = self.sdk / "src/system/rexruntime.def"
        path.parent.mkdir(parents=True)
        path.write_text(original_def(), encoding="utf-8")
        command("git", "init", "-q", str(self.sdk))
        command("git", "config", "user.email", "test@example.com", cwd=self.sdk)
        command("git", "config", "user.name", "SDK Test", cwd=self.sdk)
        command("git", "add", "--", "src/system/rexruntime.def", cwd=self.sdk)
        command("git", "commit", "-qm", "SDK base", cwd=self.sdk)
        self.pin = command("git", "rev-parse", "HEAD", cwd=self.sdk).stdout.strip()
        self.def_path = path

    def launch(self, pinned=True):
        with patch.object(pr, "SDK_PIN", self.pin if pinned else "0" * 40), \
             patch.object(pr, "PATCHES", (pr.DEBUG_EXPORT_PATCH,)), \
             patch.object(sys, "argv", [str(SCRIPT), str(self.sdk), "--skip-dependencies"]):
            captured = io.StringIO()
            with redirect_stdout(captured), redirect_stderr(captured):
                pr.main()
        return captured.getvalue()

    def test_original_sdk_patch_keeps_tree_intact(self):
        self.assertTrue(PATCH.exists())
        self.launch()
        self.assertTrue(pr.debug_exports_applied(self.sdk, PATCH))
        self.assertNotIn("LIBRARY rexruntime", self.def_path.read_text())
        self.assertFalse((self.def_path.with_name("rexruntime.def.before-debug-exports-repair.bak")).exists())

    def test_game_speed_exports_after_debug_patch_are_accepted_without_writing(self):
        command("git", "apply", str(PATCH), cwd=self.sdk)
        with self.def_path.open("a", encoding="utf-8") as handle:
            handle.write("\n".join(SPEED_EXPORTS) + "\n")
        before = self.def_path.read_bytes()
        output = self.launch()
        self.assertIn("Already applied", output)
        self.assertEqual(before, self.def_path.read_bytes())
        self.assertTrue(pr.debug_exports_applied(self.sdk, PATCH))

    def test_speed_exports_present_before_debug_patch_are_retained(self):
        with self.def_path.open("a", encoding="utf-8") as handle:
            handle.write("\n".join(SPEED_EXPORTS) + "\n")
        self.launch()
        contents = self.def_path.read_text()
        self.assertTrue(pr.debug_exports_applied(self.sdk, PATCH))
        self.assertTrue(all(contents.count(item) == 1 for item in SPEED_EXPORTS))
        self.assertEqual(contents.count("vmaGetAllocationInfo"), 1)
        before = self.def_path.read_bytes()
        self.launch()
        self.assertEqual(before, self.def_path.read_bytes())

    def test_partial_patch_completed_atomically_and_idempotent(self):
        original = self.def_path.read_text()
        debug_exports = pr.required_debug_exports(PATCH)
        partial = original.replace("LIBRARY rexruntime\n", "")
        partial += "\n".join(debug_exports[:2] + tuple(SPEED_EXPORTS)) + "\n"
        self.def_path.write_text(partial, encoding="utf-8")
        self.launch()
        new_text = self.def_path.read_text()
        self.assertTrue(pr.debug_exports_applied(self.sdk, PATCH))
        self.assertTrue(all(new_text.count(e) == 1 for e in (*debug_exports, *SPEED_EXPORTS)))
        backup = self.def_path.with_name("rexruntime.def.before-debug-exports-repair.bak")
        self.assertEqual(backup.read_text(), partial)
        first = self.def_path.read_bytes()
        self.launch()
        self.assertEqual(first, self.def_path.read_bytes())
        self.assertEqual(backup.read_text(), partial)

    def test_crlf_and_utf8_bom_preserved(self):
        content = original_def() + "\n".join(SPEED_EXPORTS) + "\n"
        original = b"\xef\xbb\xbf" + content.replace("\n", "\r\n").encode("utf-8")
        self.def_path.write_bytes(original)
        self.launch()
        patched = self.def_path.read_bytes()
        self.assertTrue(patched.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\n", patched.replace(b"\r\n", b""))
        self.assertTrue(pr.debug_exports_applied(self.sdk, PATCH))
        self.assertEqual(self.def_path.with_name("rexruntime.def.before-debug-exports-repair.bak").read_bytes(), original)

    def test_unrecognized_file_is_not_overwritten(self):
        bad = b"LIBRARY something_else\nEXPORTS\nunknown\n"
        self.def_path.write_bytes(bad)
        with self.assertRaises(SystemExit):
            self.launch()
        self.assertEqual(self.def_path.read_bytes(), bad)

    def test_unknown_revision_is_not_rewritten(self):
        extra = "\n".join(SPEED_EXPORTS) + "\n"
        self.def_path.write_text(original_def() + extra, encoding="utf-8")
        data = self.def_path.read_bytes()
        with self.assertRaises(SystemExit):
            self.launch(pinned=False)
        self.assertEqual(self.def_path.read_bytes(), data)


if __name__ == "__main__":
    unittest.main(verbosity=2)
