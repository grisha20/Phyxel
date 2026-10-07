# Источники TN2026-10-08

Реальный обзор: [CDC/NIOSH TNT](https://www.cdc.gov/niosh/npg/npgd0641.html): бледно-жёлтое твёрдое вещество; в реальности плавится уже около80°C. Это не модель C-4 и не автоматически мгновенно взрывающийся от любого огня материал. Рецептура/инициаторы/конструкции и параметры реальной детонации не исследуются и не моделируются. Числа игрового импульса, скорости фронта и массы не являются реальными.

Точный TNT-элемент в локальном TPT не найден. Ближайший игровой аналог PLEX (C-4), это другое вещество. PLEX неподвижный TYPE_SOLID, Flammable1000/Explosive2, переход FIRE673K. Общий FIRE.cpp поджигает соседний flammable и прибавляет конечное pv при Explosive. Simulation.cpp исполняет update/тепловые переходы и общую ветку Explosive под высоким давлением; Air.cpp решает игровой pv/скорость, не детонацию в СИ. BOMB взрывается от контакта и удаляет соседние частицы — этот способ сознательно не выбирается. FUSE имеет life-таймер и PLSM; собственного дыма из паспорта реального фитиля не выводим.

Исходники `F:/GitHub/source code the powder toy/`:
- `simulation/elements/PLEX.cpp` SHA256 `e7c3cc5907e6867cef4272edfa526c662f3fb1ecc825cf1bbf14fddd656c15b3`
- `simulation/elements/BOMB.cpp` SHA256 `b3d46fcf3ae1ae2f0869cb40311205aa42db843a8101145213deb690ace899e8`
- `simulation/elements/FIRE.cpp` SHA256 `6c50aac477913cefb7ae53138491210dac93e9ce1fbe1c3a63734e1112f8ad51`
- `simulation/elements/FUSE.cpp` SHA256 `28debad853f2ec9d093e4d88e172d8f998f0fcebe3c9c64439e842a6755f9d42`
- `simulation/Simulation.cpp` SHA256 `5088e75a66263cb1c195eb41a323244916f64ff9d8914dd2c2ce660338d36359`
- `simulation/Air.cpp` SHA256 `2ca962692cb266601cb2a862010a0521a039221c3ecfe75795c89a5356a64d1f`

Phyxel baseline f61e4ed: фитиль4клетки/с/.5массы/с; burnout→Fire→Smoke даёт основную дымку, хотя прямой smokeRate=.03. Новые ID TNT/spark отсутствуют. Общие механизмы Combustion, ReactionPending/Pulse, воздушный поток и экспериментальное разрушение уже есть; твёрдый источник пока не поддержан в признаке активности давления.
