# Эталон гейта воды

После добавления конвекции: water gate14/14, критерии не менялись;
холодный бассейн отдельно PASS до/после с уровнями135/136 и без утечек.
Закрытые новые сцены сохраняют массу28039.
[WATER_CONVECTION_RESULTS.md](WATER_CONVECTION_RESULTS.md).

Обновление 2026-10-01: [HYDRAULICS_RESULTS.md](HYDRAULICS_RESULTS.md).
Реальное застревание тонкого слоя на склоне исправлено локальным стеканием.
В `hydro` требование позднего неравенства заменено проверкой постепенного
переноса и баланса массы на нескольких снимках. Критерии преграды прежние.
Полный повторный water gate 14/14; новые фиксированные сцены 8/8.
Числа и известные сбои ниже — исторический эталон до этой правки.

Снимок поведения выверенной физики жидкостей и granular **до** начала работ
по дорожной карте `ROADMAP_TPT.md`.

Смысл файла: любое расхождение с этими цифрами на последующих этапах означает,
что мы задели воду. Даже если сценарий формально `PASS`, изменившаяся
диагностическая строка — повод остановиться.

---

## Как получить

```powershell
dotnet build Phyxel.sln -c Debug --nologo
.\Diagnostics\RunWaterGate.ps1 -ArtifactRoot artifacts/water-gate-baseline
```

Скрипт сам сформирует таблицу в
`artifacts/water-gate-baseline/water-gate-baseline.md` — её содержимое нужно
перенести в раздел ниже.

---

## Результат

**Статус:** ✅ зафиксирован — 13 PASS, 1 FAIL (известный)
**Дата:** 2026-08-01
**GPU:** NVIDIA GeForce RTX 4070 (vendor 0x10DE, device 0x2786)
**Конфигурация:** Debug, scale 0.25 (кроме `water_stress` — native), 60 FPS
**Время полного прогона:** ~4,3 минуты (256 с)

**Базовый коммит:** `bdf28be` (`тест добавления материалов`, 2026-07-25)
⚠️ Эталон снят с **незакоммиченного рабочего дерева**: 120 изменённых файлов
поверх `bdf28be`. См. раздел «Риск незафиксированного дерева» ниже.

| Сценарий | Группа | Статус | Диагностика |
|----------|--------|--------|-------------|
| `sand` | granular | PASS | `PHYXEL_C sand=989 resting=989 moving=0 width=73 leftAngle=39.8 rightAngle=30.8 roughness=0.78 gaps=0 red=0` |
| `slope` | granular | PASS | `PHYXEL_E sand=1094 upper=42 settled=1042 resting=1094 moving=0 bounds=266,64-418,245` |
| `granular_displacement` | granular | PASS | `PHYXEL_GRANULAR_DISPLACEMENT granular=1257/1257 granularMass=1257.0 liquid=49384/49384 liquidMass=49384.0 liquidTop=101 shoulderTop=101 outsideTop=124 rise=23 highTrail=0 upwardFast=0` |
| `buoyancy` | liquid | PASS | `PHYXEL_M_BUOYANCY waterTop=170 drafts=24/35 bottoms=194/205/249 metal=3590/2338 sandLoad=3325 sunkBlock=2961 closedWater=0 openWater=0 deepHull=0 water=32359 movingSolids=0 fps=60.0` |
| `slope_underwater` | granular | PASS | `PHYXEL_UNDERWATER_GRANULAR granular=1257/1257 granularMass=1257.0 liquid=49384/49384 liquidMass=49384.0 bounds=211,207-269,247 width=59 height=41 settled=1257` |
| `granular_barrier_off` | granular | PASS | `PHYXEL_GRANULAR_BARRIER hydraulics=0 granular=10198/10198 granularMass=10198.0 liquid=9268/9268 liquidMass=9268.0 granularTop=145 liquidTop=180 rightLiquid=0 settled=10198 pressureMoves=0 farColumnMoves=0` |
| `granular_barrier_on` | granular | PASS | `PHYXEL_GRANULAR_BARRIER hydraulics=1 granular=10198/10198 granularMass=10198.0 liquid=9268/9268 liquidMass=9268.0 granularTop=145 liquidTop=178 rightLiquid=0 settled=10198 pressureMoves=0 farColumnMoves=0` |
| `bowl` | liquid | PASS | `PHYXEL_A bowlMetal=3930 water=10807 sand=6498 leaked=0 gaps=0 movingWater=0 movingSand=0 waterY=161.7 sandY=204.3 imageColumns=198 imageGaps=0 surfaceRange=1 red=0` |
| `hydro` | liquid | **FAIL** | `PHYXEL_D water=12294 leftTop=204 rightTop=205 image2s=203/205 waterfallTop=234 fallingWater=2202 resting=12294 moving=0 leaks=0 leakMass=0.000 wallGaps=0 fps=60.0 statsMoving=0 red=0` |
| `flat_surface` | liquid | PASS | `PHYXEL_FLAT_SURFACE water=49882 levels=135/136 difference=1 resting=49871 moving=0 streamMin=1191 striped=0 stray=0 leaks=0 fps=60.0` |
| `water_drain` | liquid | PASS | `PHYXEL_WATER_DRAIN water=21323 sand=9660 leftTop=187 rightTop=187 hanging=0 moving=0 unsettled=0` |
| `water_stress` | liquid | PASS | `PHYXEL_STRESS_WATER water=1293044 resting=507857 moving=77046 fps=60.0` |
| `pressure_tube` | hydraulic | PASS | `PHYXEL_I_PRESSURE_TUBE water=15021 final=174/173 image300=173/172 resting=15021 moving=0 pressureMoves=0 farColumnMoves=0 leaks=0` |
| `communicating_vessels` | hydraulic | PASS | `PHYXEL_H_VESSELS water=23079 final=192/191/191 finalRange=1 image2s=233/227/139 imageRange=94 resting=23079 unsettled=0 moving=0 pressureMoves=0 farColumnMoves=0 leaks=0` |

---

## Как сравнивать с эталоном: симуляция НЕ детерминирована

Два последовательных прогона `hydro` на одной и той же сборке дали разные числа:

| Показатель | Прогон 1 | Прогон 2 | Расхождение |
|---|---|---|---|
| `water` | 12330 | 12294 | 36 клеток (0,3 %) |
| `waterfallTop` | 233 | 234 | 1 пиксель |
| `fallingWater` | 2250 | 2202 | 48 (2,1 %) |
| `image2s` | 203/205 | 203/205 | совпало |
| `leftTop`/`rightTop` | 204/205 | 204/205 | совпало |

Причина ожидаема: физика идёт на GPU, порядок выполнения потоков между
диспатчами не фиксирован, а часть решений принимается вероятностно через
`HashUnitFloat`. Побитовой воспроизводимости нет и не будет.

**Практический вывод для всех последующих этапов:**

1. Сравнивать нужно **структурные** величины, а не все подряд.
   Устойчивые между прогонами: уровни (`leftTop`, `rightTop`, `levels`,
   `final`), счётчики нарушений (`leaks`, `gaps`, `stray`, `hanging`,
   `moving`, `unsettled`, `red`), массы (`granularMass`, `liquidMass`).
2. Плавающие: общее число клеток жидкости (±0,5 %), счётчики визуальных
   пикселей (`fallingWater`, `streamMin`, ±4 %), одиночные пиксели
   поверхности (±1).
   **`communicating_vessels.finalRange` плавает в диапазоне 1-3.** Замерено
   тремя прогонами подряд на одной сборке: 3, 1, 1. Уровни при этом
   держатся в пределах `190..193`. Регрессией считать значение выше 3
   или расхождение уровней более чем на 3 пикселя.
3. Любое изменение счётчика нарушений с нуля на ненулевое значение — регрессия,
   независимо от величины.
4. Изменение уровня более чем на 2 пикселя — регрессия.
5. Одиночный прогон не является доказательством. При сомнении прогонять
   подозрительный сценарий трижды через `-Only`.

---

### `granular_barrier_on` — МИГАЮЩИЙ

Замеры `liquidTop`: 178 (эталон), PASS, 145, 151. Вариант **без** гидравлики
(`granular_barrier_off`) при этом стабильно даёт 180 во всех прогонах.

Значит нестабилен именно гидравлический путь, а не granular-физика: код
сыпучих у обоих вариантов один и тот же. Тот же класс, что `hydro`.

**Как относиться:** судить по `granular_barrier_off`, он стабилен. Значение
`liquidTop` в варианте `on` регрессией считать только если уйдёт за пределы
140-185 или если изменятся счётчики (`granular`, `liquidMass`, `settled`,
`rightLiquid`, `pressureMoves`) — они во всех прогонах совпадают точно.

Разобраться стоит перед этапом 6 вместе с `hydro`: оба упираются в один и тот
же выверенный вручную гидравлический solver.

---

## Риск незафиксированного дерева

На момент снятия эталона в рабочем дереве **120 изменённых файлов** поверх
коммита `bdf28be`, включая почти все шейдеры физики
(`CellularAutomataSolver.hlsl`, `Combustion.hlsl`, `PhysicsShared.hlsli`) и весь
`Diagnostics/`.

Это значит, что эталон привязан к состоянию, которого нет в истории git.
Если рабочее дерево будет случайно потеряно или частично откачено, вернуться
к этим числам будет невозможно, а откат любого нашего этапа перестанет быть
дешёвым.

**Действие до начала этапа 1:** зафиксировать текущее рабочее дерево отдельным
коммитом. Только после этого начинать менять физику.

---

## Известные красные сценарии

Если что-то не проходит **уже сейчас**, до наших изменений — это фиксируется
здесь как известное состояние, а не считается нашей поломкой.

### `hydro` — МИГАЮЩИЙ, не стабильно красный

**Обновление по итогам четырёх прогонов:** FAIL, FAIL, PASS, PASS.
Сценарий перекидывается через порог сам по себе, без изменений в коде.

Причина видна из чисел: провальный критерий требует
`abs(imageLeft - imageRight) >= 4`, а мы даём ровно 2-3. Порог проходит
впритык, и шума GPU хватает, чтобы качнуть результат в любую сторону.

**Как относиться:** `hydro` нельзя использовать как сигнал ни в одну сторону.
Его PASS не доказывает, что всё хорошо, а FAIL не доказывает поломки.
Судить по остальным тринадцати сценариям. Починить сам критерий имеет смысл
перед этапом 6 — он проверяет переходное состояние в конкретном кадре, а не
результат, и в текущем виде бесполезен.

Разбор ниже сохранён как исходная диагностика.

### `hydro` — исходный разбор провала

Прогон от 2026-08-01 на RTX 4070, Debug, scale 0.25:

```
PHYXEL_D water=12294 leftTop=204 rightTop=205 image2s=203/205 waterfallTop=234
fallingWater=2202 resting=12294 moving=0 leaks=0 leakMass=0.000 wallGaps=0
fps=60.0 statsMoving=0 red=0
```

Провал воспроизводится: в обоих прогонах `image2s` дал ровно 203/205.

Сверка с критериями `AcceptanceRegressionVerifier.ValidateHydro`:

| Критерий | Порог | Факт | Итог |
|---|---|---|---|
| `water > 5000` | > 5000 | 12330 | OK |
| `abs(leftTop - rightTop) <= 3` | <= 3 | 1 | OK |
| `imageLeft > 0 && imageRight > 0` | > 0 | 203 / 205 | OK |
| **`abs(imageLeft - imageRight) >= 4`** | **>= 4** | **2** | **ПРОВАЛ** |
| `waterfallTop > 0` | > 0 | 233 | OK |
| `resting >= water * 0.99` | >= 12207 | 12330 | OK |
| `moving == 0` | 0 | 0 | OK |
| `leaks == 0` | 0 | 0 | OK |
| `framesPerSecond >= 55` | >= 55 | 60.0 | OK |
| `colors.Red == 0` | 0 | 0 | OK |
| `fallingWater > 100` | > 100 | 2250 | OK |

Проваливается ровно один критерий. Он проверяет, что на кадре 125 (2 секунды)
уровни в двух отсеках ещё **различаются** минимум на 4 пикселя — то есть
что выравнивание идёт постепенно, а не мгновенно. Фактически к этому моменту
разница уже 2 пикселя: вода выравнивается быстрее, чем ожидает тест.

Важно: это утверждение о **переходном состоянии** в конкретном кадре, а не о
конечном результате. Конечное состояние (`leftTop`/`rightTop`, отсутствие
утечек, полный покой) полностью корректно. Скорее всего критерий устарел после
одной из оптимизаций гидравлики.

Два критерия — `leftVisual.Gaps == 0`, `rightVisual.Gaps == 0` и
`colors.Blue > 500` — в отчёт не выводятся, поэтому нельзя исключить, что
провален не только один пункт. Если понадобится точность, стоит добавить эти
значения в строку отчёта.

**Решение отложено.** Трогать критерий сейчас нельзя: этап 0 фиксирует
реальность, а не подгоняет тесты. `hydro` считается известным красным и не
блокирует работу. Вернуться к нему нужно перед этапом 6.

---

## Визуальные эталоны

PNG-снимки контрольных кадров лежат в `artifacts/water-gate-baseline/<сценарий>/`.
Они не коммитятся, но должны сохраняться локально до конца работ по дорожной карте.
