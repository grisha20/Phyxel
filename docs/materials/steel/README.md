# Сталь — `core:steel`

- [FP: общий лучистый обмен между стенами, выбранные проверки и границы](../shared/FURNACE_POWER_RESULTS.md), [контракт](../shared/FURNACE_POWER_CONTRACT.md).

- [FA: новые печи, перенос E/C и проверяемая сборка](../shared/FURNACE_ATMOSPHERE_RESULTS.md), [контракт](../shared/FURNACE_ATMOSPHERE_CONTRACT.md).

- [FL: длительная печь, границы воздуха и выбранные проверки](../shared/FURNACE_LONG_RUN_RESULTS.md), [контракт](../shared/FURNACE_LONG_RUN_CONTRACT.md).

- [SE: спокойная заливка и выход пара](../shared/STEAM_ESCAPE_CONTRACT.md), [измерения и границы](../shared/STEAM_ESCAPE_RESULTS.md).

- [QR: повторная работа над заливкой, пузырьками и отскоком](../shared/WATER_REWETTING_CONTRACT.md), [новые результаты](../shared/WATER_REWETTING_RESULTS.md).

- [QH: мокрая горячая поверхность, капля и конечная энергия](../shared/WATER_QUENCH_CONTRACT.md), [новые проверки](../shared/WATER_QUENCH_RESULTS.md).

- [BD: общий перенос осколков и новые проверки](../shared/BLAST_DEBRIS_RESULTS.md).

- [RS: локальный прорыв, давление и длинные печи](../shared/PRESSURE_RELIABILITY_RESULTS.md).

- [PC: замкнутый напор и отдельное включение разрушения](../shared/PRESSURE_CONFINEMENT_CONTRACT.md), [новые проверки](../shared/PRESSURE_CONFINEMENT_RESULTS.md).

- [PV — сброс давления через выход](../shared/PRESSURE_VENT_CONTRACT.md),
  [проверки](../shared/PRESSURE_VENT_RESULTS.md).

## PB2026-10-07: прямой разрыв

При превышении местной нагрузки — сыпучие клетки того же материала, без деформации.
[Контракт](../shared/PRESSURE_BURST_CONTRACT.md), [проверки и ограничения](../shared/PRESSURE_BURST_RESULTS.md).


## PM2026-10-07 — история, заменено PB

Игровая прочность24, пластичность .025: повреждение и местное смещение перед отрывом.
[Контракт](../shared/PRESSURE_MATERIALS_CONTRACT.md), [новые проверки](../shared/PRESSURE_MATERIALS_RESULTS.md).

- [Паспорт](PASSPORT.md): источники, поведение, критерии, статусы и ограничения.
- [Настройки игры](../../../Materials/core/steel.json): JSON, загружаемый движком.
- [Общая регистрация материалов](../../../Materials/MaterialRegistry.cs).
- [Правило работы](../WORKFLOW.md), [шаблон](../TEMPLATE.md), [каталог](../README.md).

Общие контракты и результаты перечислены в паспорте; они не копируются
в эту папку. Новый специализированный документ добавляйте сюда и в список
выше. У скрытой фазы свой паспорт, а общая проверка семейства может
храниться у исходного материала. Настройки JSON не являются справочником СИ.


## PF2026-10-07: поджиг и разрушение оболочки

`physics.pressureStrength=24`: сильный перепад реакционной волны может отрывать клетки и двигать их наружу. Материал, масса, температура и плавильная энтальпия сохраняются. Значение игровое, не Па/МПа.

[Контракт](../shared/PRESSURE_FRACTURE_CONTRACT.md), [свежие проверки и ограничения](../shared/PRESSURE_FRACTURE_RESULTS.md). Реализован выбранный этап; крупные жёсткие осколки и полный механический баланс остаются будущими механизмами.

- [FJ: выброс и перенос осколков](../shared/FRAGMENT_JET_CONTRACT.md), [свежие проверки и ограничения](../shared/FRAGMENT_JET_RESULTS.md).

- [FS: тепло сохранённой печи](../shared/FURNACE_SENSOR_HEAT_CONTRACT.md), [новые результаты и ограничения](../shared/FURNACE_SENSOR_HEAT_RESULTS.md).
