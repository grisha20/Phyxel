# Состояние проекта Phyxel

## Current handoff (working tree, 2026-07-19)

- Last completed commit: `e13d065 Add water ice steam phase chain`
- Last completed functional commit: `e13d065 Add water ice steam phase chain`
- Current task: combustion temperature/quenching and Powder Toy-like gas motion
- Implemented locally (not committed): combustion ceiling, inert coal, latent-heat
  boiling, faster thermal exchange, generic hot-solid glow, and proposal/resolve
  sparse-gas advection.
- `core:coal` is granular/BCOL-like so burned wood collapses into a charcoal pile
  instead of retaining the exact tree silhouette.
- Next task: review GPU acceptance captures and split changes into small commits.

> [!IMPORTANT]
> ## CURRENT HANDOFF

> **Steam movement correction (2026-07-19):** Sparse low-density gas now
> switches to generic frame-rate-independent buoyant-particle movement instead
> of freezing at the gas transfer tolerance. The user's saved 1440x810 scene
> was reproduced from a copy and steam visibly diffuses upward and along the
> ceiling. `water_ice_steam_motion` passes at 30/60/100 FPS with mass=64,
> average Y about 120-122 and horizontal span 29-31; ordinary gas and the full
> combustion chain remain green. No commit has been created.

> **Latest working-tree fire verification (2026-07-18):** Powder-Toy-style
> tuning is validated on the actual GPU after fixing flame dilution, over-fast
> rise, solid-brush contact, transient smoke mass, and slow universal thermal
> exchange. Full `combustion_chain` at 144 FPS reached
> `PHYXEL_ACCEPTANCE_SUCCESS`: `coal=38`, `partialWood=147`, `hotWood=140`,
> `maxWoodTemp=1491.6 C`, `flame=39`, `smoke=90`, average combustion pass
> `0.0993 ms`. CPU verifiers for world codec, thermal, combustion, and phase
> systems are green. Manual gameplay visual check is the only remaining step;
> no commit has been created.
>
> **Fire update (2026-07-18):** The combustion prototype has been expanded to a Powder-Toy-style selectable `core:fire` gas with lifetime, radius-2 flame ignition, smoke decay, and a user brush. `GridCell` is now 40 bytes and the current save format is v6; v3/v4/v5 remain loadable with zero transient lifetime. Full GPU acceptance reports real flame, smoke, coal, and partially burned wood.
>
> **Working-tree fire update (2026-07-18):** The Powder-Toy-style fire extension is implemented in the working tree for review: selectable `core:fire` gas, lifetime/decay, radius-2 flame ignition, data-driven smoke/CO2/flame emissions, wood/coal fuel burn, v6 world cells, and render-only flame halo. v3/v4/v5 remain loadable through migration. Next: optional visual check in the game window, then manually split and commit the changes.
> **Verification:** compile target and CPU regression verifiers are green; the latest real GPU `combustion_chain` reached `PHYXEL_ACCEPTANCE_SUCCESS` with `coal=38`, `partialWood=147`, `hotWood=140`, `flame=39`, `smoke=90`, and `maxWoodTemp=1491.6 °C`.
>
> - **Last completed commit:** `e13d065 Add water ice steam phase chain`.
> - **Current implementation step:** separate `core:fire` lifetime/spread, emission resolve, v6 migration, and acceptance are implemented in the working tree.
> - **Next planned engine task:** optional visual tuning after manual gameplay check; then split the implementation into reviewable commits.
> - **Last completed functional commit:** `e13d065 Add water ice steam phase chain`.
> - **Last corrective commit:** `7f32357 Fix reusable phase fallback readback`.
> - **Just completed:** пользовательские `core:ice` и `core:steam`, переходы `water ↔ ice ↔ steam`, меню, реальное движение фаз, Pause/Continue, v5 round-trip и отдельное core GPU acceptance-покрытие.
> - **Current project state:** базовая универсальная температура и первая полная пользовательская фазовая цепочка работают через общий data-driven runtime без material-specific веток в shader.
> - **Current task:** финальная проверка Powder-Toy-style fire implementation.
> - **Next planned engine task:** после ручного visual check — отдельные reviewable commits; smoke/CO₂ reactions остаются следующим расширением.
> - **Do not start yet:** explosions, corrosion, packs, hub, and a larger reaction system.
> - **Blocking issues:** известных блокеров нет. Есть отдельный технический долг в OOM-fallback масштаба, описанный ниже.
> - **Tests last run:** Debug build (0 ошибок/предупреждений), холодная компиляция 15 shader entry points, 31/31 generic phase GPU cases, 6/6 core water/ice/steam cases, performance на 25/35/50/75/85/100% и 14/14 прежних cellular/solid/thermal regression-сценариев.
> - **Documentation update rule:** после каждой завершённой крупной задачи обновлять этот блок.
>
> После каждой крупной задачи также обновляются соответствующие разделы статуса.

## Прогресс по фазам

Проценты — инженерная оценка готовности, а не автоматически вычисляемая метрика.

| Фаза | Состояние | Готовность |
|---|---|---:|
| Фаза 0 — стабилизация ядра | В работе | около 65% |
| Фаза 1 — материалы наружу | Завершена | 100% |
| Фаза 2 — универсальные системы | В работе | около 40% |
| Фаза 3 — реакции из JSON | Не начата | 0% |
| Фаза 4 — локальные пакеты | Не начата | 0% |
| Фаза 5 — официальный хаб | Не начата | 0% |

От исходного общего плана выполнено примерно **40%**. Архитектура простых материалов, температуры и первой фазовой цепочки уже работает end-to-end; реакции, горение и контентные паки остаются отдельными крупными этапами. Эти значения нужны для планирования и должны пересматриваться после завершения очередной универсальной системы.

## Что уже реализовано

### Материалы

- Один JSON-файл описывает один материал; постоянный ID имеет вид `namespace:name`.
- `MaterialRegistry` загружает core и внешние JSON, валидирует их и назначает `ushort RuntimeIndex` только на текущий запуск.
- `core:empty` всегда занимает runtime-индекс 0; остальные материалы не зависят от фиксированных runtime-индексов.
- Все 10 core-материалов находятся в `Materials/core`: empty, sand, water, ice, steam, metal, stone, gas, fixture и UI-инструмент eraser.
- Список кнопок материала строится из реестра. Eraser является инструментом и не записывается в активную клетку.
- Поддерживаются внешние granular, liquid, gas, movable solid и fixture/неподвижный solid.
- Старые `CreateBuiltIns()` и `MaterialId` удалены из основного пути.
- `core:gold_sand` удалён и при загрузке v4 мигрирует в `core:sand`; `core:concrete` переименован и мигрирует в `core:stone`.
- Реестр ограничен 256 материалами. Некорректный core JSON прерывает запуск; некорректный внешний файл пропускается с `PHYXEL_MATERIAL_ERROR`.
- Необязательные `thermal.transitions.below/above` хранят string target IDs, после стабилизации реестра разрешаются в текущие runtime-индексы и попадают в 64-байтную GPU-таблицу материалов.
- Registry отбрасывает невалидные внешние ссылки до устойчивого набора и запрещает мгновенные температурные циклы.
- `PhaseTransitions.hlsl` выполняет не более одного перехода клетки после thermal-пачки, сохраняет массу/температуру и нормализует velocity, pressure, body/rest state по source/target kind.
- CPU получает OR-summary через три staging/query slot без `Flush`; coordinator немедленно обновляет композицию и асинхронно будит нужные cellular/solid/hydraulic подсистемы.

### Физика

- Клеточная симуляция granular, liquids и gases выполняется compute shaders на GPU.
- Обычная вода растекается и сливается; отдельный переключатель гидравлики включает напор и сообщающиеся сосуды.
- Газ перемещается и перераспределяет массу без смешивания разных `MaterialIndex`.
- Movable solids образуют тела, падают под действием отдельного переключателя solid gravity и взаимодействуют с опорой.
- Работают плотность, вытеснение жидкости и плавучесть твёрдых тел.
- Granular-материалы осыпаются в жидкости, вытесняют её без встречного импульса и не превращают устойчивую насыпь в водяной канал.
- Fixture остаётся неподвижной опорой.

### Температура

- JSON материала содержит `initialTemperature`, `conductivity` и `heatCapacity` с обратимо совместимыми defaults и строгой валидацией.
- `GridCell` имеет поле `Temperature`; текущий C#/HLSL layout клетки — 36 байт.
- Температура переносится вместе с полной клеткой при движении материала. Для газа энергия учитывает массу.
- Теплопередача между четырьмя соседями работает отдельным ping-pong compute pass без гонок записи.
- Расчёт использует массу, теплоёмкость и гармоническое среднее проводимостей; тепловая энергия сохраняется в пределах численной погрешности.
- Thermal scheduler использует фиксированный шаг 0,05 с и не более четырёх шагов за кадр.
- Неактивные клетки считаются пустотой и не участвуют в теплопередаче.
- Temperature probe асинхронно читает выбранную клетку с GPU и показывает её температуру.
- Температурный инструмент задаёт целевую температуру клеткам в радиусе кисти, в том числе при Pause.
- Сценарий `temperature_tool` проверяет работу инструмента, сохранение материала/массы и неизменность соседних клеток.
- GPU timestamps измеряют стоимость thermal pass после прогрева.
- Автоматического thermal sleep пока нет намеренно: это отдельная оптимизация после стабилизации фаз и источников тепла.
- Первая пользовательская фазовая цепочка работает через данные: вода замерзает ниже 0 °C и кипит выше 100 °C, лёд тает выше 2 °C, пар конденсируется ниже 95 °C; точные пороги остаются стабильными благодаря строгим сравнениям и hysteresis.

### Интерфейс и управление

- Доступны масштабы симуляции 25%, 35%, 50%, 75%, 85% и 100%.
- Отдельно настраиваются размер кисти, плотность нанесения и целевая температура.
- Есть сохранение/загрузка, Clear, Pause, solid gravity и hydraulic pressure.
- Материалы и инструменты выводятся из `MaterialRegistry`; внешние видимые материалы появляются после перезапуска без пересборки.

### Сохранения и совместимость

- Текущий формат сцены — v5: строковая палитра материалов и world-клетки со stride 36.
- v4 использует строковую палитру, но старую 32-байтную клетку; v3 использует отдельную legacy-палитру.
- `LegacyGridCellV3V4` навсегда фиксирует старый 32-байтный layout, а `WorldCellCodec` преобразует поля в текущий `GridCell`.
- При загрузке v3/v4 температура инициализируется из свойств соответствующего материала.
- При сохранении отдельная копия snapshot перекодируется `runtime index → scene compact index`; живая GPU-сетка не изменяется.
- При загрузке заранее строится числовая таблица `scene compact index → current runtime index`, поэтому строковый lookup не выполняется для каждой клетки.

### Diagnostics и acceptance

- Есть CPU regression verifiers для world codec, thermal material layout/валидации и thermal diffusion.
- Acceptance harness создаёт воспроизводимые GPU-сценарии, снимает snapshot/метрики и возвращает явный success/failure.
- Покрыты базовые sand, slope, water, hydraulics, gas, solid gravity, buoyancy, внешние материалы и granular/liquid-взаимодействие.
- Thermal acceptance проверяет контакт, проводимость, разные теплоёмкости, изолятор, vacuum, газовую энергию, temperature probe и длительное равновесие.
- Phase acceptance проверяет строгие пороги, hysteresis, один переход за pass и четыре catch-up ticks, нормализацию восьми kind-пар, точные summary flags, Pause при 30/60/100 FPS, физическое пробуждение gas/liquid, два независимых overflow readback ring, runtime reorder, disabled registry, Model A и v5 round-trip.
- Отдельный core acceptance использует настоящие `core:water`, `core:ice` и `core:steam`: проверяет стабильные состояния и переходы, fixed ice, падение воды, подъём пара без смешивания с `core:gas`, температурную кисть на Pause и побайтный v5 save/load.
- Проверяется v5 round-trip, чтение v3/v4 и миграции удалённых/переименованных core ID.

## Контрольные коммиты

| Коммит | Результат |
|---|---|
| `8928284` — Phase 1-Lite | Внешний JSON-материал, строковый ID, runtime-индекс, меню и палитра сцены v4. |
| `fcd6709…06b0fc1` — Phase 1 Full | Универсальные GPU-предикаты и flags, общая физика типов, legacy v3 и все core-материалы из JSON. |
| `a3d42b3` | Исправлен обмен granular/liquid; gold sand удалён с миграцией. |
| `ac7d6fe` | Concrete переименован в stone с миграцией v4 и legacy mapping v3. |
| `8aaf7f6` | Добавлены scale presets 25/35/50/75/85/100%. |
| `78291af` | Чтение v3/v4 отделено в версионированный codec 32-байтных legacy-клеток. |
| `3e94d23` | В JSON и GPU-таблицу материалов добавлены thermal properties. |
| `12dcea8` | В `GridCell` добавлена температура; включены 36-байтная клетка и сохранения v5. |
| `6879501` | Реализованы GPU diffusion, fixed-step scheduler и temperature probe. |
| `97428dd` | Завершено GPU acceptance-покрытие температуры и сохранения энергии. |
| `4d4eb7c` | Добавлен интерактивный temperature brush и его проверки. |
| `dd66c0e` | Завершена generic GPU acceptance-матрица универсальных фазовых переходов. |
| `Add water ice steam phase chain` | Добавлены пользовательские ice/steam, переходы воды и core GPU acceptance; SHA указывается в handoff. |

## Что ещё не реализовано

### Ближайшие универсальные системы

- CO₂;
- дерево, универсальное горение, дым и уголь;
- отдельное состояние горящей клетки и тушение.

### Более поздняя физика и химия

- взрывы и давление взрыва;
- коррозия, растворение и damage;
- продукты реакций и ограничения их выхода.

### Контентная архитектура

- JSON-схема реакций;
- runtime/GPU-таблица реакций;
- разрешение конфликтов, отсутствующих ссылок и симметричных/асимметричных правил.

### Паки и распространение контента

- `manifest.json`, каталог `Packs`, зависимости, версии и совместимость;
- формат `.pxpack`, подписи и локальная установка;
- серверный hub, аккаунты, покупки, официальные загрузки и обновления.

## Рекомендуемый порядок следующих работ

1. Проверить и при необходимости настроить пользовательское поведение цепочки `water ↔ ice ↔ steam` без изменения её общей архитектуры.
2. Выбрать следующий универсальный материал (например, CO₂) либо отдельно спроектировать универсальное горение.
3. Добавить wood/smoke/coal через данные и acceptance только после утверждения модели горения.
4. Спроектировать JSON-реакции и общую GPU-таблицу правил.
5. Реализовать взрывы как отдельную универсальную механику.
6. Реализовать коррозию и растворение.
7. Ввести локальные Packs.
8. Только после стабилизации Packs начинать hub.

Следующие универсальные системы требуют отдельного анализа перед кодом. Горение, реакции и взрывы не следует смешивать с настройкой первой фазовой цепочки.

## Известные ограничения и технический долг

- Автоматический thermal sleep отсутствует намеренно; thermal pass работает для непустого мира даже после равновесия.
- Empty — вакуум: неактивные клетки не проводят тепло и не охлаждают соседей.
- Нет heatmap и температурной окраски материала; доступно числовое значение probe.
- Нет огня, источников постоянного нагрева, latent heat и реакций; первая фазовая цепочка использует утверждённую модель A с сохранением массы и температуры при смене материала.
- Falling rigid-body/rotation требуют будущего развития; текущий solid-body solver не является полноценной механикой жёстких тел.
- Hub зависит от стабильного локального формата Packs и не должен разрабатываться раньше него.
- Универсальные системы должны оставаться отдельными небольшими коммитами; добавление обычного материала не должно требовать правок общих engine-файлов.
- OOM-fallback в `PhyxelGame` всё ещё делает `settings.ApplyScale(settings.Scale - 0.25f)`. Для preset-шкалы это может пройти через непредусмотренное промежуточное значение. Разрешённая будущая правка: по OOM переходить строго `100 → 85 → 75 → 50 → 35 → 25`, не меняя обычный slider.

## Definition of Done

### Для новой универсальной механики

- схема данных/JSON определена и валидируется;
- C# и HLSL layouts совпадают и проверяются по размеру/порядку полей;
- в общей логике нет проверок конкретного `MaterialIndex`;
- старые сцены продолжают загружаться;
- есть CPU regression для чистой логики и реальный GPU acceptance для поведения;
- Debug собирается без ошибок и предупреждений;
- все shader entry points компилируются после очистки cache;
- прежние acceptance-сценарии остаются зелёными;
- этот документ обновлён новым HEAD, состоянием, тестами и следующим шагом.

### Для нового материала

- материал находится в отдельном JSON и имеет валидный строковый ID;
- общие C#/HLSL engine-файлы не меняются без необходимости новой универсальной механики;
- видимый материал появляется в меню из реестра;
- есть acceptance, подтверждающий его kind/properties и нужное поведение;
- ID не конфликтует с core/другими материалами;
- save/load сохраняет материал через строковую палитру.
# Актуальный handoff — fire implementation (2026-07-18)

Последний подтверждённый commit в истории проекта: `e13d065 Add water ice steam phase chain`.
В рабочем дереве реализован следующий этап: отдельный выбираемый `core:fire` как gas/flame-клетка, lifetime/decay, радиус-2 распространение, расход Mass дерева и угля, smoke-продукты, GPU acceptance и сохранение v6 с загрузкой v3/v4/v5.

- **Текущая задача:** финальная проверка и ручное разделение изменений на коммиты.
- **Следующий шаг:** проверить визуальное поведение fire tool в окне игры и после подтверждения вручную закоммитить изменения.
- **Сборка:** compile target проходит; полный `dotnet build` на UNC-пути блокируется существующим wildcard Content-copy ограничением среды.
- **Проверки:** CPU material/world/phase/thermal verifiers зелёные; реальный GPU `combustion_chain` завершён `PHYXEL_ACCEPTANCE_SUCCESS`.
