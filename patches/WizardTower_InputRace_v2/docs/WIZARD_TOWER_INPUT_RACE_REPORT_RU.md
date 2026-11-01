# Wizard's Tower: причина вылета и патч rexruntime (v2, 2026-11-01)

## Итог одной строкой

Все 8 имеющихся дампов (2 новых + 6 от 25.10) падают в одном месте:
`InputSystem::RefreshDevices()` → `devices_.clear()` (уничтожение `std::string` в `DeviceInfo`),
вызванном из `XamInputGetState`/`XamInputGetCapabilities`. Вызовы идут из **разных** гостевых
потоков (`GameThread` и `3D Engine`), а в RefreshDevices **нет блокировки**. Это гонка данных
(data race) с двойным освобождением памяти. Патч (одна блокировка) собран на изолированном
стенде: оригинал воспроизводит 0xC0000374 / 0xC0000005 5 раз из 5, с патчем 5 из 5 запусков без ошибок.
**Игра с патчем ещё не проверялась: нужна сборка rexruntime.dll из полного дерева исходников.**

---

## 00. ИГРОВОЙ ТЕСТ (2026-11-01, после установки бинарного патча)

Сессия `session-20261101-175242-74a297ba`: rexruntime.dll `cbc1eecd…79dd` (пропатченная), fable_2.exe
`9bad4536…`, rexgpu-xenos.dll `50ba9945…` (без изменений), режим подсказок Keyboard/Mouse, SWF башни — модифицированный.
Длительность 20:52:43 → 21:06:32 (~14 мин), выход через закрытие окна, код 0x00000000, WER-дампа нет.
По словам пользователя, в Wizard's Tower он провёл больше 10 минут без вылета. До патча было 8 вылетов из 8 через 1–2 минуты.
Ограничение: логи runtime не фиксируют вход в мини-игру, поэтому сам факт входа подтверждён только пользователем.
Ещё не проверено: оригинальный Xbox SWF, геймпад, несколько повторных сессий.

## 0. ОБНОВЛЕНИЕ: бинарный патч установленной DLL (без исходников)

Полного native-дерева на компьютере нет (`out\launcher` содержит только лаунчер). Поэтому сделан
точечный патч **той же самой** rexruntime.dll — ABI не меняется вообще.

* `tools\binpatch_rexruntime_inputlock.py` принимает только сборку `1D8CBB87…5AEA` и выдаёт `CBC1EECD…79DD`.
* На входы GetCapabilities (0x129320), GetState (0x1294B0), SetState (0x1296B0) и GetKeystroke (0x1298A0)
  ставится `jmp` на общий переходник в свободном хвосте `.text`. Переходник вызывает
  `AcquireSRWLockExclusive`, затем исходную функцию (с перенесённым прологом), затем
  `ReleaseSRWLockExclusive`. SRW-блокировка лежит в хвосте `.data` (обнулена загрузчиком).
  Для переходника добавлены UNWIND_INFO и запись `.pdata`, поэтому исключения разворачиваются корректно:
  `RtlLookupFunctionEntry` находит запись.
* Набор экспортов не изменился (470 = 470); все импорты fable_2.exe (368) и rexgpu-xenos.dll (105) разрешаются.

**Проверка на самой DLL вне игры** (`tools\VERIFY-DLL-INPUTRACE.cmd`): DLL загружается, создаётся
`CreateDefaultInputSystem(tool_mode)` с драйвером NOP, 4 потока вызывают 4 input-функции.

| DLL | 5 запусков по 200 000 итераций |
|---|---|
| Установленная `1D8CBB87…` | падает 5 из 5: 0xC0000374 / 0xC0000005 / 0xC0000409 / 0xC000001D (`ud2` в той же `clear()`) |
| Пропатченная `CBC1EECD…` | 5 из 5 без ошибок; ещё 8 млн вызовов за 2,6 с |

Однопоточные результаты совпадают: GetState=0, Caps=0, SetState=0, Keystroke=0x10D2.

**Применение:** закрыть игру → `tools\BINPATCH-INSTALL-INPUTLOCK.cmd` (по умолчанию `D:\Fable2\Game`) →
делается резервная копия, проверяется SHA-256, пишется `inputfix-install.json`.
Откат: `tools\ROLLBACK-REXRUNTIME-INPUTFIX.cmd`. Полный цикл установки и отката проверен на копии папки игры.

Ограничения: `Shutdown/AddDriver/AttachWindow/SetActiveCallback` в бинарном патче не блокируются
(они вызываются при старте и выходе, в дампах их нет). Блокировка не рекурсивная: повторный вход в
input API из драйвера не обнаружен (вне игры зависаний нет), но с SDL-драйвером проверяется только в игре.

---

## 1. ПОДТВЕРЖДЕНО

### 1.1 RVA → код (rexruntime.dll SHA256 `1D8CBB87…5AEA`, 2026-10-23 10:18)

`.pdata` даёт границы функций. Все три адреса из отчётов лежат в **одной** функции 0x12BCE0–0x12BDC5:

| RVA | Инструкция | Смысл |
|---|---|---|
| 0x12BD05 | после `call operator delete` | адрес возврата из `free()` → кадр «double free» |
| 0x12BD41 | `mov r8, [rcx-8]` | чтение заголовка выровненного блока строки `+0x30` (guid) → AV на мусорном указателе |
| 0x12BD94 | `mov r8, [rcx-8]` | то же для строки `+0x10` (name) |
| 0x12BD61 | после второго `operator delete` | вариант кадра в дампах 14132, 16264 |

Функция 0x12BCE0 — это `std::vector<T>::clear()`, где `sizeof(T)=0x58`, строки на +0x10 и +0x30
(MSVC STL: SSO-порог 16, путь «большого выравнивания» при capacity ≥ 0x1000).
Это **точно** `rex::input::DeviceInfo` (`id` u64 +0, `ordinal` +8, `name` +0x10, `guid` +0x30,
`synthetic` +0x50; в RefreshDevices видно копирование байта +0x50).

Вызывающие (поиск `call 0x18012BCE0` по всему `.text`), только два:

| RVA функции | Соответствие исходнику | Признаки |
|---|---|---|
| 0x1281E0 | `InputSystem::Shutdown()` | clear(+0x50), clear(+0x68), delete каждого драйвера из +0x10 — один в один с исходником |
| 0x1285D0 | `InputSystem::RefreshDevices()` | цикл по drivers_ (+0x10) с `vcall +0x10` (EnumerateDevices), затем `devices_.clear()` (+0x50) на 0x128F3B, `device_owners_` (+0x68), push_back. **Ни одного вызова lock/SRW/CriticalSection** |

RefreshDevices вызывают ровно 4 функции, каждая — при каждом обращении, после проверки `assignment_` (+0x28):

| RVA | Функция | Гостевой шлюз |
|---|---|---|
| 0x129320 | GetCapabilities (проверка `out_caps` в r9) | 0x2F01B6/0x2F02FE/0x2F3414 |
| 0x1294B0 | GetState | только 0x2F0416 (функция 0x2F0310 = `XamInputGetState_entry`) |
| 0x1296B0 | SetState (стартовое `0x48F` = DEVICE_NOT_CONNECTED, DeviceInfoFor) | 0x2F04D1/0x2F34DD |
| 0x1298A0 | GetKeystroke (4 аргумента) | 0x2F054E/… |

Раскладка полей класса в DLL совпадает с `input_system.h` от 2026-10-25:
drivers_ +0x10, assignment_ +0x28, active_devices_ +0x30, devices_ +0x50, device_owners_ +0x68.

### 1.2 Стек WinDbg расшифрован

Метки вида `CreateDefaultInputSystem+0x22b5`, `SetActiveCallback+0xc20`, `_imp__XamInputSetState+0x2e1a`
— это **ближайший экспорт**, а не реальные функции. Реальная цепочка (база 0x7FF82F280000, дамп 16188):

```
ntdll!RtlFreeHeap → ucrtbase!free_base
07 rexruntime+0x12BD05  vector<DeviceInfo>::clear()       (WinDbg: CreateDefaultInputSystem+0x22b5)
08 rexruntime+0x128F40  InputSystem::RefreshDevices()     (WinDbg: SetActiveCallback+0xc20)
09 rexruntime+0x1294F0  InputSystem::GetState()           (WinDbg: SetActiveCallback+0x11d0)
0a rexruntime+0x2F041B  XamInputGetState_entry            (WinDbg: _imp__XamFree+0x12cab)
0b rexruntime+0x2F347A  гостевой шлюз XamInputGetState    (WinDbg: _imp__XamInputSetState+0x2e1a)
0c fable_2.exe          рекомпилированный код игры
```

То есть это **XamInputGetState**, а не XamInputSetState/XamFree.

### 1.3 Все 8 дампов — одна и та же точка

Проверено cdb (WinDbg 10.0.29661) с `.ecxr; kn; ~*kn`:

| Дамп | Код | Место | Через | Гостевой поток |
|---|---|---|---|---|
| 16188 (01.11) | C0000374 | clear+free | GetState | **3D Engine (F80000E4)** |
| 10976 (01.11) | C0000374 | clear+free | GetState | **GameThread (F800002C)** |
| 14132 (25.10) | C0000374 | clear+free (0x12BD61) | GetCapabilities | GameThread |
| 16264 (25.10) | C0000374 | clear+free (0x12BD61) | GetCapabilities | GameThread |
| 15004 (25.10) | C0000005 | 0x12BD94 | GetCapabilities | GameThread |
| 15008 (25.10) | C0000005 | 0x12BD41 | GetState | GameThread |
| 9636 (25.10) | C0000005 | 0x12BD41 | GetCapabilities | GameThread |
| 1836 (25.10) | C0000374 | malloc внутри RefreshDevices | GetState | **3D Engine (F80000E4)** |

Вывод: XamInputGetState/GetCapabilities вызываются минимум из двух гостевых потоков;
все повреждения кучи находятся в данных `devices_`, которые RefreshDevices пересоздаёт при каждом вызове.
Аудио, SWF, readback, GPU в стеках упавших потоков отсутствуют.

### 1.4 Воспроизведение без игры (изолированный стенд)

`tools\RUN-INPUTFIX-HARNESS.cmd`: настоящие `input_system.cpp`, `device_assignment.cpp`, `state_merge.cpp`
и настоящие input-заголовки ReXGlue + заглушки окружения (логирование, cvar, драйверы-пустышки),
clang 23.1.4, тот же тестовый файл. 4 потока: GetState / GetState+GetCapabilities / SetState / GetKeystroke.

| Вариант | 5 запусков |
|---|---|
| Оригинал 2026-10-25 | 0xC0000374 ×4, 0xC0000005 ×1 (те же коды, что в игре) |
| Патч v2 | 0 ×5; все 9 тестов input PASS |

### 1.5 Исходник соответствует DLL

`input_system.cpp/.h` из GPT_Project_Source_20261025_221029 побайтно равны файлам из ALL_DIAGNOSTICS
(05_SOURCE_CONTEXT/ORIGINAL). Структурное соответствие DLL — п.1.1. Скрипты подготовки сборки
(`prepare_runtime_sdk.py`, `prepare_game_speed_sdk.py`) input не трогают.

## 2. ГИПОТЕЗА (правдоподобно, но не доказано)

* Почему именно Wizard's Tower: мини-игра, видимо, опрашивает ввод ещё и из потока «3D Engine»
  (кроме GameThread). В обычной игре конкурентные вызовы реже. Подтверждается дампами 16188/1836,
  но поведение гостевого кода не трассировалось.
* После патча вылеты в Wizard's Tower исчезнут. Высокая вероятность, но **проверяется только игрой**.

## 3. АЛЬТЕРНАТИВЫ — проверены и отклонены

| Альтернатива | Почему отклонена |
|---|---|
| Чужой аллокатор (wrong allocator) | `devices_` создаётся и освобождается только внутри rexruntime: `operator new`→`malloc` и `operator delete`→`free` одного ucrtbase. fable_2.exe к полям не обращается |
| Несоответствие владения (ownership) | `DeviceInfo` хранится по значению; `device_owners_` — сырые указатели без удаления. Один владелец — `InputSystem` |
| Время жизни guest/host указателей | Строки — обычная host-куча; гостевая память участвует только как `out_state` (запись 16 байт после выбора устройства, не освобождается) |
| XamFree / XamInputSetState | Это метки ближайшего экспорта (п.1.2) |
| Shutdown во время игры | Shutdown вызывает clear, но в дампах его нет в стеках; патч всё равно закрывает и его |
| Аудио / SWF / readback | Уже исключены экспериментами; в стеках отсутствуют |

## 4. Разбор кандидата `WizardTower_ReXGlue_InputRace_FixCandidate_ROOT_20261101`

Направление верное, но:

1. **ABI-риск:** мьютекс вставлен **перед** `window_` — сдвигаются смещения всех полей. Инлайн-методы
   из заголовка (`window()`), скомпилированные в другом модуле, читали бы неправильные смещения. В v2 мьютекс —
   **последний** член класса: смещения не меняются. Размер растёт, но объект создаётся только внутри DLL
   (`CreateDefaultInputSystem`); fable_2.exe импортирует лишь AddDriver/AttachWindow/SetActiveCallback/
   CreateDefaultInputSystem и не импортирует конструктор.
2. `AttachWindow()` (перебирает `drivers_`) была без блокировки — в v2 добавлена.
3. `recursive_mutex` оставлен: исходники драйверов (sdl/mnk/xinput) отсутствуют в экспорте, повторный вход из
   колбэков нельзя исключить. Блокировка держится во время вызовов драйвера; поток SDL hotplug в
   `InputSystem` не заходит, так что взаимная блокировка не ожидается (не проверено без исходников драйверов).
4. Тест кандидата тоже ловит гонку (на стенде оригинал падает и с короткими именами — буфер вектора тоже
   в куче). В v2 тест использует длинные `name/guid` (>15 символов): строки в куче — ровно та сигнатура, что в
   дампах; добавлены GetKeystroke и общий старт потоков.

## 5. Сборка — что нужно и чего не хватает

* `BUILD-LAUNCHER-LOCAL.cmd` **не собирает** native runtime. Штатный путь — `tools\build_runtime_sdk.cmd`
  (вызывается из `build.cmd`): clang/clang++ + lld + Ninja + CMake + Python, x64 VS Developer Prompt,
  `-march=x86-64-v2 -g`, D3D12 ON, Vulkan OFF, цели `rexruntime rexgpu-xenos`.
* **Экспорт GPT_Project_Source непригоден для сборки:** в манифесте `"truncated": true`,
  «not restorable»; нет драйверов input (`src/input/sdl` и др.), `tests/unit` неполный (21 из 42 файлов).
* Установленная DLL собрана в другом дереве: PDB-путь
  `D:\Fable2\Fable-2-Recomp-1.5.0-texturediag-v1\thirdparty\rexglue-sdk\out\win-amd64\rexruntime.pdb`
  (GUID `…4C4C44205044422E` = детерминированный «LLD PDB.»). Этой папки на диске D: сейчас нет.
* fable_2.exe скомпилирован с заголовками SDK. Поэтому DLL нужно собирать **из той же ревизии**, что и установленная.
  `INSTALL` проверяет это автоматически (п.6).

**Нужен один недостающий элемент:** полное native-дерево, из которого собрана установленная DLL —
папка `Fable-2-Recomp-1.5.0-texturediag-v1` (или её полный архив/git-клон с подмодулем `thirdparty/rexglue-sdk`).

## 6. Порядок применения

Все скрипты — ASCII/CRLF, Windows PowerShell 5.1.

1. **Проверка без игры:**
   `tools\RUN-INPUTFIX-HARNESS.cmd <папка rexglue-sdk с оригинальными include\ и src\input\>`
2. **Применить к полному дереву** (сверяет SHA-256 исходных файлов, CRLF допускается, делает `.pre-inputfix.bak`):
   `tools\APPLY-INPUTFIX-SOURCE.cmd -Root <дерево>`; откат: `… -Revert`.
3. **Сборка + все unit-тесты SDK:** `tools\BUILD-REXRUNTIME-INPUTFIX.cmd <дерево>` (x64 Native Tools Prompt).
4. **Установка:** `tools\INSTALL-REXRUNTIME-INPUTFIX.cmd -NewDll <...\rexruntime.dll>`
   * проверяет, что игра закрыта; базовый SHA-256 `1D8CBB87…5AEA`;
   * ABI-проверка: набор экспортов новой DLL = набору установленной (470), и все импорты
     fable_2.exe (368) и rexgpu-xenos.dll (105) из rexruntime.dll разрешаются;
   * резервная копия `rexruntime.dll.pre-inputfix-<время>.bak` с проверкой хеша, журнал `inputfix-install.json`;
   * опционально `-NewGpuDll` для парной rexgpu-xenos.dll.
5. **Откат:** `tools\ROLLBACK-REXRUNTIME-INPUTFIX.cmd` — восстанавливает и сверяет SHA-256.
6. **Игровой тест:** Wizard's Tower, 3 сессии по ≥10 минут (до патча — вылет за 1–2 минуты в 8/8 случаях),
   WER-дампы включены. Также проверить меню, вибрацию, клавиатуру/мышь и переподключение геймпада.

Скрипты проверены 01.11: стенд; APPLY/повтор/Revert на макете дерева (в т.ч. CRLF);
INSTALL — отказ при совпадающей DLL, отказ ABI при чужой DLL, успешная установка + ROLLBACK на **копии** папки игры
(D:\Fable2\Game не изменялась).

## 7. НЕ ПРОВЕРЕНО

* Сборка rexruntime.dll с патчем (нет полного дерева исходников).
* Тест в игре. Пока он не пройден, это **не исправление игры**, а исправление гонки в исходнике.
* Поведение драйверов SDL/MnK/XInput под блокировкой (исходников нет в экспорте).
* Производительность: RefreshDevices по-прежнему выполняется при каждом вызове (как и раньше), теперь последовательно.
  Ожидается незначительное влияние; отдельно не измерялось.

## Файлы

* `thirdparty/rexglue-sdk/include/rex/input/input_system.h` — +`<mutex>`, `state_mutex_` последним членом.
* `thirdparty/rexglue-sdk/src/input/input_system.cpp` — `lock_guard` во всех публичных методах.
* `thirdparty/rexglue-sdk/tests/unit/input/input_assignment_test.cpp` — `FakeDriver::AddNamed` + тест конкурентности.
* `docs/INPUT-RACE-PATCH-v2.diff` — unified diff против оригинала 2026-10-25.
* `docs/evidence/` — фрагменты дизассемблера и стеки cdb.
* `tools/` — APPLY / BUILD / INSTALL / ROLLBACK / HARNESS.
