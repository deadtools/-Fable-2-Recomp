"""Prepare a source-ZIP project for the existing native build, without game edits.
Reuses an existing SDK checkout; if absent, fetches the project's exact public
SDK pin into a temporary directory before making it visible to the build.
Stages only a validated local default.xex for codegen, never other game content.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from prepare_runtime_sdk import SDK_PIN

ROOT = Path(__file__).resolve().parents[1]
SDK_REMOTE = "https://github.com/himdo/rexglue-sdk.git"

def digest(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as file:
        for block in iter(lambda: file.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def stage_xex(root, game, catalogue):
    target = root / "default.xex"
    source = target if target.is_file() else game / "default.xex"
    if not source.is_file():
        raise ValueError("default.xex is missing from both the project and installed game")
    actual = digest(source)
    valid = {v["hash"] for v in catalogue["versions"] if v["compatible"] and v["codeGroup"] == catalogue["codeGroup"]}
    if actual not in valid:
        raise ValueError(f"Unrecognized/unsupported default.xex: {source}; sha256={actual}. Not overwritten.")
    if target.is_file():
        return False
    # Copy, hash-check, then create destination exclusively. Never replace an existing XEX.
    fd, temporary = tempfile.mkstemp(prefix=".game-speed-xex-", dir=root)
    os.close(fd)
    temp = Path(temporary)
    created = False
    try:
        shutil.copyfile(source, temp)
        if digest(temp) != actual:
            raise ValueError("XEX changed during copying; original game is untouched")
        with target.open("xb") as output, temp.open("rb") as input_file:
            created = True
            shutil.copyfileobj(input_file, output)
        if digest(target) != actual:
            raise ValueError("Staged XEX hash mismatch")
    except Exception:
        if created:
            target.unlink(missing_ok=True)
        raise
    finally:
        temp.unlink(missing_ok=True)
    return True

def ensure_sdk(root):
    source = root / "thirdparty/rexglue-sdk"
    if (source / "CMakeLists.txt").is_file():
        print("Using existing SDK source; prepare_runtime_sdk.py will validate its patches")
        return False
    if source.exists() and (not source.is_dir() or any(source.iterdir())):
        raise ValueError(f"Incomplete/nonempty SDK directory retained: {source}. Restore that checkout first.")
    if not shutil.which("git"):
        raise ValueError("Git for Windows is required to fetch the pinned SDK source")
    source.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".speed-sdk-", dir=source.parent) as temporary:
        stage = Path(temporary) / "checkout"
        stage.mkdir()
        def git(*args):
            subprocess.run(["git", "-C", str(stage), *args], check=True)
        git("init")
        git("remote", "add", "origin", SDK_REMOTE)
        git("fetch", "--depth", "1", "origin", SDK_PIN)
        git("checkout", "--detach", "FETCH_HEAD")
        head = subprocess.check_output(["git", "-C", str(stage), "rev-parse", "HEAD"], text=True).strip()
        if head != SDK_PIN or not (stage / "CMakeLists.txt").is_file():
            raise ValueError("Fetched SDK pin/content did not match; source not installed")
        if source.exists():
            source.rmdir()  # Only the initially empty directory; refuses newly added files.
        stage.rename(source)
    print("Pinned SDK source prepared: " + SDK_PIN)
    return True

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path)
    args = parser.parse_args()
    try:
        catalog = json.loads((ROOT / "constants/game_versions.json").read_text(encoding="utf-8"))
        copied = stage_xex(ROOT, args.game.resolve(), catalog)
        print("Codegen XEX: " + ("copied from your installed game" if copied else "existing validated file"))
        ensure_sdk(ROOT)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Native build preparation refused: {error}\n")

if __name__ == "__main__":
    main()
