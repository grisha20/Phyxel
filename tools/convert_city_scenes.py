#!/usr/bin/env python3
"""
Конвертер и генератор сцен городов (The Powder Toy -> Phyxel)
из доступных физических элементов:
- core:water   (океан, вода)
- core:sand    (песчаный берег, песчаные пласты)
- core:coal    (угольные пласты, темная порода)
- core:stone   (скалы, бетонные набережные, опоры, фундаменты)
- core:metal   (металлоконструкции, пирс, рамы небоскребов)
- core:steel   (стальные фасады, шпили, рельсы)
- core:wood    (деревянный настил пирса)
- core:copper  (теплое освещение окон, фонари)
- core:cooler  (неоновое свечение реактора / окон)
- core:heater  (янтарные огни)
"""

import os
import sys
import json
import struct
from datetime import datetime, timezone
from PIL import Image

PALETTE = [
    "core:empty",   # 0
    "core:water",   # 1
    "core:sand",    # 2
    "core:stone",   # 3
    "core:coal",    # 4
    "core:metal",   # 5
    "core:steel",   # 6
    "core:wood",    # 7
    "core:copper",  # 8
    "core:cooler",  # 9
    "core:heater"   # 10
]

PALETTE_INDEX = {mat_id: idx for idx, mat_id in enumerate(PALETTE)}

def map_pixel_port(x, y, r, g, b):
    # Очищаем небо слева от зданий
    if (x <= 418 and y <= 50) or (x < 370 and y <= 70):
        return 0
    if (r, g, b) == (0, 0, 0):
        return 0

    # Вода: насыщенный синий цвет
    if (r, g, b) == (64, 80, 240) or (b > 180 and b > r + 50 and b > g + 30):
        return PALETTE_INDEX["core:water"]

    # Песок: золотистый / бежевый
    if (r, g, b) in [(255, 208, 144), (255, 224, 160)] or (r > 200 and g > 160 and 100 < b < 170 and r > b + 50):
        return PALETTE_INDEX["core:sand"]

    # Дерево: коричневый цвет настила пирса
    if (r, g, b) == (128, 64, 0) or (100 < r < 160 and 40 < g < 90 and b < 40):
        return PALETTE_INDEX["core:wood"]

    # Неоновые реакторы / бирюзовое свечение (Cooler)
    if (r, g, b) in [(32, 255, 255), (32, 170, 255)] or (r < 100 and g > 180 and b > 180):
        return PALETTE_INDEX["core:cooler"]

    # Теплые огни окон (Heater / Copper)
    if (r, g, b) in [(255, 160, 64), (192, 160, 64), (240, 240, 187)] or (r > 200 and g > 130 and b < 110):
        return PALETTE_INDEX["core:copper"]

    # Уголь / темные породы
    if max(r, g, b) <= 55:
        return PALETTE_INDEX["core:coal"]

    # Сталь / яркий металл / шпили / белый контур
    if r > 180 and g > 180 and b > 180:
        return PALETTE_INDEX["core:steel"]

    # Металлоконструкции (пирс, опоры, каркасы зданий)
    if (r, g, b) in [(160, 160, 160), (128, 128, 128), (170, 170, 170)]:
        return PALETTE_INDEX["core:metal"]

    # Камень / бетон / геологические слои
    return PALETTE_INDEX["core:stone"]

def map_pixel_skyline(x, y, r, g, b):
    # Очищаем текст в небе (y < 170)
    if y < 170:
        return 0
    if (r, g, b) == (0, 0, 0):
        return 0

    # Песок в геологических пластах
    if (r, g, b) in [(255, 208, 144), (255, 224, 160)] or (r > 200 and g > 160 and 100 < b < 170):
        return PALETTE_INDEX["core:sand"]

    # Бирюзовые окна небоскребов (Cooler)
    if (g > 150 and b > 150 and r < 180) or (r, g, b) in [(136, 187, 187), (184, 235, 235), (200, 251, 251), (216, 255, 255), (104, 155, 155), (88, 139, 139)]:
        return PALETTE_INDEX["core:cooler"]

    # Теплые золотистые окна
    if (r > 190 and g > 150 and b < 100) or (r, g, b) in [(192, 160, 64), (216, 168, 47), (216, 176, 42)]:
        return PALETTE_INDEX["core:copper"]

    # Уголь / темные пласты
    if max(r, g, b) <= 65:
        return PALETTE_INDEX["core:coal"]

    # Сталь / шпили / светлые акценты
    if r > 185 and g > 185 and b > 185:
        return PALETTE_INDEX["core:steel"]

    # Металл (каркасы зданий)
    if (r, g, b) in [(160, 160, 160), (128, 128, 128), (96, 96, 96)]:
        return PALETTE_INDEX["core:metal"]

    # Камень / прочие пласты
    return PALETTE_INDEX["core:stone"]

def convert_image_to_grid(image_path, mapping_func):
    im = Image.open(image_path).convert("RGB")
    w, h = im.size
    grid = [[0 for _ in range(w)] for _ in range(h)]
    for y in range(h):
        for x in range(w):
            r, g, b = im.getpixel((x, y))
            grid[y][x] = mapping_func(x, y, r, g, b)
    return grid, w, h

def export_phyxel_scene(grid, width, height, output_json_path, output_world_path):
    os.makedirs(os.path.dirname(os.path.abspath(output_json_path)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(output_world_path)), exist_ok=True)

    # 1. JSON
    scene_data = {
        "Version": 19,
        "Scale": 0.32, # 612x384 ~ 0.32 scale
        "Gravity": 980.0,
        "BrushRadius": 18,
        "SpawnDensity": 0.82,
        "SolidGravity": False,
        "SelectedMaterialId": "core:metal",
        "SavedAt": datetime.now(timezone.utc).isoformat(),
        "HydraulicPressure": True,
        "PressureDestruction": False,
        "TemperatureSensors": [],
        "FilterSelection": 0,
        "OpenBoundaries": True,
        "Mode": "Sandbox",
        "MaterialPalette": PALETTE
    }

    with open(output_json_path, "w", encoding="utf-8") as f:
        json.dump(scene_data, f, indent=2, ensure_ascii=False)

    # 2. WORLD
    magic = 0x5058594C
    version = 19
    cell_stride = 56
    grid_len = width * height * cell_stride
    oxidizer_len = 0

    header = struct.pack("<IIiiiii", magic, version, width, height, cell_stride, grid_len, oxidizer_len)

    # Заготовки ячеек для каждого материала в палитре
    # struct GridCell:
    # MaterialIndex, Mass, VelX, VelY, Pressure, IsActive, BodyId, RestFrames, Temp, Life, MoistM, MoistE, FuelM, RetLiq
    material_cells = {}
    material_cells[0] = struct.pack("<IffffIIIfffffI", 0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0) # empty
    material_cells[1] = struct.pack("<IffffIIIfffffI", 1, 1.0, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # water
    material_cells[2] = struct.pack("<IffffIIIfffffI", 2, 1.6, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # sand
    material_cells[3] = struct.pack("<IffffIIIfffffI", 3, 2.5, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # stone
    material_cells[4] = struct.pack("<IffffIIIfffffI", 4, 1.3, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # coal
    material_cells[5] = struct.pack("<IffffIIIfffffI", 5, 7.8, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # metal
    material_cells[6] = struct.pack("<IffffIIIfffffI", 6, 7.8, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # steel
    material_cells[7] = struct.pack("<IffffIIIfffffI", 7, 0.7, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # wood
    material_cells[8] = struct.pack("<IffffIIIfffffI", 8, 8.9, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # copper
    # Cooler: TargetTemp=20, Power=0
    material_cells[9] = struct.pack("<IffffIIIfffffI", 9, 5.0, 0.0, 0.0, 20.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # cooler
    # Heater: TargetTemp=20, Power=0
    material_cells[10] = struct.pack("<IffffIIIfffffI", 10, 5.0, 0.0, 0.0, 20.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # heater

    cell_bytes = bytearray()
    for y in range(height):
        for x in range(width):
            mat_idx = grid[y][x]
            cell_bytes.extend(material_cells[mat_idx])

    with open(output_world_path, "wb") as f:
        f.write(header)
        f.write(cell_bytes)
        f.write(struct.pack("<i", 0)) # heatLength
        f.write(struct.pack("<iiii", 0, 0, 0, 0)) # pending, pulse, air, motion

    print(f"Экспортирована сцена: {output_json_path} ({width}x{height})")

def render_preview(grid, width, height, output_png_path, scale=2):
    # Цвета Phyxel рендера:
    COLOR_MAP = {
        0: (12, 14, 18),      # empty: ночное небо
        1: (43, 132, 207),    # water: #2B84CF
        2: (218, 184, 92),    # sand: #DAB85C
        3: (92, 96, 101),     # stone: #5C6065
        4: (35, 35, 38),      # coal: #232326
        5: (142, 156, 166),   # metal: #8E9CA6
        6: (173, 184, 196),   # steel: #ADB8C4
        7: (139, 90, 43),     # wood: #8B5A2B
        8: (201, 128, 67),    # copper: #C98043 (теплое свечение)
        9: (80, 201, 232),    # cooler: #50C9E8 (бирюзовое свечение)
        10: (239, 115, 75)    # heater: #EF734B (янтарное свечение)
    }

    img = Image.new("RGB", (width * scale, height * scale), (12, 14, 18))
    pixels = img.load()

    for y in range(height):
        for x in range(width):
            col = COLOR_MAP[grid[y][x]]
            for sy in range(scale):
                for sx in range(scale):
                    pixels[x * scale + sx, y * scale + sy] = col

    os.makedirs(os.path.dirname(os.path.abspath(output_png_path)), exist_ok=True)
    img.save(output_png_path)
    print(f"Превью сохранено: {output_png_path}")

def main():
    project_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    local_app_data = os.environ.get("LOCALAPPDATA", os.path.expanduser("~\\AppData\\Local"))
    user_phyxel_dir = os.path.join(local_app_data, "Phyxel")

    img1_src = r"C:/Users/Chichiryaka/.gemini/antigravity/brain/e3b86735-f16c-4a08-9a54-15fb03b56b87/.user_uploaded/media_1791454876816_c08f08f8.png"
    img2_src = r"C:/Users/Chichiryaka/.gemini/antigravity/brain/e3b86735-f16c-4a08-9a54-15fb03b56b87/.user_uploaded/media_1791454898516_9f74b1f6.png"

    # --- 1. Сцена 1: Портовый прибрежный город (Image 2) ---
    print("\n--- Генерация сцены Портового Города (Port 12) ---")
    grid2, w2, h2 = convert_image_to_grid(img2_src, map_pixel_port)
    port_json = os.path.join(project_root, "scenes", "city_port.json")
    port_world = os.path.join(project_root, "scenes", "city_port.world")
    export_phyxel_scene(grid2, w2, h2, port_json, port_world)
    render_preview(grid2, w2, h2, os.path.join(project_root, "artifacts", "city_port_preview.png"), scale=2)

    # --- 2. Сцена 2: Панорамный скайлайн города (Image 1) ---
    print("\n--- Генерация сцены Панорамы Скайлайна (City Skyline) ---")
    grid1, w1, h1 = convert_image_to_grid(img1_src, map_pixel_skyline)
    skyline_json = os.path.join(project_root, "scenes", "city_skyline.json")
    skyline_world = os.path.join(project_root, "scenes", "city_skyline.world")
    export_phyxel_scene(grid1, w1, h1, skyline_json, skyline_world)
    render_preview(grid1, w1, h1, os.path.join(project_root, "artifacts", "city_skyline_preview.png"), scale=2)

    # --- 3. Устанавливаем основную сцену в слот сохранения пользователя (%LOCALAPPDATA%\Phyxel) ---
    # Портовый город с водой, пирсом и подземным бункером — интерактивен и физичен
    user_scene_json = os.path.join(user_phyxel_dir, "scene.json")
    user_scene_world = os.path.join(user_phyxel_dir, "scene.world")
    export_phyxel_scene(grid2, w2, h2, user_scene_json, user_scene_world)
    print(f"\nОсновная сцена (Port City) установлена в слот сохранения игры: {user_scene_json}")

if __name__ == "__main__":
    main()
