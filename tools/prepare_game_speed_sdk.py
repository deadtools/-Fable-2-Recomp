"""Add exact tenths to the SDK guest-clock ratio before compiling the runtime.
Recognizes the existing ReXGlue clock implementation; never guesses offsets.
Creates a one-time original copy and performs an atomic, idempotent source edit.
"""
import argparse
from pathlib import Path
import os
import re
import tempfile

MARKER = "FABLE2_GAME_SPEED_EXACT_TENTHS_V18"
OLD = """  if (guest_time_scalar_ > 1.0) {
    frac.first *= static_cast<uint64_t>(guest_time_scalar_ * 10.0);
    frac.second *= 10;
  } else {
    frac.first *= 10;
    frac.second *= static_cast<uint64_t>(10.0 / guest_time_scalar_);
  }"""
NEW = """  // FABLE2_GAME_SPEED_EXACT_TENTHS_V18
  // Values from the launcher are N/10. Preserve that exact rational ratio:
  // the old reciprocal truncation turned e.g. 0.6 into 10/16 = 0.625.
  const double requested_tenths = guest_time_scalar_ * 10.0;
  if (guest_time_scalar_ >= 0.1 && guest_time_scalar_ <= 2.0) {
    const auto tenths = static_cast<uint64_t>(requested_tenths + 0.5);
    const double error = requested_tenths - static_cast<double>(tenths);
    if (error > -1.0e-8 && error < 1.0e-8) {
      frac.first *= tenths;
      frac.second *= 10;
    } else if (guest_time_scalar_ > 1.0) {
      frac.first *= static_cast<uint64_t>(guest_time_scalar_ * 10.0);
      frac.second *= 10;
    } else {
      frac.first *= 10;
      frac.second *= static_cast<uint64_t>(10.0 / guest_time_scalar_);
    }
  } else if (guest_time_scalar_ > 1.0) {
    frac.first *= static_cast<uint64_t>(guest_time_scalar_ * 10.0);
    frac.second *= 10;
  } else {
    frac.first *= 10;
    frac.second *= static_cast<uint64_t>(10.0 / guest_time_scalar_);
  }"""

def transform(source: str) -> str:
    if MARKER in source:
        if NEW.lstrip() not in source:
            raise ValueError("Modified/incomplete v18 SDK clock patch; not overwritten")
        return transform_durations(source)
    start = source.find("void RecomputeGuestTickScalar() {")
    end = source.find("reduce_fraction(frac);", start)
    if start < 0 or end < 0:
        raise ValueError("Unsupported SDK clock source; original file retained")
    body = source[start:end]
    # Permit indentation / line-ending changes, not different statements.
    pattern = r"\s+".join(re.escape(x) for x in OLD.split())
    matches = list(re.finditer(pattern, body))
    if len(matches) != 1:
        raise ValueError("Unknown SDK clock ratio implementation; original retained")
    match = matches[0]
    # Pattern starts at 'if', leaving the source line's indentation intact.
    return transform_durations(source[:start + match.start()] + NEW.lstrip() + source[start + match.end():])


DURATION_MARKER = "FABLE2_GAME_SPEED_WAIT_SCALING_V18"
OLD_DURATION_FUNCTIONS = [
"""uint32_t Clock::ScaleGuestDurationMillis(uint32_t guest_ms) {
  if (REXCVAR_GET(clock_no_scaling)) {
    return guest_ms;
  }
  constexpr uint64_t max = std::numeric_limits<uint32_t>::max();
  if (guest_ms >= max) {
    return max;
  } else if (!guest_ms) {
    return 0;
  }
  uint64_t scaled_ms =
      static_cast<uint64_t>((static_cast<uint64_t>(guest_ms) * guest_time_scalar_));
  return static_cast<uint32_t>(std::min(scaled_ms, max));
}""",
"""int64_t Clock::ScaleGuestDurationFileTime(int64_t guest_file_time) {
  if (REXCVAR_GET(clock_no_scaling)) {
    return static_cast<uint64_t>(guest_file_time);
  }
  if (!guest_file_time) {
    return 0;
  } else if (guest_file_time > 0) {
    // Absolute time.
    uint64_t guest_time = Clock::QueryGuestSystemTime();
    int64_t relative_time = guest_file_time - static_cast<int64_t>(guest_time);
    int64_t scaled_time = static_cast<int64_t>(relative_time * guest_time_scalar_);
    return static_cast<int64_t>(guest_time) + scaled_time;
  } else {
    // Relative time.
    uint64_t scaled_file_time =
        static_cast<uint64_t>((static_cast<uint64_t>(guest_file_time) * guest_time_scalar_));
    // TODO(benvanik): check for overflow?
    return scaled_file_time;
  }
}""",
"""void Clock::ScaleGuestDurationTimeval(int32_t* tv_sec, int32_t* tv_usec) {
  if (REXCVAR_GET(clock_no_scaling)) {
    return;
  }
  uint64_t scaled_sec = static_cast<uint64_t>(static_cast<uint64_t>(*tv_sec) * guest_time_scalar_);
  uint64_t scaled_usec =
      static_cast<uint64_t>(static_cast<uint64_t>(*tv_usec) * guest_time_scalar_);
  if (scaled_usec > std::numeric_limits<uint32_t>::max()) {
    uint64_t overflow_sec = scaled_usec / 1000000;
    scaled_usec -= overflow_sec * 1000000;
    scaled_sec += overflow_sec;
  }
  *tv_sec = int32_t(scaled_sec);
  *tv_usec = int32_t(scaled_usec);
}"""
]
NEW_DURATION_FUNCTIONS = [
"""uint32_t Clock::ScaleGuestDurationMillis(uint32_t guest_ms) {
  // FABLE2_GAME_SPEED_WAIT_SCALING_V18: guest duration -> host duration.
  // A slower clock requires LONGER host waits, not shorter waits.
  if (REXCVAR_GET(clock_no_scaling) || guest_time_scalar_ == 1.0) return guest_ms;
  constexpr uint32_t infinite = std::numeric_limits<uint32_t>::max();
  if (!guest_ms || guest_ms == infinite) return guest_ms;
  const double scaled = std::ceil(static_cast<double>(guest_ms) / guest_time_scalar_);
  // A finite wait must never become the INFINITE sentinel.
  if (scaled >= static_cast<double>(infinite - 1)) return infinite - 1;
  return static_cast<uint32_t>(std::max(1.0, scaled));
}""",
"""int64_t Clock::ScaleGuestDurationFileTime(int64_t guest_file_time) {
  // Preserve sign: negative FILETIME values are relative waits, not unsigned dates.
  if (REXCVAR_GET(clock_no_scaling) || guest_time_scalar_ == 1.0 || !guest_file_time)
    return guest_file_time;
  double scaled;
  if (guest_file_time < 0) {
    scaled = std::floor(static_cast<double>(guest_file_time) / guest_time_scalar_);
  } else {
    const double remaining = static_cast<double>(guest_file_time) -
        static_cast<double>(Clock::QueryGuestSystemTime());
    scaled = static_cast<double>(Clock::QueryHostSystemTime()) + remaining / guest_time_scalar_;
  }
  if (scaled >= static_cast<double>(std::numeric_limits<int64_t>::max()))
    return std::numeric_limits<int64_t>::max();
  if (scaled <= static_cast<double>(std::numeric_limits<int64_t>::min()))
    return std::numeric_limits<int64_t>::min();
  return static_cast<int64_t>(scaled);
}""",
"""void Clock::ScaleGuestDurationTimeval(int32_t* tv_sec, int32_t* tv_usec) {
  if (REXCVAR_GET(clock_no_scaling) || guest_time_scalar_ == 1.0) return;
  // Preserve invalid/sentinel negative timeouts for the caller to handle.
  if (*tv_sec < 0 || *tv_usec < 0) return;
  const double total = static_cast<double>(*tv_sec) * 1000000.0 + *tv_usec;
  const double limit = static_cast<double>(std::numeric_limits<int32_t>::max()) * 1000000.0 + 999999.0;
  const uint64_t micros = static_cast<uint64_t>(std::min(limit, std::ceil(total / guest_time_scalar_)));
  *tv_sec = static_cast<int32_t>(micros / 1000000);
  *tv_usec = static_cast<int32_t>(micros % 1000000);
}"""
]

CHRONO_MARKER = "FABLE2_GAME_SPEED_CLOCK_CAST_V18"
CHRONO_OLD_GUEST = "std::chrono::floor<WClock_::duration>(delta * rex::chrono::Clock::guest_time_scalar())"
CHRONO_OLD_HOST = "std::chrono::floor<WClock_::duration>(delta / rex::chrono::Clock::guest_time_scalar())"
CHRONO_NEW_GUEST = "std::chrono::floor<WClock_::duration>(delta / rex::chrono::Clock::guest_time_scalar()) /* FABLE2_GAME_SPEED_CLOCK_CAST_V18 guest-to-host */"
CHRONO_NEW_HOST = "std::chrono::floor<WClock_::duration>(delta * rex::chrono::Clock::guest_time_scalar()) /* FABLE2_GAME_SPEED_CLOCK_CAST_V18 host-to-guest */"

def _canonical(text):
    return re.sub(r"\s+", " ", text).strip()

def transform_durations(source):
    if DURATION_MARKER in source:
        if any(new not in source for new in NEW_DURATION_FUNCTIONS):
            raise ValueError("Incomplete v18 duration patch; source retained")
        return source
    for old, new in zip(OLD_DURATION_FUNCTIONS, NEW_DURATION_FUNCTIONS):
        signature = old.split("{", 1)[0].strip()
        at = source.find(signature)
        if at < 0: raise ValueError("Missing SDK duration function: " + signature)
        end = source.find("{", at)
        depth = 1; end += 1
        while end < len(source) and depth:
            if source[end] == "{": depth += 1
            elif source[end] == "}": depth -= 1
            end += 1
        if depth or _canonical(source[at:end]) != _canonical(old):
            raise ValueError("Unrecognized SDK duration implementation; not overwritten: " + signature)
        source = source[:at] + new + source[end:]
    if "#include <cmath>" not in source:
        if "#include <algorithm>" not in source: raise ValueError("Unknown SDK include layout")
        source = source.replace("#include <algorithm>", "#include <algorithm>\n#include <cmath>", 1)
    return source

def transform_chrono(source):
    if CHRONO_MARKER in source:
        if CHRONO_NEW_GUEST not in source or CHRONO_NEW_HOST not in source:
            raise ValueError("Incomplete v18 clock-cast patch; source retained")
        return source
    if source.count(CHRONO_OLD_GUEST) != 1 or source.count(CHRONO_OLD_HOST) != 1:
        raise ValueError("Unrecognized SDK guest/host clock conversion; source retained")
    return source.replace(CHRONO_OLD_GUEST, "FABLE2_CAST_PLACEHOLDER").replace(
        CHRONO_OLD_HOST, CHRONO_NEW_HOST).replace("FABLE2_CAST_PLACEHOLDER", CHRONO_NEW_GUEST)


# MSVC x64 names verified with clang's x86_64-pc-windows-msvc target.
# The runtime uses an explicit .def list; declaring Clock APIs in a header
# alone is not sufficient to call them from the separately linked game EXE.
SPEED_EXPORTS = (
    "?set_guest_time_scalar@Clock@chrono@rex@@SAXN@Z",
    "?guest_time_scalar@Clock@chrono@rex@@SANXZ",
    "?guest_tick_ratio@Clock@chrono@rex@@SA?AU?$pair@_K_K@std@@XZ",
    "?FLAGS_clock_no_scaling_storage_@@YAAEA_NXZ",
)

# v18.6: fable2.exe also imports two SDK methods not present in the original
# rexruntime.def.  Both already have implementations in the pinned SDK:
#   src/core/cvar.cpp      rex::cvar::ListFlags()
#   src/system/xmemory.cpp rex::memory::BaseHeap::QueryRegionInfo(...)
# MSVC x64 decorated names cross-checked with clang -target x86_64-pc-windows-msvc.
# Exporting the EXISTING methods keeps the address-scan hook and cvar diagnostics
# working; replacing them with no-op stubs would silently change game behavior.
RECOMP_LINK_EXPORTS = (
    "?ListFlags@cvar@rex@@YA?AV?$vector@V?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@V?$allocator@V?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@2@@std@@XZ",
    "?QueryRegionInfo@BaseHeap@memory@rex@@QEAA_NIPEAUHeapAllocationInfo@23@@Z",
)

REQUIRED_EXPORTS = SPEED_EXPORTS + RECOMP_LINK_EXPORTS

def transform_exports(source):
    if not re.search(r"^\s*EXPORTS\s*$", source, re.MULTILINE):
        raise ValueError("SDK runtime export list is missing/unsupported; not overwritten")
    existing = [line.strip() for line in source.splitlines()]
    for name in REQUIRED_EXPORTS:
        if existing.count(name) > 1:
            raise ValueError("Duplicate SDK runtime export: " + name)
    known = set(existing)
    missing = [name for name in REQUIRED_EXPORTS if name not in known]
    return source if not missing else source.rstrip() + "\n" + "\n".join(missing) + "\n"

def apply(sdk: Path) -> bool:
    edits = []
    for relative, transform_fn in (("src/core/clock.cpp", transform), ("include/rex/chrono/chrono.h", transform_chrono), ("src/system/rexruntime.def", transform_exports)):
        path = sdk / relative
        raw = path.read_bytes()
        newline = "\r\n" if b"\r\n" in raw else "\n"
        source = raw.decode("utf-8").replace("\r\n", "\n")
        result = transform_fn(source)
        if result != source:
            edits.append((path, raw, result.replace("\n", newline).encode("utf-8")))
    # Validate ALL files before modifying any of them.
    for path, raw, _ in edits:
        backup = path.with_suffix(path.suffix + ".before-speed-v18.bak")
        if not backup.exists():
            with backup.open("xb") as output: output.write(raw)
    committed = []
    def atomic_write(path, content):
        fd, temp = tempfile.mkstemp(prefix=".clock-v18-", dir=path.parent)
        try:
            with os.fdopen(fd,"wb") as output: output.write(content)
            os.replace(temp,path)
        finally:
            if os.path.exists(temp): os.unlink(temp)
    try:
        for path, raw, new in edits:
            atomic_write(path,new)
            committed.append((path,raw))
    except Exception:
        for path, raw in reversed(committed): atomic_write(path,raw)
        raise
    return bool(edits)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sdk", type=Path)
    args = parser.parse_args()
    try:
        changed = apply(args.sdk.resolve())
    except (OSError, ValueError, UnicodeError) as error:
        parser.exit(1, f"Game-speed SDK patch refused: {error}\n")
    print("Game-speed clock/timeout patch: " + ("applied" if changed else "already applied"))
