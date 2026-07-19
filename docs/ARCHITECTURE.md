# Архитектура Phyxel

Документ описывает фактическую архитектуру после реализации температуры, фаз, горения, угля и общего gas redistribution.

## Главные инварианты

- Постоянная идентичность материала — нормализованный string ID `namespace:name`.
- `RuntimeIndex` назначается только на текущий запуск. Только `core:empty` обязан иметь индекс 0.
- Основная физика использует `SimulationKind`, `Density`, flags и таблицы свойств, а не ID конкретного материала.
- Одна клетка содержит один материал; смешение разных газов внутри клетки не моделируется.
- C# и HLSL layouts совпадают по порядку, типам и размеру.
- Массовая симуляция остаётся на GPU; игровая петля не делает блокирующих `Flush`/полных readback.

## Основные компоненты

- `Materials/MaterialFileLoader.cs` — строгий JSON parser и проверка диапазонов/полей.
- `Materials/MaterialRegistry.cs` — загрузка core/external JSON, разрешение string targets и построение GPU-таблиц.
- `Graphics/GpuResourceLifecycleManager.cs` — создание сеток, таблиц, proposal/summary/staging ресурсов и шейдеров.
- `Graphics/SimulationDispatchCoordinator.cs` — расписание cellular, gas, thermal, contact, combustion, phase и composition passes.
- `Serialization/SimulationStateSerializer.cs` и `WorldCellCodec.cs` — versioned scene/world I/O.
- `Content/Shaders/PhysicsShared.hlsli` — общий контракт структур, kinds, flags и predicates.

## Layout клетки

`GridCell` имеет последовательный packed layout 40 байт:

| Offset | Поле | Тип | Назначение |
|---:|---|---|---|
| 0 | `MaterialIndex` | `uint` | Runtime-позиция материала. |
| 4 | `Mass` | `float` | Масса/заполнение. |
| 8 | `VelocityX` | `float` | Горизонтальный импульс. |
| 12 | `VelocityY` | `float` | Вертикальный импульс. |
| 16 | `Pressure` | `float` | Гидравлическое состояние. |
| 20 | `IsActive` | `uint` | Нулевая клетка является empty. |
| 24 | `BodyId` | `uint` | Принадлежность movable-solid body. |
| 28 | `RestFrames` | `uint` | Состояние покоя. |
| 32 | `Temperature` | `float` | Температура, °C. |
| 36 | `Lifetime` | `float` | Остаточное время transient-материала. |

`LegacyGridCellV3V4` навсегда остаётся 32-байтным. Legacy v5 имеет stride 36. Codec переводит каждый layout по полям, без предположения о совпадении памяти.

## Таблица материалов

`MaterialProperties` — packed структура 120 байт (30 четырёхбайтных полей). Порядок в C# и HLSL одинаков:

1. flags, kind, density, friction, flow rate и RGBA;
2. initial temperature, conductivity, heat capacity;
3. below/above phase thresholds и runtime targets;
4. ignition, burn rate, heat per mass, burned-into target, flame spread;
5. minimum/maximum lifetime и decay target;
6. maximum combustion temperature и latent heat;
7. ambient temperature/cooling rate;
8. liquid-contact target/rate.

Отдельная `MaterialEmissionProperties` хранит runtime targets/rates для smoke, gas и flame. JSON всегда хранит string IDs; registry разрешает их только после стабилизации полного набора материалов.

## JSON-модель

Один файл соответствует одному материалу. Базовые разделы: `schema`, `id`, `name`, `kind`, `flags`, `color`, `physics`, `thermal`, `combustion`, `emissions`, `lifecycle`, `contactTransitions`, `ui`.

Пример внешнего теплообмена:

```json
"thermal": {
  "initialTemperature": 105.0,
  "conductivity": 0.04,
  "heatCapacity": 2.08,
  "ambientCooling": {
    "temperature": 20.0,
    "rate": 0.04
  }
}
```

Отсутствующий `ambientCooling` означает rate 0 и сохраняет прежнее поведение. Rate конечный и находится в `(0, 100]`, temperature — в `[-273.15, 5000]`.

Пример контактного перехода:

```json
"contactTransitions": {
  "liquid": {
    "into": "core:wet_charcoal",
    "ratePerSecond": 0.35
  }
}
```

Текущая модель поддерживает общий переход granular-источника при контакте с liquid. Target должен быть granular; rate конечный и находится в `(0, 100]`. Вероятность считается из фиксированного `dt`, поэтому результат не зависит от render FPS.

## GPU-ресурсы

- Два structured buffer сетки меняются ролями source/destination там, где нужен race-free pass.
- Таблицы material/emission properties доступны шейдерам только для чтения.
- Cellular proposal/resolve, pressure routes, solid geometry, summaries и brush commands имеют отдельные buffers.
- `CellMaterials` синхронизируется со сменой материала/клетки и используется для статистики и scheduling.
- Timestamp/query и staging slots читаются асинхронно с `DoNotFlush`.

Clear обнуляет сетку. Это корректно, потому что нулевой layout представляет неактивную клетку, а `core:empty` всегда имеет runtime index 0.

## Расписание кадра

При активной симуляции coordinator выполняет:

1. применение brush commands и обслуживание GPU-ресурсов;
2. solid gravity/hydraulic preparation и cellular schedule при наличии awake matter;
3. fixed 60 Hz ordinary-gas ticks;
4. fixed 20 Hz thermal diffusion и следующий за ним contact transition;
5. combustion/emission/transient lifecycle на thermal ticks;
6. phase transition после thermal/combustion;
7. composition только при изменившемся представлении.

Pause останавливает cellular, gas, thermal, contact, combustion и phase clocks без накопления отложенного времени. Явные команды кисти всё ещё могут изменить сцену.

## Газовая подсистема

`FixedStepGasScheduler` выдаёт шаги по 1/60 с. Один tick запускает четыре `GasRedistribution` dispatch:

- один вертикальный parity (parity чередуется между ticks);
- оба соседних горизонтальных parity;
- один вращающийся long-range span из 1/4/16/64.

Обычным газом считается `kind: gas` без флага `flame`. Для пары empty/одинакового газа сохраняются масса, mass-weighted температура и lifetime. Разные материалы не сливаются: вертикально более плотный газ оказывается ниже, горизонтально допустима ограниченная перестановка.

Минимальная представимая порция — 0.01. Остатки не удаляются. Если пакет нельзя корректно разделить, целая порция выбирает сторону по детерминированному hash и целевой доле. Подробнее: [GAS_SIMULATION.md](GAS_SIMULATION.md).

## Температура, контакты и фазы

`FixedStepThermalScheduler` работает с шагом 0.05 с. `ThermalDiffusion.hlsl` читает source и пишет destination, поэтому соседние threads не конкурируют. Обмен учитывает разницу температур, проводимости и `Mass × HeatCapacity`.

Ambient cooling применяется в thermal pass по стабильной формуле:

```text
factor = 1 - exp(-rate * dt)
T += (ambientTemperature - T) * factor
```

Это открытая система: энергия уходит во внешнюю среду намеренно.

`ContactTransitions.hlsl` меняет material state при геометрическом контакте с liquid. `PhaseTransitions.hlsl` применяет не более одного below/above перехода за thermal batch, сохраняет массу/температуру и нормализует kind-specific поля. Summary readback использует ring slots; fallback wake-up не переиспользуется до завершения предыдущего запроса.

## Горение и transient-материалы

Combustion pass использует данные материала: ignition threshold, burn rate, heat per mass, burned-into target и emissions. `core:fire` имеет флаг `flame`, собственный lifetime и decay target. Emission resolve создаёт продукты только в разрешённых destination-клетках.

Обычная material-кисть пишет только в empty. Исключение не является заменой материала: кисть flame при попадании в combustible fixed solid повышает его температуру выше ignition threshold, сохраняя `MaterialIndex`, массу и геометрию.

## Сохранения

- v3/v4: header 20 байт, неявный stride 32; v3 использует изолированную legacy palette, v4 — строковую scene palette.
- v5: header с явным stride 36, клетка содержит temperature.
- v6: текущий writer, явный stride 40, клетка содержит temperature и lifetime.

Сцена хранит `MaterialPalette` как массив string IDs, где позиция — компактный scene index. При сохранении отдельная копия snapshot преобразуется runtime→scene числовой таблицей. При загрузке palette один раз преобразуется string→runtime, затем grid remap выполняется численно. Живая сетка не перекодируется.

## Ограничения расширения

- Нельзя вводить material-specific runtime-ID в общие HLSL passes.
- Новые универсальные свойства требуют синхронного изменения C#/HLSL layout, валидатора, defaults и acceptance.
- Настоящая многокомпонентная смесь потребует отдельной модели состава клетки.
- Flame остаётся отдельной transient-моделью и не должен попадать в обычное выравнивание газа.
- Пакеты, зависимости, магазин/hub и произвольные реакции — отдельные будущие подсистемы.
