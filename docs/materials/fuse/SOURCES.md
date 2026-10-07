# Источники фитиля, 2026-10-08

Реальная роль: [Dyno Nobel, Always & Nevers, Safety Fuse, стр. 2](https://www.dynonobel.com/siteassets/resource-hub/product-application-guides/always--nevers.pdf): гибкий запальный шнур переносит фронт горения от точки поджига к концу. Данные конкретной марки, теплопроводности, покрытия, влагопоглощения и скорости в СИ не установлены. Рецептура и изготовление не моделируются.

Локальный TPT: `F:/GitHub/source code the powder toy/`.
- `simulation/elements/FUSE.cpp` SHA256 `28debad853f2ec9d093e4d88e172d8f998f0fcebe3c9c64439e842a6755f9d42`
- `simulation/elements/FSEP.cpp` SHA256 `58edcc156203c895fe949002c7c949a3a3df8880b70af9dd2b99f370cdfa4e27`
- `simulation/elements/FIRE.cpp` SHA256 `6c50aac477913cefb7ae53138491210dac93e9ce1fbe1c3a63734e1112f8ad51`
- `simulation/Simulation.cpp` SHA256 `5088e75a66263cb1c195eb41a323244916f64ff9d8914dd2c2ce660338d36359`

FUSE: неподвижный TYPE_SOLID, life50; при температуре≥973.15K либо SPRK случайно запускает life39, затем обратный отсчёт до PLSM. В ходе отсчёта редкие PLSM. Давление>2.7 запускает tmp39, затем превращение в сыпучий FSEP. FSEP имеет другие пороги и более частые PLSM. Это игровые ticks и давление, не СИ. Числа и код TPT не копируются.
FIRE: общий вероятностный поджиг Flammable; у FUSE Flammable0, собственный update. Simulation.cpp: общий обмен теплом и исполнение update элемента в цикле частиц. Эти механизмы не являются расчётом реального шнура.

Phyxel baseline: HEAD2c4df4e, нового ID нет. Combustion.hlsl управляет расходом топлива и эмиссиями, общее движение газа отдельно. Для переноса вдоль шнура нужен собственный сохранённый таймер, не направление скорости пламени.
