# Архитектура Phyxel

Документ описывает архитектуру на кодовом baseline `4d4eb7c`. Он фиксирует действующие границы системы, а не проектирует ещё не реализованные реакции, фазовые переходы или паки.

## Архитектурные правила

1. Вся физика отдельных клеток выполняется на GPU; CPU координирует ресурсы, команды, сохранения и проверки.
2. Материал содержит данные, а движок реализует общее поведение его `SimulationKind` и flags.
3. Один материал описывается одним JSON-файлом.
4. Постоянная идентичность материала в файлах и сохранениях — строковый `namespace:name`.
5. Компактный `ushort RuntimeIndex` существует только в рамках текущего процесса и может измениться после перезапуска.
6. Shader и общий engine-код не должны проверять ID конкретного материала. Единственное индексное системное соглашение — `core:empty` имеет runtime-индекс 0, потому что GPU-буферы очищаются нулями.
7. Будущие реакции должны описываться JSON-правилами и компилироваться в общую runtime/GPU-таблицу.
8. Каждая универсальная механика добавляется отдельным engine-коммитом; контент использует её через данные.
9. Каждый новый элемент и каждая общая механика получают acceptance-сценарий на реальном GPU.
10. JSON-схемы и форматы сохранений версионируются; старый бинарный layout никогда не переопределяется текущей C#-структурой.

## Общий поток данных

```text
Materials/**/*.json
        ↓
MaterialFileLoader → MaterialRegistry → runtime ushort indices
        ↓                                  ↓
MaterialProperties GPU table          scene palette mapping
        ↓
HLSL compute passes ↔ ping-pong GridCell buffers
        ↓
RenderComposition → back buffer
```

Вход пользователя идёт отдельным путём: UI/Input формирует `BrushDrawCommand`, coordinator загружает команды на GPU, `BrushApplication.hlsl` изменяет клетки, после чего остальные проходы видят обновлённую сетку.

## Основные модули

| Область | Ответственность |
|---|---|
| `Program.cs`, `PhyxelGame.cs` | Запуск игры, основной цикл, переключатели и интеграция UI, GPU и сериализации. |
| `Core/` | Настройки симуляции, включая разрешение и scale presets. |
| `Materials/` | JSON-модель, строгая загрузка, core ID и runtime registry. |
| `Physics/PhysicsDataStructures.cs` | Общие C# layouts данных, которые зеркалируются в HLSL. |
| `Graphics/` | Жизненный цикл DX11-ресурсов, shader compilation/cache, dispatch-порядок, thermal scheduler и GPU timing. |
| `Content/Shaders/` | Кисть, cellular/solid-симуляция, thermal diffusion, probe и композиция. |
| `Input/`, `UI/` | Кодирование команд кисти и пользовательские элементы управления. |
| `Serialization/` | Сцены v3/v4/v5, строковые палитры, world codec и GPU snapshot upload/readback. |
| `Diagnostics/` | CPU regression, GPU acceptance, сценарии, метрики и артефакты. |

## Материалы и runtime registry

### Загрузка

`MaterialRegistry` сначала загружает обязательный набор из `Materials/core`, затем внешние JSON. В установленной игре корневая папка по умолчанию находится рядом с executable; для diagnostics её можно переопределить через `PHYXEL_CORE_MATERIALS_PATH` и `PHYXEL_MATERIALS_PATH`.

Loader выполняет следующие действия:

- читает schema 1 и нормализует ID в нижний регистр;
- проверяет формат `namespace:name`, дубли, kind, flags и числовые диапазоны;
- запрещает внешнему файлу заменять core ID;
- останавливает запуск при ошибке core-файла;
- пропускает повреждённый внешний файл и пишет `PHYXEL_MATERIAL_ERROR`;
- ограничивает общий реестр 256 материалами;
- сортирует `core:empty` первым, остальные материалы — по ID, затем назначает `RuntimeIndex`;
- строит UI-список по `ui.order` и `ui.hidden`.

Сейчас общие kinds: `none`, `granular`, `liquid`, `gas`, `solid`, `tool`. Единственный универсальный flag — `movable-solid`. Eraser имеет kind `tool`: удаление кодируется режимом команды, а индекс eraser не попадает в активную клетку.

### GPU-таблица

`MaterialRegistry.CreateGpuTable()` размещает `MaterialProperties` в runtime-позициях. Все shaders получают одну и ту же таблицу и принимают решения по `SimulationKind`, `Density`, `Friction`, `FlowRate`, thermal-полям и flags. Runtime-индекс — ключ массива, а не постоянная идентичность материала.

## Общие layouts C# и HLSL

Структуры используют последовательный layout без скрытого padding и должны оставаться синхронными с `Content/Shaders/PhysicsShared.hlsli`.

### `GridCell` — 36 байт

| Поле | Тип | Назначение |
|---|---|---|
| `MaterialIndex` | `uint` | Текущая runtime-позиция материала. |
| `Mass` | `float` | Масса/заполнение клетки. |
| `VelocityX`, `VelocityY` | `float` | Импульс клеточного материала. |
| `Pressure` | `float` | Гидравлическое состояние жидкости. |
| `IsActive` | `uint` | Нулевая клетка не содержит материал. |
| `BodyId` | `uint` | Временная принадлежность movable-solid body. |
| `RestFrames` | `uint` | Состояние покоя клеточной физики. |
| `Temperature` | `float` | Температура клетки в °C. |

`LegacyGridCellV3V4` навсегда остаётся отдельной 32-байтной структурой без температуры. Runtime-проверки фиксируют оба размера.

### `MaterialProperties` — 48 байт

Порядок полей: `Flags`, `SimulationKind`, `Density`, `Friction`, `FlowRate`, четыре компонента цвета, `InitialTemperature`, `ThermalConductivity`, `HeatCapacity`.

Thermal-параметры валидируются в пределах:

- initial temperature: `-273.15…5000` °C;
- conductivity: `0…1`;
- heat capacity: `0.01…100`.

### `BrushDrawCommand` — 36 байт

Команда содержит координаты, runtime-индекс, радиус, плотность, mode, seed и `TargetTemperature`. Режимы: нанести материал, стереть клетку, установить температуру. Temperature mode меняет только активные клетки и не создаёт материал.

## GPU-ресурсы

`GpuResourceLifecycleManager` создаёт или пересоздаёт ресурсы под текущее разрешение. Основные данные симуляции находятся в двух structured buffers `GridCell`, которые меняются ролями source/destination между проходами. Отдельно существуют:

- immutable/read-only для кадра таблица `MaterialProperties`;
- командный buffer кисти;
- вспомогательные buffers cellular proposals, статистики, маршрутов давления и solid-body geometry;
- staging/query ресурсы для сохранения, probe, acceptance и timing;
- output texture композиции.

Сетка выделяется лениво. Clear обнуляет buffers, что одновременно создаёт валидный `core:empty` благодаря инварианту runtime index 0.

## Порядок GPU-проходов кадра

`SimulationDispatchCoordinator.DispatchFrame()` выполняет только нужные для состояния мира ветви:

1. создать/изменить размер ресурсов и при необходимости очистить их;
2. загрузить и применить brush commands;
3. завершить отложенное обновление cellular rest-state;
4. при включённой solid gravity: разметить компоненты, собрать геометрию и выполнить solid-body pass;
5. при необходимости подготовить hydraulic routing buffers;
6. выполнить основной cellular automata schedule, включая общие granular/liquid/gas-предикаты, pressure routes и возможный второй fluid step;
7. получить предыдущие GPU timing results и выполнить от нуля до четырёх фиксированных thermal ticks;
8. при изменившемся представлении выполнить `RenderComposition`.

Cellular и solid-проходы используют несколько proposal/resolve стадий, чтобы конкурирующие клетки не записывали один destination произвольно. Конкретный schedule оптимизируется по масштабу, гидравлике и присутствующим типам материала, но не по ID конкретного материала.

## Тепловая подсистема

`FixedStepThermalScheduler` накапливает frame time и выдаёт шаги по 0,05 с, максимум четыре за кадр. Pause полностью останавливает diffusion; температурная кисть при этом остаётся доступной как явная пользовательская команда.

`ThermalDiffusion.hlsl` читает только source grid и пишет только destination grid, поэтому соседние threads не конфликтуют. Для каждой активной клетки берутся четыре ортогональных соседа. Поток энергии учитывает:

- разницу температур;
- гармоническое среднее проводимостей двух материалов;
- меньшую из контактирующих теплоёмкостей `Mass × HeatCapacity`;
- ограниченную долю обмена за tick.

Пара клеток получает равные по модулю вклады при вычислении каждой стороны, поэтому суммарная энергия сохраняется с учётом float-погрешности. Неактивный neighbor не участвует: empty в текущей модели является вакуумом.

Газовая клетка хранит температуру и массу. При объединении одинакового gas material температура пересчитывается из суммарной энергии; разные `MaterialIndex` не смешиваются.

`TemperatureProbe.hlsl` считывает одну координату в маленький result buffer. CPU получает результат асинхронно, не блокируя каждый кадр полным snapshot.

## Форматы сцен

Сохранение состоит из JSON-метаданных сцены и бинарной секции `.world`.

### v3

- world header 20 байт, неявный cell stride 32;
- numeric material indices интерпретируются только `LegacySceneV3Loader`;
- legacy index 4 отображается в `core:stone`;
- после codec-конвертации температура берётся из текущего JSON материала.

### v4

- world header 20 байт, неявный cell stride 32;
- JSON содержит `MaterialPalette` как массив строковых ID, где позиция — компактный индекс сцены;
- `core:gold_sand → core:sand` и `core:concrete → core:stone` выполняются при загрузке с предупреждением;
- температура после конвертации берётся из материала.

### v5 — текущая запись

- world header 24 байта содержит magic, version, width, height, явный stride 36 и длину секции;
- JSON сохраняет строковую палитру;
- клетки содержат temperature и валидируются при чтении;
- writer создаёт только v5, readers сохраняют поддержку v3/v4.

Перед записью snapshot копируется и числовой lookup переводит runtime indices в scene compact indices. Живая сетка не модифицируется. При чтении палитра один раз переводится в массив scene-to-runtime, затем весь snapshot remap выполняется численно.

`ReadWorldAsync` проверяет положительные размеры, stride конкретной версии, переполнение, точную длину секции, обрезанный header/data и лишние байты. `WorldCellCodec` отдельно преобразует legacy layout по полям.

## Границы будущего расширения

Следующие возможности пока отсутствуют и не должны подразумеваться текущими структурами:

- phase-transition schema и универсальный GPU pass;
- огонь, горение, дым и постоянные heat sources;
- JSON reaction rules и GPU reaction table;
- explosion/corrosion/dissolution;
- pack manifest, dependency resolver и hub.

При проектировании фазовых переходов нужно отдельно решить порядок относительно cellular/thermal passes, сохранение энергии при смене материала и поведение движущейся клетки. Добавлять проверки `core:water` или других конкретных ID в HLSL нельзя.
