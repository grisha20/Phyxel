# Общие документы материалов

FL2026-10-10: длительная печь, согласованный закрытый пол, перенос O₂
и конечное тепло атмосферного выхода — [контракт](FURNACE_LONG_RUN_CONTRACT.md),
[измерения, стоимость и ограничения](FURNACE_LONG_RUN_RESULTS.md).
Общая оптимизация этим этапом не закрыта; превосходство над TPT не заявляется.

HC2026-10-10: угольный фронт, бункер и наружные потоки —
[контракт/источники](HOPPER_COMBUSTION_CONTRACT.md), [результаты/границы](HOPPER_COMBUSTION_RESULTS.md).

CG2026-10-09: полоса угля, кислород и межкадровое мерцание дыма —
[контракт](COAL_GPU_CONTRACT.md), [A/B, новые проверки и helper для RTX3050](COAL_GPU_RESULTS.md).

WT2026-10-09: ускорены мокрые тепловые проходы без изменения коэффициентов —
[контракт](WATER_THERMAL_PERFORMANCE_CONTRACT.md), [A/B, проверки и оставшиеся просадки](WATER_THERMAL_PERFORMANCE_RESULTS.md).
Полный FPS ещё не исправлен; поверхностный кандидат исключён.

WN2026-10-09: полное поле с большой кистью и заполненной ванной —
[контракт](WATER_NATIVE_PERFORMANCE_CONTRACT.md), [измерения и ограничения](WATER_NATIVE_PERFORMANCE_RESULTS.md).
Коэффициенты материалов прежние; это выбранные проверки общего переноса.

WP2026-10-09: оптимизация общего жидкостного уровня, без новых реакций/ID.
[Контракт](WATER_POUR_PERFORMANCE_CONTRACT.md), [A/B и защита поведения](WATER_POUR_PERFORMANCE_RESULTS.md).
Карта пар/CSV прежние; полная приёмка всех взаимодействий не заявляется.

SE2026-10-09: охлаждение металла и малая капля QR приняты пользователем;
ограничение внутренних пузырьков и быстрый выход пара — [контракт](STEAM_ESCAPE_CONTRACT.md),
[новые измерения и границы](STEAM_ESCAPE_RESULTS.md). Масло отложено.
Это выбранные проверки, не полная приёмка паспортов.

QR2026-10-08: [пузырьки у горячей стенки, охлаждение глубины и отскок](WATER_REWETTING_CONTRACT.md),
[повторные проверки после отклонённого QH](WATER_REWETTING_RESULTS.md).
Масло отложено до принятия воды. Полная приёмка паспортов/пар не заявляется.

QH2026-10-08: вода/пар и смоченные проводящие solid — [контракт](WATER_QUENCH_CONTRACT.md), [новые проверки и ограничения](WATER_QUENCH_RESULTS.md). Полная приёмка паспортов/пар не заявляется.

- [QW: вода на горячей поверхности — наблюдение и предлагаемая модель](HOT_SURFACE_WATER_OBSERVATION.md).

- [PA: профиль портрета и устранение повторного поиска](PORTRAIT_GPU_CONTRACT.md), [свежие проверки и оставшаяся просадка](PORTRAIT_GPU_RESULTS.md).

- [GW: уменьшение нагрузки GPU после отзывов RTX 3050](GPU_WORKLOAD_CONTRACT.md), [проверки и оставшиеся просадки](GPU_WORKLOAD_RESULTS.md).

- [RS: локальный прорыв, измерение давления и FPS](PRESSURE_RELIABILITY_CONTRACT.md), [новые результаты и исключения](PRESSURE_RELIABILITY_RESULTS.md).

- [PC: замкнутый напор и экспериментальное включение](PRESSURE_CONFINEMENT_CONTRACT.md), [новые проверки](PRESSURE_CONFINEMENT_RESULTS.md).

Здесь хранятся контракты и планы, относящиеся к нескольким материалам.
Начните с [каталога](../README.md), [workflow](../WORKFLOW.md) и [шаблона](../TEMPLATE.md).

- [PV — сброс через выход](PRESSURE_VENT_CONTRACT.md),
  [результаты и границы](PRESSURE_VENT_RESULTS.md).

- [PB — прямой разрыв без деформации](PRESSURE_BURST_CONTRACT.md),
  [результаты и границы](PRESSURE_BURST_RESULTS.md).

- [PM — история: пластичность металлов, отменена PB](PRESSURE_MATERIALS_CONTRACT.md),
  [результаты и границы](PRESSURE_MATERIALS_RESULTS.md).

- [AB — заметная пропитка и каталог жидкостей](ABSORPTION_CONTRACT.md).
- [MA: сталь и чугун, 2026-10-06](ALLOYS_CONTRACT.md).
- [Свободные тела и равновесие — BB, 2026-10-04](BODY_BALANCE_CONTRACT.md).
- [BW — прогрев котла без изменения тяги, 2026-10-06](BOILER_HEAT_CONTRACT.md).
- [Выгорание основы с удержанной жидкостью — 2026-10-05](BURNOUT_STOCK_CONTRACT.md).
- [Объёмный поток в трубе — контракт2026-10-04](CHIMNEY_WIDTH_CONTRACT.md).
- [Заявки продуктов горения: один шаг — 2026-10-06](EMISSION_LIFETIME_CONTRACT.md).
- [Свободный факел после CW —2026-10-04, до правки](FREE_FLAME_CONTRACT.md).
- [Свободные замёрзшие тела — FB, 2026-10-04](FROZEN_BODY_CONTRACT.md).
- [Намокание топлива: порученный этап, 2026-10-03](FUEL_MOISTURE_CONTRACT.md).
- [Перегрев воздуха и тяга сохранённой печи — 2026-10-06](FURNACE_DRAFT_CONTRACT.md).
- [BH — нагрев дна без изменения алгоритма тяги, 2026-10-06](FURNACE_SURFACE_HEAT_CONTRACT.md).
- [HF — пять замечаний из видео, 2026-10-05](HANDOFF_FIXES_CONTRACT.md).
- [История этапов и прежний индекс](HISTORY.md).
- [interaction_pairs](interaction_pairs.csv).
- [Карта взаимодействий текущего каталога](INTERACTIONS.md).
- [Струя жидкости на уголь — контракт до правки, 2026-10-03](LIQUID_FEED_CONTRACT.md).
- [LL: округлая чаша и вода под маслом — 2026-10-05](LIQUID_LAYERS_CONTRACT.md).
- [LT — вязкость масла и перенос тепла в жидкости](LIQUID_TEMPERATURE_CONTRACT.md).
- [Масляный цвет, капиллярный подъём и тепло воздуха — 2026-10-04](MATERIAL_ENVIRONMENT_CONTRACT.md).
- [Заливка горячего угля: исправление, 2026-10-03](MOISTURE_POUR_CONTRACT.md).
- [Промокание кучи — порученный этап, 2026-10-03](MOISTURE_WICKING_CONTRACT.md).
- [Пропитка угля горючей жидкостью — контракт, 2026-10-03](OIL_ABSORPTION_CONTRACT.md).
- [OC: открытый огонь и дым, 2026-10-05](OPEN_COMBUSTION_CONTRACT.md).
- [Отдельные элементы и устанавливаемые пакеты](PACKS.md).
- [PL: уровень нескольких сосудов, 2026-10-06](POOL_LEVEL_CONTRACT.md).
- [Доведение существующего каталога](ROADMAP.md).
- [Карман у потолка вместо дымохода — 2026-10-06](ROOF_OUTLET_CONTRACT.md).
- [Зафиксированные источники паспортов](SOURCES.md).
- [Подводные кучи и плавающая смесь — 2026-10-03](SUBMERGED_HEAPS_CONTRACT.md).
- [Горизонт воды и плавающий лёд — WL](WATER_LEVEL_CONTRACT.md).

- [CU: ограниченный теплообмен новых проводников](COPPER_HEAT_CONTRACT.md).

- [PF: центральный поджиг и разлёт оболочки](PRESSURE_FRACTURE_CONTRACT.md), [результаты](PRESSURE_FRACTURE_RESULTS.md).
- [SP: холодная крыша котла, давление и падение осколков](STEAM_ROOF_DIAGNOSIS.md).

- [FJ: исправленный выброс и перенос осколков](FRAGMENT_JET_CONTRACT.md), [результаты и ограничения](FRAGMENT_JET_RESULTS.md).

- [FS — печь с датчиками: видимое тепло и проверка FPS](FURNACE_SENSOR_HEAT_CONTRACT.md), [результаты и ограничения](FURNACE_SENSOR_HEAT_RESULTS.md).

- [BD — разлёт многослойной оболочки и нагрузка разрушения](BLAST_DEBRIS_CONTRACT.md), [результаты, FPS и оставшиеся дефекты](BLAST_DEBRIS_RESULTS.md).
