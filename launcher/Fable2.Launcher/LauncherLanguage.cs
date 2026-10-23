using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Fable2Launcher;

/// <summary>Launcher UI localization only. Never rewrites game configuration or inputs.</summary>
public static class LauncherLanguage
{
    public const string English = "en";
    public const string Russian = "ru";
    public static string Current { get; private set; } = English;
    public static string Normalize(string? value) => string.Equals(value?.Trim(), Russian, StringComparison.OrdinalIgnoreCase) ? Russian : English;
    public static void SetCurrent(string? language) => Current = Normalize(language);

    public static string StateFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fable2Launcher", "launcher-language.txt");

    public static string Load(string path)
    {
        try { return File.Exists(path) ? Normalize(File.ReadAllText(path)) : English; }
        catch (IOException) { return English; }
        catch (UnauthorizedAccessException) { return English; }
    }

    public static void Save(string path, string language)
    {
        string value = Normalize(language);
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string T(string russian) => Translate(russian, Current);
    public static string Translate(string? russian, string? language)
    {
        if (russian is null) return string.Empty;
        if (Normalize(language) == Russian || russian.Length == 0) return russian;
        if (EnglishText.TryGetValue(russian, out string? exact)) return exact;
        // Translate whole lines first (for multiline SDK reports), then known
        // variable-prefix fragments. Paths, version names and numeric IDs survive.
        string translated = string.Join("\n", russian.Split('\n').Select(line =>
            EnglishText.TryGetValue(line.TrimEnd('\r'), out string? target)
                ? target + (line.EndsWith('\r') ? "\r" : "") : line));
        foreach (var (from, to) in Fragments)
            if (translated.Contains(from, StringComparison.Ordinal))
                translated = translated.Replace(from, to, StringComparison.Ordinal);
        // A localized sentence may be embedded in a longer status message.
        // Prefer longest entries to avoid replacing fragments of other phrases.
        if (ContainsCyrillic(translated))
            foreach (var (from, to) in EnglishPhrases)
                if (translated.Contains(from, StringComparison.Ordinal))
                    translated = translated.Replace(from, to, StringComparison.Ordinal);
        return translated;
    }

    private static readonly IReadOnlyDictionary<string, string> EnglishText =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Fable II Recompiled — Лаунчер 1.6.1"] = "Fable II Recompiled — Launcher 1.6.1",
            ["ЛАУНЧЕР РЕКОМПИЛЯЦИИ"] = "RECOMPILED LAUNCHER",
            ["Версия 1.6.1"] = "Version 1.6.1",
            ["ЯЗЫК"] = "LANGUAGE",
            ["Язык интерфейса"] = "Interface language",
            ["Конфигурация готова"] = "Configuration ready",
            ["Расширенные настройки"] = "Advanced options",
            ["Что нового в лаунчере 1.6.1"] = "What’s new in launcher 1.6.1",
            ["Исправлено отображение текстур героя и собаки."] = "Fixed hero and dog textures.",
            ["Добавлено переключение значков управления Xbox / PlayStation (DualShock)."] = "Added Xbox / PlayStation (DualShock) controller prompts.",
            ["Добавлена регулировка скорости игры: 0,1–2,0×, шаг 0,1; по умолчанию 1,0×."] = "Added game speed adjustment: 0.1–2.0× in steps of 0.1 (default: 1.0×).",
            ["Добавлено переключение языка интерфейса между английским и русским (EN / RU)."] = "Added launcher interface language switching between English and Russian (EN / RU).",
            ["Автор этих доработок: "] = "Modifications by: ",
            ["Скорость игры"] = "Game speed",
            ["Обычная: 1,0×"] = "Normal: 1.0×",
            ["Постоянное замедление или ускорение. 1,0× — как сейчас. Шаг 0,1; применяется при новом запуске игры."] = "Permanent slow motion or speed-up. 1.0× is normal speed; step 0.1. Applies when you next launch the game.",
            ["Экспериментально: меню и таймеры тоже могут менять темп; возможна рассинхронизация звука/роликов. Нужна совместимая сборка игры с поддержкой изменения скорости."] = "Experimental: menus and timers may also speed up or slow down, and audio/video may lose sync. Requires a game build that supports game-speed control.",
            ["Графика"] = "Graphics",
            ["РАЗРЕШЕНИЕ ЭКРАНА"] = "DISPLAY RESOLUTION",
            ["ВНУТРЕННИЙ МАСШТАБ РЕНДЕРА"] = "INTERNAL RENDER SCALE",
            ["1× — Нативные 720p"] = "1× — Native 720p",
            ["2× — Нативные 1440p"] = "2× — Native 1440p",
            ["3× — Нативные 4K"] = "3× — Native 4K",
            ["ФИЛЬТРАЦИЯ ТЕКСТУР"] = "TEXTURE FILTERING",
            ["Как в игре"] = "Game default",
            ["1× анизотропная"] = "1× anisotropic",
            ["2× анизотропная"] = "2× anisotropic",
            ["4× анизотропная"] = "4× anisotropic",
            ["8× анизотропная"] = "8× anisotropic",
            ["16× анизотропная"] = "16× anisotropic",
            ["РЕЖИМ ЭКРАНА"] = "DISPLAY MODE",
            ["Оконный"] = "Windowed",
            ["Полноэкранный без рамки"] = "Borderless fullscreen",
            ["Эксклюзивный полноэкранный"] = "Exclusive fullscreen",
            ["СГЛАЖИВАНИЕ (ПОСТОБРАБОТКА)"] = "ANTI-ALIASING (POST PROCESS)",
            ["Выкл. — сглаживание игры"] = "Off — game anti-aliasing",
            ["FXAA Экстремальное"] = "FXAA Extreme",
            ["Дополнительное сглаживание краёв. Более высокий внутренний масштаб также даёт суперсэмплинг."] = "Extra edge smoothing. A higher internal render scale also provides supersampling.",
            ["ОГРАНИЧЕНИЕ FPS"] = "FRAME RATE LIMIT",
            ["Требует совместимой среды выполнения. VSync может ограничить FPS ниже выбранного значения; работа игровой логики выше 60 FPS проверена не полностью."] = "Requires a compatible runtime. VSync may cap FPS below the selected limit; game logic above 60 FPS has not been fully tested.",
            ["Вертикальная синхронизация"] = "Vertical synchronization",
            ["Синхронизирует вывод кадров; может ограничивать FPS ниже выбранного лимита."] = "Synchronizes frame output and may cap FPS below the selected limit.",
            ["Быстрые профили"] = "Quick presets",
            ["Оригинал"] = "Original",
            ["Сбалансированный"] = "Balanced",
            ["Улучшенный"] = "Enhanced",
            ["Ультра"] = "Ultra",
            ["Выберите профиль или настройте параметры вручную."] = "Choose a preset or configure settings manually.",
            ["Расположение игры"] = "Game location",
            ["ОРИГИНАЛЬНЫЕ ФАЙЛЫ ИГРЫ"] = "ORIGINAL GAME FILES",
            ["Не выбрано"] = "Not selected",
            ["Выбрать папку игры…"] = "Choose game folder…",
            ["Извлечь из ISO"] = "Extract from ISO",
            ["Фактическое качество"] = "Actual render quality",
            ["Внутреннее: 1280 × 720"] = "Internal: 1280 × 720",
            ["На экране: 1280 × 720"] = "Output: 1280 × 720",
            ["Повышение внутреннего масштаба улучшает края геометрии и качество рендера, но заметно увеличивает расход видеопамяти."] = "Increasing internal render scale improves geometry edges and image quality, but greatly increases VRAM usage.",
            ["Назначение клавиш"] = "Key bindings",
            ["КЛАВИША ПК  →  КНОПКА XBOX"] = "PC KEY  →  XBOX BUTTON",
            ["+ Добавить"] = "+ Add",
            ["Сбросить"] = "Reset",
            ["Нажмите поле клавиши ПК, затем нажмите нужную клавишу или кнопку мыши (Esc — отмена). Изменения сохраняются в fable2_config.toml и применяются при следующем запуске игры."] = "Click a PC key field and press the desired key or mouse button (Esc cancels). Changes are saved in fable2_config.toml and take effect the next time you launch the game.",
            ["Мышь"] = "Mouse",
            ["Управление камерой мышью"] = "Mouse camera control",
            ["Преобразует движение мыши в правый стик Xbox-геймпада. Отключите, чтобы курсор оставался свободным."] = "Maps mouse movement to the right stick of an Xbox controller. Disable to keep the cursor free.",
            ["ЧУВСТВИТЕЛЬНОСТЬ МЫШИ"] = "MOUSE SENSITIVITY",
            ["Патчи"] = "Patches",
            ["Патчи уровня рекомпиляции (перехваты промежуточного ассемблерного кода). Применяются при следующем запуске игры."] = "Recompilation-level patches (intermediate assembly hooks). Applied the next time the game starts.",
            ["Производительность"] = "Performance",
            ["Пакетная обработка уступки выполнения в часто вызываемых функциях. 1 = оригинальное поведение; больше = меньше переключений контекста; 0 = никогда не уступать выполнение."] = "Batch yielding in frequently called functions. 1 = original behavior; larger values = fewer context switches; 0 = never yield.",
            ["HOTFUNC: УСТУПАТЬ КАЖДЫЕ"] = "HOTFUNC: YIELD EVERY",
            ["Выберите папку оригинальной игры, содержащую default.xex и data."] = "Select the original game folder containing default.xex and data.",
            ["Диагностический запуск: оставить лаунчер открытым и сохранить локальный отчёт (логи и настройки; без сохранений игры и без отправки файлов)."] = "Diagnostic launch: keep the launcher open and save a local report (logs and settings only; no game saves and no files uploaded).",
            ["Сохранить настройки"] = "Save settings",
            ["Играть в Fable II"] = "Play Fable II",
            ["Сохранить расширенные настройки"] = "Save advanced settings",
            ["Вернуться в лаунчер"] = "Back to launcher",
            ["Извлечение из ISO"] = "Extracting ISO",
            ["Запуск…"] = "Starting…",
            ["Остановить"] = "Stop",
            ["Отмена"] = "Cancel",
            ["Всё равно извлечь"] = "Extract anyway",
            ["Ожидание…"] = "Waiting…",
            ["Лимит 30 FPS"] = "30 FPS limit",
            ["Лимит 60 FPS"] = "60 FPS limit",
            ["Лимит 120 FPS — экспериментально"] = "120 FPS limit — experimental",
            ["Лимит 144 FPS — экспериментально"] = "144 FPS limit — experimental",
            ["Лимит 165 FPS — экспериментально"] = "165 FPS limit — experimental",
            ["Лимит 240 FPS — экспериментально"] = "240 FPS limit — experimental",
            ["Без ограничений — экспериментально"] = "Unlimited — experimental",
            ["нажмите клавишу…"] = "press a key…",
            ["ожидание ввода…"] = "waiting for input…",
            ["Удалить это назначение"] = "Remove this binding",
            ["(нестабильно)"] = "(unstable)",
            ["Разблокировать предметы с сайта"] = "Unlock website items",
            ["Принудительно выдаёт предметы, которые раньше получались через сайт Fable 2, при загрузке сохранения."] = "Automatically grants items previously awarded through the Fable 2 website when loading a save.",
            ["Разблокировать коллекционное издание"] = "Unlock Collector’s Edition",
            ["Принудительно выдаёт содержимое сундука коллекционного издания при загрузке сохранения."] = "Automatically grants the Collector’s Edition chest contents on load.",
            ["Пропускать вступительные ролики"] = "Skip intro videos",
            ["Пропускает ролики с логотипами Microsoft и Lionhead при запуске."] = "Skips Microsoft and Lionhead intro videos.",
            ["Повышенная частота тиков"] = "Higher tick rate",
            ["Увеличивает частоту LF-тиков с 15 до 30 Гц (режим Xenia \"High Tick Rate\")."] = "Increases LF tick rate from 15 to 30 Hz (Xenia “High Tick Rate” mode).",
            ["Повышенная частота HF-тиков"] = "Higher HF tick rate",
            ["Увеличивает частоту HF-тиков с 30 до 60 Гц (режим Xenia \"Higher HF Tick Rate\"). Требует включённого режима «Повышенная частота тиков»."] = "Increases HF tick rate from 30 to 60 Hz (Xenia “Higher HF Tick Rate” mode). Requires Higher tick rate.",
            ["Отключить размытие в движении"] = "Disable motion blur",
            ["Полностью отключает полноэкранное размытие при движении камеры."] = "Completely disables full-screen motion blur when moving the camera.",
            ["Использовать геймпад DualShock"] = "Use DualShock controller prompts",
            ["Переключает экранные подсказки на стиль PlayStation во всех поддержанных GUI-путях: A→✕, B→○, X→□, Y→△, LB→L1, RB→R1, LT→L2, RT→R2. Включает отдельный атлас меню (Принять/Назад/Отмена) и HUD. Внутренняя логика игры остаётся Xbox-совместимой."] = "Shows PlayStation-style prompts wherever supported: A→✕, B→○, X→□, Y→△, LB→L1, RB→R1, LT→L2, RT→R2. Includes menu and HUD graphics; game input semantics remain Xbox-compatible.",
            ["При первом запуске создаётся резервная копия data\\art\\gui\\gui_textures.bnk.xbox-prompts-backup. Снятие галочки и следующий запуск восстанавливают исходные иконки."] = "A backup named data\\art\\gui\\gui_textures.bnk.xbox-prompts-backup is created on first launch. Unchecking this option restores original icons on the next launch.",
            ["Текстуры героя и собаки"] = "Hero and dog textures",
            ["Морфинг на GPU в реальном времени"] = "Realtime GPU morphing",
            ["Совместимый Readback — альтернативный режим"] = "Compatibility Readback — alternate mode",
            ["Совместимый Readback"] = "Compatibility Readback",
            ["Отключено — только диагностика (возможны чёрные текстуры)"] = "Disabled — diagnostics only (black textures possible)",
            ["Для этого запуска принудительно задать адрес Readback текстур героя/собаки (переопределяет конфигурацию SDK и переменные среды только для этого параметра)."] = "Force the hero/dog texture Readback address for this run (overrides SDK config and environment variables for this setting only).",
            ["Использует существующий GPU-перехват морфинга. При сохранении старый переключатель Readback отключается. Стабильность всё равно зависит от игры и среды выполнения."] = "Uses the existing GPU morphing hook. Saving disables the legacy Readback switch. Stability still depends on the game and runtime.",
            ["Отключает принудительный GPU-морфинг и включает выборочное чтение результата через CPU. Это альтернативный режим, а не гарантированное исправление вылетов. Сохраните настройки и перезапустите игру."] = "Disables forced GPU morphing and enables selective CPU readback. This is an alternative mode, not a guaranteed crash fix. Save and restart the game.",
            ["ВНИМАНИЕ: оба исправления текстур будут отключены. Герой и собака могут снова стать чёрными. Используйте только для контролируемой диагностики."] = "WARNING: both texture fixes will be disabled. The hero and dog may appear black again. Use only for controlled diagnostics.",
            ["Загруженные / последние сохранённые настройки (сохраните, чтобы применить новый выбор):"] = "Loaded / previously saved settings (save to apply your selection):",
            ["Не удалось проверить настройки SDK: "] = "Could not check SDK settings: ",
            ["Не готово к запуску"] = "Not ready to launch",
            ["Настройки загружены"] = "Settings loaded",
            ["Настройки сохранены"] = "Settings saved",
            ["Есть несохранённые изменения"] = "Unsaved changes",
            ["Игра запущена"] = "Game running",
            ["Fable II запущена."] = "Fable II started.",
            ["Подготовка локального диагностического отчёта…"] = "Preparing local diagnostic report…",
            ["Диагностический отчёт сохранён"] = "Diagnostic report saved",
            ["Диагностический сеанс"] = "Diagnostic session",
            ["Диагностический сеанс завершён"] = "Diagnostic session complete",
            ["Ошибка запуска или диагностики"] = "Launch or diagnostics error",
            ["Не удалось сохранить настройки"] = "Could not save settings",
            ["Не удалось сохранить расширенные настройки"] = "Could not save advanced settings",
            ["Не удалось прочитать расширенные настройки"] = "Could not read advanced settings",
            ["ISO-образ не найден."] = "ISO image not found.",
            ["Поиск заголовка GDFX на диске…"] = "Searching for GDFX header on disc…",
            ["В корне этого ISO-образа не найден default.xex."] = "default.xex was not found at the root of this ISO image.",
            ["Настройки языка"] = "Language settings",
            ["Игра не найдена"] = "Game not found",
            ["Режим текстур"] = "Texture mode",
            ["Только диагностический режим"] = "Diagnostic mode only",
            ["Выберите папку оригинальной игры (default.xex и data)"] = "Select the original game folder (default.xex and data)",
            ["В этой папке нет файла default.xex."] = "This folder does not contain default.xex.",
            ["Выберите ISO-образ"] = "Select an ISO image",
            ["Сканирование…"] = "Scanning…",
            ["Сканирование диска…"] = "Scanning disc…",
            ["Поиск заголовка GDFX…"] = "Finding GDFX header…",
            ["Проверка SHA-256 файла default.xex…"] = "Checking default.xex SHA-256…",
            ["Извлечение default.xex"] = "Extracting default.xex",
            ["Завершение…"] = "Finishing…",
            ["Извлечение остановлено."] = "Extraction stopped.",
            ["Ошибка извлечения."] = "Extraction error.",
            ["Не удалось извлечь ISO"] = "Could not extract ISO",
            ["Готово"] = "Done",
            ["Остановка…"] = "Stopping…",
            ["Извлечение…"] = "Extracting…",
            ["Неизвестный default.xex"] = "Unknown default.xex",
            ["Чтение диска…"] = "Reading disc…",
            ["Редактируется fable2_config.toml (изменения не сохранены)"] = "Editing fable2_config.toml (unsaved changes)",
            ["Для скорости отличной от 1,0× нужен новый fable_2.exe.\n\nЗакройте игру и запустите BUILD-GAME-SPEED-LOCAL.cmd из папки исходников. Одной пересборки лаунчера недостаточно."] = "Speeds other than 1.0× require a new fable_2.exe.\n\nClose the game and run BUILD-GAME-SPEED-LOCAL.cmd in the source folder. Rebuilding the launcher alone is not enough.",
            ["Оба исправления текстур героя и собаки отключены. Ожидаются чёрные текстуры. Продолжить диагностический тест?"] = "Both hero and dog texture fixes are disabled. Black textures are expected. Continue the diagnostic test?",
            ["Отключить оба исправления текстур? Герой и собака могут отображаться чёрными."] = "Disable both texture fixes? The hero and dog may appear black.",
            ["Не удалось запустить процесс игры."] = "Could not start the game process.",
            ["Сначала выберите папку игры."] = "Select a game folder first.",
            ["Сначала выберите папку, содержащую fable_2.exe."] = "Select a folder containing fable_2.exe first.",
            ["Игра продолжит работать, но диагностический отчёт останется неполным. Закрыть лаунчер?"] = "The game will continue running, but the diagnostic report will be incomplete. Close the launcher?",
            ["Никакие файлы не отправлялись. Перед передачей отчёта проверьте пути и настройки. Один код завершения не определяет причину сбоя."] = "No files have been uploaded. Review paths and settings before sharing the report. An exit code alone does not identify the cause of a crash.",
            ["Не удалось открыть GitHub: "] = "Could not open GitHub: ",
            ["Ссылка на автора доработок"] = "Modifications author link",
            ["Оригинальный рендеринг Xbox 360: внутренние 720p и фильтрация текстур под управлением игры."] = "Original Xbox 360 rendering: internal 720p and game-controlled texture filtering.",
            ["Внутренний рендеринг 1440p с 8× анизотропной фильтрацией и выводом в 1080p."] = "1440p internal rendering with 8× anisotropic filtering, output at 1080p.",
            ["Настоящий внутренний рендеринг 4K с 16× анизотропной фильтрацией. Рекомендуется для современных видеокарт."] = "True internal 4K rendering with 16× anisotropic filtering. Recommended for modern GPUs.",
            ["Внутренний суперсэмплинг 5K с уменьшением до 4K. Экспериментальный режим с очень высоким расходом видеопамяти."] = "5K internal supersampling downscaled to 4K. Experimental and very VRAM-intensive.",
            ["Файлы игры не найдены"] = "Game files not found",
            ["В выбранной папке игры не найден default.xex."] = "No default.xex was found in the selected game folder.",
            ["Обнаружена несовместимая смесь файлов"] = "Incompatible mix of game files detected",
            ["Этот XEX используется с несовместимыми файлами retail/TU1. Используйте полный комплект файлов одной версии."] = "This XEX is combined with incompatible retail/TU1 files. Use a complete set from a single version.",
            ["Группа гостевого кода этой версии не включена в данную сборку."] = "The guest code group for this version is not included in this build.",
            ["Отсутствуют необходимые данные или локализация. Выберите полный комплект файлов соответствующей версии."] = "Required data or localization is missing. Select a complete file set for that version.",
            ["Неизвестная версия XEX"] = "Unknown XEX version",
            ["Файл fable_2.exe не найден рядом с лаунчером или в выбранной папке."] = "fable_2.exe was not found alongside the launcher or in the selected folder.",
            ["Принудительный Readback требует режима «Совместимый Readback»."] = "Forced Readback requires Compatibility Readback mode.",
            ["Исправления отключены (диагностика)"] = "Fixes disabled (diagnostic)",
            ["Оба старых флага включены. При сохранении останется только один; сейчас приоритет имеет GPU-режим."] = "Both old flags are enabled. Saving will leave only one; GPU mode currently takes priority.",
            ["ВНИМАНИЕ: исправление текстур не выбрано; герой и собака могут отображаться чёрными."] = "WARNING: No texture fix selected; the hero and dog may appear black.",
            ["Это входные параметры конфигурации, а не измерение фактически применённого режима. Подтверждение во время работы появится в строках [texture-runtime] лога после пересборки игры."] = "These are requested configuration inputs, not proof of what the runtime applied. Check [texture-runtime] log lines after rebuilding the game.",
            ["<не задано>"] = "<not set>",
        };

    private static readonly (string From, string To)[] EnglishPhrases = EnglishText
        .Where(pair => pair.Key.Length >= 12)
        .OrderByDescending(pair => pair.Key.Length)
        .Select(pair => (pair.Key, pair.Value)).ToArray();

    private static bool ContainsCyrillic(string value)
    {
        foreach (char c in value)
            if (c is >= '\u0400' and <= '\u04FF') return true;
        return false;
    }

    private static readonly (string From, string To)[] Fragments =
    {
        ("атлас проверен: да", "atlas verified: yes"),
        ("атлас проверен: нет — см. лог", "atlas verified: no — see log"),
        ("<не задано>", "<not set>"),
        ("DualShock включён", "DualShock enabled"),
        ("DualShock выключен", "DualShock disabled"),
        ("заменено ", "replaced "),
        ("GUI-текстур", "GUI textures"),
        ("атлас проверен:", "atlas verified:"),
        ("нет — см. лог", "no — see log"),
        ("значок MenuHighlight:", "MenuHighlight badge:"),
        ("восстановлены оригинальные подсказки Xbox 360", "original Xbox 360 prompts restored"),
        ("резервные копии подсказок не найдены", "prompt backups not found"),
        (" (нестабильно)", " (unstable)"),
        ("Осталось", "Remaining"),
        ("Не удалось извлечь ISO", "Could not extract ISO"),
        ("Проект принимает только проверенные версии, поэтому извлечение не продолжено.", "Only verified versions are supported; extraction was not continued."),
        ("но группа гостевого кода этой версии не включена в данную сборку.", "but this build does not include the guest code group for that version."),
        ("Группа гостевого кода этой версии не включена в данную сборку.", "The guest code group for this version is not included in this build."),
        ("Установите сборку Recomp с поддержкой нескольких GOTY-версий.", "Install a Recomp build with support for multiple GOTY versions."),
        ("Для скорости отличной от 1,0× нужен новый fable_2.exe.", "Speeds other than 1.0× require a new fable_2.exe."),
        ("Перезапустите игру, чтобы применить режим.", "Restart the game to apply the mode."),
        ("Этот fable_2.exe не поддерживает профиль ", "This fable_2.exe does not support profile "),
        ("Неизвестный SHA-256 файла default.xex: ", "Unknown default.xex SHA-256: "),
        ("Неизвестный SHA-256 файла default.xex:", "Unknown default.xex SHA-256:"),
        ("Не удалось сохранить default.xex: ", "Could not save default.xex: "),
        ("Морфинг на GPU в реальном времени", "Realtime GPU morphing"),
        ("Игра завершилась с кодом ", "Game exited with code "),
        ("Код завершения процесса: ", "Process exit code: "),
        ("Локальный отчёт сохранён:", "Local report saved:"),
        ("Неполный комплект файлов ", "Incomplete file set: "),
        ("Запрошенный режим: ", "Requested mode: "),
        ("Извлечено файлов: ", "Files extracted: "),
        ("Конфигурация SDK ", "SDK config "),
        ("Переменная среды ", "Environment variable "),
        ("Автоматически", "Automatic"),
        ("Внутреннее: ", "Internal: "),
        ("На экране: ", "Output: "),
        ("Сохранено: ", "Saved: "),
        ("Извлечение ", "Extracting "),
        ("Обнаружена ", "Detected "),
        (" (файлов: ", " (files: "),
        ("Отчёт: ", "Report: "),
    };
}
