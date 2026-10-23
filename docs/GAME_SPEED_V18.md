# Game speed v18: implementation and validation scope

Changes: a startup guest-clock scalar, explicit PPC timebase routing for non-unity speeds,
exact N/10 clock ratios, inverse host-wait durations, sign-preserving FILETIME waits,
and matching host/guest chrono conversions. At 1.0 the application leaves the normal
clock path alone. It does not rewrite inputs, FPS-cap options or saves.

SDK base pin: babc769a94be5618010abfd075ed84f3c2bc09f5 (in tools/prepare_runtime_sdk.py).
The SDK file's existing BSD copyright notice is preserved when applying source edits.

Primary implementation references, inspected at the pinned revision:
- https://raw.githubusercontent.com/himdo/rexglue-sdk/babc769a94be5618010abfd075ed84f3c2bc09f5/include/rex/chrono/clock.h
- https://raw.githubusercontent.com/himdo/rexglue-sdk/babc769a94be5618010abfd075ed84f3c2bc09f5/src/core/clock.cpp
- https://raw.githubusercontent.com/himdo/rexglue-sdk/babc769a94be5618010abfd075ed84f3c2bc09f5/include/rex/chrono/chrono.h
- https://raw.githubusercontent.com/himdo/rexglue-sdk/babc769a94be5618010abfd075ed84f3c2bc09f5/src/system/xobject.cpp
- https://raw.githubusercontent.com/himdo/rexglue-sdk/babc769a94be5618010abfd075ed84f3c2bc09f5/src/system/xtimer.cpp
- https://raw.githubusercontent.com/himdo/rexglue-sdk/babc769a94be5618010abfd075ed84f3c2bc09f5/src/system/rexruntime.def

The old reciprocal quantization makes 0.6 into 0.625; the new branch preserves tenths.
A guest clock advancing at s units per host unit requires host waits of guest/s.
The negative relative FILETIME path is kept signed before scaling, with saturation.
The four additional x64 MSVC .def exports were cross-checked with clang's Windows target.

The C++ policy/configuration tests compiled locally with g++ 14.2. The real TOML loader
was compiled with the bundled parser and stubbed platform UI/logging. Clock integration
used a controlled SDK double. These are not full SDK/game runtime tests. SDK delta
transforms were exercised on pinned-format fixtures; a full Windows build was not run.

The new full-build helper supports a source ZIP without the top-level .git directory.
It reuses a present SDK, or fetches the exact public pin if absent. Unknown source edits
are refused. XEX input is read only from the user's existing local installation and
validated against the existing game-version catalogue, never distributed in this ZIP.

Installation stages/hashes and backs up native files before replacement, refuses a running
game, and does not kill it. Launcher deployment remains scoped to its exact executable path.
Native and launcher builds must both succeed before deployment begins. Native-file rollback
covers failures within native deployment; later launcher/menu deployment failures are
reported and do not undo an already successful native installation.

In-game behavior is experimental, including menus, audio/video and multiplayer timing.
The selected factor is the requested guest-clock multiplier, not a measured gameplay-speed
guarantee. The game must be restarted to apply a new value. One-time native rebuild required.
