#!/usr/bin/env python3
"""
Генератор интерактивной физической сцены Phyxel:
"Автоматическая доменная печь и металлургический литейный завод" (Foundry & Blast Furnace v2).

Исправления и улучшения физики:
1. Полностью сквозной дымоход без перекрытий: дым и газы поднимаются в атмосферу под действием тяги.
2. Свободный гравитационный тракт меди: загрузочный бункер ссыпает медь по наклонному лотку в ядро печи.
3. Отсутствие стальных преград: под тигля выполнен из наклонного огнеупорного шамота с подовыми нагревателями (1250 °C).
4. Расплавленная медь (core:molten_copper, 1160 °C) самотёком беспрепятственно выходит через широкую лётку.
5. Каскадный литейный тракт: расплав течет по желобу -> ковш -> Форма №1 (слиток) -> переливной порог ->
   Форма №2 (деталь с сердечником) -> переливной порог -> водяная шлаковая ванна со вскипанием пара!
6. Активные крио-контуры охлаждения (core:cooler, 10 °C) для кристаллизации меди в формах.
7. Цифровые термодатчики Phyxel.
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
    "core:stone",           # 1  (огнеупорный шамот, фундамент, футеровка)
    "core:steel",           # 2  (стальные несущие колонны, формы)
    "core:cast_iron",       # 3  (броневой кожух печи)
    "core:copper",          # 4  (медь: сырье в бункере и застывшие отливки)
    "core:molten_copper",   # 5  (жидкий расплав меди 1160 °C)
    "core:stone_coal",      # 6  (высокотемпературный уголь 1250+ °C)
    "core:fire",            # 7  (пламя горения)
    "core:heater",          # 8  (электродуговой нагреватель 1250 °C)
    "core:cooler",          # 9  (охладители литейных форм 10 °C)
    "core:water",           # 10 (вода шлаковой ванны)
    "core:steam",           # 11 (пар)
    "core:smoke",           # 12 (дым)
    "core:wood"             # 13 (помосты, лестницы)
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
    fill_rect(0, 335, WIDTH - 1, 359, "core:stone", 20.0)
    fill_rect(0, 332, WIDTH - 1, 334, "core:steel", 20.0)

    # Несущие стальные колонны цеха
    for col_x in [20, 70, 240, 360, 480, 620]:
        fill_rect(col_x - 3, 20, col_x + 3, 332, "core:steel", 20.0)
        draw_line(col_x - 30, 45, col_x + 30, 25, "core:steel", 20.0)
        draw_line(col_x - 30, 25, col_x + 30, 45, "core:steel", 20.0)

    fill_rect(0, 18, WIDTH - 1, 23, "core:steel", 20.0)
    fill_rect(0, 28, WIDTH - 1, 31, "core:steel", 20.0)

    # Мостовой кран под потолком
    fill_rect(280, 32, 350, 42, "core:steel", 20.0)
    fill_rect(313, 43, 317, 60, "core:steel", 20.0)
    fill_rect(310, 61, 320, 67, "core:cast_iron", 20.0)

    # Лестницы и площадки слева
    for step_y in range(100, 330, 25):
        fill_rect(15, step_y, 65, step_y + 2, "core:steel", 20.0)
        fill_rect(15, step_y - 12, 17, step_y, "core:wood", 20.0)

    # 2. ДОМЕННАЯ ПЕЧЬ (BLAST FURNACE)
    # Корпус печи: x=75..215, y=50..290
    fill_rect(75, 70, 80, 290, "core:cast_iron", 40.0)
    fill_rect(210, 70, 215, 290, "core:cast_iron", 40.0)
    fill_rect(81, 70, 95, 280, "core:stone", 60.0)
    fill_rect(195, 70, 209, 210, "core:stone", 60.0)
    fill_rect(182, 70, 195, 130, "core:stone", 60.0) # правый свод печи

    # АБСОЛЮТНО ОТКРЫТЫЙ ВЕРТИКАЛЬНЫЙ ДЫМОХОД (Chimney)
    # Идет от плавильной камеры (y=130) прямо вверх сквозь крышу в открытое небо!
    # Канал дымохода: x=140..175 (ширина 35 пикселей), полностью свободен!
    fill_rect(134, 0, 139, 130, "core:stone", 50.0)
    fill_rect(176, 0, 181, 130, "core:stone", 50.0)
    fill_rect(130, 0, 133, 70, "core:cast_iron", 40.0)
    fill_rect(182, 0, 185, 70, "core:cast_iron", 40.0)
    # Дым и тяга внутри трубы
    fill_rect(140, 0, 175, 120, "core:smoke", 260.0)

    # НАКЛОННЫЙ ЗАГРУЗОЧНЫЙ БУНКЕР МЕДИ (СЛЕВА)
    fill_rect(30, 85, 95, 88, "core:steel", 20.0)
    fill_rect(30, 70, 32, 85, "core:steel", 20.0)
    for bx in range(60, 120):
        by = int(90 + (bx - 60) * 0.9)
        fill_rect(bx, by, bx, by + 4, "core:stone", 100.0)
        fill_rect(bx, by - 8, bx, by - 1, "core:copper", 300.0)
    fill_rect(65, 75, 95, 87, "core:copper", 250.0)

    # ВНУТРЕННЯЯ ПЛАВИЛЬНАЯ КАМЕРА / ТИГЕЛЬ (y=135..235, x=96..194)
    # Наклонный огнеупорный под (дно тигля) из камня без стальных преград
    for hx in range(96, 186):
        hy = int(210 + (hx - 96) * 0.2)
        fill_rect(hx, hy, hx, hy + 8, "core:stone", 950.0)

    # Мощные электродуговые нагреватели (1250 °C)
    fill_rect(96, 140, 103, 205, "core:heater", 1250.0)
    fill_rect(188, 140, 194, 205, "core:heater", 1250.0)
    fill_rect(110, 222, 175, 226, "core:heater", 1250.0)

    # Раскаленный каменный уголь и пламя
    fill_rect(106, 185, 175, 212, "core:stone_coal", 1250.0)
    fill_rect(108, 160, 172, 184, "core:fire", 1280.0)

    # Плавящаяся медь: ссыпается прямо в ядро печи
    fill_rect(116, 145, 148, 170, "core:copper", 980.0)
    # Жидкий расплав molten_copper на поду печи
    fill_rect(125, 185, 185, 225, "core:molten_copper", 1180.0)

    # Поддув воздуха снизу печи
    fill_rect(50, 245, 95, 260, "core:empty", 20.0)
    fill_rect(48, 242, 96, 244, "core:steel", 20.0)
    fill_rect(48, 261, 96, 263, "core:steel", 20.0)
    fill_rect(96, 235, 140, 270, "core:empty", 180.0)

    # ВЫПУСКНАЯ ЛЁТКА (TAPHOLE) — ШИРОКИЙ ОТКРЫТЫЙ ПРОЕМ
    fill_rect(185, 229, 215, 236, "core:stone", 950.0) # порог лётки
    fill_rect(195, 208, 215, 214, "core:stone", 900.0) # свод лётки
    fill_rect(185, 215, 215, 228, "core:molten_copper", 1160.0)

    # 3. ГЛАВНЫЙ НАКЛОННЫЙ ЖЕЛОБ (MAIN RUNNER)
    for rx in range(215, 366):
        ry = int(228 + (rx - 215) * 0.25)
        fill_rect(rx, ry, rx, ry + 6, "core:stone", 900.0)
        fill_rect(rx, ry - 6, rx, ry - 1, "core:molten_copper", 1130.0)

    for sup_x in [245, 295, 345]:
        sup_y = int(228 + (sup_x - 215) * 0.25) + 7
        fill_rect(sup_x - 3, sup_y, sup_x + 3, 332, "core:steel", 40.0)

    # 4. РАЗЛИВОЧНЫЙ КОВШ (TUNDISH)
    fill_rect(366, 276, 405, 282, "core:stone", 900.0)
    fill_rect(366, 250, 372, 276, "core:stone", 850.0)
    fill_rect(400, 250, 405, 270, "core:stone", 850.0)
    fill_rect(373, 256, 399, 275, "core:molten_copper", 1120.0)
    for nx in range(398, 415):
        ny = int(271 + (nx - 398) * 0.5)
        fill_rect(nx, ny, nx, ny + 3, "core:stone", 850.0)
        fill_rect(nx, ny - 3, nx, ny - 1, "core:molten_copper", 1110.0)

    # 5. КАСКАДНЫЙ ЛИТЕЙНЫЙ ЦЕХ (CASCADE CASTING SYSTEM)
    fill_rect(410, 322, 565, 332, "core:steel", 30.0)

    # ФОРМА №1 (Слиток, x=415..475, y=285..321)
    fill_rect(415, 318, 475, 321, "core:steel", 25.0)
    fill_rect(415, 280, 420, 318, "core:steel", 25.0)
    fill_rect(470, 296, 475, 318, "core:steel", 25.0) # переливной порог
    fill_rect(410, 280, 414, 321, "core:cooler", 10.0)
    fill_rect(415, 322, 475, 325, "core:cooler", 10.0)
    fill_rect(421, 308, 469, 317, "core:copper", 350.0) # застывший слиток
    fill_rect(421, 294, 469, 307, "core:molten_copper", 1060.0)

    # ПЕРЕЛИВНОЙ ЛОТОК 1 -> 2
    for px1 in range(475, 496):
        py1 = int(296 + (px1 - 475) * 0.3)
        fill_rect(px1, py1, px1, py1 + 3, "core:steel", 200.0)
        fill_rect(px1, py1 - 3, px1, py1 - 1, "core:molten_copper", 1050.0)

    # ФОРМА №2 (Фасонная деталь с сердечником, x=495..555, y=290..321)
    fill_rect(495, 318, 555, 321, "core:steel", 25.0)
    fill_rect(495, 290, 500, 318, "core:steel", 25.0)
    fill_rect(550, 302, 555, 318, "core:steel", 25.0) # переливной порог
    fill_rect(522, 298, 528, 318, "core:steel", 25.0) # сердечник
    fill_rect(495, 322, 555, 325, "core:cooler", 10.0)
    fill_rect(501, 305, 549, 317, "core:molten_copper", 1040.0)

    # ПЕРЕЛИВНОЙ ЛОТОК 2 -> Шлаковая ванна
    for px2 in range(555, 576):
        py2 = int(302 + (px2 - 555) * 0.3)
        fill_rect(px2, py2, px2, py2 + 3, "core:steel", 150.0)
        fill_rect(px2, py2 - 3, px2, py2 - 1, "core:molten_copper", 1030.0)

    # 6. ШЛАКОВАЯ ЯМА И ВОДЯНОЙ ГРАНУЛЯТОР (x=575..635, y=295..332)
    fill_rect(575, 328, 635, 332, "core:stone", 25.0)
    fill_rect(575, 295, 580, 328, "core:stone", 25.0)
    fill_rect(631, 295, 635, 328, "core:stone", 25.0)
    fill_rect(581, 306, 630, 327, "core:water", 22.0)
    fill_rect(582, 285, 628, 305, "core:steam", 100.0)

    # 7. ОПЕРАТОРСКАЯ КАБИНА
    fill_rect(460, 125, 600, 129, "core:steel", 20.0)
    fill_rect(460, 70, 464, 125, "core:steel", 20.0)
    fill_rect(596, 70, 600, 125, "core:steel", 20.0)
    fill_rect(460, 68, 600, 72, "core:steel", 20.0)
    fill_rect(475, 129, 479, 322, "core:steel", 20.0)
    fill_rect(585, 129, 589, 322, "core:steel", 20.0)
    fill_rect(475, 110, 520, 124, "core:stone", 20.0)
    fill_rect(480, 95, 510, 108, "core:cooler", 20.0)
    fill_rect(530, 105, 550, 124, "core:wood", 20.0)

    return grid, temp

def export_scene(output_json_path, output_world_path):
    os.makedirs(os.path.dirname(os.path.abspath(output_json_path)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(output_world_path)), exist_ok=True)

    grid, temp_map = generate_foundry_grid()

    # Точки цифровых термодатчиков Phyxel
    sensors = [
        {"X": 140, "Y": 190},  # Тигель печи (зона плавления, ~1250 °C)
        {"X": 290, "Y": 235},  # Главный желоб расплава (~1120 °C)
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
            return struct.pack("<IffffIIIfffffI", 5, 8.0, 0.2, 0.1, 0.0, 1, 0, 0, t, 0.0, 0.0, 0.0, 0.0, 0)
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

    # 1. Сохранение в scenes/
    scene_json = os.path.join(project_root, "scenes", "blast_furnace.json")
    scene_world = os.path.join(project_root, "scenes", "blast_furnace.world")
    export_scene(scene_json, scene_world)

    # 2. Превью
    preview_png = os.path.join(project_root, "artifacts", "blast_furnace_preview.png")
    render_preview(preview_png, scale=2)

    # 3. Активный слот пользователя (%LOCALAPPDATA%\Phyxel)
    user_json = os.path.join(user_phyxel_dir, "scene.json")
    user_world = os.path.join(user_phyxel_dir, "scene.world")
    export_scene(user_json, user_world)
    print(f"\nОбновленная сцена установлена в активный слот игры: {user_json}")

if __name__ == "__main__":
    main()
