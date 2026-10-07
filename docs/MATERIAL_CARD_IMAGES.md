# Изображения карточек материалов

2026-10-07: [карточки десяти фильтров и исправление кодировки названий](FILTER_CARD_IMAGES.md).

Обновлено 2026-10-06. Семь изображений созданы встроенным инструментом
`image_gen.imagegen` (навык imagegen), отдельным вызовом для каждой карточки.
Скриншоты пользователя служили примером проблемы в интерфейсе; генерация
выполнялась по тексту, без редактирования исходных картинок и без загрузки
чужих фотографий. Изображения иллюстрируют материалы и инструменты,
но не определяют их физические свойства.

Исходные результаты: 1536×1024, непрозрачный фон. В игру экспортированы
PNG 512×341 с уменьшением Lanczos. Полные исходники сохранены в локальном
каталоге Codex `generated_images/01a10ac8-7019-7643-ae15-dd3501b81a9f/`;
для запуска игры достаточно файлов ниже.

| Карточка | Файл в проекте | Исходный результат генерации |
|---|---|---|
| Сталь | [steel.png](../Content/UI/MaterialCards/steel.png) | `exec-ea1dcb85-3cf4-4687-819c-7ee4a4631896.png` |
| Чугун | [cast_iron.png](../Content/UI/MaterialCards/cast_iron.png) | `exec-3172b2e0-6c70-418a-a20b-6fd346a72231.png` |
| Медь | [copper.png](../Content/UI/MaterialCards/copper.png) | `exec-747b2566-6cd3-448f-b469-3b05b7921f16.png` |
| Металл | [metal.png](../Content/UI/MaterialCards/metal.png) | `exec-aee8a280-9a1a-4c6c-908a-dd62c55d3881.png` |
| Опора | [fixture.png](../Content/UI/MaterialCards/fixture.png) | `exec-e7f5e4e7-fbbd-4495-a675-9291ab50614c.png` |
| Нагреватель | [heater.png](../Content/UI/MaterialCards/heater.png) | `exec-76f63c86-6bb0-47a6-841d-25b326cc4bf9.png` |
| Охладитель | [cooler.png](../Content/UI/MaterialCards/cooler.png) | `exec-9bec1c43-193d-436d-91a6-e63d024781d0.png` |

## Подключение и проверка

`UI/MaterialCardPreviewCache.cs` связывает ID материалов с PNG.
Палитра и свойства используют одинаковые изображения, обрезают их по
соотношению сторон и рисуют без дополнительного цветового тонирования.
Опора получила самостоятельную картинку; старые процедурные текстуры
стали, чугуна и меди заменены изображениями.

Проверено 2026-10-06:

- Debug-сборка: 0 ошибок и предупреждений.
- `PHYXEL_VERIFY_UI=1`: `PHYXEL_UI_REGRESSION_SUCCESS`.
- Семь отдельных запусков с выбором новых карточек: загрузка 19/19 PNG,
  выход 0; просмотр всех семи карточек палитры и пяти квадратных превью
  материалов в свойствах при 1920×1080. Нагреватель и охладитель используют
  существующую компактную панель параметров без квадратного превью.
- SHA-256 каждого из семи файлов в сборке совпадает с файлом проекта.

Локальные логи и снимки: `artifacts/material-card-images-20261006/`
(не включаются в Git). Физика материалов не изменялась.

## Точный набор запросов генерации

Каждый запрос состоит из общего текста ниже и соответствующего `Subject:`.
Параметр `transparent_background=false`; CLI/API fallback не использовался.

```text
Use case: photorealistic-natural. Asset type: one raster thumbnail for the Phyxel physics sandbox material palette. Generate a SINGLE landscape image, approximately 3:2 aspect ratio. Photorealistic macro product photograph, dark charcoal backdrop, large bold subject filling most of the frame, believable surface detail and dramatic but natural studio lighting. Must remain instantly recognisable at 190 by 110 pixels. Keep the identifying subject in the middle 80 percent; bottom 20 percent may be covered by the game's label. No text, lettering, numbers, logo, watermark, border, UI, contact sheet or multiple panels. No flat featureless colour swatch.
```

### Сталь

```text
Subject: a close cluster of silver steel structural I-beam offcuts stacked diagonally, the clear I-shaped cut ends and polished milled bevels prominent, cool silver-grey metallic reflections and fine longitudinal machining scratches. Strong sturdy architectural steel identity. Show tangible three-dimensional metal, not a smooth brushed gradient.
```

### Чугун

```text
Subject: several thick dark cast-iron billets with rugged black-grey cast skin and one fresh broken cross section showing characteristic coarse granular grey fracture. Heavy, dull graphite appearance, subtle metallic highlights, angular chunky pieces. Clearly different from shiny steel and orange copper. No coal, rust, rocks or furnace fire.
```

### Медь

```text
Subject: thick reddish-orange pure copper strips curled into two broad overlapping loops with short clean cut ends, rich burnished copper reflections, lightly hammered and scratched real surface texture. Bold curved shapes and unmistakable warm copper colour. No gold, brass, coins, electrical wires or text.
```

### Металл

```text
Subject: three solid neutral-grey generic metal ingots with chamfered corners, stacked to show broad cut faces and edges. Medium-grey reflective metal with scratched industrial surfaces, broad white specular highlights, clearly dense and solid. Restrained neutral colour, no copper orange, no steel I-beams, no text.
```

### Опора

```text
Subject: a robust fixed industrial support: a thick graphite-grey steel L bracket with a large triangular reinforcing gusset, fixed firmly by two heavy silver bolts to a small squared concrete foundation. The triangular brace and bolted foot dominate the close-up, clear visual meaning of rigid anchored support. Compact recognizable silhouette, no machinery beyond the support itself, no scenery.
```

### Нагреватель

```text
Subject: an industrial electric heating element with three thick U-shaped resistance coils glowing bright orange-red, fitted into pale ceramic holders on a dark metal mount. Show real incandescent coils, vivid warm glow and subtle heat shimmer, instantly recognizable heating device. Large simple serpentine silhouette. No open flames, no furnace, no cables, no text. Predominantly warm orange and red on charcoal.
```

### Охладитель

```text
Subject: a compact industrial cooling radiator with broad aluminium fins and a thick U-shaped chilled pipe, visible white frost crystals and slight cold vapour along the metal, illuminated by crisp icy cyan-blue highlights. Large clear fin silhouette and frost, instantly recognizable cooling device. Predominantly cold cyan and pale silver on charcoal. No fire, no glowing orange, no fan, no text.
```
