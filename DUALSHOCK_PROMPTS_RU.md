# Fable II DualShock prompts — v14 live-bank repair

Этот overlay исправляет ситуацию, когда старый патчер поменял обычные подсказки,
но зелёная A в паузе осталась. Теперь при включении DualShock патчатся именно
текущие live-файлы `gui_textures.bnk` и `gui_texture_headers.bnk`, а бэкапы
используются только для режима OFF / восстановления.

# Fable II Recompiled — DualShock v13 / pause A + automatic launcher deployment

This is a **source overlay**, not a complete game or a compiled EXE. Extract at
`D:\Fable2\Fable-2-Recomp-1.5.0-texturediag-v1\` with replacement.

Run `BUILD-LAUNCHER-LOCAL.cmd` **as before**. It now:

1. Runs the .NET launcher tests.
2. Publishes `out\texture-diagnostics-launcher\Fable2Launcher.exe`.
3. If publishing succeeded, finds and stops **only** a running
   `D:\Fable2\Fable 2 1.5.0\Fable2Launcher.exe` by its full process path.
4. Waits for the process to exit, SHA256-verifies a staged new EXE, and
   replaces the installed file; the previous EXE is saved as
   `Fable2Launcher.exe.previous.bak`.
5. Does not touch game data or the installed launcher if the .NET build fails.

The install folder must already exist. If it is absent, installation fails
rather than creating a second game directory. A blocked/privileged process
results in a deployment error rather than an unsafe overwrite.

## Что изменено в графике

- За основу взята **рабочая v12**, не v9/v11: PS-значки ✕ ○ □ △, L1/L2/R1/R2,
  прозрачность, нижняя половина атласа и анимация удержания сохранены.
- Доработана замена `Art\GUI\Persistant\abyx.tex`: предыдущие сборки могли
  сохранить в `.xbox-prompts-backup` промежуточную версию атласа. v13 принимает
  оригинальную сжатую структуру 16 212 байт или совместимую raw 256×256,
  даже если SHA256 не совпал с двумя известными контрольными суммами.
- Содержимое нового атласа остаётся **строго контрольным**, восстановленным из
  оригинальных файлов пользователя. Сценарии BGF и управление не изменяются.
- Перед установкой графических банков проверяется точное совпадение
  перезапакованного `abyx.tex` с ожидаемым PS-атласом. В
  `launcher-dualshock-patch.log` появляется `Pause menu A: verified: ...`.
- Если формат атласа неизвестен, он по-прежнему **не перезаписывается вслепую**,
  но сообщение `Pause menu A: not updated...` в логе показывает причину.

**Важно:** проверена структура кода и содержимое оригинальных игровых банков.
Сама Fable II и сборка Windows/.NET 8 не запускались в среде подготовки;
реальное изменение последней зелёной A в паузе пока нужно проверить в игре.
В случае если она останется, приложи `launcher-dualshock-patch.log`: он теперь
покажет, применён ли именно атлас паузы, вместо следующей догадки.
