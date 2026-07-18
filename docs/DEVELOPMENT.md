# Разработка Phyxel

Практическое руководство соответствует baseline `4d4eb7c`. Актуальное состояние и следующий шаг всегда сначала проверяйте в [PROJECT_STATUS.md](PROJECT_STATUS.md), а системные границы — в [ARCHITECTURE.md](ARCHITECTURE.md).

## Требования

- Windows 10/11;
- .NET 8 SDK;
- DirectX 11-совместимая видеокарта и актуальный драйвер;
- Git;
- PowerShell или обычная Windows command prompt.

Проект использует `net8.0-windows`, KNI WinForms DX11 и runtime-компиляцию HLSL через SharpDX D3DCompiler. Реальные GPU acceptance нельзя заменить только CPU-проверками.

## Сборка

Из корня репозитория:

```powershell
dotnet restore Phyxel.sln
dotnet build Phyxel.sln -c Debug --nologo
```

Короткая команда Windows:

```powershell
./build.bat debug
```

`build.bat` принимает `debug`, `release` или `all`. Для завершённого engine-коммита Debug-сборка должна иметь 0 ошибок и 0 предупреждений.

## Запуск

С одновременной сборкой:

```powershell
dotnet run --project Phyxel.csproj -c Debug
```

После готовой сборки:

```powershell
./bin/Debug/net8.0-windows/Phyxel.exe
```

При запуске проверьте, что core JSON скопированы в `bin/Debug/net8.0-windows/Materials/core`, окно создаёт GPU-ресурсы и в меню присутствует один обычный песок, вода, металл, камень, газ, опора и ластик.

## Холодная компиляция shaders

Скомпилированные bytecode-файлы кешируются в `%LOCALAPPDATA%/Phyxel/ShaderCache`. После изменения HLSL или общей C#/HLSL-структуры очистите только этот каталог:

```powershell
Remove-Item -LiteralPath "$env:LOCALAPPDATA/Phyxel/ShaderCache" -Recurse -Force -ErrorAction SilentlyContinue
```

Затем запустите игру или GPU acceptance. Инициализация ресурсов должна заново скомпилировать зарегистрированные entry points из:

- `BrushApplication.hlsl`;
- `CellularAutomataSolver.hlsl`;
- `SolidBodySolver.hlsl` и `SolidComponents.hlsl`;
- `ThermalDiffusion.hlsl`;
- `PhaseTransitions.hlsl`;
- `TemperatureProbe.hlsl`;
- `RenderComposition.hlsl`.

Ошибка compile/create resource считается блокером коммита. Cache и `bin/obj` в Git не добавляются.

## CPU regression verifiers

В проекте нет отдельного test runner: встроенные verifiers выбираются переменной окружения и завершают процесс кодом результата. Запускайте их по одному на уже собранном executable.

### World codec и совместимость сцен

```powershell
$env:PHYXEL_VERIFY_WORLD_CODEC = '1'
./bin/Debug/net8.0-windows/Phyxel.exe
Remove-Item Env:PHYXEL_VERIFY_WORLD_CODEC
```

Проверяет layouts 32/36 байт, чтение v3/v4/v5, legacy palette, миграции, повреждённые заголовки/длины и v5 round-trip.

### Thermal properties материалов

```powershell
$env:PHYXEL_VERIFY_THERMAL_MATERIALS = '1'
./bin/Debug/net8.0-windows/Phyxel.exe
Remove-Item Env:PHYXEL_VERIFY_THERMAL_MATERIALS
```

Проверяет 64-байтный C#/HLSL layout, core-значения, defaults внешнего JSON, строгие диапазоны и runtime-позиции GPU-таблицы.

### Phase-transition schema и registry

```powershell
$env:PHYXEL_VERIFY_PHASE_MATERIALS = '1'
./bin/Debug/net8.0-windows/Phyxel.exe
Remove-Item Env:PHYXEL_VERIFY_PHASE_MATERIALS
```

Проверяет raw string targets, строгую вложенную schema, двухэтапное разрешение ссылок, каскад external dependencies, interval-based cycle validation, offsets 64-байтного GPU layout и sentinel defaults.

### Thermal diffusion и brush logic

```powershell
$env:PHYXEL_VERIFY_THERMAL_DIFFUSION = '1'
./bin/Debug/net8.0-windows/Phyxel.exe
Remove-Item Env:PHYXEL_VERIFY_THERMAL_DIFFUSION
```

Проверяет fixed-step scheduler, энергетику, перенос температуры, gas merge, probe и команды температурной кисти без зависимости от интерактивного UI.

### Phase-transition runtime

```powershell
$env:PHYXEL_VERIFY_PHASE_RUNTIME = '1'
./bin/Debug/net8.0-windows/Phyxel.exe
Remove-Item Env:PHYXEL_VERIFY_PHASE_RUNTIME
```

Проверяет layout phase constants/summary, строгие предикаты и below-first priority, CPU-нормализацию всех kind-пар, summary-флаги, Pause/catch-up dispatch policy и одноразовый fallback wake-up.

Успешный verifier обязан вернуть exit code 0. После аварийного прерывания очистите установленную env-переменную вручную, чтобы следующий обычный запуск не повторил verifier.

## GPU acceptance harness

Acceptance-сценарий выбирается через `PHYXEL_ACCEPTANCE_MODE` или совместимый alias `PHYXEL_SPEC_SCENARIO`. Harness создаёт сцену, ждёт контрольный кадр/tick, читает GPU snapshot/metrics и печатает `PHYXEL_ACCEPTANCE_SUCCESS` либо `PHYXEL_ACCEPTANCE_FAILED`.

Пример базового сценария:

```powershell
$env:PHYXEL_ACCEPTANCE_MODE = 'sand'
$env:PHYXEL_ARTIFACT_DIR = './artifacts/local-sand'
./bin/Debug/net8.0-windows/Phyxel.exe
Remove-Item Env:PHYXEL_ACCEPTANCE_MODE
Remove-Item Env:PHYXEL_ARTIFACT_DIR
```

Acceptance-only материалы не входят в пользовательскую сборку. Для сценариев внешних и thermal-материалов явно укажите их каталог:

```powershell
$env:PHYXEL_MATERIALS_PATH = (Resolve-Path './Diagnostics/AcceptanceMaterials').Path
$env:PHYXEL_ACCEPTANCE_MODE = 'conductivity_compare'
$env:PHYXEL_ARTIFACT_DIR = './artifacts/local-conductivity'
./bin/Debug/net8.0-windows/Phyxel.exe
Remove-Item Env:PHYXEL_MATERIALS_PATH
Remove-Item Env:PHYXEL_ACCEPTANCE_MODE
Remove-Item Env:PHYXEL_ARTIFACT_DIR
```

Дополнительные параметры harness:

| Переменная | Назначение |
|---|---|
| `PHYXEL_ACCEPTANCE_SCALE` | Масштаб сценария, если он не требует native resolution. |
| `PHYXEL_ACCEPTANCE_TARGET_FPS` | Целевой FPS для воспроизводимого шага harness. |
| `PHYXEL_ACCEPTANCE_CAPTURE_FRAME` | Переопределение кадра snapshot. |
| `PHYXEL_ACCEPTANCE_HYDRAULICS` | Явно включить/выключить гидравлику. |
| `PHYXEL_ACCEPTANCE_RECORD=1` | Периодически записывать промежуточные данные. |
| `PHYXEL_ARTIFACT_DIR` | Каталог отчётов и снимков. |
| `PHYXEL_VERIFY_SCENE_PATH` | Сцена для save/load acceptance. |

Основной regression-набор следует выбирать по затронутой подсистеме:

- cellular: `sand`, `slope`, `gas`, `water_drain`, `flat_surface`;
- hydraulics: `communicating_vessels`, `pressure_tube`, saved pressure/isolation;
- granular/liquid: `underwater_granular`, `granular_displacement`, `granular_barrier`, `granular_barrier_hydraulic`;
- solid: `solid_gravity`, `buoyancy`, saved gravity;
- external kinds: `external_granular`, `external_liquid`, `external_gas`, `external_solids`;
- thermal: `thermal_uniform`, `thermal_contact`, `thermal_capacity`, `conductivity_compare`, `thermal_insulator`, `thermal_vacuum`, `thermal_gas`, `temperature_probe_gpu`;
- temperature tool: `temperature_brush`, `temperature_tool`.
- phase runtime: `phase_dispatch_smoke` с `PHYXEL_MATERIALS_PATH=Diagnostics/PhaseAcceptanceMaterials`.

Для изменения общей GPU-структуры или pass order прогоните все группы. Acceptance-артефакты создаются только локально и не должны попадать в коммит.

## Добавление материала

Обычный материал должен добавляться данными без изменения общих C# или HLSL-файлов.

1. Выберите уникальный ID `namespace:name` в нижнем регистре.
2. Создайте отдельный JSON вне `Materials/core`; для acceptance-only материала используйте `Diagnostics/AcceptanceMaterials`.
3. Выберите существующий `kind` и только реально поддерживаемые flags.
4. Укажите физические, thermal и UI-свойства.
5. Соберите/перезапустите игру и убедитесь, что материал появился в меню, если `ui.hidden=false`.
6. Добавьте GPU acceptance, проверяющий поведение, количество материала и save/load.

Минимальный полный пример:

```json
{
  "schema": 1,
  "id": "example:material",
  "name": { "ru": "Материал", "en": "Material" },
  "kind": "granular",
  "flags": [],
  "color": "#C8A060",
  "physics": {
    "density": 1.5,
    "friction": 0.45,
    "flowRate": 0.0
  },
  "thermal": {
    "initialTemperature": 20.0,
    "conductivity": 0.15,
    "heatCapacity": 1.0
  },
  "ui": {
    "order": 100,
    "hidden": false
  }
}
```

Старый внешний JSON без `thermal` получает defaults `20 / 0.15 / 1`. Неизвестное поле внутри `thermal` является ошибкой. Внешний файл не может заменить `core:*`; общий лимит — 256 материалов.

Для ручной проверки «без пересборки» положите новый JSON в `Materials` рядом с уже собранным executable, но не в `Materials/core`, и перезапустите игру. Runtime indices могут измениться — это нормально; сохранения используют строковую палитру.

## Добавление универсальной системы

До реализации подготовьте короткий план и зафиксируйте:

1. данные и version/schema migration;
2. точные C# и HLSL layouts;
3. место нового pass в GPU schedule;
4. правила для moving granular/liquid/gas/solid и сохранения массы/энергии;
5. условия пропуска pass и стоимость памяти/кадра;
6. совместимость v3/v4/v5;
7. CPU regression и реальные GPU acceptance;
8. границу задачи — какие соседние системы не входят в коммит.

Во время реализации:

- используйте `SimulationKind`, свойства и универсальные flags, а не конкретные runtime indices;
- не добавляйте материал-специфическую ветку в общий shader;
- при изменении layout одновременно меняйте C#, HLSL, размер GPU buffer, codec и verifier;
- сохраняйте старую структуру для старого формата, не переименовывая её в текущую;
- стабилизируйте один небольшой коммит до перехода к следующему;
- после каждого шага выполняйте Definition of Done из `PROJECT_STATUS.md`.

## Проверка масштабов

Разрешённые presets и ожидаемые размеры:

| Scale | Grid |
|---:|---:|
| 25% | 480×270 |
| 35% | 672×378 |
| 50% | 960×540 |
| 75% | 1440×810 |
| 85% | 1632×918 |
| 100% | 1920×1080 |

После изменения GPU-ресурсов вручную создайте сетку на каждом preset и проверьте Clear, обычный запуск и отсутствие device/resource errors.

Известный долг: OOM-fallback сейчас вычитает 0,25 из текущего scale. Будущая изолированная правка должна выбирать предыдущий preset строго по цепочке `100 → 85 → 75 → 50 → 35 → 25`; обычную работу slider при этом менять нельзя.

## Правила коммитов

- Один коммит — одна законченная техническая цель.
- Не смешивайте универсальную механику, новый контент и рефакторинг.
- Не начинайте следующую фазу, пока текущий коммит не собирается и не проходит относящиеся к нему regression/acceptance.
- Не добавляйте `bin`, `obj`, shader cache, `artifacts/*`, временные сцены, записи и screenshots.
- Перед staging выполните `git diff --check`, просмотрите `git diff --stat` и `git status --short`.
- Проверяйте, что diff содержит только разрешённые задачей файлы.
- Документационный коммит не должен содержать изменения кода или сгенерированные артефакты.
- После major engine/content milestone обновляйте `README.md` при необходимости и обязательно `PROJECT_STATUS.md`.

## Правила handoff

Перед завершением рабочей сессии обновите верхний блок **CURRENT HANDOFF** в `PROJECT_STATUS.md`:

- Current HEAD с коротким названием коммита;
- Just completed;
- Current task, если работа ещё продолжается;
- Next task;
- Blocking issues;
- Tests last run и их результат.

Если задача не завершена, дополнительно оставьте:

- точный список изменённых файлов;
- что уже работает;
- что ещё не работает;
- команду и результат падающего теста;
- какие проверки ещё не запускались;
- что следующему разработчику не нужно делать повторно.

Не скрывайте regression и не отмечайте фазу завершённой только потому, что код компилируется. Handoff должен позволить продолжить работу без чтения всей истории коммитов.

## Финальная проверка перед handoff

```powershell
git diff --check
dotnet build Phyxel.sln -c Debug --nologo
git status --short
```

Если менялись shaders или GPU layouts, между `git diff --check` и сборкой/acceptance очистите shader cache и прогоните нужный cold GPU-набор. Итоговый статус должен содержать только ожидаемые исходные/документационные файлы, а после коммита — быть чистым.
