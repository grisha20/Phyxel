# Каменный уголь — `core:stone_coal`

- [CP: тепло угольных продуктов, измерения и ограничения](../shared/COAL_PRODUCT_HEAT_RESULTS.md), [критерии](../shared/COAL_PRODUCT_HEAT_CONTRACT.md).

- [CR: лучевой обмен открытых зёрен и ограничения проверки](../shared/COAL_REHEAT_RESULTS.md).

- [FP15: конечный кислород и оплаченный факел; выбранные проверки](../shared/FURNACE_POWER_RESULTS.md).

- [FP12: общий рендер горячего конечного угольного FIRE](../shared/FURNACE_POWER_RESULTS.md).

- [FP: пламя, поддув, излучение стен и нерешённый нагрев](../shared/FURNACE_POWER_RESULTS.md), [контракт](../shared/FURNACE_POWER_CONTRACT.md).

- [FA: новые печи, перенос E/C и проверяемая сборка](../shared/FURNACE_ATMOSPHERE_RESULTS.md), [контракт](../shared/FURNACE_ATMOSPHERE_CONTRACT.md).

- [FL: длительная печь, границы воздуха и выбранные проверки](../shared/FURNACE_LONG_RUN_RESULTS.md), [контракт](../shared/FURNACE_LONG_RUN_CONTRACT.md).

- [HC: угольный фронт, бункер, наружный воздух и выбранные регрессии](../shared/HOPPER_COMBUSTION_RESULTS.md), [контракт](../shared/HOPPER_COMBUSTION_CONTRACT.md).

- [CG: свежий контроль поджига/выгорания после оптимизации O₂](../shared/COAL_GPU_RESULTS.md).

- [Паспорт](PASSPORT.md): источники, поведение, критерии, статусы и ограничения.
- [Настройки игры](../../../Materials/core/stone_coal.json): JSON, загружаемый движком.
- [Общая регистрация материалов](../../../Materials/MaterialRegistry.cs).
- [Правило работы](../WORKFLOW.md), [шаблон](../TEMPLATE.md), [каталог](../README.md).

Общие контракты и результаты перечислены в паспорте; они не копируются
в эту папку. Новый специализированный документ добавляйте сюда и в список
выше. У скрытой фазы свой паспорт, а общая проверка семейства может
храниться у исходного материала. Настройки JSON не являются справочником СИ.

- [MH: взвешенный лучевой бюджет, нагрев стен и выбранные контроли](../shared/METAL_FLAME_HEAT_RESULTS.md), [критерии](../shared/METAL_FLAME_HEAT_CONTRACT.md).
