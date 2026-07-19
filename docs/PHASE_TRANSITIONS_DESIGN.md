# Универсальные фазовые переходы

> **Актуальное состояние (2026-07-20).** Универсальная схема, GPU pass,
> координация и acceptance-матрица реализованы. Текущий writer создаёт сцены
> v6 с 40-байтным `GridCell` (`Temperature` и `Lifetime`); v3/v4/v5 являются
> legacy-форматами чтения. `MaterialProperties` занимает 120 байт. Рабочая
> пользовательская цепочка — `core:ice ↔ core:water ↔ core:steam`, включая
> latent heat кипения. Steam дополнительно использует общий `ambientCooling`
> и общий gas redistribution. Исторические оценки размеров и формулировки
> «ещё не реализовано» ниже следует читать как историю проектирования, если
> они противоречат этому блоку или [ARCHITECTURE.md](ARCHITECTURE.md).

Статус документа: schema/registry/layout-слой, общий GPU pass, runtime-координация, generic GPU acceptance и первая пользовательская цепочка реализованы.

Первая реализованная пользовательская цепочка:

```text
core:ice ↔ core:water ↔ core:steam
```

Цель — добавить фазовые переходы как общую GPU-механику материалов. Строковые ID используются только при загрузке и в сохранениях; shader получает только `RuntimeIndex`. В C# и HLSL не должно быть веток для `water`, `ice`, `steam` или другого конкретного материала.

## 1. Рекомендованная архитектура

Переход описывается в JSON исходного материала. `MaterialFileLoader` сначала разбирает локальные данные и сохраняет строковые `into`, затем `MaterialRegistry` загружает весь допустимый набор, назначает runtime-индексы и отдельным вторым этапом разрешает ссылки. После этого `CreateGpuTable()` записывает пороги и разрешённые индексы в `MaterialProperties`.

GPU выполняет отдельный полный in-place pass `PhaseTransitions.hlsl`:

- один thread обрабатывает одну клетку;
- соседние клетки не читаются;
- thread пишет только свою клетку;
- основной переход не требует атомарных операций;
- за один phase pass применяется не более одного ребра;
- shader выбирает ребро по данным `MaterialProperties`, а не по известному ID;
- `GridCell` не расширяется.

Pass запускается один раз после всей пачки thermal ticks текущего кадра, только если в кадре был хотя бы один thermal tick и реестр содержит переходы. Он не выполняет `Grid.Swap()`: уникальный thread безопасно читает и при необходимости перезаписывает ту же клетку через `RWStructuredBuffer<GridCell>`.

Такой порядок даёт важный барьер: solid/cellular-физика кадра уже завершена, затем температура доведена до актуального значения, меняется материал, а физика нового kind начинается не раньше следующего simulation frame. Даже при четырёх catch-up thermal ticks клетка выполняет максимум один переход за rendered frame.

### Почему не объединять pass с thermal diffusion

`ThermalDiffusion.hlsl` использует ping-pong и соседние клетки. Добавление смены kind в него связало бы энергетику, нормализацию физического состояния и diffusion layout, а несколько thermal ticks позволили бы пройти несколько рёбер цепочки за кадр. Отдельный pass проще проверять, профилировать и отключать, а также не вносит material-specific логику в diffusion.

### Уведомление CPU о фактическом переходе

Точное количество переходов для первой версии не требуется. Однако coordinator должен узнать, что нужно разбудить cellular/solid schedule, перестроить caches и перерисовать материал. Без сигнала пришлось бы консервативно будить всю физику после каждого thermal tick, даже в стабильном мире.

Реализация использует маленький GPU summary, а не систему счётчиков:

- один `uint` с битами `PhaseOccurred`, `TargetCellular`, `TargetLiquid`, `TargetGas`, `TouchesLiquid`, `TouchesSolid`, `TargetMovableSolid`;
- transitioning thread делает один `InterlockedOr` только в summary; запись самой клетки остаётся без atomics;
- summary копируется в маленький staging slot и читается асинхронно без flush/stall;
- coordinator использует кольцо из трёх query/staging slots;
- если все три slot заняты и summary текущего dispatch невозможно поставить в ring, coordinator не ждёт GPU и делает консервативное wake-up по видам, присутствующим в графе переходов;
- точные GPU counters можно добавить только для diagnostics, если появится доказанная потребность.

Сразу после phase dispatch coordinator устанавливает `presentationDirty=true`, не ожидая асинхронный GPU summary: смена материала уже могла изменить композицию текущего кадра. При ненулевом summary coordinator также устанавливает `cellMaterialsDirty`, при `TouchesLiquid` помечает hydraulic routes и warm-up dirty, сбрасывает cellular rest-finalization и будит нужные schedules. При `TouchesSolid` устанавливается `topologyDirty`; при target `movable-solid` component labeling обязателен до solid-body pass. Если readback-кольцо временно занято, заранее вычисленные kind-флаги графа дают консервативный wake-up. Gate объединяет повторные запросы до `Consume`, после чего готов принять новую независимую нехватку slot; успешно поставленный summary fallback не запрашивает.

## 2. Утверждённая JSON-схема

Для первой версии достаточно не более одного перехода вниз и одного вверх у каждого материала:

```json
"thermal": {
  "initialTemperature": 20.0,
  "conductivity": 0.60,
  "heatCapacity": 4.18,
  "transitions": {
    "below": {
      "temperature": 0.0,
      "into": "core:ice"
    },
    "above": {
      "temperature": 100.0,
      "into": "core:steam"
    }
  }
}
```

Направление относится к температуре текущей клетки:

- `below` срабатывает только при `Temperature < temperature`;
- `above` срабатывает только при `Temperature > temperature`;
- при точном равенстве перехода нет;
- отсутствующий `transitions`, `below` или `above` означает отсутствие соответствующего правила;
- `null`, пустой объект и массив вместо объекта отклоняются.

Одного `below` и одного `above` достаточно для механики с одним управляющим скаляром — температурой — и для первой цепочки. Массив правил одного направления потребовал бы явного приоритета и разрешения перекрытий. Если позже понадобятся давление, сосед, время или несколько продуктов, это должна быть новая версия схемы или отдельная reaction system, а не неявное расширение phase v1.

### Hysteresis

Hysteresis задаётся правилами обратных материалов, а не дополнительным состоянием клетки:

```jsonc
// core:ice
"transitions": {
  "above": { "temperature": 2.0, "into": "core:water" }
}

// core:steam
"transitions": {
  "below": { "temperature": 95.0, "into": "core:water" }
}
```

Таким образом, вода замерзает ниже `0 °C`, лёд тает выше `2 °C`, вода кипит выше `100 °C`, а пар конденсируется ниже `95 °C`. В промежутках `0…2` и `95…100 °C` материал сохраняет текущую фазу.

## 3. Строгая валидация и разрешение ссылок

Loader обязан проверять до построения GPU-таблицы:

1. `transitions` допускает только `below` и `above`.
2. Каждый directional object допускает только `temperature` и `into`; оба поля обязательны.
3. `temperature` — конечный JSON number в диапазоне `-273.15…5000`.
4. `into` после trim/lowercase соответствует формату `namespace:name`.
5. Если у материала есть оба направления, должно выполняться `below.temperature < above.temperature`; перекрывающиеся предикаты отклоняются.
6. Source с kind `none` или `tool` не может иметь переходы.
7. Target с kind `none` или `tool` запрещён.
8. Target не может совпадать с source.
9. Переход в `core:empty` запрещён в v1: он уничтожил бы массу и активность и потребовал бы отдельной семантики удаления.
10. `NaN`, `Infinity`, дубли и неизвестные поля отклоняются. Это правило должно распространяться на все новые вложенные объекты независимо от существующей терпимости других секций.

Ссылки разрешаются только после загрузки всех core и external definitions и назначения окончательных `RuntimeIndex`. Raw definition хранит строковый target; готовая GPU-таблица строится заново при каждом запуске. Поэтому сортировка или добавление материала между запусками не ломает переходы.

Поведение ошибок:

- неизвестный target из core definition — фатальная ошибка запуска;
- core source должен ссылаться только на bundled core target;
- внешний материал с неизвестным/недопустимым target пропускается с `PHYXEL_MATERIAL_ERROR`;
- если пропуск внешнего target делает другие внешние ссылки неразрешимыми, registry повторяет проверку до устойчивого набора и сообщает каждую отклонённую source definition;
- правило никогда не отключается молча и не компилируется в `core:empty`.

### Допустимые циклы и мгновенное зацикливание

Циклы в графе нужны и разрешены, если hysteresis делает невозможным немедленное повторение при неизменной температуре. Validator не перебирает все simple cycles. Он собирает пороги, разбивает допустимый диапазон на открытые интервалы, для каждого интервала строит детерминированный граф с не более чем одним применимым исходящим ребром на материал и проверяет этот граф обычным DFS/coloring. Точные значения порогов отдельно не создают ребро из-за строгих сравнений.

Эквивалентно, применимое ребро задаёт одно из ограничений:

- `above(t)` добавляет ограничение `T > t`;
- `below(t)` добавляет ограничение `T < t`;
- также действует общий диапазон `-273.15…5000`.

Если DFS на каком-либо открытом интервале находит цикл, одна и та же температура могла бы бесконечно обходить его по одному ребру за кадр; такой набор отклоняется. Циклы `water below 0 → ice` / `ice above 2 → water` и `water above 100 → steam` / `steam below 95 → water` допустимы, потому что ни на одном интервале оба обратных ребра одновременно не применимы.

## 4. GPU layout

`MaterialProperties` расширяется с 48 до 64 байт добавлением одного 16-байтного хвоста:

| Offset | C# / HLSL field | Type | Значение без правила |
|---:|---|---|---|
| 48 | `TransitionBelowTemperature` | `float` | `0.0f` |
| 52 | `TransitionBelowMaterialIndex` | `uint` | `uint.MaxValue` |
| 56 | `TransitionAboveTemperature` | `float` | `0.0f` |
| 60 | `TransitionAboveMaterialIndex` | `uint` | `uint.MaxValue` |

Полный layout остаётся последовательностью 16 четырёхбайтных полей. Для `StructuredBuffer` это даёт одинаковый stride 64 в `[StructLayout(LayoutKind.Sequential, Pack = 1)]` и HLSL. Дополнительный flag не нужен: при лимите 256 материалов `0xffffffff` однозначно означает отсутствие target.

Regression должен проверять:

- `Marshal.SizeOf<MaterialProperties>() == 64`;
- offsets новых полей `48/52/56/60` через `Marshal.OffsetOf`;
- тот же порядок объявлений в `PhysicsShared.hlsli`;
- sentinel для отсутствующих правил;
- разрешённые индексы в runtime-позициях таблицы;
- повторную сборку таблицы при иной сортировке registry.

Стоимость хвоста — `16 × materialCount`, максимум 4096 байт при действующем лимите 256. `GridCell` остаётся 36 байт, оба grid buffers, staging, codec и world stride не меняются.

## 5. Правила изменения `GridCell`

Энергетическая модель v1 меняет фазу мгновенно. Для любого разрешённого target:

| Поле | Правило |
|---|---|
| `MaterialIndex` | Заменить разрешённым target `RuntimeIndex`. |
| `Mass` | Сохранить побитово; phase pass не создаёт и не удаляет массу. |
| `Temperature` | Сохранить побитово согласно модели A. |
| `IsActive` | Сохранить ненулевым; target empty запрещён. Рекомендуется нормализовать в `1`. |
| `VelocityX/Y` | Сохранить только для перехода cellular → cellular; иначе установить `0`. |
| `Pressure` | Сохранить только для liquid → liquid; для всех остальных пар установить `0`. |
| `BodyId` | Всегда установить `0`; stale body membership недопустимо. |
| `RestFrames` | Для target fixed solid установить `2`; для movable solid и cellular установить `0`. |

Здесь cellular — `granular`, `liquid` или `gas`; fixed solid — `solid` без `movable-solid`.

Правила по интересующим парам:

| Переход | Mass / Temperature | Velocity | Pressure | BodyId / RestFrames |
|---|---|---|---|---|
| liquid → gas | сохранить | сохранить | `0` | `0 / 0` |
| gas → liquid | сохранить | сохранить | `0` | `0 / 0` |
| liquid → fixed solid | сохранить | `0` | `0` | `0 / 2` |
| fixed solid → liquid | сохранить | `0` | `0` | `0 / 0` |
| liquid → movable-solid | сохранить | `0` | `0` | `0 / 0`, затем component labeling |
| movable-solid → liquid | сохранить | `0` | `0` | `0 / 0`, topology dirty |
| granular → liquid | сохранить | сохранить | `0` | `0 / 0` |
| liquid → granular | сохранить | сохранить | `0` | `0 / 0` |

Сохранение velocity между cellular kinds удерживает локальное направление движения; перенос из/в solid запрещён, потому что solid-body solver не кодирует скорость тела в этих полях. Liquid pressure нельзя переносить в gas/solid/granular: оно содержит hydraulic head или ordinary landing state, не термодинамическое давление.

## 6. Решение по kind льда

Для первой реализации `core:ice` должен быть `kind: "solid"` без `movable-solid`.

Это решение основано на текущем solid-body коде:

- `IsComponentCell` объединяет все ортогонально соседние movable-solid клетки, не проверяя одинаковый `MaterialIndex`; лёд мог бы непреднамеренно стать одним телом с камнем или металлом;
- движение требует ненулевой `BodyId`, получаемый только после component labeling;
- phase pass выполняется после solid pass, поэтому новая movable-solid клетка до следующего кадра не является валидным телом;
- solver поддерживает только ограниченное вертикальное движение/плавучесть и не доказывает корректное образование, разделение и таяние фазово созданных тел.

Fixed solid сразу является корректным препятствием для cellular solver, не требует body registration и естественно моделирует лёд, замёрзший на месте. `BodyId=0`, velocity/pressure сброшены, `RestFrames=2` не позволяет неподвижному льду ошибочно учитываться как движущееся solid-тело при включённой solid gravity.

Переход к movable ice допустим отдельной будущей задачей после GPU acceptance на: component labeling после замерзания, отсутствие слияния разных solid materials, частичное таяние тела, split/merge, массу, опору, падение и плавучесть.

## 7. Энергетическая модель

| Вариант | Сложность и устойчивость | Энергия | `GridCell` / save | Риск цикла | Пригодность v1 |
|---|---|---|---|---|---|
| A. Меняется только material | Минимальная; T непрерывна, hysteresis прозрачен | Diffusion сохраняет энергию, но подразумеваемая sensible energy `Mass × HeatCapacity × T` скачком меняется при другом `HeatCapacity` | Без изменений; v5 сохраняется | Низкий при строгой проверке циклов | **Рекомендуется** |
| B. T устанавливается в threshold | Простая, но создаёт температурный plateau и скрытые скачки | Не сохраняет энергию; значение создаётся/удаляется при clamp | Без изменений; v5 сохраняется | Выше: значение на границе тесно связано с обратным правилом | Не рекомендуется |
| C. Latent heat / enthalpy | Физически наиболее полная, требует частичной фазы или энтальпии и устойчивого solver | Может сохранять sensible + latent energy | Нужно новое состояние клетки, layout, codec и v6 | Управляемый, но сложнее | Позже, только по доказанной необходимости |

Для v1 утверждается модель A: `MaterialIndex` меняется, `Mass` и `Temperature` сохраняются. Это не следует описывать как полное сохранение термодинамической энергии. Acceptance обязан подтвердить именно выбранный контракт: температура неизменна, масса неизменна, а расчётная sensible energy после перехода равна `Mass × HeatCapacity(target) × Temperature` и может отличаться от исходной.

Вариант B не даёт преимущества без latent state. Вариант C потребует как минимум phase fraction/enthalpy в `GridCell`, изменения обоих GPU buffers, staging, v6 writer/reader и legacy conversion; вводить его в первую версию преждевременно.

## 8. Место pass в pipeline, Pause и brush

Рекомендуемый порядок кадра:

```text
brush commands
→ existing solid-body schedule
→ existing cellular schedule
→ 0..4 thermal diffusion ticks (каждый со Swap)
→ 0 или 1 in-place phase pass (без Swap)
→ composition / statistics
```

Phase pass выполняется только когда:

- `RegistryHasPhaseTransitions == true`;
- мир и thermal system активны;
- кадр не на Pause;
- scheduler выдал хотя бы один thermal tick.

Он не запускается отдельно «без diffusion» в обычном цикле. Это сохраняет одну временную шкалу thermal/phase и позволяет пропустить полный pass в кадрах без thermal tick. После загрузки или изменения температуры переход произойдёт на первом следующем unpaused thermal batch.

Temperature brush на Pause меняет только `Temperature`; phase pass, kind, mass и остальные поля не меняются. Pause не накапливает thermal ticks. После Continue первый обычный thermal batch выполняет diffusion, затем один phase pass. Никакого catch-up перехода за время Pause нет.

Один pass после всей thermal-пачки, а не после каждого tick, намеренно исключает `ice → water → steam` в одном rendered frame. Термин phase tick в diagnostics означает один dispatch `PhaseTransitions`, а не каждый внутренний diffusion tick.

## 9. Первые реализованные материалы

Параметры — игровые стартовые значения в текущих диапазонах, а не обещание SI-модели.

### `core:ice`

```json
{
  "schema": 1,
  "id": "core:ice",
  "name": { "ru": "Лёд", "en": "Ice" },
  "kind": "solid",
  "color": "#A9DDF2",
  "physics": { "density": 0.92, "friction": 0.10, "flowRate": 0.0 },
  "thermal": {
    "initialTemperature": -5.0,
    "conductivity": 0.80,
    "heatCapacity": 2.10,
    "transitions": {
      "above": { "temperature": 2.0, "into": "core:water" }
    }
  },
  "ui": { "order": 21, "hidden": false }
}
```

### `core:steam`

```json
{
  "schema": 1,
  "id": "core:steam",
  "name": { "ru": "Пар", "en": "Steam" },
  "kind": "gas",
  "color": "#DDEBF0A0",
  "physics": { "density": 0.03, "friction": 0.005, "flowRate": 1.20 },
  "thermal": {
    "initialTemperature": 105.0,
    "conductivity": 0.04,
    "heatCapacity": 2.08,
    "transitions": {
      "below": { "temperature": 95.0, "into": "core:water" }
    }
  },
  "ui": { "order": 22, "hidden": false }
}
```

`core:water` получил `below 0 → core:ice` и `above 100 → core:steam` без других изменений.

Текущий gas solver позволяет steam подниматься и распространяться в empty, переносит `Mass` и при разделении/слиянии одинакового `MaterialIndex` пересчитывает температуру через теплоёмкость. Проверка `first.MaterialIndex != second.MaterialIndex` запрещает смешивание steam с `core:gas` и с другим газовым материалом. Конденсация выполняется тем же общим phase pass.

Ограничение существующей газовой модели: порции меньше `GasMinimumMass` удаляются, поэтому строгая массовая acceptance должна учитывать уже действующий допуск gas solver. Сам phase pass массу не меняет.

Bundled core-набор теперь содержит 10 определений. Фактические runtime-индексы текущего отсортированного набора — `empty=0`, `gas=3`, `ice=4`, `steam=7`, `water=9`; эти числа являются диагностическим снимком, а не контрактом. Все ссылки переходов разрешаются из string ID при каждом запуске. В UI материалы идут подряд как Water 20, Ice 21 и Steam 22; обычная кисть создаёт их при 20, −5 и 105 °C соответственно.

## 10. Сохранения и совместимость

Формат v5 менять не требуется: текущее состояние клетки уже полностью определяется `MaterialIndex`, `Temperature` и остальными существующими полями. Transition definitions являются свойствами установленного набора материалов и не записываются в каждую сцену.

- v5 string palette сохраняет `core:water`, `core:ice`, `core:steam` и внешние IDs;
- при загрузке palette один раз remap-ится в текущие runtime-индексы, затем GPU transition table строится независимо от порядка;
- старый save с водой получает сохранённую температуру и впервые оценивает переход после первого unpaused thermal batch, не во время deserialization;
- v3/v4 по-прежнему инициализируют температуру из текущего материала, затем следуют тому же правилу первого unpaused batch;
- migration aliases не нужны для новых стабильных IDs; они понадобятся только при будущем rename/remove.

Нужно различать две ошибки:

1. Если установленный core water JSON ссылается на отсутствующий `core:ice` или `core:steam`, registry невалиден и запуск останавливается до загрузки сцены.
2. Если иначе валидный registry загружает v5 palette с отсутствующим материалом, действует текущая политика serializer: клетка заменяется на `core:empty` с `PHYXEL_SCENE_WARNING`.

Вторая политика сохраняет обратную совместимость с удалёнными внешними материалами, но означает потерю их клеток. Для bundled ice/steam штатная установка должна включать оба файла; добавлять silent alias в water или fallback-фазу нельзя.

## 11. Производительность

При 1920×1080 pass запускает 2 073 600 threads. In-place вариант читает одну 36-байтную клетку на thread — около 74.65 MB (71.2 MiB) grid traffic на pass. В худшем кадре, когда переходят все клетки, добавляется столько же записи: около 149.3 MB (142.4 MiB). Небольшая 64-байтная material table должна хорошо кешироваться.

При обычных 20 thermal ticks/s верхняя оценка стабильного чтения phase pass — около 1.49 GB/s, worst-case read+write — около 2.99 GB/s. Реальное время нужно измерить GPU timestamp queries на каждом scale preset; это budget, а не прогноз времени конкретной видеокарты.

Требования к budget:

- `RegistryHasPhaseTransitions == false`: ноль dispatch и ноль новых grid reads;
- с переходами: не более одного полного phase pass на кадр и только после кадра с thermal tick;
- отсутствие отдельного destination grid и `Grid.Swap()`;
- отсутствие per-cell CPU readback;
- summary — несколько байт плюс staging/query ring, без точных counters;
- p95 phase GPU time после прогрева не более 25% среднего времени одного текущего thermal diffusion tick на том же preset;
- сумма thermal batch + phase не должна превысить существующий thermal budget более чем на измеренную стоимость одного phase pass;
- не добавлять automatic thermal sleep в эту задачу.

Существующий thermal timestamp pattern следует повторить отдельными phase query objects либо расширить единый измеряемый интервал так, чтобы overlapping pending queries были невозможны. Acceptance report должен печатать sample count, average, min, max и preset.

Фактические измерения RTX 4070, по 39 GPU timestamp samples на состояние, без `Flush` в измеряемом цикле:

| Scale | Resolution | Steady phase ms avg/min/max | Burst phase ms avg/min/max | Steady / thermal | Burst / thermal |
|---:|---:|---:|---:|---:|---:|
| 25% | 480×270 | 0.0234 / 0.0205 / 0.0287 | 0.0216 / 0.0174 / 0.0266 | 7.0% | 11.7% |
| 35% | 672×378 | 0.0371 / 0.0276 / 0.0543 | 0.0358 / 0.0266 / 0.0440 | 7.9% | 7.5% |
| 50% | 960×540 | 0.0535 / 0.0420 / 0.0655 | 0.0525 / 0.0420 / 0.0778 | 6.8% | 5.9% |
| 75% | 1440×810 | 0.2227 / 0.2150 / 0.2294 | 0.2216 / 0.1905 / 0.2273 | 38.8% | 38.2% |
| 85% | 1632×918 | 0.3092 / 0.2499 / 0.3154 | 0.3129 / 0.2959 / 0.3154 | 48.4% | 47.5% |
| 100% | 1920×1080 | 0.4013 / 0.3686 / 0.4178 | 0.3318 / 0.1659 / 0.4209 | 42.7% | 47.5% |

На 75–100% отношение выше первоначального ориентира 25%. Очевидная причина — полный линейный проход по 36-байтной grid на больших разрешениях; burst почти не дороже steady, поэтому ветка записи переходов не является причиной. Архитектура в acceptance-коммите не оптимизировалась: результат зафиксирован для отдельного решения после пользовательской phase-цепочки.

## 12. Acceptance matrix

CPU regression проверяет parser, граф, layout и чистую функцию нормализации клетки. Реальный GPU acceptance проверяет HLSL, порядок pass и взаимодействие с coordinator/save pipeline.

| № | Сценарий | CPU regression | GPU acceptance / критерий |
|---:|---|---|---|
| 1 | Water 20 °C | predicate не выбирает ребро | material остаётся water после нескольких phase passes |
| 2 | Water замерзает | `T < 0` выбирает ice | water при отрицательной T становится ice |
| 3 | Ice тает | `T > 2` выбирает water | ice становится water и просыпается cellular schedule |
| 4 | Water кипит | `T > 100` выбирает steam | water становится gas target |
| 5 | Steam конденсируется | `T < 95` выбирает water | steam становится water |
| 6 | Hysteresis | интервалы циклов пусты | 0…2 и 95…100 не дрожат между материалами |
| 7 | Один переход | чистая функция применяет одно ребро | extreme-T ice проходит только в water, не в steam, за один frame |
| 8 | Mass | нормализатор сохраняет bits | сумма mass до/сразу после phase pass равна в float tolerance |
| 9 | Temperature | модель A сохраняет bits | probe/snapshot показывает исходную T после перехода |
| 10 | Остальные поля | таблица правил для всех kind-пар | velocity/pressure/body/rest соответствуют разделу 5 |
| 11 | Empty default | empty не оценивает rules | zero cells остаются полностью zero |
| 12 | Pause | scheduler выдаёт 0 phase passes | нагретая/охлаждённая кистью клетка не меняет material |
| 13 | Continue | первый unpaused batch запускает pass | после Continue переход происходит без catch-up chain |
| 14 | v5 round-trip | codec сохраняет ice/steam fields | water/ice/steam и температуры совпадают после save/load |
| 15 | Runtime reorder | ссылки разрешены заново по ID | внешний порядок меняет indices, но targets остаются верны |
| 16 | Внешний материал | parser/registry принимает valid graph | внешний material переходит без изменения engine/HLSL |
| 17 | Missing target | core fatal, external skipped | понятный `PHYXEL_MATERIAL_ERROR`, silent fallback отсутствует |
| 18 | Invalid JSON | NaN/Infinity/unknown/overlap rejected | invalid external files не попадают в menu/GPU table |
| 19 | Энергетический контракт | T/M сохраняются, sensible E пересчитана с target capacity | snapshot соответствует модели A без threshold clamp |
| 20 | Старые acceptance | прежние verifiers зелёные | полный cellular/solid/thermal/tool/save набор не регрессирует |

Дополнительные обязательные GPU cases: liquid↔gas, liquid↔fixed-solid, granular↔liquid, разные gas IDs рядом, temperature brush на Pause, четыре thermal catch-up ticks, external target и phase timestamp на всех scale presets. Movable-solid cases не блокируют v1, пока ice fixed; они блокируют будущую смену ice kind.

Промежуточные snapshots следует использовать в сценариях № 6, 7, 12 и 13: capture до threshold, после brush/thermal и после первого/второго phase frame. Это доказывает не только конечное состояние, но и отсутствие скрытого двойного перехода.

Фактическая generic GPU-матрица проходит 31/31 автоматический case. На GPU проверены liquid→gas, gas→liquid, liquid→liquid, granular→liquid, liquid→granular, liquid→fixed-solid, fixed-solid→liquid и gas→movable-solid. Hysteresis стабилен после пяти pass; catch-up кадр выполняет четыре thermal ticks и ровно один phase dispatch; Pause/Continue проходит при 30/60/100 FPS. Diagnostics-controlled ring test получает два независимых overflow (`PhaseFallbackWakeUps=2`), после каждого возвращается к настоящему async summary readback. Reorder-набор с переименованными файлами и padding сдвигает target runtime index `2 → 3`, сохраняя строковый target ID. Generic v5 save/clear/load побайтно восстанавливает нормализованные клетки и температуры.

Фактическое core GPU-покрытие проходит 6/6 сценариев: thresholds/transitions/hysteresis/one-transition/field normalization/summary, fixed ice и пробуждение liquid/gas-движения, раздельное движение steam и `core:gas`, температурная кисть на Pause при 30/60/100 FPS и побайтный v5 round-trip настоящих water/ice/steam. В обычной игре вручную подтверждены меню и цвета, fixed ice, подъём steam, freeze/melt/boil/condense после Continue, temperature probe и Clear. Production `PhaseTransitions.hlsl` и правила нормализации для этой пользовательской цепочки не менялись.

Повторные performance-измерения с реальным core registry подтвердили прежний класс стоимости полного прохода: среднее steady/burst время составило 0.0232/0.0234 ms на 25%, 0.0520/0.0529 ms на 50%, 0.1995/0.2176 ms на 75% и 0.4016/0.3389 ms на 100%. На штатном пути `PhaseFallbackWakeUps=0`, а за кадр выполняется не более одного phase dispatch.

## 13. Безопасное разбиение реализации на коммиты

1. **Add phase transition material schema** — raw JSON model, строгая validation, двухэтапное разрешение ссылок, 64-байтный C#/HLSL layout, registry flag и CPU material regression. Без shader pass и core ice/steam.
2. **Run universal phase transition GPU pass** — реализовано: `PhaseTransitions.hlsl`, GPU resources, in-place dispatch, трёхслотовый summary/query ring, coordinator order, Pause semantics, CPU normalizer/scheduler regression и timestamps. Без core ice/steam.
3. **Add phase transition GPU acceptance** — реализовано: external acceptance-only материалы, thresholds/hysteresis, one-transition/catch-up, восемь kind-пар, точные summary, Pause/wake/readback ring, runtime reorder, disabled registry, Model A, generic v5 и performance. Без пользовательского core content.
4. **Add water ice steam phase chain** — реализовано: core JSON для ice/steam и transitions в water, UI order, content/motion/Pause/v5 acceptance. Общий shader не менялся.
5. **Complete phase transition compatibility coverage** — реализовано в generic и core acceptance: v5 round-trip, старые save cases, полный regression matrix и обновление общей документации.

Каждый engine-коммит должен отдельно проходить Debug build, холодную компиляцию всех shader entry points, относящиеся CPU verifiers и реальный GPU acceptance. Не смешивать с latent heat, movable ice, реакциями, fire или automatic sleep.

## 14. Зафиксированные решения первой версии

1. Лёд является неподвижным `solid`, пока movable-solid topology не получит отдельное acceptance-покрытие.
2. На границе фаз действует модель A: масса и температура сохраняются без latent heat.
3. Выполняется один phase pass после всей thermal-пачки кадра, а не после каждого diffusion tick.
4. `into: "core:empty"` запрещён; missing palette material использует существующую save-политику empty + warning.

Цепочка реализована без material-specific веток и без изменения `GridCell`/v5.

## 15. Коррекция движения разреженного steam

После пользовательской проверки сохранённой сцены обнаружено, что
`core:steam` мог образовать неподвижный столб. Причина находилась не в phase
transition: плотность steam (`0.03`) совпадала с допуском массового gas
balancer (`0.03`), поэтому последние шаги перераспределения считались
нулевыми, а клетки набирали `RestFrames`.

Исправление остаётся универсальным и не проверяет `core:steam`:

- gas с material density не выше `0.0301` после разрежения до mass `0.0601`
  переключается с continuum balance на discrete buoyant-particle movement;
- подъём, диагональная diffusion и горизонтальный дрейф у потолка ограничены
  через `DeltaTime`, поэтому движение одинаково при разных FPS;
- субминимальная доля при обычном gas balance переносится в другую живую
  клетку пары, а не теряется;
- более плотные `core:gas`, smoke и CO₂ сохраняют прежний массовый solver;
- `GridCell`, phase normalization и save format этой коррекцией не меняются.

Усиленный реальный GPU case `water_ice_steam_motion` проходит при 30/60/100
FPS: масса steam сохраняется на `64`, средняя высота меняется примерно с
`Y=180` до `Y=120–122`, горизонтальный span достигает `29–31` клеток, а
resting остаётся меньше 75%. Обычный gas acceptance и `combustion_chain`
также остаются зелёными.
