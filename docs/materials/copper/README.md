# Медь

- [QH: мокрая горячая поверхность, капля и конечная энергия](../shared/WATER_QUENCH_CONTRACT.md), [новые проверки](../shared/WATER_QUENCH_RESULTS.md).

- [BD: общий перенос осколков и новые проверки](../shared/BLAST_DEBRIS_RESULTS.md).

- [PC: замкнутый напор и отдельное включение разрушения](../shared/PRESSURE_CONFINEMENT_CONTRACT.md), [новые проверки](../shared/PRESSURE_CONFINEMENT_RESULTS.md).

- [PV — сброс давления через выход](../shared/PRESSURE_VENT_CONTRACT.md),
  [проверки](../shared/PRESSURE_VENT_RESULTS.md).

## PB2026-10-07: прямой разрыв

При превышении местной нагрузки — сыпучие клетки того же материала, без деформации.
[Контракт](../shared/PRESSURE_BURST_CONTRACT.md), [проверки и ограничения](../shared/PRESSURE_BURST_RESULTS.md).


## PM2026-10-07 — история, заменено PB

Игровая прочность10, пластичность .045: повреждение и местное смещение перед отрывом.
[Контракт](../shared/PRESSURE_MATERIALS_CONTRACT.md), [новые проверки](../shared/PRESSURE_MATERIALS_RESULTS.md).

- [Паспорт и критерии](PASSPORT.md).
- [Настройка материала](../../../Materials/core/copper.json).
- [Собственная жидкая фаза](../molten_copper/PASSPORT.md).
- [Проверки](RESULTS.md) и [общая карта взаимодействий](../shared/INTERACTIONS.md).
- [Реестр материалов](../../../Materials/MaterialRegistry.cs),
  [фазовая энергия](../../../Materials/PhaseEnthalpy.cs),
  [карточка](../../../UI/MaterialCardPreviewCache.cs),
  [проверка металлов](../../../Diagnostics/AlloyRegressionVerifier.cs).

## Результаты

CU01–CU06 PASS в выбранном объёме; полный паспорт и будущие механизмы не приняты.


## PF2026-10-07: поджиг и разрушение оболочки

`physics.pressureStrength=10`: сильный перепад реакционной волны может отрывать клетки и двигать их наружу. Материал, масса, температура и плавильная энтальпия сохраняются. Значение игровое, не Па/МПа.

[Контракт](../shared/PRESSURE_FRACTURE_CONTRACT.md), [свежие проверки и ограничения](../shared/PRESSURE_FRACTURE_RESULTS.md). Реализован выбранный этап; крупные жёсткие осколки и полный механический баланс остаются будущими механизмами.

- [FJ: выброс и перенос осколков](../shared/FRAGMENT_JET_CONTRACT.md), [свежие проверки и ограничения](../shared/FRAGMENT_JET_RESULTS.md).

- [FS: тепло сохранённой печи](../shared/FURNACE_SENSOR_HEAT_CONTRACT.md), [новые результаты и ограничения](../shared/FURNACE_SENSOR_HEAT_RESULTS.md).
