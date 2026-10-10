# Древесный уголь — `core:coal`

- [FP: пламя, поддув, излучение стен и нерешённый нагрев](../shared/FURNACE_POWER_RESULTS.md), [контракт](../shared/FURNACE_POWER_CONTRACT.md).

- [FA: новые печи, перенос E/C и проверяемая сборка](../shared/FURNACE_ATMOSPHERE_RESULTS.md), [контракт](../shared/FURNACE_ATMOSPHERE_CONTRACT.md).

- [FL: длительная печь, границы воздуха и выбранные проверки](../shared/FURNACE_LONG_RUN_RESULTS.md), [контракт](../shared/FURNACE_LONG_RUN_CONTRACT.md).

- [HC: угольный фронт, бункер, наружный воздух и выбранные регрессии](../shared/HOPPER_COMBUSTION_RESULTS.md), [контракт](../shared/HOPPER_COMBUSTION_CONTRACT.md).

- [CG: полоса горящего угля, профиль и защита горения](../shared/COAL_GPU_RESULTS.md), [контракт](../shared/COAL_GPU_CONTRACT.md).

TN2026-10-08: при добавлении TNT повторно проверены реакция/O₂/эмиссии/выгорание
и сохранённая печь с датчиками; [контроль и границы](../tnt/RESULTS.md).
Параметры угля и алгоритм тяги не менялись.

- [Паспорт](PASSPORT.md): источники, поведение, критерии, статусы и ограничения.
- [Настройки игры](../../../Materials/core/coal.json): JSON, загружаемый движком.
- [Общая регистрация материалов](../../../Materials/MaterialRegistry.cs).
- [Правило работы](../WORKFLOW.md), [шаблон](../TEMPLATE.md), [каталог](../README.md).

## Документы этого материала или семейства

- [Выгорание древесного угля — 2026-10-03](COAL_BURNOUT_RESULTS.md).
- [Поджиг древесного и каменного угля — 2026-10-01](COAL_FIRE_RESULTS.md).

Общие контракты и результаты перечислены в паспорте; они не копируются
в эту папку. Новый специализированный документ добавляйте сюда и в список
выше. У скрытой фазы свой паспорт, а общая проверка семейства может
храниться у исходного материала. Настройки JSON не являются справочником СИ.

- [FS: тепло сохранённой печи](../shared/FURNACE_SENSOR_HEAT_CONTRACT.md), [новые результаты и ограничения](../shared/FURNACE_SENSOR_HEAT_RESULTS.md).

- [GW: регрессии при оптимизации GPU нагрузки](../shared/GPU_WORKLOAD_RESULTS.md).
