#!/usr/bin/env python3
"""
Генератор интерактивной физической сцены Phyxel:
"Автоматическая доменная печь и металлургический литейный завод" (Foundry & Blast Furnace).

Физические процессы:
1. Загрузка медных слитков (core:copper) в шахтную печь.
2. Высокотемпературная плавильная камера (1250 °C) с дуговыми нагревателями (core:heater)
   и каменным углем (core:stone_coal), раздуваемым поддувом.
3. Фазовый переход: медь плавится в жидкий ярко-оранжевый расплав (core:molten_copper) при 1085 °C.
4. Выпуск расплава через лётку в длинный наклонный огнеупорный желоб (core:stone).
5. Промежуточный распределительный ковш (Tundish) с разливочными соплами.
6. Литейный пролет с изложницами (core:steel), окруженными охладительными контурами (core:cooler).
   При остывании ниже 1035 °C расплав кристаллизуется обратно в монолитные медные детали!
7. Водяная ванна для грануляции шлака с кипением и клубами пара (core:steam).
8. Дымовая труба с конвекционной тягой.
9. Цифровые термодатчики телеметрии Phyxel на экране.
"""

import os
import sys
import json
import struct
from datetime import datetime, timezone
import numpy as np
from PIL import Image

WIDTH = 640
HEIGHT = 360

MATERIAL_PALETTE = [
    "core:empty",           # 0
    "core:stone",           # 1  (огнеупорный шамот, фундамент, футеровка желоба)
    "core:steel",           # 2  (стальные несущие колонны, колосники, формы)
    "core:cast_iron",       # 3  (броневой кожух печи)
    "core:copper",          # 4  (медь: сырье и застывшие отливки)
    "core:molten_copper",   # 5  (жидкий расплав меди 1150 °C)
    "core:stone_coal",      # 6  (высокотемпературный уголь 1200+ °C)
    "core:fire",            # 7  (пламя горения)
    "core:heater",          # 8  (электродуговой нагреватель 1250 °C)
    "core:cooler",          # 9  (охладители литейных форм 10 °C)
    "core:water",           # 10 (вода шлаковой ванны)
    "core:steam",           # 11 (пар)
    "core:smoke",           # 12 (дым)
    "core:wood"             # 13 (помосты, ящики)
]

PALETTE_INDEX = {m: i for i, m in enumerate(MATERIAL_PALETTE)}

COLOR_MAP = {
    0: (14, 16, 20),        # empty
    1: (92, 96, 101),       # stone
    2: (173, 184, 196),     # steel
    3: (80, 85, 90),        # cast_iron
    4: (201, 128, 67),      # copper
    5: (255, 151, 64),      # molten_copper
    6: (28, 28, 30),        # stone_coal
    7: (255, 70, 15),       # fire
    8: (239, 115, 75),      # heater
    9: (80, 201, 232),      # cooler
    10: (43, 132, 207),     # water
    11: (160, 160, 255),    # steam
    12: (70, 70, 70),       # smoke
    13: (139, 90, 43),      # wood
}

def generate_foundry_grid():
    grid = np.zeros((HEIGHT, WIDTH), dtype=np.int32)
    temp = np.full((HEIGHT, WIDTH), 20.0, dtype=np.float32)

    def fill_rect(x1, y1, x2, y2, mat_name, t=20.0):
        m_idx = PALETTE_INDEX[mat_name]
        for y in range(max(0, y1), min(HEIGHT, y2 + 1)):
            for x in range(max(0, x1), min(WIDTH, x2 + 1)):
                grid[y, x] = m_idx
                temp[y, x] = t

    def draw_line(x1, y1, x2, y2, mat_name, t=20.0):
        m_idx = PALETTE_INDEX[mat_name]
        dx = abs(x2 - x1)
        dy = abs(y2 - y1)
        sx = 1 if x1 < x2 else -1
        sy = 1 if y1 < y2 else -1
        err = dx - dy
        x, y = x1, y1
        while True:
            if 0 <= x < WIDTH and 0 <= y < HEIGHT:
                grid[y, x] = m_idx
                temp[y, x] = t
            if x == x2 and y == y2:
                break
            e2 = 2 * err
            if e2 > -dy:
                err -= dy
                x += sx
            if e2 < dx:
                err += dx
                y += sy

    # 1. ФУНДАМЕНТ И ЗДАНИЕ ЦЕХА
    # Массивный каменный пол
    fill_rect(0, 335, WIDTH - 1, 359, "core:stone", 20.0)
    # Стальная плита на полу
    fill_rect(0, 332, WIDTH - 1, 334, "core:steel", 20.0)

    # Несущие стальные колонны цеха
    for col_x in [20, 80, 250, 370, 500, 620]:
        fill_rect(col_x - 3, 20, col_x + 3, 332, "core:steel", 20.0)
        # Укосины ферм
        draw_line(col_x - 30, 45, col_x + 30, 25, "core:steel", 20.0)
        draw_line(col_x - 30, 25, col_x + 30, 45, "core:steel", 20.0)

    # Верхние балки мостового крана и крыша
    fill_rect(0, 18, WIDTH - 1, 23, "core:steel", 20.0)
    fill_rect(0, 28, WIDTH - 1, 31, "core:steel", 20.0)

    # Мостовой кран под потолком
    fill_rect(280, 32, 360, 42, "core:steel", 20.0)
    fill_rect(318, 43, 322, 60, "core:steel", 20.0) # трос
    fill_rect(315, 61, 325, 67, "core:cast_iron", 20.0) # крюк

    # Лестница и технологические помосты слева
    for step_y in range(100, 330, 25):
        fill_rect(25, step_y, 75, step_y + 2, "core:steel", 20.0)
        fill_rect(25, step_y - 12, 27, step_y, "core:wood", 20.0)
    # Помост колошника печи
    fill_rect(40, 95, 230, 98, "core:steel", 20.0)
    fill_rect(40, 80, 42, 95, "core:steel", 20.0) # перила

    # 2. ДОМЕННАЯ ПЕЧЬ (BLAST FURNACE)
    # Внешний бронекожух из чугуна (x=70..210, y=98..290)
    fill_rect(70, 98, 76, 290, "core:cast_iron", 40.0)
    fill_rect(204, 98, 210, 290, "core:cast_iron", 40.0)

    # Огнеупорная футеровка из камня (толстый слой шамота)
    fill_rect(77, 98, 100, 280, "core:stone", 60.0)
    fill_rect(180, 98, 203, 220, "core:stone", 60.0)

    # Дымовая труба (Chimney) вверху по центру (x=125..155, y=0..98)
    fill_rect(122, 0, 127, 98, "core:stone", 50.0)
    fill_rect(153, 0, 158, 98, "core:stone", 50.0)
    # Внутри трубы дым и тяга
    fill_rect(128, 0, 152, 60, "core:smoke", 220.0)

    # Загрузочный бункер / колошник (x=101..179, y=98..125)
    for i in range(20):
        draw_line(100 + i, 98 + i, 100 + i, 115, "core:stone", 45.0)
        draw_line(180 - i, 98 + i, 180 - i, 115, "core:stone", 45.0)

    # Загруженная медь (кусковое сырье в бункере, x=115..165, y=102..125)
    fill_rect(116, 102, 164, 125, "core:copper", 350.0)

    # Плавильная камера / шахта печи (x=101..179, y=126..215)
    # Мощные электродуговые нагреватели по бокам (1250 °C)
    fill_rect(101, 135, 108, 195, "core:heater", 1250.0)
    fill_rect(172, 135, 179, 195, "core:heater", 1250.0)

    # Жаропрочная стальная колосниковая решетка на дне тигля (y=216..219, x=101..175)
    fill_rect(101, 216, 175, 219, "core:steel", 950.0)

    # Внутри тигля: слой каменного угля (stone_coal) и пламени
    fill_rect(109, 175, 171, 215, "core:stone_coal", 1220.0)
    fill_rect(112, 155, 168, 174, "core:fire", 1250.0)

    # Под колосниками: воздуховод поддува (приток воздуха снизу слева)
    fill_rect(50, 230, 100, 245, "core:empty", 20.0)
    fill_rect(48, 227, 101, 229, "core:steel", 20.0)
    fill_rect(48, 246, 101, 248, "core:steel", 20.0)
    fill_rect(101, 220, 150, 255, "core:empty", 160.0) # зольник/воздушная камера

    # В нижней части тигля: накопленный жидкий расплав меди (molten_copper, 1160 °C)!
    fill_rect(145, 195, 175, 215, "core:molten_copper", 1160.0)

    # Выпускная лётка (Taphole) в правой стенке печи (x=176..208, y=200..224)
    fill_rect(176, 216, 208, 224, "core:stone", 950.0) # порог лётки
    fill_rect(180, 200, 208, 205, "core:stone", 900.0) # свод лётки
    # Поток расплава в лётке
    fill_rect(176, 206, 208, 215, "core:molten_copper", 1150.0)

    # 3. ГЛАВНЫЙ НАКЛОННЫЙ ЖЕЛОБ РАСПЛАВА (MAIN RUNNER)
    for x in range(208, 366):
        base_y = int(216 + (x - 208) * 0.25)
        # Каменная постель желоба
        fill_rect(x, base_y, x, base_y + 6, "core:stone", 850.0)
        # Жидкий расплав в желобе
        fill_rect(x, base_y - 5, x, base_y - 1, "core:molten_copper", 1120.0)

    # Подпорки желоба
    for sup_x in [240, 290, 340]:
        sup_y = int(216 + (sup_x - 208) * 0.25) + 7
        fill_rect(sup_x - 3, sup_y, sup_x + 3, 332, "core:steel", 40.0)

    # 4. РАЗЛИВОЧНЫЙ ПУНКТ (TUNDISH)
    fill_rect(366, 270, 405, 276, "core:stone", 900.0) # дно
    fill_rect(366, 245, 372, 270, "core:stone", 850.0) # левая стенка
    fill_rect(400, 245, 406, 270, "core:stone", 850.0) # правая стенка
    fill_rect(373, 252, 399, 269, "core:molten_copper", 1110.0) # ванна расплава

    # Разливочное сопло из ковша вниз к формам
    fill_rect(382, 277, 390, 285, "core:molten_copper", 1100.0)

    # 5. ЛИТЕЙНЫЙ ЦЕХ И ИЗЛОЖНИЦЫ (CASTING MOLDS)
    # Литейный стол
    fill_rect(410, 318, 570, 324, "core:steel", 30.0)

    # ФОРМА №1: Слиток (Ingot Mold, x=415..480, y=285..317)
    fill_rect(415, 312, 480, 317, "core:steel", 25.0) # дно
    fill_rect(415, 285, 421, 312, "core:steel", 25.0) # левая стенка
    fill_rect(474, 285, 480, 312, "core:steel", 25.0) # правая стенка
    # Охлаждающая рубашка (cooler) вокруг формы №1
    fill_rect(410, 285, 414, 317, "core:cooler", 12.0)
    fill_rect(481, 285, 485, 317, "core:cooler", 12.0)
    fill_rect(415, 318, 480, 321, "core:cooler", 12.0)
    # Нижняя половина формы уже кристаллизовалась в твердую медь!
    fill_rect(422, 303, 473, 311, "core:copper", 820.0)
    # Верхняя половина — жидкий расплав
    fill_rect(422, 292, 473, 302, "core:molten_copper", 1060.0)

    # ФОРМА №2: Фасонная деталь с сердечником (x=495..560, y=285..317)
    fill_rect(495, 312, 560, 317, "core:steel", 25.0) # дно
    fill_rect(495, 285, 501, 312, "core:steel", 25.0)
    fill_rect(554, 285, 560, 312, "core:steel", 25.0)
    # Центральный сердечник
    fill_rect(524, 292, 532, 312, "core:steel", 25.0)
    # Охлаждающая рубашка формы №2
    fill_rect(490, 285, 494, 317, "core:cooler", 10.0)
    fill_rect(561, 285, 565, 317, "core:cooler", 10.0)
    fill_rect(495, 318, 560, 321, "core:cooler", 10.0)

    # Наклонный распределительный лоток от сопла к формам (x=380..500)
    for lx in range(385, 425):
        ly = int(282 + (lx - 385) * 0.15)
        fill_rect(lx, ly, lx, ly + 2, "core:stone", 700.0)
        fill_rect(lx, ly - 2, lx, ly - 1, "core:molten_copper", 1080.0)

    # 6. ШЛАКОВАЯ ЯМА И ВОДЯНОЙ ГРАНУЛЯТОР (x=575..635, y=295..332)
    fill_rect(575, 328, 635, 332, "core:stone", 25.0) # дно бассейна
    fill_rect(575, 295, 579, 328, "core:stone", 25.0) # стенка
    fill_rect(631, 295, 635, 328, "core:stone", 25.0)
    fill_rect(580, 305, 630, 327, "core:water", 22.0) # вода
    fill_rect(582, 285, 628, 304, "core:steam", 100.0) # пар

    # 7. ПУЛЬТ УПРАВЛЕНИЯ МЕТАЛЛУРГА (ОПЕРАТОРСКАЯ, x=460..600, y=70..130)
    fill_rect(460, 125, 600, 129, "core:steel", 20.0) # пол
    fill_rect(460, 70, 464, 125, "core:steel", 20.0)  # левая стена
    fill_rect(596, 70, 600, 125, "core:steel", 20.0)  # правая стена
    fill_rect(460, 68, 600, 72, "core:steel", 20.0)   # крыша
    fill_rect(475, 129, 479, 318, "core:steel", 20.0) # опоры
    fill_rect(585, 129, 589, 318, "core:steel", 20.0)
    fill_rect(475, 110, 520, 124, "core:stone", 20.0) # пульт
    fill_rect(480, 95, 510, 108, "core:cooler", 20.0) # экраны
    fill_rect(530, 105, 550, 124, "core:wood", 20.0)   # кресло

    return grid, temp

def export_scene(output_json_path, output_world_path):
    os.makedirs(os.path.dirname(os.path.abspath(output_json_path)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(output_world_path)), exist_ok=True)

    grid, temp_map = generate_foundry_grid()

    # Точки цифровых термодатчиков Phyxel
    sensors = [
        {"X": 140, "Y": 180},  # Тигель печи (зона плавления, ~1200 °C)
        {"X": 290, "Y": 235},  # Главный желоб расплава (~1100 °C)
        {"X": 450, "Y": 305},  # Литейная изложница №1 (зона кристаллизации)
        {"X": 605, "Y": 315}   # Шлаковая водяная ванна (~22 °C)
    ]

    # 1. JSON
    scene_data = {
        "Version": 19,
        "Scale": 0.32,
        "Gravity": 980.0,
        "BrushRadius": 18,
        "SpawnDensity": 0.82,
        "SolidGravity": False,
        "SelectedMaterialId": "core:copper",
        "SavedAt": datetime.now(timezone.utc).isoformat(),
        "HydraulicPressure": True,
        "PressureDestruction": False,
        "TemperatureSensors": sensors,
        "FilterSelection": 0,
        "OpenBoundaries": True,
        "Mode": "Sandbox",
        "MaterialPalette": MATERIAL_PALETTE
    }

    with open(output_json_path, "w", encoding="utf-8") as f:
        json.dump(scene_data, f, indent=2, ensure_ascii=False)

    # 2. WORLD
    magic = 0x5058594C
    version = 19
    cell_stride = 56
    grid_len = WIDTH * HEIGHT * cell_stride
    oxidizer_len = 0

    header = struct.pack("<IIiiiii", magic, version, WIDTH, HEIGHT, cell_stride, grid_len, oxidizer_len)

    # Шаблоны ячеек: <IffffIIIfffffI
    def pack_cell(mat_idx, t):
        if mat_idx == 0: # empty
            return struct.pack("<IffffIIIfffffI", 0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 1: # stone
            return struct.pack("<IffffIIIfffffI", 1, 9.2, 0.0, 0.0, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 2: # steel
            return struct.pack("<IffffIIIfffffI", 2, 7.8, 0.0, 0.0, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 3: # cast_iron
            return struct.pack("<IffffIIIfffffI", 3, 7.2, 0.0, 0.0, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 4: # copper
            return struct.pack("<IffffIIIfffffI", 4, 8.9, 0.0, 0.0, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 5: # molten_copper
            return struct.pack("<IffffIIIfffffI", 5, 8.0, 0.15, 0.1, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 6: # stone_coal
            return struct.pack("<IffffIIIfffffI", 6, 1.4, 0.0, 0.0, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 7: # fire
            return struct.pack("<IffffIIIfffffI", 7, 0.01, 0.0, -0.5, 0.0, 1, 0, 0, t, 25.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 8: # heater (1250 °C, Power 2000)
            return struct.pack("<IffffIIIfffffI", 8, 5.0, 0.0, 0.0, 1250.0, 1, 0, 0, t, 2000.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 9: # cooler (10 °C, Power 1500)
            return struct.pack("<IffffIIIfffffI", 9, 5.0, 0.0, 0.0, 10.0, 1, 0, 0, t, 1500.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 10: # water
            return struct.pack("<IffffIIIfffffI", 10, 1.0, 0.0, 0.0, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 11: # steam
            return struct.pack("<IffffIIIfffffI", 11, 0.01, 0.0, -0.3, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 12: # smoke
            return struct.pack("<IffffIIIfffffI", 12, 0.02, 0.0, -0.4, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        elif mat_idx == 13: # wood
            return struct.pack("<IffffIIIfffffI", 13, 0.7, 0.0, 0.0, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
        return struct.pack("<IffffIIIfffffI", 0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0)

    cell_bytes = bytearray()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            m = grid[y, x]
            t = temp_map[y, x]
            cell_bytes.extend(pack_cell(m, t))

    with open(output_world_path, "wb") as f:
        f.write(header)
        f.write(cell_bytes)
        f.write(struct.pack("<i", 0))             # heatLength
        f.write(struct.pack("<iiii", 0, 0, 0, 0)) # pending, pulse, air, motion

    print(f"Экспортирована сцена: {output_json_path}")
    print(f"Экспортирован мир:   {output_world_path}")

def render_preview(output_png_path, scale=2):
    grid, _ = generate_foundry_grid()
    img = Image.new("RGB", (WIDTH * scale, HEIGHT * scale), (14, 16, 20))
    pixels = img.load()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            c = COLOR_MAP[grid[y, x]]
            for sy in range(scale):
                for sx in range(scale):
                    pixels[x * scale + sx, y * scale + sy] = c
    os.makedirs(os.path.dirname(os.path.abspath(output_png_path)), exist_ok=True)
    img.save(output_png_path)
    print(f"Превью сохранено: {output_png_path}")

def main():
    project_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    local_app_data = os.environ.get("LOCALAPPDATA", os.path.expanduser("~\\AppData\\Local"))
    user_phyxel_dir = os.path.join(local_app_data, "Phyxel")

    # 1. Сохранение в каталог scenes/
    scene_json = os.path.join(project_root, "scenes", "blast_furnace.json")
    scene_world = os.path.join(project_root, "scenes", "blast_furnace.world")
    export_scene(scene_json, scene_world)

    # 2. Превью в artifacts
    preview_png = os.path.join(project_root, "artifacts", "blast_furnace_preview.png")
    render_preview(preview_png, scale=2)

    # 3. Установка в активный слот сохранения игры пользователя (%LOCALAPPDATA%\Phyxel)
    user_json = os.path.join(user_phyxel_dir, "scene.json")
    user_world = os.path.join(user_phyxel_dir, "scene.world")
    export_scene(user_json, user_world)
    print(f"\nСцена 'Доменная плавильня' установлена в активный слот игры: {user_json}")

if __name__ == "__main__":
    main()
