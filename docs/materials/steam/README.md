# Пар — `core:steam`

- [FP: общая проекция, выбранные контроли и стоимость](../shared/FURNACE_POWER_RESULTS.md), [контракт](../shared/FURNACE_POWER_CONTRACT.md).

- [FA: новые печи, перенос E/C и проверяемая сборка](../shared/FURNACE_ATMOSPHERE_RESULTS.md), [контракт](../shared/FURNACE_ATMOSPHERE_CONTRACT.md).

- [FL: длительная печь, границы воздуха и выбранные проверки](../shared/FURNACE_LONG_RUN_RESULTS.md), [контракт](../shared/FURNACE_LONG_RUN_CONTRACT.md).

- [HC: угольный фронт, бункер, наружный воздух и выбранные регрессии](../shared/HOPPER_COMBUSTION_RESULTS.md), [контракт](../shared/HOPPER_COMBUSTION_CONTRACT.md).

- [WT: проверка конденсации и выхода пара после оптимизации тепла](../shared/WATER_THERMAL_PERFORMANCE_RESULTS.md), [контракт](../shared/WATER_THERMAL_PERFORMANCE_CONTRACT.md).

- [SE: спокойная заливка и выход пара](../shared/STEAM_ESCAPE_CONTRACT.md), [измерения и границы](../shared/STEAM_ESCAPE_RESULTS.md).

- [QR: повторная работа над заливкой, пузырьками и отскоком](../shared/WATER_REWETTING_CONTRACT.md), [новые результаты](../shared/WATER_REWETTING_RESULTS.md).

- [QH: мокрая горячая поверхность, капля и конечная энергия](../shared/WATER_QUENCH_CONTRACT.md), [новые проверки](../shared/WATER_QUENCH_RESULTS.md).

- [RS: локальный прорыв, давление и длинные печи](../shared/PRESSURE_RELIABILITY_RESULTS.md).

- [PC: замкнутый напор и отдельное включение разрушения](../shared/PRESSURE_CONFINEMENT_CONTRACT.md), [новые проверки](../shared/PRESSURE_CONFINEMENT_RESULTS.md).

- [Паспорт](PASSPORT.md): источники, поведение, критерии, статусы и ограничения.
- [Настройки игры](../../../Materials/core/steam.json): JSON, загружаемый движком.
- [Общая регистрация материалов](../../../Materials/MaterialRegistry.cs).
- [Правило работы](../WORKFLOW.md), [шаблон](../TEMPLATE.md), [каталог](../README.md).

## Документы этого материала или семейства

- [FJ: выброс и перенос осколков](../shared/FRAGMENT_JET_CONTRACT.md), [проверки и пределы](../shared/FRAGMENT_JET_RESULTS.md).

- [Баланс энергии воды и пара — 2026-10-01](STEAM_ENERGY_RESULTS.md).
- [Пар: эталоны TPT, итоговый baseline и границы применимости](STEAM_RESULTS.md).
- [SP: разрыв крыши котла и падение осколков](../shared/STEAM_ROOF_DIAGNOSIS.md).

Общие контракты и результаты перечислены в паспорте; они не копируются
в эту папку. Новый специализированный документ добавляйте сюда и в список
выше. У скрытой фазы свой паспорт, а общая проверка семейства может
храниться у исходного материала. Настройки JSON не являются справочником СИ.
