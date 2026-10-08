#!/usr/bin/env python3
"""
Конвертер фотопортрета в физическую сцену Phyxel (.json + .world).
Использует только физически стабильные твердые материалы (kind: solid) и empty:
- core:empty     (глубокий черный фон)
- core:cast_iron (твердый чугун: вязаная шапка, темные контуры)
- core:fixture   (темный графитовый фиксатор: тени и темные швы)
- core:stone     (камень: нейтральные тени)
- core:metal     (металл: полутона, серые детали)
- core:steel     (сталь: белые цветы черемухи, светоотражающие полосы, блики)
- core:wood      (дерево: каштановые волосы, тени кожи, ветви дерева)
- core:fuse      (фитиль: зеленая листва, зеленая футболка)
- core:copper    (медь: теплый тон кожи лица, шеи, загар)
- core:tnt       (тротил: светлые участки кожи, золотистые блики)
- core:heater    (нагреватель: ярко-красная куртка, губы, румянец)
- core:cooler    (охладитель: ярко-синие полосы на рукаве и куртке)

Все материалы жестко зафиксированы (SolidGravity: false), не осыпаются и не текут.
Термоэлементы (heater/cooler) настроены на нейтральную комнатную температуру (20°C, Power=0).
"""

import os
import sys
import json
import struct
from datetime import datetime, timezone
from PIL import Image
import numpy as np

# Палитра сцены
MATERIAL_PALETTE = [
    "core:empty",       # 0
    "core:fixture",     # 1
    "core:cast_iron",   # 2
    "core:stone",       # 3
    "core:metal",       # 4
    "core:steel",       # 5
    "core:wood",        # 6
    "core:fuse",        # 7
    "core:copper",      # 8
    "core:tnt",         # 9
    "core:heater",      # 10
    "core:cooler"       # 11
]

PALETTE_INDEX = {mat: i for i, mat in enumerate(MATERIAL_PALETTE)}

# Отображаемые цвета материалов в рендере Phyxel
MATERIAL_COLORS = {
    "core:empty":     (9, 10, 12),
    "core:fixture":   (82, 91, 99),
    "core:cast_iron": (97, 102, 107),
    "core:stone":     (92, 96, 101),
    "core:metal":     (142, 156, 166),
    "core:steel":     (173, 184, 196),
    "core:wood":      (139, 90, 43),
    "core:fuse":      (102, 112, 71),
    "core:copper":    (201, 128, 67),
    "core:tnt":       (217, 172, 67),
    "core:heater":    (239, 115, 75),
    "core:cooler":    (80, 201, 232),
}

def match_pixel_to_material(r, g, b):
    """
    Интеллектуальное перцептивное сопоставление RGB пикселя с материалом Phyxel.
    Гарантирует естественные теплые тона лиц без металлических артефактов,
    насыщенные синие и красные элементы одежды, зеленую листву и белые цветы.
    """
    # 1. Синий акцент (синяя полоса на рукаве девушки, шеврон парня, воротник)
    if b > r + 15 and b > g + 5 and b > 55:
        return "core:cooler"

    # 2. Зеленый акцент (листва дерева, футболка девушки)
    if g > r + 5 and g > b + 5 and g > 45:
        return "core:fuse"

    # 3. Красный акцент (красная куртка парня, губы, румянец, бордовые участки ветровки)
    if r > g + 30 and r > b + 30 and r > 95:
        return "core:heater"

    # 4. Теплые тона кожи лица и шеи (R > B + 12 и R > G + 2)
    if r > b + 12 and r > g + 2 and r > 75:
        skin_cands = ["core:tnt", "core:copper", "core:wood", "core:heater"]
        target = np.array([r, g, b], dtype=np.float32)
        best = "core:copper"
        min_d = 1e9
        for cand in skin_cands:
            c = np.array(MATERIAL_COLORS[cand], dtype=np.float32)
            d = 2.0 * (target[0] - c[0])**2 + 3.0 * (target[1] - c[1])**2 + (target[2] - c[2])**2
            if d < min_d:
                min_d = d
                best = cand
        return best

    # 5. Очень темные участки (вязаная шапка парня, глубокие складки одежды)
    if max(r, g, b) < 38:
        return "core:empty"
    if max(r, g, b) < 70:
        return "core:cast_iron"

    # 6. Волосы девушки и ветви дерева (глубокие коричневые тона)
    if r > b + 15 and 45 < r < 140:
        return "core:wood"

    # 7. Очень яркие белые детали (цветы черемухи, светоотражающие полосы, белая полоса ветровки)
    if min(r, g, b) > 165 or (r > 155 and g > 155 and b > 155 and abs(r - g) < 20 and abs(g - b) < 20):
        return "core:steel"

    # 8. Нейтральные серые тона (тени, металлофурнитура)
    target = np.array([r, g, b], dtype=np.float32)
    neutral_cands = ["core:empty", "core:fixture", "core:cast_iron", "core:stone", "core:metal", "core:steel"]
    best = "core:stone"
    min_d = 1e9
    for cand in neutral_cands:
        c = np.array(MATERIAL_COLORS[cand], dtype=np.float32)
        d = np.sum((target - c)**2)
        if d < min_d:
            min_d = d
            best = cand
    return best

def convert_photo(image_path, width=480, height=480, dither_strength=0.45):
    im = Image.open(image_path).convert("RGB").resize((width, height), Image.Resampling.LANCZOS)
    arr = np.array(im, dtype=np.float32)

    grid = [[0 for _ in range(width)] for _ in range(height)]
    preview_rgb = np.zeros((height, width, 3), dtype=np.uint8)

    for y in range(height):
        for x in range(width):
            r, g, b = np.clip(arr[y, x], 0.0, 255.0)
            mat_id = match_pixel_to_material(r, g, b)
            grid[y][x] = PALETTE_INDEX[mat_id]
            chosen_rgb = np.array(MATERIAL_COLORS[mat_id], dtype=np.float32)
            preview_rgb[y, x] = chosen_rgb.astype(np.uint8)

            # Floyd-Steinberg error diffusion
            err = (np.array([r, g, b]) - chosen_rgb) * dither_strength
            if x + 1 < width:
                arr[y, x + 1] += err * (7.0 / 16.0)
            if y + 1 < height:
                if x - 1 >= 0:
                    arr[y + 1, x - 1] += err * (3.0 / 16.0)
                arr[y + 1, x] += err * (5.0 / 16.0)
                if x + 1 < width:
                    arr[y + 1, x + 1] += err * (1.0 / 16.0)

    return grid, preview_rgb

def export_phyxel_scene(grid, width, height, output_json_path, output_world_path):
    os.makedirs(os.path.dirname(os.path.abspath(output_json_path)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(output_world_path)), exist_ok=True)

    # 1. JSON
    scene_data = {
        "Version": 19,
        "Scale": 0.25,
        "Gravity": 980.0,
        "BrushRadius": 18,
        "SpawnDensity": 0.82,
        "SolidGravity": False,
        "SelectedMaterialId": "core:copper",
        "SavedAt": datetime.now(timezone.utc).isoformat(),
        "HydraulicPressure": True,
        "PressureDestruction": False,
        "TemperatureSensors": [],
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
    grid_len = width * height * cell_stride
    oxidizer_len = 0

    header = struct.pack("<IIiiiii", magic, version, width, height, cell_stride, grid_len, oxidizer_len)

    # Подготовка бинарных шаблонов для каждого материала палитры
    # Struct layout: <IffffIIIfffffI (56 bytes)
    # [MatIndex, Mass, Vx, Vy, Pressure/TargetTemp, IsActive, BodyId, RestFrames, Temp, Lifetime/Power, MoistureMass, MoistureEnergy, FuelMass, RetainedMatIndex]
    material_cells = {}
    material_cells[0] = struct.pack("<IffffIIIfffffI", 0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # empty
    material_cells[1] = struct.pack("<IffffIIIfffffI", 1, 7.0, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # fixture
    material_cells[2] = struct.pack("<IffffIIIfffffI", 2, 7.2, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # cast_iron
    material_cells[3] = struct.pack("<IffffIIIfffffI", 3, 2.5, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # stone
    material_cells[4] = struct.pack("<IffffIIIfffffI", 4, 7.8, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # metal
    material_cells[5] = struct.pack("<IffffIIIfffffI", 5, 7.8, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # steel
    material_cells[6] = struct.pack("<IffffIIIfffffI", 6, 0.7, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # wood
    material_cells[7] = struct.pack("<IffffIIIfffffI", 7, 1.2, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # fuse
    material_cells[8] = struct.pack("<IffffIIIfffffI", 8, 8.9, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # copper
    material_cells[9] = struct.pack("<IffffIIIfffffI", 9, 1.6, 0.0, 0.0, 0.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0) # tnt
    # heater (TargetTemp=20, Power=0 - термостабилен)
    material_cells[10] = struct.pack("<IffffIIIfffffI", 10, 5.0, 0.0, 0.0, 20.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0)
    # cooler (TargetTemp=20, Power=0 - термостабилен)
    material_cells[11] = struct.pack("<IffffIIIfffffI", 11, 5.0, 0.0, 0.0, 20.0, 1, 0, 0, 20.0, 0.0, 0.0, 0.0, 0.0, 0)

    cell_bytes = bytearray()
    for y in range(height):
        for x in range(width):
            mat_idx = grid[y][x]
            cell_bytes.extend(material_cells[mat_idx])

    with open(output_world_path, "wb") as f:
        f.write(header)
        f.write(cell_bytes)
        f.write(struct.pack("<i", 0))             # heatLength
        f.write(struct.pack("<iiii", 0, 0, 0, 0)) # pending, pulse, air, motion

    print(f"Экспортирована сцена: {output_json_path} ({width}x{height})")
    print(f"Экспортирован мир:   {output_world_path} ({len(cell_bytes)} байт сетки)")

def main():
    project_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    default_img = r"C:/Users/Chichiryaka/.gemini/antigravity/brain/e3b86735-f16c-4a08-9a54-15fb03b56b87/.user_uploaded/media_1791456015749_17e73aea.png"
    img_path = sys.argv[1] if len(sys.argv) > 1 else default_img

    if not os.path.isfile(img_path):
        print(f"Ошибка: файл не найден: {img_path}")
        sys.exit(1)

    print(f"Конвертация изображения: {img_path}")
    width, height = 480, 480
    grid, preview_rgb = convert_photo(img_path, width=width, height=height, dither_strength=0.45)

    # 1. Сохранение превью в artifacts
    preview_path = os.path.join(project_root, "artifacts", "portrait_preview.png")
    os.makedirs(os.path.dirname(preview_path), exist_ok=True)
    Image.fromarray(preview_rgb).save(preview_path)
    print(f"Превью портрета сохранено: {preview_path}")

    # 2. Сохранение сцены в каталог scenes/
    scene_json = os.path.join(project_root, "scenes", "portrait.json")
    scene_world = os.path.join(project_root, "scenes", "portrait.world")
    export_phyxel_scene(grid, width, height, scene_json, scene_world)

    # 3. Сохранение сцены в активный слот пользователя (%LOCALAPPDATA%\Phyxel)
    local_app_data = os.environ.get("LOCALAPPDATA", os.path.expanduser("~\\AppData\\Local"))
    user_phyxel_dir = os.path.join(local_app_data, "Phyxel")
    user_scene_json = os.path.join(user_phyxel_dir, "scene.json")
    user_scene_world = os.path.join(user_phyxel_dir, "scene.world")
    export_phyxel_scene(grid, width, height, user_scene_json, user_scene_world)
    print(f"Портрет установлен в активный слот сохранения игры: {user_scene_json}")

if __name__ == "__main__":
    main()
