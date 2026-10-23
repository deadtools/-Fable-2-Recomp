"""Apply the source-only runtime fixes to a known-good SDK revision.

No game content is read. Conflicting SDK edits are rejected, not overwritten.
Use --skip-dependencies for patch validation without network/dependency setup.

Accepted SDK states:
  * HEAD at the base pin - a clean SDK checkout; the patches under
    thirdparty/ are applied on top of it, and
  * any other revision where every patch is already applied - i.e. a fork
    commit with the fixes baked into the tree (no pin list to maintain).
"""
import argparse
from pathlib import Path
import os
import subprocess
import sys
import tempfile

# Clean base revision the patches were generated against.
SDK_PIN = "babc769a94be5618010abfd075ed84f3c2bc09f5"
# Public libmspack commit the SDK's broken pin is repointed at.
MSPACK_PIN = "305907723a4e7ab2018e58040059ffb5e77db837"
PATCHES = (
    "rexglue-sdk-runtime-fixes.patch",
    "rexglue-sdk-debug-exports.patch",
)


def fail(message):
    print(f"prepare_runtime_sdk: error: {message}", file=sys.stderr)
    raise SystemExit(1)


def git(source, *args, check=True):
    result = subprocess.run(["git", "-C", str(source), *args],
                            capture_output=True, text=True)
    if check and result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        fail(f"git {' '.join(args)} failed (exit {result.returncode}):\n{detail}")
    return result


def patch_state(source, patch):
    """Classify the worktree as 'applied', 'clean', or 'conflict' for a patch."""
    if git(source, "apply", "--reverse", "--check", str(patch), check=False).returncode == 0:
        return "applied"
    if git(source, "apply", "--check", str(patch), check=False).returncode == 0:
        return "clean"
    return "conflict"


# The game-speed patch appends additional required exports to rexruntime.def.
# git apply --reverse --check is not a reliable way to recognize the earlier
# debug-exports patch after those additions: its EOF hunk may no longer match.
# A semantic check (and, only for the pinned SDK, conservative reconciliation)
# avoids destructive git resets or overwriting the game-speed exports.
DEBUG_EXPORT_PATCH = "rexglue-sdk-debug-exports.patch"
DEBUG_DEF_RELATIVE = Path("src/system/rexruntime.def")


def required_debug_exports(patch):
    """Read the exact export requirements from the bundled upstream patch."""
    body = patch.read_text(encoding="utf-8")
    if body.count("diff --git ") != 1 or (
        "diff --git a/src/system/rexruntime.def b/src/system/rexruntime.def" not in body
    ) or "-LIBRARY rexruntime" not in body:
        raise ValueError("Unrecognized debug-exports patch; source retained")
    exports = tuple(
        line[1:] for line in body.splitlines()
        if line.startswith("+") and not line.startswith("+++")
    )
    if len(exports) != 5 or len(set(exports)) != 5 or not all(
        item.startswith("?") for item in exports
    ):
        raise ValueError("Unexpected debug-exports patch contents; source retained")
    return exports


def inspect_debug_exports(source, patch):
    """Validate the real SDK .def; accept extra game-speed symbols unchanged.

    The pinned original rexruntime.def contains a large EXPORTS table.  A
    structurally unrelated or truncated file must not be rewritten blindly.
    """
    path = source / DEBUG_DEF_RELATIVE
    raw = path.read_bytes()
    if b"\r\n" in raw and b"\n" in raw.replace(b"\r\n", b""):
        raise ValueError("Mixed newline styles in rexruntime.def; not overwritten")
    bom = raw.startswith(b"\xef\xbb\xbf")
    lines = raw.decode("utf-8-sig").splitlines()
    meaningful = [(i, line.strip()) for i, line in enumerate(lines)
                  if line.strip() and not line.lstrip().startswith(";")]
    if not meaningful:
        raise ValueError("Empty rexruntime.def; not overwritten")
    first = meaningful[0][1]
    if first == "LIBRARY rexruntime":
        remove_line = meaningful[0][0]
        expected_export_index = 1
    elif first == "EXPORTS":
        remove_line = None
        expected_export_index = 0
    else:
        raise ValueError("Unexpected rexruntime.def header; not overwritten")
    if len(meaningful) <= expected_export_index or (
        meaningful[expected_export_index][1] != "EXPORTS"
    ) or sum(line.strip() == "EXPORTS" for line in lines) != 1:
        raise ValueError("Invalid rexruntime.def EXPORTS section; not overwritten")
    if any(line.strip().startswith("LIBRARY ") for i, line in enumerate(lines)
           if i != remove_line):
        raise ValueError("Unexpected LIBRARY directive; not overwritten")
    existing = [line.strip() for line in lines
                if line.strip() and line.strip() not in ("LIBRARY rexruntime", "EXPORTS")
                and not line.lstrip().startswith(";")]
    # Two sentinel entries and a substantial table confirm the original layout;
    # guard against accidentally modifying a different .def or a short stub.
    if (len(existing) < 300 or
        not any(e.startswith("??0AchievementToastDialog@ui@rex@@") for e in existing) or
        "vmaGetAllocationInfo" not in existing):
        raise ValueError("Unrecognized rexruntime.def exports; not overwritten")
    required = required_debug_exports(patch)
    if any(existing.count(name) > 1 for name in required):
        raise ValueError("Duplicate debug symbol in rexruntime.def; not overwritten")
    missing = [name for name in required if name not in existing]
    return path, raw, lines, bom, remove_line, missing


def debug_exports_applied(source, patch):
    """Semantic 'already applied' check, independent of patch EOF position."""
    try:
        _path, _raw, _lines, _bom, remove_line, missing = inspect_debug_exports(
            source, patch
        )
    except (OSError, UnicodeError, ValueError):
        return False
    return remove_line is None and not missing


def reconcile_debug_exports(source, patch):
    """Safely finish a partially applied debug patch on the pinned SDK only.

    Never resets the worktree, discards unrelated exports, or touches any file
    other than rexruntime.def. The old bytes are saved exactly once.
    """
    path, raw, lines, bom, remove_line, missing = inspect_debug_exports(source, patch)
    if remove_line is None and not missing:
        return False
    if remove_line is not None:
        del lines[remove_line]
    newline = "\r\n" if b"\r\n" in raw else "\n"
    # Retain every existing export, including any v18 game-speed symbols.
    lines.extend(missing)
    result = (b"\xef\xbb\xbf" if bom else b"") + (
        newline.join(lines).rstrip("\r\n") + newline
    ).encode("utf-8")
    backup = path.with_name(path.name + ".before-debug-exports-repair.bak")
    if not backup.exists():
        with backup.open("xb") as handle:
            handle.write(raw)
    fd, temporary = tempfile.mkstemp(prefix=".rexruntime-exports-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(result)
        os.chmod(temporary, path.stat().st_mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    if not debug_exports_applied(source, patch):
        raise ValueError("Repaired export list failed validation")
    return True


def libmspack_mode(source):
    """Return the mode of thirdparty/libmspack at HEAD ('160000' submodule
    gitlink, '040000' vendored plain files), or None if absent."""
    result = git(source, "ls-tree", "HEAD", "--", "thirdparty/libmspack", check=False)
    if result.returncode != 0 or not result.stdout.strip():
        return None
    return result.stdout.split()[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--skip-dependencies", action="store_true")
    args = parser.parse_args()
    source = args.source.resolve()
    patch_directory = Path(__file__).resolve().parents[1] / "thirdparty"

    if not source.is_dir():
        fail(f"SDK source directory does not exist: {source}")
    if git(source, "rev-parse", "--is-inside-work-tree", check=False).returncode != 0:
        fail(f"{source} is not a git work tree; run "
             "'git submodule update --init thirdparty/rexglue-sdk' from the repo root.")

    head = git(source, "rev-parse", "HEAD", check=False).stdout.strip()
    if not head:
        fail(f"{source} has no commits (empty SDK checkout).")

    # Keep follow-up fixes separate so existing patched SDK checkouts can upgrade.
    states = {}
    for name in PATCHES:
        patch = patch_directory / name
        if not patch.is_file():
            fail(f"patch file is missing: {patch}")
        if name == DEBUG_EXPORT_PATCH and debug_exports_applied(source, patch):
            states[name] = "applied"
        else:
            states[name] = patch_state(source, patch)

    if head == SDK_PIN:
        # Base revision: apply whatever is missing, refuse conflicting edits.
        for name, state in states.items():
            if state == "applied":
                print(f"Already applied: {name}")
            elif state == "clean":
                git(source, "apply", str(patch_directory / name))
                print(f"Applied: {name}")
            elif name == DEBUG_EXPORT_PATCH:
                try:
                    updated = reconcile_debug_exports(source, patch_directory / name)
                except (OSError, UnicodeError, ValueError) as error:
                    fail(f"SDK patch {name} requires manual review: {error}\n"
                         "No SDK files were discarded. Do NOT run git clean/reset; "
                         "preserve your source tree and share this error log.")
                print("Reconciled debug exports (preserved game-speed symbols)" if updated
                      else f"Already applied: {name}")
            else:
                detail = git(source, "apply", "--check",
                             str(patch_directory / name), check=False).stderr.strip()
                fail(f"SDK patch {name} conflicts with local edits:\n{detail}\n"
                     "No SDK files were discarded. Do NOT run git clean/reset; "
                     "preserve your source tree and share this error log.")
    else:
        # Any other revision is accepted only with every patch baked in.
        pending = [name for name, state in states.items() if state != "applied"]
        if pending:
            fail(f"unexpected SDK revision {head} with unapplied patches: "
                 f"{', '.join(pending)}.\n"
                 f"Either check out the base revision ({SDK_PIN}) so the patches "
                 f"can be applied, or use a fork revision that bakes them in.\n"
                 f"Restore the base with: git -C {source} checkout {SDK_PIN}")
        print(f"SDK revision {head} already carries all runtime fixes (baked in).")

    if not args.skip_dependencies:
        # libmspack is either a submodule gitlink (base pin and older fork
        # commits) or plain vendored files (newer fork commits). Only the
        # gitlink form needs the index repin + submodule fetch + Windows
        # symlink materialization; the original SDK pin for it is unavailable
        # on its public remote.
        mode = libmspack_mode(source)
        if mode == "160000":
            # git submodule update reads the index, not the patched worktree gitlink.
            git(source, "update-index", "--cacheinfo",
                f"160000,{MSPACK_PIN},thirdparty/libmspack")
        elif mode != "040000":
            fail("thirdparty/libmspack is missing from the SDK tree at HEAD "
                 "(expected a submodule gitlink or vendored files).")
        result = subprocess.run(["git", "-C", str(source), "submodule", "update",
                                 "--init", "--recursive"])
        if result.returncode != 0:
            fail("SDK submodule update failed; a fresh clone needs network "
                 "access to fetch the SDK submodules.")
        if mode == "160000":
            result = subprocess.run([sys.executable,
                                     str(Path(__file__).with_name("prepare_renderer_mspack.py")),
                                     str(source / "thirdparty" / "libmspack")])
            if result.returncode != 0:
                fail("prepare_renderer_mspack.py failed.")
        else:
            # The SDK's thirdparty/CMakeLists.txt treats a dependency as
            # initialized when <dep>/.git exists. Vendored libmspack has no
            # .git, so drop an empty marker directory (invisible to git
            # status) to satisfy that check.
            marker = source / "thirdparty" / "libmspack" / ".git"
            if not marker.exists():
                marker.mkdir(parents=True, exist_ok=True)


if __name__ == "__main__":
    main()
