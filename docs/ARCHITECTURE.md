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
| 16 | `Pressure` | `float` | Гидравлика; у fixed thermal regulator — уставка. |
| 20 | `IsActive` | `uint` | Нулевая клетка является empty. |
| 24 | `BodyId` | `uint` | Принадлежность movable-solid body; на flame — marker собственного окислителя. |
| 28 | `RestFrames` | `uint` | Состояние покоя. |
| 32 | `Temperature` | `float` | Температура, °C. |
| 36 | `Lifetime` | `float` | Lifecycle; у phase-enthalpy — фазовый прогресс, у regulator — мощность; у PersistentCoalIgnition — розжиг (0/1). |

`LegacyGridCellV3V4` навсегда остаётся 32-байтным. Legacy v5 имеет stride 36. Codec переводит каждый layout по полям, без предположения о совпадении памяти.

## Таблица материалов

`MaterialProperties` — packed структура 176 байт (44 четырёхбайтных поля). Порядок в C# и HLSL одинаков:

1. flags, kind, density, friction, flow rate и RGBA;
2. initial temperature, conductivity, heat capacity;
3. below/above phase thresholds и runtime targets;
4. ignition, burn rate, heat per mass, burned-into target, flame spread;
5. minimum/maximum lifetime и decay target;
6. maximum combustion temperature и latent heat;
7. ambient temperature/cooling rate;
8. liquid-contact target/rate;
9. gas diffusion/buoyancy/hot air и motion coefficients;
10. gas haze strength, tagged device target/gas oxidizer displacement и device power (offsets 168/172).

Отдельная `MaterialEmissionProperties` хранит runtime targets/rates для smoke, gas и flame. JSON всегда хранит string IDs; registry разрешает их только после стабилизации полного набора материалов.

## JSON-модель

Один файл соответствует одному материалу. Базовые разделы: `schema`, `id`, `name`, `kind`, `flags`, `color`, `physics`, `thermal`, `combustion`, `emissions`, `lifecycle`, `contactTransitions`, `ui`.

Пример внешнего теплообмена:

```json
"thermal": {
  "initialTemperature": 122.0,
  "conductivity": 0.04,
  "heatCapacity": 2.08,
  "ambientCooling": {
    "temperature": 20.0,
    "rate": 0.0025
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

## Управляемые источники тепла

`thermal.regulator` задаёт `mode: heating/cooling`, `targetTemperature` и
`maximumPower`. Parser допускает только fixed solid с положительной плотностью,
без phases, lifecycle, combustion, emissions и contact transition.
Auto flags `ThermalHeater/ThermalCooler` исключают зависимость шейдера от core ID.

Thermal pass работает с dt 0.05: после обычного теплообмена источник добавляет
`q = clamp(C*(target-T), 0, power*dt)` или удаляет `q = clamp(..., -power*dt, 0)`.
Это внешний источник/сток энергии; соседям тепло передаётся через контакт.
Нулевая мощность оставляет устройство пассивным проводником.

Brush mode 3 создаёт устройство либо меняет настройки того же материала,
сохраняя его температуру и массу. `TargetTemperature` — уставка, `Reserved`
передаёт IEEE float bits мощности; в остальных modes этот слот сохраняет BodyId.
Temperature tool остаётся независимым внешним воздействием. Настройки блока
записываются в существующие Pressure/Lifetime slots world v6.

Probe SRV1 читает материал. Существующий 16-байтный результат использует
Reserved только для regulator: low 16 bits — уставка в десятых градуса с
offset 2732, high 16 bits — мощность в десятых единицы/с.

Только acceptance modes `thermal_devices/steam_apparatus` выделяют observer
UAV и staging для накопления Q устройства и Q среды. ThermalConstants теперь
32 байта, C#/HLSL синхронны. Observer не участвует в физических решениях;
обычная игра не выделяет его и не делает эти readbacks.

## Стекание тонкого слоя воды

Локальный `WaterCanDrainSurfaceFilm` разрешает свободной поверхностной
клетке на solid/granular основании присоединиться к воде в соседнем столбце
на ряд ниже. Движение только на одну клетку; обычные вертикальные/диагональные
проходы затем снимают получившийся выступ. Движение и проверка покоя используют
одинаковое условие. Капля на ровной сухой полке его не проходит.
Масса, температура и фазовая энтальпия переносятся существующим SwapCells;
новых ресурсов/полей и обходов стен нет.

## Конвекция воды

`WaterConvection.hlsl` выполняется перед теплопроводностью на фиксированных
20 Гц, независимо от FPS, AirSimulation, гидравлики и сна клеток.
Четыре чередующиеся разбиения на непересекающиеся блоки 2×2 вращают
полные пакеты core:water: вверх идёт более тёплый пакет, вниз — более холодный.
Изотермический блок и устойчивый тёплый верхний слой не вращаются.
Масса, температура и накопленная теплота фазового перехода движутся вместе;
занятость клеток не меняется, карты материалов/преград остаются действительны.
Частичные пакеты, поверхность, стенки и другие вещества этим проходом
не перемещаются. Новых буферов состояния и изменений layouts нет.
Это локальная игровая модель без инерции потока и особой плотности воды
около 4 °C. Теплопроводность металла остаётся прежней.
`thermalGpuMs` теперь включает конвекцию вместе с теплопроводностью.

## GPU-ресурсы

- Два structured buffer сетки меняются ролями source/destination там, где нужен race-free pass.
- Таблицы material/emission properties доступны шейдерам только для чтения.
- Cellular proposal/resolve, pressure routes, solid geometry, summaries и brush commands имеют отдельные buffers.
- `CellMaterials` синхронизируется со сменой материала/клетки и используется для статистики и scheduling.
- `AirFlowLinks` — производные восемь связей на узел воздуха 4×4, 4 байта
  на узел. Перестраиваются по fine grid перед решением воздуха; solid/liquid
  и `blocks-air` перекрывают связи. Тонкая стенка не зависит от порога
  заполнения грубой клетки. Давление, сглаживание, advection и vorticity
  используют эти связи. `FineAirGeometry.hlsli` также выбирает ближайший
  видимый узел для газовой частицы: чтение скорости, источник тепла/давления
  и drag не связывают стороны одной клетки, разделённые стенкой.
- `AirProjectionA/B` — два временных float2 на coarse-узел (16 байт суммарно).
  Общие граничные расходы временно размещаются в AirScratch. После переноса
  выполняются 128 Jacobi-итераций и согласованная поправка скорости.
  Начальное приближение сохраняется между тиками, но сбрасывается при
  clear, выключении воздуха, загрузке и перезапуске физики. Эти буферы
  не входят в GridCell, AirCell или формат сохранения.
- Timestamp/query и staging slots читаются асинхронно с `DoNotFlush`.

Clear обнуляет сетку. Это корректно, потому что нулевой layout представляет неактивную клетку, а `core:empty` всегда имеет runtime index 0.

## Расписание кадра

При активной симуляции coordinator выполняет:

1. применение brush commands и обслуживание GPU-ресурсов;
2. solid gravity/hydraulic preparation и cellular schedule при наличии awake matter;
3. fixed 60 Hz ordinary-gas ticks;
4. fixed 20 Hz water convection, thermal diffusion и следующий за ним contact transition;
5. combustion/emission/transient lifecycle на thermal ticks;
6. phase transition после thermal/combustion;
7. composition только при изменившемся представлении.

Pause останавливает cellular, gas, thermal, contact, combustion и phase clocks без накопления отложенного времени. Явные команды кисти всё ещё могут изменить сцену.

## Газовая подсистема

Ordinary motion gas и воздух работают на фиксированных 60 Гц с восемью
подшагами клеточного переноса. Старый `GasRedistribution` на 120 Гц остаётся
для совместимости; материалы с motion-параметрами из него исключаются.

Газовая частица переносится целиком в соседнюю доступную клетку: solver не
делит её массу. `MaterialIndex`, `Mass`, `Temperature` и `Lifetime`
сохраняются внутри пакета. Одна клетка хранит один материал. CO₂ получает
runtime-флаг `ThermalCarbonDioxide`: его собственный вертикальный импульс
зависит от отношения плотностей идеального газа и воздуха при 20 °C.
Холодное значение JSON сохранено; горячий CO₂ может подниматься.

Пар использует ordinary motion solver и обратимую фазовую энтальпию воды.
Температуры и ambient cooling задаются в JSON. Подробнее:
[GAS_SIMULATION.md](GAS_SIMULATION.md), [STEAM_ENERGY_RESULTS.md](STEAM_ENERGY_RESULTS.md).

## Температура, контакты и фазы

`FixedStepThermalScheduler` работает с шагом 0.05 с. `ThermalDiffusion.hlsl` читает source и пишет destination, поэтому соседние threads не конкурируют. Обмен учитывает разницу температур, проводимости и `Mass × HeatCapacity`.

Ambient cooling применяется в thermal pass по стабильной формуле:

```text
factor = 1 - exp(-rate * dt)
T += (ambientTemperature - T) * factor
```

Это открытая система: энергия уходит во внешнюю среду намеренно.

`ContactTransitions.hlsl` меняет material state при геометрическом контакте с liquid. `PhaseTransitions.hlsl` применяет не более одного below/above перехода за thermal batch, сохраняет массу/температуру и нормализует kind-specific поля. Для gas → liquid требуется связная масса того же газа в локальной окрестности: разреженный численный хвост не считается самостоятельным центром конденсации. Summary readback использует ring slots; fallback wake-up не переиспользуется до завершения предыдущего запроса.

## Горение и transient-материалы

Combustion pass использует данные материала: ignition threshold, burn rate, heat per mass, burned-into target и emissions. `core:fire` имеет флаг `flame`, собственный lifetime и decay target. Emission resolve создаёт продукты только в разрешённых destination-клетках.

`OxidizerTransport.hlsl` ведёт отдельное float-поле на мелкой сетке: свежий
воздух 1, истощённый/вытесненный 0. На каждом 60 Hz combustion tick сначала
четырёхгранная диффузия между проницаемыми клетками, затем реакция и запись
demand, затем списание с соседних доноров. Solid/granular/liquid блокируют
перенос; открытые внешние края восполняют воздух. Поле не зависит от
включения грубого AirSimulation и не восстанавливается при движении газа.
`gas.oxidizerDisplacement` (default 1, 0..1) задаёт вытеснение пакетом;
core:smoke=0.05. Для gas это tagged alias поля at offset 168; fixed regulator
использует тот же слот как уставку. Layout 176/44 сохранён.

Горение и ignition обычного топлива требуют окислителя; burnedMass
ограничивается безопасным бюджетом соседей; heat зависит от burnedMass,
emissions разрешаются при положительном расходе. Порог тушения сравнивает
запас с числом соседних клеток газового пространства: негазовая стена
блокирует приток, но не разбавляет концентрацию. CO₂/пар учитываются как
газовое пространство с вытесненным запасом. Для FIRE учитывается также
собственная клетка. Обычный FIRE при нехватке окислителя раньше превращается
в decay product. `self-oxidizing` fuel освобождён; только его flame products
несут marker `BodyId=0x80000000` до обычного decay, чтобы сохранить цепь
реакции без воздуха. EmissionRequest использует высокий бит SourceIndex
для переноса маркера, не меняя 20-byte layout. Это нормированный игровой
ресурс, не полноценная химическая смесь; перенос пока диффузионный.
При снижении ёмкости движущимся газом вытесненный запас пока не переносится
соседям: сумма поля не является количеством O₂ в движущейся смеси.
Пустой сохранённый мир с полем сохраняет GPU-ресурс и его clock; particle
sleep не восстанавливает свежий воздух. OpenBoundaries открывает стороны
и потолок для пополнения; пол остаётся закрытым.
Подробности и проверки: [OXIDIZER_RESULTS.md](OXIDIZER_RESULTS.md).

Обычная material-кисть пишет только в empty. Между предыдущей и текущей позициями указателя передаётся одна capsule-команда в координатах симуляции, поэтому быстрый штрих непрерывен и не зависит от FPS. Тот же segment-путь используют температура и ластик. Исключение не является заменой материала: кисть flame при попадании в combustible solid/granular повышает его температуру выше ignition threshold, сохраняя `MaterialIndex`, массу и геометрию. Это включает оба вида угля; доступность окислителя по-прежнему проверяется при самом горении.

## Сохранения

- v3/v4: header 20 байт, неявный stride 32; v3 использует изолированную legacy palette, v4 — строковую scene palette.
- v5: header с явным stride 36, клетка содержит temperature.
- v6: header 24 байта, явный stride 40, клетка содержит temperature и lifetime.
- v7: текущий writer, header 28 байт; тот же grid stride 40 и отдельная float-секция окислителя. Scene сохраняет OpenBoundaries. V3-v6 читаются, недостающий запас инициализируется свежим воздухом.

Сцена хранит `MaterialPalette` как массив string IDs, где позиция — компактный scene index. При сохранении отдельная копия snapshot преобразуется runtime→scene числовой таблицей. При загрузке palette один раз преобразуется string→runtime, затем grid remap выполняется численно. Живая сетка не перекодируется.

Обычная игра использует `%LOCALAPPDATA%/Phyxel/scene.json` и `scene.world`.
`PHYXEL_VERIFY_SCENE_PATH` задаёт явный путь. Generated acceptance-сцены
без требования загрузить пользовательский мир по умолчанию используют
`roundtrip-scene.json` в каталоге артефактов, чтобы save/load-тест не
перезаписывал игровую сцену. Сценарии `RequiresSavedScene` сохраняют
прежний выбор пути для чтения пользовательского файла.

## Ограничения расширения

- Нельзя вводить material-specific runtime-ID в общие HLSL passes.
- Новые универсальные свойства требуют синхронного изменения C#/HLSL layout, валидатора, defaults и acceptance.
- Настоящая многокомпонентная смесь потребует отдельной модели состава клетки.
- Flame остаётся отдельной transient-моделью и не должен попадать в обычное выравнивание газа.
- Пакеты, зависимости, магазин/hub и произвольные реакции — отдельные будущие подсистемы.
