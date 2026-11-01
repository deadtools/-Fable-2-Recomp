using System;
using System.Collections.Generic;

namespace Fable2Launcher;

/// <summary>English text for strings introduced by the 2.0.0 interface.</summary>
public static class LauncherTextV2
{
    public static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Window / home
            ["Fable II Recompiled — Лаунчер 2.0.0"] = "Fable II Recompiled — Launcher 2.0.0",
            ["ВЕРСИЯ ЛАУНЧЕРА 2.0.0"] = "LAUNCHER VERSION 2.0.0",
            ["ВЕРСИЯ 2.0.0"] = "VERSION 2.0.0",
            ["Что нового?"] = "What’s new?",
            ["Проверить обновления на GitHub"] = "Check for updates on GitHub",
            ["Файлы игры можно указать в настройках"] = "You can set the game files in Settings",
            ["Игра найдена"] = "Game found",
            ["Игра не найдена"] = "Game not found",
            ["Запуск недоступен"] = "Cannot launch",
            ["НАСТРОЙКИ"] = "SETTINGS",
            ["Настройки"] = "Settings",
            ["ИГРАТЬ"] = "PLAY",
            ["Укажите папку с оригинальными файлами Fable II GOTY"] = "Select the folder with the original Fable II GOTY files",
            ["ВЫБРАТЬ ПАПКУ"] = "SELECT FOLDER",
            ["Свернуть"] = "Minimize",
            ["Закрыть"] = "Close",

            // Sidebar
            ["Управление"] = "Controls",
            ["Игра"] = "Game",
            ["Оптимизация"] = "Optimization",
            ["Файлы игры"] = "Game files",
            ["СОХРАНИТЬ"] = "SAVE",
            ["СОХРАНИТЬ *"] = "SAVE *",
            ["←  Назад"] = "←  Back",
            ["●  Есть несохранённые изменения"] = "●  Unsaved changes",
            ["✓  Настройки сохранены"] = "✓  Settings saved",

            // Section headers
            ["Разрешение экрана, качество изображения и плавность"] = "Resolution, image quality and smoothness",
            ["Переназначение клавиш, мышь и экранные подсказки геймпадов"] = "Key bindings, mouse and on-screen controller prompts",
            ["Скорость игры, профили качества и игровые патчи"] = "Game speed, quality presets and gameplay patches",
            ["Совместимость, чтение текстур и параметры исполнения"] = "Compatibility, texture readback and execution options",
            ["Расположение, извлечение ISO и локальная диагностика"] = "Location, ISO extraction and local diagnostics",

            // Graphics
            ["ЭКРАН"] = "DISPLAY",
            ["КАЧЕСТВО"] = "QUALITY",
            ["РАЗРЕШЕНИЕ"] = "RESOLUTION",
            ["ЛИМИТ FPS"] = "FPS LIMIT",
            ["МАСШТАБ РЕНДЕРА"] = "RENDER SCALE",
            ["СГЛАЖИВАНИЕ"] = "ANTI-ALIASING",
            ["4× — 5120 × 2880"] = "4× — 5120 × 2880",
            ["Отключить размытие в движении"] = "Disable motion blur",
            ["Фактическое качество"] = "Effective quality",
            ["Слева — внутреннее разрешение рендера, справа — разрешение на экране. Параметры применяются при следующем запуске игры."] =
                "Left: internal render resolution. Right: output resolution. Changes apply the next time the game starts.",

            // Controls
            ["Клавиатура"] = "Keyboard",
            ["Мышь"] = "Mouse",
            ["Тип подсказок в игре:"] = "In-game prompts:",
            ["Тип управления"] = "Control type",
            ["Клавиатура/Мышь"] = "Keyboard/Mouse",
            ["•   Назначения применяются после запуска игры"] = "•   Bindings apply when the game starts",
            ["+ Добавить"] = "+ Add",
            ["Сбросить"] = "Reset",
            ["Кликните по клавише для нового назначения. Esc — отмена."] = "Click a key to rebind it. Esc cancels.",
            ["нажмите клавишу…"] = "press a key…",
            ["ожидание ввода…"] = "waiting for input…",
            ["Кликните, затем нажмите клавишу или кнопку мыши"] = "Click, then press a key or mouse button",
            ["Удалить это назначение"] = "Remove this binding",
            ["Управление камерой мышью"] = "Mouse camera control",
            ["Движение мыши преобразуется в движение правого стика. Отключите, чтобы курсор оставался свободным."] =
                "Mouse movement is converted to the right stick. Turn off to keep the cursor free.",
            ["ЧУВСТВИТЕЛЬНОСТЬ МЫШИ"] = "MOUSE SENSITIVITY",
            ["НАЗНАЧЕНИЯ КНОПОК МЫШИ"] = "MOUSE BUTTON BINDINGS",
            ["Левая кнопка"] = "Left button",
            ["Средняя кнопка"] = "Middle button",
            ["Правая кнопка"] = "Right button",
            ["— не назначено —"] = "— not bound —",
            ["A — основное действие"] = "A — primary action",
            ["B — дополнительно"] = "B — secondary",
            ["X — действие"] = "X — action",
            ["Y — альтернативно"] = "Y — alternative",
            ["Использовать значки геймпада PlayStation (DualShock)"] = "Use PlayStation (DualShock) controller prompts",
            ["Заменяет подсказки игровых кнопок Xbox на PlayStation (✕ ○ □ △, L1/L2/R1/R2), не меняя игровой ввод."] =
                "Replaces Xbox button prompts with PlayStation ones (✕ ○ □ △, L1/L2/R1/R2) without changing game input.",
            ["Значки PlayStation"] = "PlayStation prompts",
            ["Что происходит при переключении"] = "What happens when you switch",
            ["•  Перед первой заменой сохраняется резервная копия оригинальных значков."] = "•  A backup of the original prompts is made before the first replacement.",
            ["•  При отключении возвращаются исходные ресурсы."] = "•  Turning it off restores the original resources.",
            ["•  Повторное включение не создаёт каскад резервных копий."] = "•  Turning it on again does not create extra backups.",
            ["Проверка ресурсов"] = "Resource check",
            ["Файлы подсказок не найдены — проверьте папку игры"] = "Prompt files not found — check the game folder",
            ["Оригинальные подсказки найдены · Резервная копия доступна"] = "Original prompts found · Backup available",
            ["Оригинальные подсказки найдены · Резервная копия будет создана при первой замене"] = "Original prompts found · A backup will be made on first replacement",
            ["Использовать оригинальные значки Xbox 360"] = "Use original Xbox 360 prompts",
            ["Значки Xbox 360"] = "Xbox 360 prompts",
            ["Оригинальные кнопки Xbox 360. Сохранённые игровые назначения не меняются."] = "Original Xbox 360 buttons. Saved game bindings are not changed.",
            ["Чтобы сменить значки, включите PlayStation или выберите «Клавиатура/Мышь» на вкладке «Клавиатура»."] =
                "To change prompts, turn on PlayStation or choose “Keyboard/Mouse” on the Keyboard tab.",
            ["Сейчас выбраны значки PlayStation: ✕ ○ □ △ и L1/L2/R1/R2. Внутренняя раскладка Xbox остаётся прежней."] =
                "PlayStation prompts are selected: ✕ ○ □ △ and L1/L2/R1/R2. The internal Xbox layout is unchanged.",
            ["Сейчас выбраны значки Клавиатура/Мышь: круглые значки берутся из назначений клавиш."] =
                "Keyboard/Mouse prompts are selected: round key icons come from your key bindings.",
            ["Сейчас выбраны оригинальные значки Xbox 360."] = "Original Xbox 360 prompts are selected.",

            // Game
            ["Скорость и профили"] = "Speed and presets",
            ["Игровые патчи"] = "Gameplay patches",
            ["СКОРОСТЬ ИГРЫ"] = "GAME SPEED",
            ["Сбросить до 1,0×"] = "Reset to 1.0×",
            ["ВНИМАНИЕ"] = "WARNING",
            ["Изменение скорости применяется при следующем запуске. Возможна рассинхронизация звука и роликов; нужна сборка игры с поддержкой изменения скорости."] =
                "Speed changes apply on the next launch. Audio and cutscenes may lose sync; requires a game build that supports speed control.",
            ["БЫСТРЫЕ ПРОФИЛИ"] = "QUICK PRESETS",
            ["Оригинал"] = "Original",
            ["Сбалансированный"] = "Balanced",
            ["Улучшенный"] = "Enhanced",
            ["Ультра"] = "Ultra",
            ["Базовые параметры"] = "Base settings",
            ["Баланс качества"] = "Balanced quality",
            ["Лучшее качество"] = "Best quality",
            ["Максимум графики"] = "Maximum graphics",
            ["Пресет меняет несколько параметров графики. После ручной настройки состояние — «Пользовательский»."] =
                "A preset changes several graphics settings. After manual changes the state is “Custom”.",
            ["Текущий профиль: Оригинал."] = "Current preset: Original.",
            ["Текущий профиль: Сбалансированный."] = "Current preset: Balanced.",
            ["Текущий профиль: Улучшенный."] = "Current preset: Enhanced.",
            ["Текущий профиль: Ультра (экспериментально, очень высокий расход видеопамяти)."] = "Current preset: Ultra (experimental, very high VRAM use).",
            ["Текущий профиль: Пользовательский."] = "Current preset: Custom.",
            ["Разблокировать предметы с сайта"] = "Unlock website items",
            ["Выдача предметов, ранее получавшихся через сайт Fable II, при загрузке сохранения."] =
                "Grants items that used to come from the Fable II website when a save is loaded.",
            ["Разблокировать коллекционное издание"] = "Unlock Collector’s Edition",
            ["Открывает содержимое сундука коллекционного издания при загрузке сохранения."] =
                "Unlocks the Collector’s Edition chest contents when a save is loaded.",
            ["Пропускать вступительные ролики"] = "Skip intro videos",
            ["Пропускает ролики с логотипами Microsoft и Lionhead при старте."] = "Skips the Microsoft and Lionhead logo videos at startup.",
            ["Патчи применяются при следующем запуске игры."] = "Patches apply the next time the game starts.",

            // Optimization
            ["Совместимость"] = "Compatibility",
            ["Производительность"] = "Performance",
            ["Экспериментально"] = "Experimental",
            ["ТЕКСТУРЫ ГЕРОЯ И СОБАКИ"] = "HERO AND DOG TEXTURES",
            ["Совместимый Readback — альтернативный режим"] = "Compatibility Readback — alternative mode",
            ["Отключает принудительный GPU-morphing; выполняет выборочное чтение результата через CPU."] =
                "Disables forced GPU morphing; reads back the result selectively through the CPU.",
            ["Стабильный профиль v22: GPU realtime morphing и Forced Readback отключены. На отдельных сценах возможны прежние проблемы текстур героя/собаки."] =
                "Stable v22 profile: GPU realtime morphing and Forced Readback are off. Some scenes may show the old hero/dog texture issues.",
            ["Принудительно задать адреса Readback для этого запуска"] = "Force Readback addresses for this launch",
            ["Экспериментальная опция: не включается автоматически, не сохраняется и не меняет оригинальные файлы."] =
                "Experimental option: never enabled automatically, not saved, and does not change original files.",
            ["Принудительный Readback для этого запуска"] = "Forced Readback for this launch",
            ["ДОПОЛНИТЕЛЬНЫЕ ТЕХНИЧЕСКИЕ ПАРАМЕТРЫ  ▾"] = "ADVANCED TECHNICAL DETAILS  ▾",
            ["ДОПОЛНИТЕЛЬНЫЕ ТЕХНИЧЕСКИЕ ПАРАМЕТРЫ  ▴"] = "ADVANCED TECHNICAL DETAILS  ▴",
            ["Конфигурация SDK, переменные среды, журнал texture-runtime"] = "SDK configuration, environment variables, texture-runtime log",
            ["HOTFUNC — УСТУПАТЬ КАЖДЫЕ N ВЫЗОВОВ"] = "HOTFUNC — YIELD EVERY N CALLS",
            ["Пакетная уступка выполнения в часто вызываемых функциях. 1 — исходное поведение; больше — меньше переключений контекста; 0 — никогда не уступать. В поле можно ввести значение до 4096."] =
                "Batched yielding in hot functions. 1 is the original behaviour; higher means fewer context switches; 0 never yields. The box accepts values up to 4096.",
            ["Повышенная частота тиков"] = "High tick rate",
            ["Нестабильно · LF-тики 15 → 30 Гц (режим Xenia «High Tick Rate»)"] = "Unstable · LF ticks 15 → 30 Hz (Xenia “High Tick Rate”)",
            ["Повышенная частота HF-тиков"] = "Higher HF tick rate",
            ["Нестабильно · HF-тики 30 → 60 Гц (режим Xenia «Higher HF Tick Rate»)"] = "Unstable · HF ticks 30 → 60 Hz (Xenia “Higher HF Tick Rate”)",
            ["Недоступно: сначала включите «Повышенная частота тиков»."] = "Unavailable: turn on “High tick rate” first.",
            ["Оба режима экспериментальные. Не включайте их по умолчанию."] = "Both modes are experimental. Do not enable them by default.",

            // Files
            ["РАСПОЛОЖЕНИЕ ОРИГИНАЛЬНЫХ ФАЙЛОВ"] = "ORIGINAL FILES LOCATION",
            ["ОБЗОР"] = "BROWSE",
            ["Файлы игры не найдены"] = "Game files not found",
            ["Найдена "] = "Found ",
            ["УСТАНОВКА ИЗ ISO-ОБРАЗА"] = "INSTALL FROM ISO IMAGE",
            ["Выберите оригинальный образ диска. Файлы извлекаются в папку лаунчера, рядом с fable_2.exe."] =
                "Select the original disc image. Files are extracted into the launcher folder, next to fable_2.exe.",
            ["ВЫБРАТЬ ISO И ИЗВЛЕЧЬ"] = "SELECT ISO AND EXTRACT",
            ["ДИАГНОСТИЧЕСКИЙ ЗАПУСК"] = "DIAGNOSTIC LAUNCH",
            ["Сохранять локальный отчёт и журнал запуска (без отправки файлов). Лаунчер остаётся открытым до выхода из игры. Действует только для текущего сеанса лаунчера."] =
                "Save a local report and launch log (nothing is uploaded). The launcher stays open until the game exits. Applies to this launcher session only.",
            ["Диагностический запуск"] = "Diagnostic launch",

            // What's new
            ["Что нового в лаунчере"] = "What’s new in the launcher",
            ["Версия 2.0.0"] = "Version 2.0.0",
            ["Новый главный экран: быстрый доступ к запуску и настройкам."] = "New home screen: quick access to Play and Settings.",
            ["Все параметры объединены в пяти логичных разделах."] = "All options are grouped into five clear sections.",
            ["Сохранены патчи, Readback, HOTFUNC и режимы управления."] = "Patches, Readback, HOTFUNC and control modes are all kept.",
            ["Единое сохранение, предупреждения и понятные состояния."] = "One Save button, warnings and clear states.",
            ["ПРОВЕРИТЬ ОБНОВЛЕНИЯ"] = "CHECK FOR UPDATES",
            ["ЗАКРЫТЬ"] = "CLOSE",
            ["Автор доработок: "] = "Modifications by: ",
            ["   •   Проверка обновлений на GitHub"] = "   •   Updates on GitHub",

            // Unsaved dialog
            ["Несохранённые изменения"] = "Unsaved changes",
            ["Сохранить настройки перед выходом?"] = "Save settings before leaving?",
            ["НЕ СОХРАНЯТЬ"] = "DON’T SAVE",
            ["ОТМЕНА"] = "CANCEL",

            // Achievements
            ["Достижения"] = "Achievements",
            ["ДОСТИЖЕНИЯ"] = "ACHIEVEMENTS",
            ["Список всех достижений Fable II"] = "All Fable II achievements",
            ["←  На главную"] = "←  Home",
            [" · прогресс рекомпиляции"] = " · recomp progress",
            ["Выполнено"] = "Completed",
            ["из"] = "of",
            ["Все"] = "All",
            ["Выполненные"] = "Completed",
            ["Не выполненные"] = "Not completed",
            ["Сортировка достижений"] = "Sort achievements",
            ["Сначала полученные"] = "Unlocked first",
            ["По порядку в игре"] = "Game order",
            ["Сначала дорогие"] = "Highest points first",
            ["По названию"] = "By name",
            ["Получено"] = "Unlocked",
            ["Не получено"] = "Locked",
            ["Пока нет полученных достижений."] = "No achievements unlocked yet.",
            ["Все достижения получены!"] = "All achievements unlocked!",
            ["Файл прогресса ещё не создан: он появится после первого достижения в игре."] = "No progress file yet: it appears after your first achievement in the game.",

            ["   •   Проект: "] = "   •   Project: ",
            ["Проект на GitHub"] = "Project on GitHub",

            // Misc
            ["Не удалось сохранить настройки"] = "Could not save settings",
            ["Не удалось прочитать расширенные настройки"] = "Could not read advanced settings",
        };
}
