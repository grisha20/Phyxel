# Зафиксированные источники паспортов

ME2026-10-04: прочитаны WOOD/COAL/BCOL/METL/WATR/OIL.cpp,
Simulation.cpp2449–2461 (aheat/hv/heat capacity) и Air.cpp79–252
(ambient boundaries, diffusion/advection). Поровых oil-цветов/капиллярного
wood-пути Phyxel в TPT нет. [Хеши](../../artifacts/material-environment-20261004/source-hashes.json),
[контракт](MATERIAL_ENVIRONMENT_CONTRACT.md), [результаты](../MATERIAL_ENVIRONMENT_RESULTS.md).

WO2026-10-04: повторно прочитаны WOOD/OIL/GUNP/FIRE.cpp и общие механизмы
Phyxel. TPT FIRE±2/flammability/давление, WOOD fixed/тепловая история цвета;
поровой масляной/водной модели в этих элементах нет. Числа WO — игровое
решение; [хеши](../../artifacts/wood-oil-20261004/validation.json),
[контракт](WOOD_OIL_CONTRACT.md), [результаты](../WOOD_OIL_RESULTS.md).

2026-10-04, WD: WOOD/FIRE/BCOL.cpp и общий Simulation.cpp (тепловой
переход2523–2530) прочитаны. WOOD873K→FIRE; updateT>773K/P≤−10→BCOL;
Flammable20/вероятность/life не принимаются за секунды/массу Phyxel.
Поровой воды в прочитанных обработчиках дерева нет. Moisture.30/.05/.08
и HeatPerMass3200 — игровая калибровка, новых реальных данных СИ нет.
[Контракт](WOOD_CYCLE_CONTRACT.md), [измерения](../WOOD_CYCLE_RESULTS.md),
[зафиксированные хеши](../../artifacts/wood-20261004/source-hashes.json).

2026-10-04, BB: прочитаны локальные ICEI/OIL/METL/ROCK.cpp и общий
Simulation.cpp (do_move/Falldown). Там нет вращения наших связанных тел.
Массовые моменты, наклон15Гц/5.625° и плотностное распределение вытеснения
— игровая модель Phyxel, без новых справочных чисел СИ.
[Контракт](BODY_BALANCE_CONTRACT.md), [результаты](../BODY_BALANCE_RESULTS.md),
[SHA256 источников и итоговой рабочей копии](../../artifacts/body-motion-20261004/validation.json).

2026-10-04, OP01–OP05: TPT OIL/GAS/FIRE и общая фазовая ветка Simulation.cpp.
GAS pressure>6→OIL,573K→FIRE; OIL333K→GAS. Прочитаны именно игровые
механизмы, они не принимаются за свойства реального масла.
Thermo Fisher A10322 SDS/NIST для n-гексадекана по ссылкам
[контракта](OIL_PHASE_CONTRACT.md); состав C16H34 выбран явно. Температуры
18/287/135/205°C основаны на выбранном SDS; гистерезис и L230/270 игровые.
[Результаты](../OIL_PHASE_RESULTS.md), [SHA256](../../artifacts/oil-phases-20261004/validation.json).

2026-10-04, LT01–LT04: перечитаны OIL/WATR и Simulation.cpp,
конвекция TYPE_LIQUID (2432–2445) и общий Falldown/горизонтальный поиск
(3183–3233 и далее). В OIL нет температурной вязкости; температурная
мобильность и горизонтальные обмены Phyxel — выбранная игровая модель,
без Pa·s/СИ-калибровки. [Результаты](../LIQUID_TEMPERATURE_RESULTS.md),
[SHA256/валидация](../../artifacts/liquid-temperature-20261004/validation.json).

2026-10-03, падающая вода/масло: перечитаны OIL/WATR/COAL/BCOL/FIRE,
SimulationData::init_can_move и общий Falldown Simulation.cpp.
Новое правило поддержки жидкостью — выбранная модель Phyxel, не
TPT Weight и не справочные силы/вязкость. [Результаты](../LIQUID_FEED_RESULTS.md),
снимок исходников: `artifacts/liquid-feed-20261003/source-hashes.json`.

Дата: 2026-10-03. Phyxel HEAD `0583f2a` + незакоммиченная рабочая копия.
Хеши ниже относятся к прочитанному состоянию файлов, а не к чистому коммиту.
Для TPT фиксируется локальный исходник; соответствие версии запущенного exe не установлено.

## Единицы и общие обработчики

- Температура Phyxel — °C; температура исходников TPT — K. Порог ST нужно читать вместе с общим обработчиком.
- Density, Mass, k/c, reaction Q/P, скорость и запас воздуха Phyxel нормированы. СИ-калибровка не установлена; числовое сходство с справочником её не доказывает.
- `HeatConduct`, `Weight`, `Gravity`, `Advection` TPT — игровые параметры. Тик и `life` не переводятся автоматически в секунды.
- Кисть создаёт solid с Mass=Density; granular/liquid/gas с Mass1. Переход фазы сохраняет Mass, а выгорание имеет отдельный остаток.
- Вода/пар учитывают энтальпию в Lifetime. Устройства используют Pressure/Lifetime для target/power; coal имеет ignition latch. Значение слота зависит от материала.
- Тепловые/фазовые/контактные проходы20Гц; газ/горение/воздух60Гц. Обычное granular/liquid движение остаётся привязанным к проходам кадра; special60Гц для reaction-pressure порошка не распространяется на все вещества.
- Writer scene/world сейчас v14 (`SimulationStateSerializer.CurrentVersion`); старые номера в отчётах описывают момент проверки, не текущий writer.
- Песочница не расходует/не транспортирует кислородный бюджет; Симуляция делает оба. Давление и фоновое тепло имеют отдельные поля; обычная empty не вакуум.
- `Simulation.cpp` TPT содержит движение, вероятностный теплообмен, конвекцию жидкости, фазовые исключения/ctype и поправку порога водой/паром на давление. `Air.cpp` содержит отдельное обновление воздуха/ambient heat. Их настройки не равны режимам Phyxel.
- В этом этапе не назначались новые реальные физические числа. Неизвестный состав металла/камня/дерева и отсутствие смеси отмечены в паспортах.

## Обновление теплового контракта — 2026-10-03

Отдельное исправление заливки горячего угля: заново прочитаны COAL/BCOL/WATR
и SimulationData::init_can_move. [Снимок SHA256](../../artifacts/moisture-pour-20261003/source-hashes.json),
[новый результат](../MOISTURE_POUR_RESULTS.md). Числа весаTPT не перенесены в СИ.

Лёд↔вода реализован; прежние хеши ниже — снимок до этой правки. [Новый снимок кода](../../artifacts/ice-fusion-20261003/source-hashes.json), [измерения/NIST/TPT](../ICE_FUSION_RESULTS.md). Новое нормированное L333.4 не объявляется СИ-массой клетки.

Металл↔расплав реализован следующим этапом: [снимок](../../artifacts/metal-fusion-20261003/source-hashes.json), [результаты/TPT/выбор L370](../METAL_FUSION_RESULTS.md). Перечитаны METL/LAVA и фазовая ветка Simulation.cpp; локальные исходники TPT неизменны. L370 выбрано из прежних игровых c/порогов для сохранения нуля энергии, не из справочника сплавов. Общий энтальпийный механизм после льда не менялся.

## Phyxel: исходные файлы

WC01a — следующий снимок [выбора жидкости](../../artifacts/wetting-contact-20261003/source-hashes.json), [измерения](../WETTING_CONTACT_RESULTS.md). Перечитаны COAL/BCOL/WATR, FIRE и общий теплообмен Simulation.cpp; TPT неизменён. Скорость.35/с прежняя, реальных новых чисел нет. Mass влаги и сушка остаются неподдерживаемыми механизмами этого первого подэтапа.

| Файл | SHA256 |
|---|---|
| [co2.json](../../Materials/core/co2.json) | `b3f31d40af1629f10a51d250c221fadfd2e0c68bf6c8248e2b79cb612053cc29` |
| [coal.json](../../Materials/core/coal.json) | `90b202ade590d1f2750dfdfcd0490f60a9a6b438605e7b22e534aec14e271ddc` |
| [cooler.json](../../Materials/core/cooler.json) | `ece15a327d8af331c35350a9eeee6bdc628121a7763c2481aafb5727af6f5673` |
| [empty.json](../../Materials/core/empty.json) | `f06446e846fe998d052a1f63d66d2edd0451aa941046d6ea5c8f4f4b24733a64` |
| [eraser.json](../../Materials/core/eraser.json) | `c640e4098b7ec5156be97598e9e2a9e7d349aabec4235e72ca2c2b921303fece` |
| [fire.json](../../Materials/core/fire.json) | `8f6a572ca6f48e6694bba67f555c10c1a84923109b236ab78556d9fdafe9c15c` |
| [fixture.json](../../Materials/core/fixture.json) | `ba08f0c63d498c59f0151e9bff68ae79bc5456a0aa6938ef1effba90f3545a31` |
| [gunpowder.json](../../Materials/core/gunpowder.json) | `d47fd65b9d04f4ee37ea3209614ea9dceca090a098d07b0415c797d02a630d92` |
| [heater.json](../../Materials/core/heater.json) | `098041739c4eab1055d29efe7e6035a01e4ae653d5b1c308dc17932439585f27` |
| [ice.json](../../Materials/core/ice.json) | `5900bef7dd7ba91119f1296f37c5e00ecf725e45ff0c979cf26979d3c036591e` |
| [metal.json](../../Materials/core/metal.json) | `74f2bf5e444fc07977268b776cc9550657b46e9eb306e117eca4832fff669506` |
| [molten_metal.json](../../Materials/core/molten_metal.json) | `1cb6511d2b0b4ab658b3ff6aeb1ea25bb19f77b2fb7262680f358c6fc09107f7` |
| [sand.json](../../Materials/core/sand.json) | `986f334708a78ea597d76d26336f556c503528b5e4c223d129495e8380bb964e` |
| [smoke.json](../../Materials/core/smoke.json) | `6d03b401ea03ed9bac7e773b6c078b24768b8207f0a572464df55124dbb3e378` |
| [steam.json](../../Materials/core/steam.json) | `a2cf52058f60bce1da82740ac3aa963b175b6dcee2365f9ef7936b15db47ada6` |
| [stone.json](../../Materials/core/stone.json) | `6f999c775990d7dbd0acbe53d2fc4b491cf4fa2da978f3dc829b5f93679a9201` |
| [stone_coal.json](../../Materials/core/stone_coal.json) | `92f857d912dd0fc8baf79953218c9b6ce00220cea1e984004f4c8085b5f584b5` |
| [water.json](../../Materials/core/water.json) | `c64a437c8f02935dfb407a10ceb29d0fa741448279633e2f05035c60772c67b7` |
| [wet_charcoal.json](../../Materials/core/wet_charcoal.json) | `2b42cd776ed32b09dccf1e21a7e2a4e0a370030b3a171d7e3b8fe7acdcdc2596` |
| [wood.json](../../Materials/core/wood.json) | `d7e32b9f8fad86fe5b4ec87783b56e648e3c9c6f2d84e792fad4fd2f31f2b08b` |
| [MaterialRegistry.cs](../../Materials/MaterialRegistry.cs) | `399e70f466753b79ed0a4b1f2ef17c209afd7b35a5d8c064c2dd07633beed54f` |
| [MaterialDefinition.cs](../../Materials/MaterialDefinition.cs) | `2ded635182e65214845deec2ea03de2448a91e1b4613d19547ad1dd9b37b4fb9` |
| [BrushApplication.hlsl](../../Content/Shaders/BrushApplication.hlsl) | `0a1a40db2f5ce6308db11a78fd9d0fa7f4ffff7980393fcec265de3805e2c93e` |
| [CellularAutomataSolver.hlsl](../../Content/Shaders/CellularAutomataSolver.hlsl) | `9ce6929a59d1331f5a1e035a6afff81e82fde346a7c8db45df1db50b8b096842` |
| [PhysicsShared.hlsli](../../Content/Shaders/PhysicsShared.hlsli) | `7824c9ffdb75c22aea26bac005118487bc69bbbc52e446d892f0ed70955f485c` |
| [ThermalDiffusion.hlsl](../../Content/Shaders/ThermalDiffusion.hlsl) | `2577bba3d2fbcdcb9ea68bc7bc99947284a2302446a9d7de01ecce2b81b36833` |
| [PhaseTransitions.hlsl](../../Content/Shaders/PhaseTransitions.hlsl) | `7afc4d23c07f640644dda5214e1deaf40f3133021b10b31bf2fcb0588e27f09b` |
| [PhaseEnthalpy.hlsli](../../Content/Shaders/PhaseEnthalpy.hlsli) | `3300a7351cc576ce6e49166c938115a50d9b591ca813ccfcd9b84d97327507f7` |
| [ContactTransitions.hlsl](../../Content/Shaders/ContactTransitions.hlsl) | `190ed894bb3071ebc26d35b321bd20a461c92052960810a3a30b35cda2eba29b` |
| [Combustion.hlsl](../../Content/Shaders/Combustion.hlsl) | `a963abf8fc310176a37f208392136ba7eedfbc7fb22c8c5a66a23f5b089bee9b` |
| [AirSimulation.hlsl](../../Content/Shaders/AirSimulation.hlsl) | `ab2736d72189f0c0fac5a87996fa3d8fdf724502a1fac73fe258ec8af4a7931c` |
| [AirThermal.hlsl](../../Content/Shaders/AirThermal.hlsl) | `4662a07cce72fef987b4df56f85f94a9f278d9f377b2235c58e8a89886ecddcd` |
| [FineAirGeometry.hlsli](../../Content/Shaders/FineAirGeometry.hlsli) | `7d0ee0e3cde04ae91d00c0982279893c9d87ce67bc32e7186bfca04fabfc8e97` |
| [OxidizerTransport.hlsl](../../Content/Shaders/OxidizerTransport.hlsl) | `7d32fe4c62200a4ec7e2cb63805a279c78f955787622b68e9c5065d38d8af67f` |
| [SimulationDispatchCoordinator.cs](../../Graphics/SimulationDispatchCoordinator.cs) | `00a972fe3a0b5bc8b9e3b224fcc6f052e0e8628c551fd240fc79ccb20fb375af` |
| [SimulationStateSerializer.cs](../../Serialization/SimulationStateSerializer.cs) | `5da6c02a0ac3a3acbb8e99931deb4087527d6bc4d68f86a5913c57348df1ac97` |
| [WorldCellCodec.cs](../../Serialization/WorldCellCodec.cs) | `b1b3d0dc8c1d846aaafefa6aeefd857f7c8f9a580cdd0ae4912bb84ad62d7ee0` |

## TPT: локальный исходник

Корень: `F:/GitHub/source code the powder toy`.

| Файл | SHA256 |
|---|---|
| [simulation/elements/BCOL.cpp](<F:/GitHub/source code the powder toy/simulation/elements/BCOL.cpp>) | `95181855f47ee77c28e8b4461177dced13b6b505d1d26d94171c206a2a6ac754` |
| [simulation/elements/CO2.cpp](<F:/GitHub/source code the powder toy/simulation/elements/CO2.cpp>) | `ca0ad891ee7f87de3aa9502ebd1253f53475eb3c83f68ca11562f2053cd9271a` |
| [simulation/elements/COAL.cpp](<F:/GitHub/source code the powder toy/simulation/elements/COAL.cpp>) | `2261abce0a4bba035cee73c5672afc04e58494c6af76bde7b418b253b9342027` |
| [simulation/elements/DMND.cpp](<F:/GitHub/source code the powder toy/simulation/elements/DMND.cpp>) | `9b74e426728efbd144f8535f2b0f71d357f84ca18116979a41d23e1a0578a020` |
| [simulation/elements/FIRE.cpp](<F:/GitHub/source code the powder toy/simulation/elements/FIRE.cpp>) | `6c50aac477913cefb7ae53138491210dac93e9ce1fbe1c3a63734e1112f8ad51` |
| [simulation/elements/GUNP.cpp](<F:/GitHub/source code the powder toy/simulation/elements/GUNP.cpp>) | `ede937f04d60ea7f7c961ae0fdb1f0f287e7cc32f54b8f327bdbe32b9a56e684` |
| [simulation/elements/ICEI.cpp](<F:/GitHub/source code the powder toy/simulation/elements/ICEI.cpp>) | `12b799228471f0caf46d1954006a00716d5462987c03b422bc09b5a413887b8a` |
| [simulation/elements/LAVA.cpp](<F:/GitHub/source code the powder toy/simulation/elements/LAVA.cpp>) | `1f2d9032ff0c7444a385ab5ef8a6ac9738a64c15a2fcb2e6fa45f22ce7c0218e` |
| [simulation/elements/METL.cpp](<F:/GitHub/source code the powder toy/simulation/elements/METL.cpp>) | `4f4b1ed7da9ce04e19228aee9dfe3e501d6b93d15a3058c199ff11b91bf5444e` |
| [simulation/elements/NONE.cpp](<F:/GitHub/source code the powder toy/simulation/elements/NONE.cpp>) | `7a96e5b293c14fb4e2af2d7af301b1cf5cdb08e684434e96855547b954bc2220` |
| [simulation/elements/ROCK.cpp](<F:/GitHub/source code the powder toy/simulation/elements/ROCK.cpp>) | `ef819c37e878353c755846969f3317bfd691ed87da3dde48281cdd04868bf4aa` |
| [simulation/elements/SAND.cpp](<F:/GitHub/source code the powder toy/simulation/elements/SAND.cpp>) | `e4c61e7c9568c63ff6cc953e8f1d98662b1d04af7b56db417910ae188860bb62` |
| [simulation/elements/SMKE.cpp](<F:/GitHub/source code the powder toy/simulation/elements/SMKE.cpp>) | `83c79712afbf1f2fc4e2cfd099671a61c4b21d3df4e441654f6efa09fd935d86` |
| [simulation/elements/STNE.cpp](<F:/GitHub/source code the powder toy/simulation/elements/STNE.cpp>) | `fc9d9a37c17e308ea88714d7bc8e963097bdbab197bc528768274f156763679d` |
| [simulation/elements/WATR.cpp](<F:/GitHub/source code the powder toy/simulation/elements/WATR.cpp>) | `9d34127b059230756cf31e584071f72073b57f6beaf1569eb976101056794630` |
| [simulation/elements/WOOD.cpp](<F:/GitHub/source code the powder toy/simulation/elements/WOOD.cpp>) | `8264e90a5ac33c4cfdc30dac58743725a7eb9c7597117fec1ca00fd3746dba83` |
| [simulation/elements/WTRV.cpp](<F:/GitHub/source code the powder toy/simulation/elements/WTRV.cpp>) | `2cab4591f40a7ec97006e7ebe71d2f0f90e21954c1a14f3cd36b0e30e03936a7` |
| [simulation/simtools/HEAT.cpp](<F:/GitHub/source code the powder toy/simulation/simtools/HEAT.cpp>) | `ee566017426950b171219c7ba211d89ebdd9fa5cf264b56c9d393bf2461ab870` |
| [simulation/simtools/COOL.cpp](<F:/GitHub/source code the powder toy/simulation/simtools/COOL.cpp>) | `7bdd9d69079a4fd9a725521fc5ae6cb59e74d688f29b9b7b76e5e029426df96d` |
| [simulation/Simulation.cpp](<F:/GitHub/source code the powder toy/simulation/Simulation.cpp>) | `5088e75a66263cb1c195eb41a323244916f64ff9d8914dd2c2ce660338d36359` |
| [simulation/Air.cpp](<F:/GitHub/source code the powder toy/simulation/Air.cpp>) | `2ca962692cb266601cb2a862010a0521a039221c3ecfe75795c89a5356a64d1f` |
| [simulation/SimulationData.cpp](<F:/GitHub/source code the powder toy/simulation/SimulationData.cpp>) | `74b90e93d0d32bbe6f308c9ea1dabb4ed2a61a3ebf9dfb5091d4d2bfe9e94eed` |

## Обновление WC02/G08

Grid48/material208, явные поля воды/скрытого тепла. Источники TPT повторно изучены, прежние хеши TPT проверяются в `artifacts/fuel-moisture-20261003/validation.json`; актуальные JSON/шейдеры зафиксированы там же. [Результаты](../FUEL_MOISTURE_RESULTS.md).

## Промокание и плавучесть — 2026-10-03

Повторно изучены COAL/BCOL/WATR/GUNP/SAND/STNE и общая таблица
SimulationData::init_can_move. Модель сохранённой воды/межзернового
промокания отсутствует в этих обработчиках; новое поведение Phyxel не
выдаётся за перенесённую физику TPT.22 прежних хеша TPT повторно сверены.
Хеши двух изменённых шейдеров, verifier и контракта:
`artifacts/moisture-wicking-20261003/source-hashes.json`.
[Выбранная проверка и ограничения](../MOISTURE_WICKING_RESULTS.md).

## Новый oil,2026-10-03

| Файл | SHA256 |
|---|---|
| [OIL.cpp](<F:/GitHub/source code the powder toy/simulation/elements/OIL.cpp>) | `6be34a5eadce7b9c6f6261df91d1a7c82c76b1d60a6590e95b7c8627f5ba064e` |

Читать вместе с уже зафиксированными FIRE/Simulation/SimulationData.
[NOAA CAMEO](https://cameochemicals.noaa.gov/chemical/12191) — только качественный ориентир; игровые значения oil не СИ.

## OA —2026-10-04

Локальные OIL/COAL/BCOL/FIRE и SimulationData::init_can_move повторно
прочитаны. В этих элементах TPT нет отдельного масляного запаса угля;
OIL→GAS333K не переносится в Phyxel без отдельной паровой модели.
Ёмкость.25/скорость.025/плотность.9 — игровые решения, не СИ/справочник.
Состояние источников и C#/HLSL/JSON: `artifacts/oil-absorption-20261003/validation.json`.
[Результаты](../OIL_ABSORPTION_RESULTS.md). Прежние хеши TPT неизменны.

## CW —2026-10-04, carrier и столкновения

Повторно прочитаны FIRE/SMKE/CO2/WTRV, Simulation.cpp и Air.cpp TPT.
FIRE/SMKE Diffusion0; kernel/advection воздуха и графика FIRE_BLEND
не эквивалентны одному увеличению Diffusion. Collision fallback TPT
сбрасывает обе компоненты по Collision; сохранение касательной скорости
в Phyxel — собственное игровое решение. Поперечная дисперсия1.5/cap1.5
не СИ и не откалиброванная турбулентная энергия.
23 прежних SHA256 TPT проверены; source/bin и исходная пользовательская
сцена: [validation.json](../../artifacts/chimney-width-20261004/validation.json).
[Результаты/границы](../CHIMNEY_WIDTH_RESULTS.md); соответствие версии exe
TPT локальным исходникам и превосходство над TPT не доказаны.

## FW —2026-10-04

FIRE/SMKE Diffusion0 и nearest coarse carrier из Simulation.cpp
повторно прочитаны вместе с Air.cpp/collision. Ограничение дополнительной
Phyxel CW-дисперсии поперечной твёрдой границей — игровая closure,
не число TPT/СИ/CFD.23 прежних SHA256 TPT сохранены;50 shader/JSON
source/bin совпадают. [Результаты](../FREE_FLAME_RESULTS.md),
[текущие хеши](../../artifacts/free-flame-20261004/validation.json).

## FB —2026-10-04

Повторно прочитаны ICEI.cpp/OIL.cpp и движение общего Simulation.cpp:3099.
ICEI имеет TYPE_SOLID, Falldown0, Advection/Gravity/Diffusion0; OIL не
содержит замёрзшей фазы. Плавучие компактные тела Phyxel не взяты из TPT;
нормированные плотности .92/.85/1/.8 сохранены, новых физических чисел СИ
не назначалось. [Хеши до изменений](../../artifacts/frozen-bodies-20261004/tpt-hashes.json),
[проверка неизменности](../../artifacts/frozen-bodies-20261004/validation.json),
[контракт](FROZEN_BODY_CONTRACT.md), [результаты](../FROZEN_BODY_RESULTS.md).
