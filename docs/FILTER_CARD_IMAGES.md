# Изображения фильтров — 2026-10-07

Десять отдельных PNG созданы встроенным `image_gen.imagegen` (навык
imagegen), по одному вызову на фильтр. На первом этапе — текстовые запросы,
при исправлении формата — редактирование этих исходных PNG; чужие
фотографии и код TPT не использовались. Картинки иллюстрируют назначение,
но не задают физику. Подписи рисуются интерфейсом, внутри PNG текста нет.

Файлы: `Content/UI/FilterCards/`. `MaterialCardPreviewCache` загружает
их один раз и освобождает вместе с обычными карточками. Нижняя палитра
и панель свойств используют одинаковый PNG с центральной обрезкой
по размеру прямоугольника. При отсутствии файла остаётся прежняя пиктограмма.
Файлы копируются в сборку через Phyxel.csproj.

## Формат карточек

Карточки фильтров используют размеры карточек материалов: общая формула
ширины по высоте, одинаковые отступы и рамка выбора. Изображение занимает
всю карточку, подпись — в одну строку на полупрозрачной полосе снизу;
размер текста подбирается тем же `FitCardTitle`. Первоначально картинки
оставались квадратными; далее описана замена широкими композициями.
Колесо и стрелки прокручивают палитру на одну карточку. При открытии
выбранного фильтра прокрутка учитывает текущий размер карточек.

После изменения формата: сборка без ошибок и предупреждений,
существующие UI layout/input regression PASS на 12 сочетаниях разрешения
и DPI, включая выбор всех десяти фильтров и доступ к последнему на узкой
палитре. На новых снимках 2560×1440 проверены обе части списка,
читаемость подписей и рамка выбора. Физика не менялась.

[Начало списка](../artifacts/filter-card-layout-20261007/filters-first.png),
[конец списка](../artifacts/filter-card-layout-20261007/filters-last.png),
[UI regression](../artifacts/filter-card-layout-20261007/ui.log).

## Широкие исходники вместо квадратных

Первоначальные квадратные изображения заменены десятью широкими PNG
(1701–1706 × 922–925 px, отношение сторон около 1.85:1).
Встроенный `image_gen.imagegen` пересоздал композиции по прежним PNG как
edit targets: полный фильтр с запасом по краям, потоки по горизонтали,
тёмный низ для подписи. Это не растяжение и не механическая обрезка.
Рамки, крепления, кристалл и сито с горкой песка показаны целиком.
Из каталога Codex скопированы исходные PNG без дальнейшего редактирования.

Новые проверки: сборка без ошибок и предупреждений; все десять PNG
читаются, различны и имеют широкий формат; GPU загрузил 10/10 фильтров.
На двух новых снимках 2560×1440 проверены все десять карточек в палитре:
рамки и главные детали помещаются целиком, подписи читаемы. Код и физика
не менялись, проверки физики не запускались.

[Начало списка](../artifacts/filter-wide-art-20261007/filters-first.png),
[конец списка](../artifacts/filter-wide-art-20261007/filters-last.png),
[загрузка изображений](../artifacts/filter-wide-art-20261007/capture-first.log).

### Запросы исправления

Для steam.png:

```text
Use case: precise-object-edit. Asset: Phyxel wide landscape inventory card, 1.86:1 aspect ratio, approximately 1536x832. Edit this square reference into a genuinely wide landscape composition. Preserve the same silver perforated steam filter, white steam, realistic industrial 3D style and charcoal studio background. Pull the camera back and recompose: the COMPLETE round metal filter including its top and bottom must be visible with 10% margin, centered horizontally, entirely in the upper 78% of the landscape canvas. Wisps may spread sideways into the newly extended background. Reserve the bottom 18% as quiet dark background for the game label; do not draw the label. Never crop the filter, never stretch the circle, no close-up, no text, no borders. Output landscape, not square.
```

Для остальных девяти — общий текст плюс Subject из таблицы:

```text
Use case: precise-object-edit. Asset: Phyxel WIDE LANDSCAPE inventory card, aspect ratio 1.86:1, approximately 1536x832. Recompose this square reference into a genuinely wide landscape image. Preserve subject identity, its material, colors, realistic industrial 3D finish, charcoal studio background and three-quarter view. Pull the camera back: show the COMPLETE filter frame including all top and bottom tabs, with a 10% margin. Put the complete object within the upper 78% of the canvas, centered horizontally; spread flows horizontally on both sides. Keep bottom 18% quiet dark background for a game title overlay (do not draw any title). Do not crop any part of the filter; no stretching, close-up, text, logo, borders. Output a wide landscape image, NOT a square. Subject:
```

| Файл | Subject |
|---|---|
| water.png | silver blue water filter, flowing cobalt blue water and droplets |
| oil.png | bronze oil filter, glossy amber oil ribbon and golden droplet |
| gases.png | silver perforated gas filter, green and teal gaseous wisps |
| liquids.png | silver mesh liquid filter, BOTH blue water and amber oil ribbons crossing it |
| powders.png | silver slotted sieve filter containing golden sand, with sand falling and a small complete pile below; fit the sieve AND sand pile above the bottom 18% title area |
| selected_material.png | silver square aperture gate, a complete violet faceted crystal passing through, other dull colored rocks held back on its left; show ALL four frame edges and the complete crystal |
| wall.png | a COMPLETE solid brick wall panel in its heavy dark steel frame, show all corners, posts and feet within the upper 78% of canvas, no openings |
| air_only.png | complete silver round fine grille, blue droplets and golden grains stopped on its left, clean pale cyan wind streaks passing to its right |
| no_air.png | complete pink-tinted silver round gate, blue water droplets and amber grains passing through, pale white wind wisps halted against the sealed membrane |

### Текущие исходные файлы генератора

Каталог: `C:/Users/Степан/.codex/generated_images/01a10ac8-7019-7643-ae15-dd3501b81a9f/`.
Игра использует копии в `Content/UI/FilterCards/`; каталог Codex ей не нужен.

| Файл проекта | Исходник генератора |
|---|---|
| steam.png | exec-1ce42f02-3c68-447b-89aa-beb8a0a84e0b.png |
| water.png | exec-7756cddd-4baf-4146-9f3a-35306bfc3d97.png |
| oil.png | exec-be3ae436-186d-43fc-b87a-69836d69fd6a.png |
| gases.png | exec-700234b8-989c-4469-b921-617b58907cf3.png |
| liquids.png | exec-b1058448-7607-49b7-b45a-2b1567347a01.png |
| powders.png | exec-eded71bb-12e8-496a-a69a-01ecbc212269.png |
| selected_material.png | exec-192ad5f1-2489-49d3-95b3-ce50c615ad0c.png |
| wall.png | exec-094340ab-49b0-4f53-84b0-087efd30c962.png |
| air_only.png | exec-36729ff9-a89d-4351-8206-9cc08ca8c420.png |
| no_air.png | exec-1acc6f7d-842f-4321-a4a1-3a8e0da03bd7.png |

## Первоначальные запросы генерации (квадратные изображения)

Общий текст каждого запроса:

```text
Use case: stylized-concept. Asset type: Phyxel game inventory filter brush illustration. Create one polished square 3D studio render: [SUBJECT]. Close-up object centered and large, three quarter view, realistic finely textured metal, physically rendered material, dark charcoal studio background, restrained colored light, sharp readable silhouette, consistent premium industrial game inventory art. Image should remain recognizable at 100 pixels. No text, letters, numbers, UI frames, labels, logos, watermarks, no explosion or fire.
```

| Файл | SUBJECT |
|---|---|
| steam.png | white steam curls flowing through a silver perforated filter gate, pale ice blue glow |
| water.png | a clear cobalt blue stream and suspended water droplets passing through a silver filter gate, bright blue highlights |
| oil.png | a thick glossy amber oil ribbon and golden oil droplet passing through a dark bronze filter gate |
| gases.png | wispy green and teal translucent gas plumes flowing through a silver perforated filter gate |
| liquids.png | two distinct blue and amber liquid ribbons passing through a silver filter gate, clear droplets |
| powders.png | golden sand grains pouring through a silver slotted filter gate, a small pile below |
| selected_material.png | one glowing violet faceted crystal passing through a precise silver square aperture, other dull colored particles held back |
| wall.png | a solid dark steel framed brick wall with no openings, hefty masonry blocks, subtle silver rim light |
| air_only.png | clean pale cyan wind streaks passing through a fine silver grille, several blue droplets and golden grains held on the inlet side |
| no_air.png | blue water droplets and amber grains passing through a pink tinted silver gate, pale wind streaks halted by a sealed pane |

## Текст

Исправлены повреждённые строки name.ru в metal/steel/cast_iron/copper:
«Металл», «Сталь», «Чугун», «Медь». Шрифтовой атлас исправен.
Параметры материалов и физические обработчики не менялись.

Исправлена подсветка hover обычных карточек: белый RGB теперь умножен
на alpha для BlendState.AlphaBlend. Прежняя непреумноженная подсветка
закрывала картинку белым прямоугольником при наведении.

## Проверки

Сборка без предупреждений/ошибок; существующие UI layout/input regression
PASS (1280–2560, DPI100/125/150%). PNG10/10 валидны и различны;
в игре загружены10/10 фильтров и19/19 материалов. На снимках2560×1440
проверены все десять карточек, панель свойств и четыре исправленных названия.
Отдельный снимок подтвердил сохранение картинки меди при hover.
Сравнение JSON с исходным коммитом подтвердило: изменены только name.ru.
Полные проверки физики не запускались.

[Снимок фильтров](../artifacts/filter-art-20261007/filters.png),
[снимок названий и hover](../artifacts/filter-art-20261007/metals-final.png),
[UI regression](../artifacts/filter-art-20261007/ui-final.log).


## Первоначальные квадратные файлы генератора (заменены)

Локальный каталог Codex: `generated_images/01a10ac8-7019-7643-ae15-dd3501b81a9f/`. В проект скопированы исходные PNG без ретуши; для игры не нужен каталог Codex.

| Карточка | Исходный файл |
|---|---|
| steam | exec-8e555d90-d61b-4da7-b60a-b0cc99aade00.png |
| water | exec-55145c0e-9191-4f95-969c-8c2e1e0e68c1.png |
| oil | exec-a01c7d49-4634-4c3e-b135-997a6b172da4.png |
| gases | exec-06fbbb30-50a5-4264-b202-03e948677cb0.png |
| liquids | exec-6907dca0-62f7-4b35-b1f4-e170ba10bd02.png |
| powders | exec-167e44d9-b2c6-42b0-a7ec-314b9ecc6362.png |
| selected_material | exec-d3f7c9e3-085e-457b-a4ed-baba49f463db.png |
| wall | exec-f9a9cade-290c-438f-a913-c69f89f1c7b7.png |
| air_only | exec-4bf1a53d-f689-4171-b26f-d79fea839bba.png |
| no_air | exec-487b4f5d-1292-47f2-b0a0-9f850f82f81e.png |
