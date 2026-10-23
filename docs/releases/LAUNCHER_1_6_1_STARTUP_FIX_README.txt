FABLE II RECOMPILED — LAUNCHER 1.6.1 STARTUP FIX
Modifications: deadtools, https://github.com/deadtools

SYMPTOM: After installing the EN/RU language patch, the launcher window does
not open. The most likely cause is the immediate recursive WPF tree translation
performed during initial construction of MainWindow. The precise user-side
exception has not been captured yet.

CHANGES:
* Localization now runs after Window.Loaded, never during MainWindow construction.
* Translation traverses only declared WPF element and inline collections,
  without traversing the internal visual/template tree.
* Changes to UI collections are safe: child lists are snapshotted first.
* Exceptions during translation are caught and logged; launcher remains usable.
* Early WPF startup failures are written to:
  %LOCALAPPDATA%\Fable2Launcher\launcher-startup-error.log
* EN remains the default; language choice is saved; RU stays selectable.
* All game, GPU, texture and DualShock settings remain unchanged.

INSTALL:
1. CLOSE Fable2Launcher.exe (if running).
2. Unzip the overlay into the SOURCE project folder, replacing files:
   D:\Fable2\Fable-2-Recomp-1.5.0-texturediag-v1\
3. Run BUILD-LAUNCHER-LOCAL.cmd.
4. This rebuilds and deploys only the launcher to:
   D:\Fable2\Fable 2 1.5.0\Fable2Launcher.exe
5. Run launcher; verify EN / RU dropdown.

EMERGENCY RESTORE (no rebuild):
  RESTORE-LAUNCHER-BACKUP.cmd
  This restores D:\Fable2\Fable 2 1.5.0\Fable2Launcher.exe.previous.bak
  if it exists. It stops only the launcher running from the target path.

IF IT STILL DOES NOT OPEN:
  Send launcher-startup-error.log from the path above, along with the
  last 10-20 lines printed by BUILD-LAUNCHER-LOCAL.cmd.

TEST STATUS: static XAML checks and source-level startup checks passed;
Windows WPF startup and runtime switching require user-side verification.
