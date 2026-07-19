# Универсальная система горения

> **Implementation update (2026-07-19):** The design is now implemented and
> tuned in the working tree. Combustion uses `Mass` as fuel, a mandatory
> per-material `maximumTemperature` (wood 900 C), stable heat-capacity scaling,
> and inert coal residue. Water phase changes use `latentHeat: 2256` so boiling
> consumes excess thermal energy instead of converting an entire cell at once.
> `MaterialProperties` is 104 bytes; `GridCell` and the v6 save contract are
> unchanged by these additions. Sparse gases use a deterministic
> proposal/resolve advection pass, eliminating checkerboard stripe motion.
> Thermal exchange is intentionally faster (`16`, capped at `0.80`) to make
> contact cooling visible.

> **Residue correction:** The bundled `core:coal` is now a granular,
> BCOL-like residue rather than a fixed-solid clone of the wood shape. A
> burnt wood cell therefore becomes a falling charcoal particle that can pile
> up. This matches the Powder Toy distinction between fixed COAL and broken
> coal more closely than preserving a solid black tree silhouette. The target
> remains data-driven; external combustion materials may still choose a fixed
> solid residue.

Статус документа: архитектурный анализ перед реализацией. Игровая механика горения, `core:wood`, `core:coal`, дым, CO₂ и пламя этим документом не добавляются.

Цель первой пользовательской цепочки:

```text
дерево рисуется как fixed solid
→ нагрев выше температуры воспламенения
→ расход горючей части Mass и выделение тепла
→ передача тепла существующей thermal diffusion
→ воспламенение соседнего дерева
→ охлаждение водой через ту же diffusion прекращает горение
→ после выгорания остаётся уголь
```

Обязательный архитектурный инвариант: shader не знает `core:wood`. Любой допустимый внешний материал получает то же поведение из JSON через общую GPU-таблицу.

## 1. Рекомендуемая архитектура первой версии

Рекомендуется **stateless combustion v1**:

- отдельного `IsBurning` и новых полей `GridCell` нет;
- клетка горит тогда и только тогда, когда материал имеет валидное combustion-правило, в клетке осталась горючая масса и `Temperature > IgnitionTemperature`;
- точное равенство порогу не зажигает клетку;
- охлаждение до `Temperature <= IgnitionTemperature` немедленно прекращает расход массы и выделение тепла;
- повторный нагрев снова запускает горение;
- `Mass` хранит оставшееся вещество клетки, а горючая часть равна `max(0, Mass - residueMass)`;
- для fixed-solid target в минимальной схеме `residueMass` берётся из его канонической массы `Density`; для `core:empty` остаток равен нулю;
- тепло зависит от фактически сгоревшей массы и `HeatCapacity`, а температура жёстко ограничивается действующим диапазоном `-273.15…5000 °C`;
- один in-place full-grid combustion pass выполняется после thermal-пачки и до phase pass;
- pass запускается только при наличии горючих материалов и хотя бы одного thermal tick в кадре;
- визуализация использует тот же predicate и подсвечивает реально горящую клетку без отдельного выбираемого fire material;
- smoke, CO₂ и физические flame-клетки не входят в v1.

Первая реализация намеренно ограничивается источником `fixed solid` и результатом `fixed solid` либо `core:empty`. Это покрывает `wood → coal` и не переносит преждевременно топливную семантику `Mass` на liquid/gas/granular solver.

## 2. Что подтверждено текущим кодом

### 2.1 Материалы и JSON

- `MaterialDefinition` хранит `MaterialProperties`, строковый ID, runtime index и отдельно raw `PhaseTransitions`.
- `MaterialFileLoader` использует schema 1, нормализует `namespace:name`, проверяет конечность чисел и строго валидирует вложенный `thermal`, включая неизвестные и дублирующиеся поля `thermal.transitions`.
- Повреждённый bundled core JSON останавливает запуск. Повреждённый внешний JSON пропускается с `PHYXEL_MATERIAL_ERROR`.
- `MaterialRegistry` сначала стабилизирует допустимый набор, затем сортирует его, назначает runtime indices и только после этого разрешает string targets фазовых переходов.
- Невалидные внешние зависимости удаляются итеративно до устойчивого набора. Такой же двухэтапный шаблон нужен `combustion.burnedInto`.
- `core:empty` имеет runtime index 0; остальные числовые индексы не являются постоянными.
- Текущий `MaterialProperties` имеет stride 64 байта, переходы используют `uint.MaxValue` как sentinel отсутствующего target.

Важная деталь: корневой `MaterialFileDocument.UnknownFields` сейчас не проверяется, хотя вложенный `thermal` строгий. Combustion v1 обязан как минимум строго проверять сам объект `combustion`; отдельное ужесточение всей root schema не следует незаметно смешивать с первым schema/layout-коммитом без compatibility-проверки внешних материалов.

### 2.2 `GridCell` и `Mass`

Текущий C#/HLSL layout — 36 байт:

| Offset | Поле | Фактическое назначение |
|---:|---|---|
| 0 | `MaterialIndex` | Runtime-позиция материала; remap-ится через строковую palette. |
| 4 | `Mass` | Количество/заполнение клетки и thermal capacity. |
| 8/12 | `VelocityX/Y` | Движение cellular material. |
| 16 | `Pressure` | Состояние liquid hydraulics. |
| 20 | `IsActive` | Ноль означает empty; штатная активная клетка нормализуется в `1`. |
| 24 | `BodyId` | Временная принадлежность movable-solid body. |
| 28 | `RestFrames` | Cellular/solid sleep state. |
| 32 | `Temperature` | Температура в °C. |

`BrushApplication.hlsl` создаёт fixed и movable solid с `Mass = material.Density`; остальные kinds получают `Mass = 1`. Параметр плотности кисти определяет вероятность нанесения пикселя, а не массу созданной клетки.

Для fixed solid уменьшение `cell.Mass` совместимо с существующей физикой:

- fixed solid остаётся препятствием, пока `IsActive != 0` и kind равен solid;
- movable-solid mass и buoyancy сейчас вычисляются из `MaterialProperties.Density`, а не из `cell.Mass`;
- composition для solid также не масштабирует цвет по `Mass`;
- thermal diffusion, напротив, использует `Mass × HeatCapacity`, поэтому частично выгоревшая клетка получает меньшую теплоёмкость;
- cellular gas действительно переносит и делит `Mass`, а liquid/granular используют её как количество/заполнение. Поэтому эти source kinds нельзя включать в v1 без отдельного acceptance.

Свободного поля для `IsBurning` нет. `RestFrames`, `BodyId`, `Pressure` и velocity имеют действующее назначение. Упаковка бита в `IsActive` или `MaterialIndex` изменила бы существующий codec/layout-контракт, конфликтовала бы с нормализацией кисти/phase pass и была бы скрытым изменением формата.

### 2.3 Температура и порядок проходов

- `FixedStepThermalScheduler` выдаёт шаги по `0.05 s`, максимум четыре за rendered frame.
- При Pause `Advance` возвращает 0 до прибавления frame time: время паузы не попадает в accumulator. Существующая дробная часть до Pause сохраняется, но catch-up за время Pause отсутствует.
- `ThermalDiffusion.hlsl` — ping-pong pass; capacity равна `HeatCapacity × max(Mass, 0.0001)`.
- Coordinator выполняет brush, solid, cellular, затем `0..4` thermal ticks, один phase pass и composition.
- Temperature brush работает на Pause, но thermal и phase не выполняются до Continue.
- Phase pass in-place, меняет только собственную клетку, использует async summary ring из трёх slots и GPU timestamps с `DoNotFlush`.

Combustion естественно вставляется между thermal и phase, используя тот же fixed-step clock и тот же проверенный шаблон summary/timing.

### 2.4 Физика, composition и diagnostics

- Fixed solid не требует `BodyId`; movable solid требует component labeling и topology wake-up.
- Granular/liquid/gas работают через общий cellular shader и proposal/resolve schedule; прямые конкурентные записи в соседнюю клетку не допускаются.
- `RenderComposition.hlsl` уже читает grid и material table на каждый пиксель, поэтому burning predicate можно вычислить без readback и без изменения клетки.
- `SimulationStatistics` собирается атомиками в composition. Burning count не требуется для runtime; при необходимости diagnostics должен получить отдельный счётчик, а не менять игровую статистику без причины.
- Acceptance infrastructure поддерживает промежуточные GPU snapshots, target FPS, acceptance-only материалы, async query/readback и отдельные timestamp-метрики.

### 2.5 Сохранения

- v5 хранит 36-байтный `GridCell`, включая `Mass` и `Temperature`, и строковую palette.
- v3/v4 используют отдельный 32-байтный legacy layout; температура при миграции берётся из текущих material properties.
- Загрузка v5 сохраняет `Mass` и `Temperature` побитово, затем remap-ит palette в актуальные runtime indices.
- Pause не входит в scene state и не перезаписывается `SimulationStateSerializer.Apply`, поэтому Load во время Pause оставляет симуляцию на Pause.
- Отсутствующий palette material заменяется `core:empty` с `PHYXEL_SCENE_WARNING`.

Если состояние горения определяется только material ID, `Mass` и `Temperature`, v5 уже содержит всё необходимое.

## 3. Сравнение вариантов состояния горения

| Критерий | A — вывод из material/Mass/Temperature | B — отдельный `IsBurning` | C — новые per-cell поля |
|---|---|---|---|
| Устойчивость | Детерминированный строгий predicate; около порога возможны включения/выключения | Даёт полноценный hysteresis | Даёт hysteresis и сложный прогресс |
| Тушение | `T <= ignition` сразу останавливает горение | Можно продолжать до отдельного extinguish threshold | Полностью настраиваемо |
| FPS-независимость | Да, через `burnRate × fixed dt` | Да | Да |
| Разные массы | Длительность зависит от доступной горючей массы | То же | Можно отделить fuel от physical Mass |
| Save/load | v5 уже хранит всё | Нужен доказанно безопасный persistent bit; его нет | Требуется v6 |
| GPU memory при 100% | 0 новых bytes/cell | Если отдельное поле — как C | +4 bytes/cell даёт примерно +15.8 MiB на два grid buffers, ещё +7.9 MiB на staging и на каждую CPU-копию |
| Визуализация | Тот же predicate, без рассинхронизации | Прямая | Прямая |
| Слабое место | Нет настоящего hysteresis и памяти «уже зажжено» | Упаковка в существующие поля небезопасна | Layout 40+ bytes, codec и migration |
| Пригодность v1 | **Рекомендуется** | Не рекомендуется | Только если hysteresis станет обязательным требованием |

Если добавить два `float` (`FuelRemaining` и `BurnProgress`), stride станет 44 байта: два grid buffers на 1920×1080 вырастут примерно на 31.6 MiB, staging — ещё на 15.8 MiB. При этом `FuelRemaining` дублирует пригодный для fixed-solid v1 `Mass`, а `BurnProgress` не нужен для линейного расхода топлива.

### Почему hysteresis нельзя корректно получить без сохранённого состояния

Две клетки одного материала с одинаковыми `Mass` и `Temperature` неразличимы для shader. Если одна никогда не загоралась, а другая уже горела и остыла, применить к ним разные ignition/extinguish thresholds невозможно без дополнительного persistent state.

Попытка считать `Mass < Density` признаком прежнего горения ненадёжна: phase/save/diagnostics могут создать клетку с нестандартной массой, а после тушения частично сгоревшая клетка навсегда получила бы пониженный порог повторного воспламенения. Поэтому `extinguishTemperature` нельзя включать в stateless schema v1.

## 4. Рекомендуемая JSON-схема v1

```json
"combustion": {
  "ignitionTemperature": 300.0,
  "burnRate": 0.08,
  "heatPerMass": 1800.0,
  "burnedInto": "core:coal"
}
```

Все четыре поля обязательны:

| Поле | Единица/диапазон | Правило |
|---|---|---|
| `ignitionTemperature` | конечное число `-273.15 ≤ T < 5000` °C | Горение только при строгом `Temperature > threshold`; порог 5000 недостижим из-за общего clamp. |
| `burnRate` | `(0…100]` mass units/s | Абсолютная скорость, не доля в секунду. |
| `heatPerMass` | `(0…1_000_000]` thermal-energy units/mass | Энергия на фактически сгоревшую массу; не °C/s. |
| `burnedInto` | нормализованный `namespace:name` | Raw string до стабилизации registry, затем runtime index. |

Почему `burnRate` не fraction/s: абсолютная скорость даёт понятную длительность `(initialMass - residueMass) / burnRate`, линейно работает с разной массой и достигает конечного состояния без произвольного процентного cutoff.

Почему `heatPerSecond` заменяется на `heatPerMass`: при последнем неполном шаге или малом остатке тепла должно выделиться пропорционально реально сгоревшему веществу, а не полному времени pass.

Правила schema/registry:

1. `combustion` необязателен; его отсутствие означает негорючий материал.
2. Объект не может быть `null`, пустым, массивом или содержать неизвестные/дублирующиеся поля.
3. `NaN`, `Infinity`, нулевые и отрицательные rates отклоняются.
4. Source v1 — только `solid` без `movable-solid`.
5. Source v1 и non-empty `burnedInto` target не могут иметь `thermal.transitions`: при порядке combustion→phase продукт иначе мог бы пройти ещё один material transition в том же кадре. Совмещение систем требует отдельного решения о precedence.
6. Target v1 — fixed solid либо специальный `core:empty`.
7. Self-target запрещён; `none`/`tool` запрещены, кроме явного разрешения `core:empty` как полного удаления.
8. Для fixed-solid target его `Density` должна быть меньше source `Density`, иначе стандартная клетка не имеет горючей части.
9. Core source может ссылаться только на bundled core target. Ошибка core фатальна.
10. Невалидный внешний source/target пропускается с `PHYXEL_MATERIAL_ERROR`; зависимые внешние sources повторно проверяются до устойчивого набора.
11. `burnedInto` разрешается после окончательной сортировки runtime registry; числовой индекс никогда не сохраняется в JSON или сцену.
12. `emissions` не является частью v1 и не должен молча включать частичную механику.

`minimumBurnMass` как настраиваемый numerical epsilon не нужен. Численная защита должна быть единой engine-константой, например `0.0001`, согласованной с thermal minimum. Остаточная масса продукта — отдельная семантика, описанная ниже.

`maximumTemperature` также не добавляется в каждый материал v1. Универсальный hard clamp использует уже действующий верхний предел `5000 °C`. Если gameplay покажет, что разным топливам нужны разные пределы, это можно добавить material-level полем без изменения `GridCell` и save version.

## 5. `Mass` как вещество и запас топлива

Буквальная модель «сжечь Mass до нуля, затем создать coal» некорректна: активный coal с нулевой массой получил бы почти нулевую thermal capacity, а присвоение стандартной массы после нуля создало бы вещество. Для fixed-solid v1 рекомендуется следующий контракт:

```text
residueMass = burnedInto == core:empty ? 0 : target.Density
availableFuel = max(0, cell.Mass - residueMass)
burnedMass = min(availableFuel, burnRate × elapsedTime)
cell.Mass -= burnedMass
```

Когда `Mass` достигает `residueMass` с единым epsilon, она устанавливается **точно** в `residueMass`, после чего материал один раз меняется на `burnedInto`. Поэтому:

- стандартная wood-клетка получает начальную `Mass = wood.Density` от кисти;
- wood с большей нестандартной Mass горит дольше;
- wood→coal сохраняет реальный остаток, равный канонической fixed-solid массе coal;
- wood→empty расходует всю Mass и затем полностью обнуляет клетку;
- Mass никогда не становится отрицательной;
- partial wood остаётся fixed obstacle до burnout, что соответствует текущему fixed-solid contract;
- уменьшение Mass снижает thermal capacity, но не изменяет solid topology или цвет до превращения.

Для клетки, загруженной с `Mass <= residueMass + epsilon`, combustion pass должен нормализовать burnout независимо от температуры: топлива уже нет. На Pause это не происходит; нормализация ждёт первого unpaused thermal batch.

Связь residue с target `Density` минимальна по layout и согласуется с тем, как кисть уже задаёт canonical solid Mass. Её недостаток — density одновременно задаёт физическое свойство материала и выход остатка. Если это сцепление не утверждается, безопасная альтернатива — добавить **material-level** `residueMass` и расширить `MaterialProperties` до 96 байт. Это всё ещё не требует нового поля клетки или v6.

## 6. Поддержание, тушение и поведение порога

Predicate v1:

```text
combustible = BurnedIntoMaterialIndex != uint.MaxValue
hasFuel = Mass > residueMass + epsilon
burning = IsActive && combustible && hasFuel && Temperature > IgnitionTemperature
```

- `Temperature == IgnitionTemperature`: не горит.
- `Temperature <= IgnitionTemperature`: не горит и не выделяет тепло.
- `Temperature > IgnitionTemperature`: горит, пока есть fuel.
- После охлаждения и повторного нагрева клетка снова горит с оставшейся Mass.
- Вода не проверяется в combustion shader. Она тушит только тогда, когда существующая thermal diffusion отводит достаточно тепла, чтобы predicate стал false.
- Горячий негорючий металл не подсвечивается, потому что у него sentinel вместо combustion target.

Около порога возможен физически объяснимый цикл «один step горит → охлаждается ниже порога → не горит → снова нагревается». Настоящее hysteresis устраняет его только с persistent state. Для v1 мерцание ограничивается строгим сравнением, fixed thermal clock и тем, что renderer использует ровно тот же predicate. Если GPU acceptance покажет неприемлемую дрожь, следует не прятать её shader-трюком, а вернуться к явному `StateFlags` и v6.

## 7. Энергетическая модель

### Сравнение вариантов

| Вариант | Плюсы | Минусы | Решение |
|---|---|---|---|
| A. `Temperature += heatPerSecond × dt` | Самый простой | Не зависит от реально сгоревшей массы и HeatCapacity; последний step выделяет лишнее тепло | Не использовать |
| B. `Q = burnedMass × heatPerMass`, `ΔT = Q / capacity` | Связан с fuel, Mass и HeatCapacity; не требует нового state | Химическая энергия намеренно добавляется; нужен clamp | **Рекомендуется** |
| C. Отдельный heat-source buffer | Можно объединять много источников и отделить thermal solve | Новый full-grid buffer/pass или scatter conflicts; избыточно для local self-heating | Позже при множественных источниках |

Алгоритм v1:

```text
massBefore = max(cell.Mass, 0)
burnedMass = min(max(0, massBefore - residueMass), BurnRate × dt)
energyAdded = burnedMass × HeatPerMass
capacityBefore = max(massBefore, 0.0001) × source.HeatCapacity
temperatureAfter = min(5000, Temperature + energyAdded / capacityBefore)
massAfter = massBefore - burnedMass
```

Использование pre-burn capacity не допускает деления на исчезающий остаток и даёт устойчивый terminal step. Выделенное тепло пропорционально фактически сгоревшей массе. Clamp защищает от неограниченной температуры и float overflow в дальнейших системах.

Горение является химическим источником энергии, поэтому суммарная sensible thermal energy мира во время combustion **не обязана сохраняться**. Acceptance проверяет формулу добавленной энергии, finite values и clamp, а не conservation как в чистой diffusion.

После расчёта температуры burnout normalization сохраняет полученную `Temperature` для non-empty target. При переходе в empty вся клетка обнуляется. Пересчёт температуры по `HeatCapacity` target в v1 не выполняется, как и в утверждённой phase model A; это нужно явно считать gameplay-контрактом, а не полной термодинамикой.

## 8. Порядок GPU-проходов и fixed time

Рекомендуемый порядок:

```text
brush commands
→ existing solid-body schedule
→ existing cellular schedule
→ 0..4 thermal diffusion ticks
→ 0 или 1 combustion pass, dt = thermalTicks × 0.05
→ 0 или 1 phase transition pass
→ composition/statistics
```

Следствия порядка:

- wood, нагретое diffusion выше ignition, может гореть в том же rendered frame;
- тепло, созданное combustion, попадёт в соседей на следующем thermal tick;
- соседнее wood воспламеняется после того, как существующая diffusion реально поднимет его T выше threshold;
- вода, нагретая thermal diffusion выше 100 °C, превращается в steam тем же последующим phase pass;
- v1 запрещает одновременно combustion и phase rules у source, поэтому добавленное самим горением тепло не создаёт неутверждённый конфликт двух target systems;
- composition видит уже обновлённые Mass, Temperature и burnedInto material.

Один pass после всей thermal batch — осознанное performance-приближение. При четырёх catch-up ticks он сжигает `BurnRate × 0.20 s` по конечному temperature predicate. Это обеспечивает линейный burn rate и одинаковый результат для одинакового числа thermal ticks, но не воспроизводит промежуточное распространение combustion heat внутри этой пачки. Клетка, пересёкшая threshold только на четвёртом tick, также получает полный `0.20 s` burn interval.

Точная альтернатива — combustion после каждого thermal tick. Она корректнее по моменту ignition и распространению, но в худшем кадре делает четыре дополнительных full-grid passes. Для первой версии рекомендуется один pass и обязательный acceptance на 1/4 ticks; переход к per-tick order допустим только после измерений и если ошибка catch-up визуально значима.

Fixed scheduler ограничивает один кадр 0.20 s simulation time; очень длинный hitch отбрасывает excess time. Это существующий контракт thermal system. При Pause combustion pass не выполняется, `dt` не копится; после Continue используется только обычный следующий thermal batch.

## 9. GPU-представление

### `MaterialProperties`

Implementation status: the 80-byte material layout, stateless combustion pass, deterministic emission proposal/resolve, scheduler integration, core wood/coal/smoke/CO2 data, and render-only burning glow are implemented in the working tree. This document records the implementation contract and acceptance obligations; no save format version was changed.

Минимальный aligned tail расширяет stride с 64 до 80 байт:

| Offset | Поле C#/HLSL | Type | Без combustion |
|---:|---|---|---|
| 64 | `IgnitionTemperature` | `float` | `0` |
| 68 | `BurnRate` | `float` | `0` |
| 72 | `HeatPerMass` | `float` | `0` |
| 76 | `BurnedIntoMaterialIndex` | `uint` | `uint.MaxValue` |

`BurnedIntoMaterialIndex == 0` специально означает допустимый `core:empty`; `uint.MaxValue` означает отсутствие combustion. Поэтому отдельный `MaterialFlags.Combustible` дублировал бы sentinel и не нужен.

Обязательные metadata:

- `RegistryHasCombustibleMaterials` для нулевого dispatch в registry без топлива;
- `CombustionGraphFlags` для консервативного wake-up, если async summary ring переполнен;
- raw string `BurnedIntoId` в `MaterialDefinition` до второго этапа resolution;
- C#/HLSL verifier: size 80, offsets `64/68/72/76`, порядок полей и sentinel defaults;
- reorder acceptance, сдвигающий target runtime index при неизменном string ID.

Стоимость хвоста при лимите 256 материалов — 4096 bytes. `GridCell`, два grid buffers, staging и v5 stride не меняются.

### Combustion pass и summary

`Combustion.hlsl` может быть in-place: один thread читает и пишет только собственную клетку. Соседи не читаются, атомики для grid не нужны.

Рекомендуемый 16-байтный constant buffer: `ElapsedSeconds`, `Width`, `Height`, `MaterialCount`.

Отдельный OR-summary должен различать как минимум:

- `CombustionOccurred` — расходовалась Mass/добавлялось тепло;
- `BurnoutOccurred` — изменился material либо клетка стала empty;
- `TargetCellular`, `TargetLiquid`, `TargetGas`;
- `TouchesLiquid`, `TouchesSolid`;
- `TargetMovableSolid`.

Для fixed→fixed/empty v1 достаточно первых двух и `TouchesSolid`, но полный набор сохраняет общий normalizer и безопасный будущий wake-up. Composition можно консервативно помечать dirty сразу после каждого combustion dispatch; ждать readback нельзя. Physics wake-up нужен только при `BurnoutOccurred`, а не при каждом уменьшении Mass fixed-solid клетки.

Summary/readback и timestamps следует сделать отдельными от phase, но повторить проверенный трёхслотовый `DoNotFlush` pattern и reusable fallback gate.

## 10. Правила burnout normalization

### Утверждаемая пара v1: fixed solid → fixed solid

Для `wood → coal`:

| Поле | Правило |
|---|---|
| `MaterialIndex` | Разрешённый runtime index coal. |
| `Mass` | Точно canonical residue (`coal.Density` в минимальной схеме). |
| `Temperature` | Сохранить результат combustion step. |
| `VelocityX/Y` | `0`. |
| `Pressure` | `0`. |
| `BodyId` | `0`. |
| `RestFrames` | `2` для fixed solid. |
| `IsActive` | `1`. |

### Fixed solid → empty

Вся клетка становится `(GridCell)0`. Температура и остаточные physics fields не сохраняются, потому что empty в текущей модели — вакуум без thermal capacity.

Нельзя слепо вызвать phase normalizer: phase сохраняет Mass побитово и запрещает empty, тогда как combustion обязан установить продуктовую Mass или полностью удалить клетку.

### Зарезервированные будущие пары

| Пара | Потребуется перед разрешением |
|---|---|
| solid → granular | Определить product Mass/yield, `Velocity=0`, `Pressure=0`, wake cellular. |
| solid → movable solid | Product Mass, `BodyId=0`, topology dirty и component labeling. |
| solid → gas | Product yield и проверка gas mass cap/minimum; соседняя emission не подразумевается. |
| liquid → gas | Отделить расход топлива от occupancy Mass и согласовать с gas split/merge. |
| granular → solid | Product yield, остановка velocity и fixed/movable target rule. |
| liquid/gas/granular source | Доказать, что уменьшение Mass не ломает transfer/merge/rest semantics. |

Эти пары должны быть явно отклонены validator v1, а не «работать» частично.

## 11. Визуализация огня

### A. Подсветка горящей клетки — v1

Composition читает combustion properties текущего material и использует тот же строгий predicate, что pass. Цвет смешивается с оранжево-красным emissive оттенком; небольшое детерминированное мерцание строится из coordinate и `FrameIndex`, без material-specific ID.

Плюсы: нет GridCell/save state, renderer не подсвечивает горячий негорючий металл, visual state не расходится с simulation predicate. На Pause анимация замораживается вместе с симуляцией; temperature brush может убрать подсветку, охладив клетку.

Этого достаточно, чтобы пользователь видел первую работающую систему, но это **не изображение отдельного язычка пламени**.

### B. Временная flame-клетка

Не рекомендуется v1: она становится физическим продуктом, требует target material/времени жизни и сталкивается с теми же конфликтами соседней записи, что smoke/CO₂.

### C. Render-only overlay

Безопасный будущий вариант: composition рисует flame shape над burning source, не меняя grid. Он не требует сохранений или физики, но расширяет rendering scope и должен идти отдельным визуальным коммитом после базовой подсветки.

Огонь не появляется в material menu и не получает fire button.

## 12. Smoke, CO₂ и transient flame

Emission system не входит в первую combustion implementation. Проблема не в JSON target, а в конкурентной записи: несколько горящих sources могут выбрать одну пустую соседнюю клетку.

| Подход | Оценка |
|---|---|
| Прямая atomic запись | Недостаточна: целый `GridCell` нельзя безопасно выбрать одним простым atomic; порядок становится недетерминированным. |
| Atomic append request queue | Возможна, но требует capacity, overflow policy, детерминированного resolve и дополнительных buffers/readback metrics. |
| Proposal/resolve по destination | **Предпочтительно**: source пишет proposal, destination выбирает один request по стабильному priority/hash, затем отдельный apply pass. |
| Checkerboard emissions | Детерминировано и дёшево, но анизотропно, снижает rate и добавляет многокадровую задержку. |
| Временный per-cell request buffer | Естественная основа proposal/resolve; стоимость минимум 4–16 bytes/cell плюс один-два passes. |

Рекомендация: basic combustion сначала включает fuel, heat, burnout и glow. Smoke/CO₂ — отдельный последующий engine-коммит после утверждения emission proposal/resolve. Если будущая JSON reaction system уже решит общий product placement, emissions лучше объединить с ней, а не строить второй конкурирующий механизм.

## 13. Первые материалы

Параметры предварительные и должны пройти gameplay/GPU tuning; единицы текущие игровые, не SI.

### `core:wood`

| Свойство | Предложение |
|---|---:|
| kind/flags | `solid`, без `movable-solid` |
| density / initial Mass | `0.80` |
| friction | `0.65` |
| initialTemperature | `20 °C` |
| conductivity | `0.08` |
| heatCapacity | `1.70` |
| ignitionTemperature | `300 °C` |
| burnRate | `0.08 mass/s` |
| heatPerMass | `1800` |
| burnedInto | `core:coal` |
| цвет | `#8B5A2B` |

При coal density `0.20` горючая масса стандартной клетки равна `0.60`, а номинальное время без тушения — около `7.5 s`.

### `core:coal`

| Свойство | Предложение |
|---|---:|
| kind/flags | fixed `solid`, без `movable-solid` |
| density / residue Mass | `0.20` |
| friction | `0.75` |
| initialTemperature | `20 °C` при рисовании; после burnout сохраняется T wood |
| conductivity | `0.15` |
| heatCapacity | `1.00` |
| combustion | отсутствует в v1 |
| цвет | `#2C2520` |

`wood → coal` предпочтительнее `wood → ash`: coal уже находится в roadmap, даёт видимый ненулевой остаток и проверяет generic material replacement. Делать coal горючим сразу не следует: каскадный fuel product усложнит acceptance и параметры первой цепочки. Ash можно добавить позже как granular target после утверждения соответствующей normalization.

Будущие `core:smoke` и `core:co2` должны быть отдельными gas materials с разными IDs, density/thermal/color, но не добавляются до emission system. Smoke не следует маскировать под CO₂ или существующий `core:gas`.

## 14. Сохранения и совместимость

Для рекомендованного варианта остаётся **v5**:

- частично сгоревшее wood сохраняет текущие `MaterialIndex`, `Mass` и `Temperature`;
- после Load оно продолжит гореть на первом unpaused thermal batch только если `Temperature > ignition` и осталась fuel mass;
- охлаждённое partial wood остаётся потушенным до нового нагрева;
- Load на Pause не расходует Mass и не накапливает время;
- runtime reorder не влияет на `burnedInto`, потому что target каждый запуск разрешается из string ID;
- v3/v4 wood, если такой ID когда-либо существовал во внешнем palette, получает текущую initial temperature и полную сохранённую legacy Mass; оценка горения начинается только после unpaused tick;
- если внешний burnedInto target исчез, source definition становится невалидным и исключается registry; сохранённые клетки отсутствующего source затем заменяются на empty по текущей palette policy с warning;
- отсутствующий bundled core target является фатальной ошибкой registry до загрузки сцены.

Вариант с новым `StateFlags`/`FuelRemaining` потребовал бы v6 даже при попытке сохранить тот же physical stride через bit packing: меняется persistent meaning и нужны явные reader/writer/verifier contracts. Скрыто переопределять v5 нельзя.

## 15. Производительность

Combustion — ещё один full-grid in-place pass. При 1920×1080 он читает около `74.65 MB` (`71.2 MiB`) клеток. Запись происходит только для burning/burnout cells; worst case добавляет до такого же объёма записи. Material table 80 bytes × максимум 256 хорошо помещается в cache.

Ожидаемый класс стоимости близок к phase pass, который фактически занимает примерно `0.2–0.4 ms` на 75–100% scale. Это ориентир, не принятый результат.

Budget v1:

- `RegistryHasCombustibleMaterials == false`: 0 combustion dispatch, 0 новых grid reads;
- максимум один pass за rendered frame и только при `thermalTicks > 0`;
- без destination grid, `Grid.Swap()` и per-cell CPU readback;
- отдельный маленький summary и трёхслотовый async ring;
- timestamps без `Flush`, с sample count/average/min/max на 25/35/50/75/85/100%;
- целевой p95 — не хуже измеренного phase класса; значение выше `0.5 ms` на 100% требует отдельного решения до content-коммита;
- active-region dispatch не использовать в первой версии: thermal/phase уже full-grid, огонь может находиться в любой части сохранённого мира, а корректная persistent dirty-region система сложнее самого pass;
- automatic sleep не проектировать вместе с combustion.

## 16. Acceptance matrix

CPU regression проверяет parser, resolution, layouts, чистый combustion step/normalizer и dispatch policy. Реальный GPU acceptance проверяет production HLSL, pass order, diffusion, Pause, save/load, renderer и timing.

| № | Сценарий | Обязательный результат |
|---:|---|---|
| 1 | Негорючий material при высокой T | Mass/T не меняются combustion pass, glow отсутствует. |
| 2 | Wood ниже ignition | Не горит. |
| 3 | Wood ровно на ignition | Не горит: используется строгое `>`. |
| 4 | Wood выше ignition | Mass уменьшается на `min(fuel, rate×dt)`. |
| 5 | 30/60/100 FPS | При одинаковом числе thermal ticks расход совпадает в tolerance. |
| 6 | Четыре catch-up ticks | Один dispatch, `dt=0.20`, ожидаемый суммарный расход. |
| 7 | Pause | 0 dispatch и 0 расхода. |
| 8 | Долгий Pause→Continue | Нет catch-up burn; только обычный следующий batch. |
| 9 | Energy formula | `ΔT = burnedMass×heatPerMass/capacityBefore`, finite и clamp ≤5000. |
| 10 | Соседняя клетка | Получает тепло только последующим ThermalDiffusion. |
| 11 | Соседнее external wood-like | Позже воспламеняется без material-specific проверки. |
| 12 | Охлаждение | При `T <= ignition` расход и heat прекращаются. |
| 13 | Water тушит | Только через thermal contact; shader не знает water ID. |
| 14 | Нижняя граница Mass | Неотрицательна и не проходит ниже residue. |
| 15 | Wood→coal | Происходит ровно один раз на residue boundary. |
| 16 | Нормализация wood→coal | Index/Mass/T/velocity/pressure/body/rest/active соответствуют разделу 10. |
| 17 | Wood→empty | Клетка полностью zero, масса не создаётся. |
| 18 | Внешний горючий material | Работает тем же production shader. |
| 19 | Runtime reorder | Изменившийся target index корректно разрешён по string ID. |
| 20 | v5 round-trip partial wood | Material/Mass/Temperature побитово восстановлены. |
| 21 | Load на Pause | Горение не продолжается до Continue. |
| 22 | Visual predicate | Совпадает с simulation predicate до/после threshold и burnout. |
| 23 | Registry без combustion | 0 dispatch и 0 combustion timing samples. |
| 24 | GPU timing | Query samples читаются через `DoNotFlush`, без `Flush` в measured loop. |
| 25 | Ошибки JSON/targets | Core fatal; external skipped с понятным `PHYXEL_MATERIAL_ERROR`; unknown nested fields rejected. |
| 26 | Unsupported kinds/phase conflict | Movable/cellular source, non-fixed target и source с transitions отклоняются. |
| 27 | Intermediate snapshots | До ignition, после thermal, после combustion и после burnout доказывают правильный pass order. |
| 28 | Старые regression | World codec, material/phase layouts, 31/31 generic phase, 6/6 core phase и прежние cellular/solid/thermal cases зелёные. |

Отдельный diagnostic case должен подтвердить, что при четырёх ticks lumped-модель отличается от четырёх чередующихся thermal/combustion steps только в заранее описанных моментах распространения/ignition, а не из-за FPS-зависимого burn rate.

## 17. Безопасное разбиение будущей реализации

1. **Add combustion material schema** — raw JSON model, строгая nested validation, двухэтапное target resolution, 80-байтный C#/HLSL layout, registry metadata и CPU material verifier. Без pass и core content.
2. **Run universal combustion GPU pass** — `Combustion.hlsl`, fixed-step dispatch policy, energy/fuel algorithm, burnout normalizer, summary/readback fallback и timestamps. Без wood/coal.
3. **Add combustion GPU acceptance** — acceptance-only fixed-solid materials, thresholds, 1/4 ticks, FPS/Pause, cooling, external/reorder, v5 round-trip, timing и regression matrix.
4. **Add wood coal combustion chain** — `core:wood`, `core:coal`, menu/content acceptance и generic burning-cell glow. Общий shader не получает ID checks.
5. **Design and run emission proposal resolve** — отдельный утверждённый этап для smoke/CO₂/transient products; не автоматически часть combustion v1.
6. **Complete combustion performance and documentation** — все scale presets, user tuning, README/architecture/status и финальная compatibility матрица.

Каждый шаг должен быть отдельным небольшим коммитом и не начинать следующий, пока текущий не проходит относящиеся CPU/GPU проверки.

## 18. Открытые вопросы для Степана

1. Утверждаем ли stateless v1 без hysteresis: `T > ignition` горит, `T <= ignition` тушится?
2. Утверждаем ли fixed-solid-only source/target для первой реализации и запрет combustion вместе с phase transitions?
3. Утверждаем ли residue Mass равной `burnedInto.Density` для fixed target? Если нет, schema/layout-коммит должен сразу добавить material-level `residueMass` и stride 96, всё ещё без изменения `GridCell`/v5.
4. Разрешаем ли `burnedInto: "core:empty"` как явное полное сгорание?
5. Утверждаем ли `heatPerMass`, pre-burn capacity и общий clamp 5000 °C вместо `heatPerSecond`/per-material maximum?
6. Принимаем ли один combustion pass после всей thermal batch и описанную погрешность catch-up, либо нужна точная схема после каждого thermal tick?
7. Достаточна ли для первой версии подсветка burning cell, оставляя render-only flame overlay отдельной задачей?
8. Подходят ли стартовые параметры wood/coal (`0.80→0.20 Mass`, ignition 300, rate 0.08, heatPerMass 1800), или нужен другой gameplay target времени/температуры?
9. Smoke/CO₂ откладываются до отдельного proposal/resolve design либо должны проектироваться совместно с будущими JSON reactions?

## 19. Зафиксированная рекомендация до ответов

До подтверждения открытых вопросов безопасный baseline таков:

- вариант состояния A;
- новых полей `GridCell` нет;
- save format остаётся v5;
- Mass уменьшается только до массы остатка, heat зависит от реально сгоревшей части;
- тушение выполняется только охлаждением;
- fire отображается универсальным glow predicate;
- smoke/CO₂ не входят в первую версию;
- порядок: thermal batch → combustion → phase;
- первый кодовый шаг — только JSON schema, registry resolution и GPU material layout.
## 20. Рабочее состояние после реализации

Разделы выше сохраняют исходные архитектурные решения и открытые вопросы как журнал проектирования. На текущем рабочем дереве выполнены следующие технические шаги:

- `MaterialProperties` расширен с 64 до 80 байт; `GridCell` остаётся 36 байт, а сохранения остаются v5.
- Добавлен stateless `Combustion.hlsl`: источник горит только при `Temperature > ignitionTemperature`, расходует `Mass` по `burnRate * dt`, добавляет тепло от фактически сгоревшей массы с учётом `HeatCapacity`, ограничивает температуру общим clamp и нормализует burnout в target.
- Координатор запускает максимум один combustion pass после thermal batch и до phase pass; на Pause dispatch и накопление dt отсутствуют. Четыре catch-up ticks передаются как один суммарный интервал.
- Для smoke/CO2 добавлены data-driven emission tables и отдельные deterministic proposal/resolve passes. Запросы разрешаются через claim buffer, поэтому несколько источников не делают неконтролируемые записи в одну клетку.
- В core-каталоге присутствуют `core:wood`, `core:coal`, `core:smoke`, `core:co2`; общий shader не содержит material-specific ID checks. Composition использует тот же burning predicate и render-only glow.
- CPU verifier подтверждает layout, пороги, расход Mass, тепло, pause/dispatch policy, burnout, строгую JSON validation и runtime reorder. Реальный GPU acceptance `combustion_chain` прошёл: `coal=797`, `partialWood=72`, `combustionDispatches=195`, `combustionSummaryReadbacks=195`, timing samples=155.

Это рабочая реализация для ревью, а не новый коммит. Следующий безопасный шаг — ручное разделение изменений на небольшие коммиты и отдельное подтверждение gameplay-параметров у Степана.
## 21. Уточнение после сравнения с The Powder Toy

Предыдущий baseline был намеренно stateless и показывал только glow. Для игрового огня он недостаточен. Текущая реализация добавляет отдельный `core:fire`:

- `core:fire` — selectable gas material с flag `flame`, температурой 650 °C и lifetime 2.0–2.8 s;
- lifetime хранится в новом поле `GridCell.Lifetime`, поэтому текущий world format повышен с v5 до v6; v3/v4/v5 продолжают загружаться с `Lifetime=0`;
- flame lifecycle pass уменьшает lifetime, удерживает пламя горячим, превращает его в `core:smoke` и позволяет smoke позже исчезнуть в `core:empty`;
- burning material видит flame в радиусе 2 клеток и с data-driven `combustion.spreadRate` вероятностно получает ignition temperature; в shader нет проверки `core:wood`;
- emission table получила третий продукт `flameInto/flameRate`. Запросы пламени используют отдельный слот и тот же deterministic claim/resolve механизм;
- brush material `core:fire` является пользовательским инструментом рисования пламени. Composition больше не делает дерево огненным: glow и halo строятся по реальным flame-клеткам и их lifetime.

Поэтому v6 и отдельная flame-клетка являются сознательным пересмотром прежнего требования «не менять GridCell/v5»: оно было корректно для stateless architectural prototype, но не соответствует новой цели — получить FIRE как в Powder Toy.
# Актуальный статус реализации

Ниже сохранён исходный архитектурный журнал, включая решения прежнего stateless baseline. Текущая реализация для цели «пламя как в Powder Toy» находится в рабочем дереве и описана в разделе 21: `core:fire` — отдельный выбираемый gas с lifetime, spread, smoke-decay и render-only flame overlay; формат world writer — v6, v5 загружается через миграцию.

## 22. Powder Toy parity tuning

По исходникам `FIRE.cpp`, `WOOD.cpp` и `COAL.cpp` уточнены отличия игрового поведения:

- flame-клетка не проходит через обычный gas mass-balancing: она не дробится ниже `GasMinimumMass`, не смешивается со smoke/CO₂ и поднимается преимущественно вверх с небольшим `DeltaTime`-ограниченным диагональным дрейфом;
- `core:fire` сохраняет lifetime 2.0–2.8 s, близкий к FIRE `life=120..169` при 60 Hz; `flameRate` трактуется как вероятность появления дискретной flame-клетки в секунду;
- fire tool создаёт flame в пустых пикселях и зажигает fixed-solid combustible материал в месте контакта, не вырезая из него газовую полость;
- wood/coal/fire thermal coefficients подняты к относительным `HeatConduct` Powder Toy (wood≈164/255, coal≈200/255, fire≈88/255), поэтому пламя заметно нагревает соседний материал через существующую diffusion;
- охлаждение больше не обрывает flame при первом контакте с горячим деревом: lifetime является основным таймером, а температура тушит только почти остывшую flame-клетку;
- composition рисует не только физическую flame-клетку, но и data-driven hot-combustible halo; shader не проверяет `core:wood`.

GPU acceptance после этой настройки: `coal=51`, `partialWood=302`, `hotWood=302`, `maxWoodTemp≈2183 °C`, `flame=35`, `smoke=83`, `invalidFlameLifetime=0`, `PHYXEL_ACCEPTANCE_SUCCESS`.

## 23. Финальная настройка поведения огня после GPU-прогона

Предыдущие числа выше относятся к промежуточному capture. После устранения
разбавления FIRE обычным gas solver, слишком быстрого подъёма flame-клетки,
потери контакта искры с solid и ошибочного переноса массы FIRE в smoke полный
прогон `combustion_chain` на 1800 кадрах завершился так:

`coal=38`, `wood=11218`, `partialWood=147`, `hotWood=140`,
`maxWoodTemp=1491.6 °C`, `flame=39`, `smoke=90`,
`invalidFlameLifetime=0`, `combustionDispatches=249`,
`combustionSummaryReadbacks=249`, средний combustion GPU pass `0.0993 ms`,
`PHYXEL_ACCEPTANCE_SUCCESS`.

Актуальные правила рабочего дерева:

- brush `core:fire` не заменяет fixed-solid: пустые пиксели создают настоящие
  flame-клетки, а combustible solid получает ignition temperature в месте
  контакта;
- подъём flame-клеток вероятностно ограничен через `DeltaTime`, поэтому одна
  и та же скорость получается при разных FPS; горизонтальное растекание не
  используется как обычный gas drift;
- flame emission сначала ищет свободную клетку вверх, затем сбоку и вниз,
  поэтому пламя появляется с внешней стороны любой поверхности;
- переход FIRE в smoke получает каноническую массу smoke, а не массу FIRE,
  иначе один transient particle ошибочно размножался в десятки smoke-клеток;
- composition добавляет узкий render-only flame trail между дискретными
  частицами. Он не участвует в физике и не попадает в сохранение;
- универсальный thermal exchange ускорен до `ThermalExchangeRate=10` и
  `MaximumExchangeFraction=0.50`; отдельный `thermal_contact` acceptance
  остаётся энерго-сохраняющим и проходит монотонные checkpoints.
