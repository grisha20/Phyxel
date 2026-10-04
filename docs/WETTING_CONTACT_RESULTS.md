# Уголь: выбор смачивающей жидкости

2026-10-03. HEAD `0583f2a` + рабочая копия; NVIDIA RTX4070.
Область: первый подэтап влажности WC01a — убрать coal→wet_charcoal от
расплава металла. Хранение массы влаги и сушка ещё не реализованы.

## Изменение и источники

В coal.json добавлено `contactTransitions.liquid.with: core:water`.
Общий parser разрешает необязательный ID, реестр проверяет существование,
класс liquid и независимость core от внешнего пакета. Шейдер сравнивает
разрешённый runtime index, без проверки core ID. Без with сохраняется
ANY-liquid семантика прежних внешних материалов. Вероятность.35/с и
четыре грани/контактные шаги20Гц прежние; диагональ не смачивает.
Температура, масса, теплообмен и настройки горения не перенастраивались.

Селектор использует незадействованный у granular contact-источника слот172
таблицы (`ThermalDeviceMaximumPower`): float index+1,0=wildcard. При текущем
лимите256 материалов индекс представим точно. Regulator разрешён только
для fixed solid и несовместим с contact, поэтому смыслы не пересекаются.
Материал176/Grid40/worldv11 прежние; новых полей состояния клетки нет.

Прочитаны локальные COAL.cpp, BCOL.cpp, WATR.cpp, общие ветви FIRE.cpp
и теплообмена Simulation.cpp. BCOL использует COAL_update, горение
через life110→99 и FIRE-соседство. WATR удаляет соседний FIRE; FIRE
обрабатывает WATR/DSTW/SLTW отдельно. Соответствующего wet_charcoal и
баланса намокание→сушка в просмотренных ветвях нет. Реакции LAVA/угля
в FIRE связаны с ctype/отдельными продуктами TPT, не с мокрым углём.
Новые химические реакции и игровые числа TPT в Phyxel не перенесены.
[Источники/хеши](materials/SOURCES.md); новых реальных справочных чисел нет.

## Результат

Критерии до правки находятся в [паспорте WC01a](materials/wet_charcoal.md).
GPU480×270; изолированный контакт: без теплообмена/движения,1000 шагов
по.05с с одинаковым seed. Это контролируемая проверка идентичности жидкости,
не модель50с кипящего расплава. Допуск Mass/тепла≤1e−5; изменение соседней
клетки и запрещённого контакта проверяется побитово. Cadence-проверка
использует настоящий DispatchFrame и отдельные двухклеточные карманы,
T20°C, закрытые края: движение не меняет длительность контакта.

| Сценарий | Результат |
|---|---|
| Вода с каждой из четырёх граней |64/64 клеток угля стали мокрыми; Mass/T прежние, latch сброшен |
| Расплав, пар, лёд, металл и пустота |0/64 мокрых; исходное состояние угля, включая latch, неизменно |
| Вода только по диагонали |0/64; неизменные оба участника |
| Вода с одной грани, расплав с другой |64/64: допустимая вода не теряется из-за соседства расплава |
| Четыре угла сетки |Контакт обрабатывается, выходов за границы нет |
| Generic selector/wildcard |Диагностическая таблица требует molten и принимает его вместо water; wildcard принимает оба. Проверяется общий механизм, рабочий coal требует water |
| Соседние материалы и балансы |Соседи неизменны побитово; ошибки Mass/тепла0 |
| Load schema/reference validation |9 неверных external selectors отвергнуты;3 core cases с solid/missing/external with останавливают загрузку. Custom liquid и нормализация ID разрешены; legacy ANY-liquid сохранён |
| Пауза/save/load/продолжение |Частично намокший набор сохранён побитово; следующие200 контактных шагов при одинаковых TickIndex/dt совпадают с памятью; пауза не меняет grid |
| Два режима × Air on/off ×30/60/100FPS |12/12 PASS за10с:64/64 мокрых, Mass/тепло сохранены.200 тиков на30/60 и199 на100 из-за прежнего округления планировщика |
| Сухое горение углей/кислород/поджиг/latch |[FURNACE_COMBUSTION PASS](../artifacts/wetting-contact-20261003/regressions/FURNACE_COMBUSTION/run.log); штатный burnout Mass1≈16.7с |
| Контакт вода/металл, тепло воздуха |[WATER_CONTACT PASS](../artifacts/wetting-contact-20261003/regressions/WATER_CONTACT/run.log), [AIR_HEAT PASS](../artifacts/wetting-contact-20261003/regressions/AIR_HEAT/run.log) |
| Тепловые фазы |[Металл384 PASS](../artifacts/wetting-contact-20261003/regressions/METAL_FUSION/run.log), [лёд934 PASS](../artifacts/wetting-contact-20261003/regressions/ICE_FUSION/run.log) |
| Тепловые устройства |4/4 PASS:30/60/100FPS и restart; слот мощности устройств сохраняет свой смысл |
| CPU-регрессии |7 групп PASS: codec, thermal/combustion/phase properties, thermal diffusion, phase runtime, gas brush |

Главный набор: [1503 проверки PASS](../artifacts/wetting-contact-20261003/current/run.log).
Сценарии различают отсутствие смачивания и сохранность latch; просто
переименованный мокрый ID не считается исправлением. Горящая пользовательская
печь заново не снималась, её исходное/текущее сохранение не изменялось.
Полной визуальной приёмки всех топлив нет.

## Сравнение до/после и воспроизведение

Прежние JSON и ContactTransitions.hlsl сохранены до правки. Две контрольные
ветки используют ту же1503-проверочную диагностику:

- [Старый JSON coal + новый шейдер](../artifacts/wetting-contact-20261003/baseline-json/run.log): расплав даёт64/64 мокрых, FAIL.
- [Старый шейдер + новый JSON](../artifacts/wetting-contact-20261003/baseline-shader/run.log), изолированная копия runtime: тот же ложный переход64/64, FAIL.

Новая версия:0/64 от расплава при64/64 от воды, PASS. Контрольные ветки
сравнивают конкретный механизм, а не запускают целиком историческую сборку.
После `dotnet build Phyxel.sln -c Debug`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Diagnostics/RunWettingContact.ps1 -ArtifactRoot artifacts/wetting-contact-20261003/current
powershell -NoProfile -ExecutionPolicy Bypass -File Diagnostics/RunWettingContact.ps1 -ArtifactRoot artifacts/wetting-contact-20261003/baseline-json -CoreMaterialsPath artifacts/wetting-contact-20261003/baseline-core -ExpectFailure
powershell -NoProfile -ExecutionPolicy Bypass -File Diagnostics/RunWettingContact.ps1 -ArtifactRoot artifacts/wetting-contact-20261003/baseline-shader -RuntimeDirectory artifacts/wetting-contact-20261003/baseline-runtime -ExpectFailure
```

Копия baseline runtime содержит текущую сборку, в ней заменён только
ContactTransitions.hlsl сохранённым исходным файлом; рабочий runtime не
подменяется. Shader cache привязан к SHA256 всего развёрнутого исходника,
поэтому новая версия include/контакта компилируется отдельно от старой.
Заключительный1503-проверочный прогон выполнен после удаления только кэша
изменённого ContactTransitions: шейдер заново скомпилирован, PASS.
[Покрытие/источники/сохранность](../artifacts/wetting-contact-20261003/validation.json).

## Что остаётся

Wet_charcoal ещё игровой негорючий ID: вода при его образовании не
списывается и не хранится внутри топлива. Следующий подэтап — сохранённый
запас воды, отдельный перенос/сохранение, баланс массы и теплоты испарения,
возврат coal после сушки. После него — влажность/сушка пороха G08 и выбранные
правила остальных топлив. Давление granular уже занято счётчиком
приземления, Lifetime coal — поджигом; влажность не должна их разрушить.
WC01a PASS не означает завершения WC02 или всего этапа2.
