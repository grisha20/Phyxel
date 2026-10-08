# TN: контракт до реализации

Исторический контракт добавления. Текущий фронт изменён этапом
[RF](RADIAL_FRONT_CONTRACT.md); расход/тепло/давление ниже сохранены.

2026-10-08, baseline f61e4ed. Data-driven TNT: fixed/self-oxidizing/progressive-ignition, density1.6, k.08, c1, auto300/contact100, burn15/spread100/Q2600/W12/max2200, empty. Эмиссии smoke.3/CO₂.2/fire60, flameLifetimeMultiplier.5. Масса кисти1 не заменяется density.

Поддержать положительный ReactionPressurePerMass у fixed solid в трёх признаках активности: registry, brush wake-up и combustion summary. Оставить историческое имя PressurePowderPresent/RegistryHasPressurePowders совместимым; отдельный PowderFront остаётся только granular. Не трогать силу/скорость/геометрию wave и параметры имеющихся топлив. Иначе фиксированный TNT может записать pending, но не разбудить перенос давления.

Почти бездымный фитиль: burn2/spread16 (4×), heat2400/max650 прежние; smokeRate.001, CO₂.002, flameRate.5. Собственные огоньки — скрытый core:fuse_spark, газ flame с density.05, life2..2.8×родительский multiplier.15, decayEmpty. Ни обычный Fire→Smoke, ни уголь не меняются. Сохранения по ID сохраняют прежние сцены.

Фронт нового TNT — быстрая игровая последовательность, не научная модель детонации. Разрушение включает пользователь; оно не включается автоматически при выборе TNT.

Калибровка до приёмки: loader ограничивает spreadRate≤100. Проба200 отклонена при загрузке; выбран100, предел schema не расширяется. Критерии TN не меняются.

Окончательная визуальная калибровка фитиля: smokeRate0/gasRate0 вместо
пробных.001/.002, так как даже лёгкие пакеты рисовали серую полосу.
Короткая искра/поджиг сохранены; новые критерии не ослаблены.
