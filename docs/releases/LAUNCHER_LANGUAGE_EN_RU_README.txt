Fable II Recompiled 1.6.0 — EN/RU launcher localization overlay
Modifications by deadtools: https://github.com/deadtools

INSTALLATION
1. Close Fable2Launcher.exe.
2. Extract this ZIP over your Fable-2-Recomp-1.5.0-texturediag-v1 source folder,
   preserving paths and allowing file replacement.
3. Run BUILD-LAUNCHER-LOCAL.cmd.
4. The script builds and installs Fable2Launcher.exe to
   D:\Fable2\Fable 2 1.5.0\Fable2Launcher.exe and waits for a key press.

LANGUAGES
* EN (English) is the default on a fresh installation.
* RU (Russian) can be selected from the top-right language selector.
* Switching updates the main page, Advanced Options, status messages, extraction UI,
  GPU/texture settings, controller prompts descriptions and release notes without restart.
* Selected language persists in the user's local AppData:
  %LOCALAPPDATA%\Fable2Launcher\launcher-language.txt
* Language selection affects the launcher UI ONLY. Game dialog language,
  content, controller mapping and game-speed settings are unchanged.

This ZIP contains SOURCE FILES ONLY. It does not replace a built EXE,
ReXGlue runtime, or game resources. The launcher rebuild must complete on Windows.

Automated checks: static XAML/translation coverage and pure-language tests
linked into Fable2.Launcher.ConfigTests. WPF visual testing on Windows required.
