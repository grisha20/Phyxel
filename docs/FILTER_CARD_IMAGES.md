# Изображения фильтров — 2026-10-07

Десять отдельных PNG созданы встроенным `image_gen.imagegen` (навык
imagegen), по одному вызову на фильтр. Только текстовые запросы; чужие
фотографии и код TPT не использовались. Картинки иллюстрируют назначение,
но не задают физику. Подписи рисуются интерфейсом, внутри PNG текста нет.

Файлы: `Content/UI/FilterCards/`. `MaterialCardPreviewCache` загружает
их один раз и освобождает вместе с обычными карточками. Нижняя палитра
и панель свойств используют одинаковый PNG с центральной обрезкой
по размеру прямоугольника. При отсутствии файла остаётся прежняя пиктограмма.
Файлы копируются в сборку через Phyxel.csproj.

## Запросы генерации

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


## Исходные файлы встроенного генератора

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

