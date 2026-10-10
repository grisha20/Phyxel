# Smoke — `core:smoke`

- [FP: пламя, поддув, излучение стен и нерешённый нагрев](../shared/FURNACE_POWER_RESULTS.md), [контракт](../shared/FURNACE_POWER_CONTRACT.md).

- [FA: новые печи, перенос E/C и проверяемая сборка](../shared/FURNACE_ATMOSPHERE_RESULTS.md), [контракт](../shared/FURNACE_ATMOSPHERE_CONTRACT.md).

- [FL: длительная печь, границы воздуха и выбранные проверки](../shared/FURNACE_LONG_RUN_RESULTS.md), [контракт](../shared/FURNACE_LONG_RUN_CONTRACT.md).

- [HC: угольный фронт, бункер, наружный воздух и выбранные регрессии](../shared/HOPPER_COMBUSTION_RESULTS.md), [контракт](../shared/HOPPER_COMBUSTION_CONTRACT.md).

- [CG: межкадровое мерцание, sparse/dense проверки](../shared/COAL_GPU_RESULTS.md), [контракт](../shared/COAL_GPU_CONTRACT.md).

- [SE: контроль сухой тяги при изменении выхода пара](../shared/STEAM_ESCAPE_RESULTS.md).

- [Паспорт](PASSPORT.md): источники, поведение, критерии, статусы и ограничения.
- [Настройки игры](../../../Materials/core/smoke.json): JSON, загружаемый движком.
- [Общая регистрация материалов](../../../Materials/MaterialRegistry.cs).
- [Правило работы](../WORKFLOW.md), [шаблон](../TEMPLATE.md), [каталог](../README.md).

Общие контракты и результаты перечислены в паспорте; они не копируются
в эту папку. Новый специализированный документ добавляйте сюда и в список
выше. У скрытой фазы свой паспорт, а общая проверка семейства может
храниться у исходного материала. Настройки JSON не являются справочником СИ.

- [FS: тепло сохранённой печи](../shared/FURNACE_SENSOR_HEAT_CONTRACT.md), [новые результаты и ограничения](../shared/FURNACE_SENSOR_HEAT_RESULTS.md).
