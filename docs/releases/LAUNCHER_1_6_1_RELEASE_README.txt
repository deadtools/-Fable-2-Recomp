Fable II Recompiled Launcher 1.6.1 — deadtools
===============================================

Изменения версии 1.6.1:
  + Переключатель языка интерфейса EN / RU (английский и русский)

Сохранены нововведения 1.6.0:
  + Исправление текстур героя и собаки
  + Переключение Xbox / PlayStation (DualShock)
  + Регулировка скорости игры 0.1–2.0×

Автор доработок: deadtools — https://github.com/deadtools

УСТАНОВКА И СБОРКА
-----------------
1. Распакуйте ZIP в корень исходников Fable II Recompiled, заменив файлы.
2. Запустите BUILD-LAUNCHER-LOCAL.cmd.
3. После сборки файл будет здесь, относительно корня исходников:

   out\texture-diagnostics-launcher\Fable2Launcher.exe

4. Закройте работающий лаунчер игры. Вручную скопируйте собранный
   Fable2Launcher.exe в папку установленной игры с заменой старого файла.

BUILD-LAUNCHER-LOCAL.cmd больше не копирует EXE автоматически и НЕ изменяет
банки текстур в игровой папке. Путь к установленной игре у каждого свой.
После завершения окно CMD остаётся открытым до нажатия клавиши.
Для вызова из общего скрипта сборки сохранён флаг --build-only.

English:
Launcher 1.6.1 adds EN/RU language switching to the existing 1.6 features.
Run BUILD-LAUNCHER-LOCAL.cmd; the output EXE is located at
out\texture-diagnostics-launcher\Fable2Launcher.exe.
Close the current game launcher, then manually copy the built EXE into your
own game installation folder and replace the previous Fable2Launcher.exe.
No automatic deployment and no modification of game data files.

NOTE: This overlay updates the launcher source; a Windows .NET 8 build is
needed to generate the executable. The game itself does not need recompilation.
