#!/usr/bin/env python3
"""
Скрипт генерации сцены с 4-цилиндровым двигателем внутреннего сгорания (ДВС)
в продольном разрезе из металла (core:metal) для игры Phyxel.
Архитектура: Рядный 4-цилиндровый 4-тактный двигатель DOHC,
плоский коленвал 180° (цилиндры 1 и 4 в ВМТ, цилиндры 2 и 3 в НМТ).
"""

import os
import sys
import json
import struct
import math
from datetime import datetime, timezone
from PIL import Image

def build_engine_scene():
    width = 480
    height = 270
    grid = [[0 for _ in range(width)] for _ in range(height)]

    def set_pixel(x, y, val=1):
        if 0 <= x < width and 0 <= y < height:
            grid[y][x] = val

    def fill_rect(x1, y1, x2, y2, val=1):
        xa, xb = max(0, min(x1, x2)), min(width - 1, max(x1, x2))
        ya, yb = max(0, min(y1, y2)), min(height - 1, max(y1, y2))
        for y in range(ya, yb + 1):
            for x in range(xa, xb + 1):
                grid[y][x] = val

    def draw_rect(x1, y1, x2, y2, val=1):
        xa, xb = max(0, min(x1, x2)), min(width - 1, max(x1, x2))
        ya, yb = max(0, min(y1, y2)), min(height - 1, max(y1, y2))
        for x in range(xa, xb + 1):
            grid[ya][x] = val
            grid[yb][x] = val
        for y in range(ya, yb + 1):
            grid[y][xa] = val
            grid[y][xb] = val

    def draw_line(x1, y1, x2, y2, val=1):
        dx = abs(x2 - x1)
        dy = abs(y2 - y1)
        sx = 1 if x1 < x2 else -1
        sy = 1 if y1 < y2 else -1
        err = dx - dy
        cx, cy = x1, y1
        while True:
            set_pixel(cx, cy, val)
            if cx == x2 and cy == y2:
                break
            e2 = 2 * err
            if e2 > -dy:
                err -= dy
                cx += sx
            if e2 < dx:
                err += dx
                cy += sy

    def fill_circle(cx, cy, r, val=1):
        for y in range(max(0, cy - r), min(height, cy + r + 1)):
            for x in range(max(0, cx - r), min(width, cx + r + 1)):
                if (x - cx) ** 2 + (y - cy) ** 2 <= r ** 2:
                    grid[y][x] = val

    def draw_circle(cx, cy, r, val=1):
        for deg in range(0, 360, 2):
            rad = math.radians(deg)
            x = int(round(cx + r * math.cos(rad)))
            y = int(round(cy + r * math.sin(rad)))
            set_pixel(x, y, val)

    # -------------------------------------------------------------
    # ОСНОВНЫЕ ПАРАМЕТРЫ ДВИГАТЕЛЯ
    # -------------------------------------------------------------
    cyl_centers = [94, 168, 242, 316]  # Шаг цилиндров = 74 px
    bore_w = 44                        # Диаметр цилиндра (расточка гильзы)
    bore_half = bore_w // 2            # 22 px
    crank_y = 186                      # Ось коленчатого вала
    stroke_r = 22                      # Радиус кривошипа (ход = 44 px)

    piston_tdc_y = 78                  # Днище поршня в ВМТ
    piston_bdc_y = 122                 # Днище поршня в НМТ
    piston_h = 28                      # Высота поршня
    piston_w = bore_w - 2              # 42 px (зазор 1 px с каждой стороны)

    # =============================================================
    # 1. КЛАПАННАЯ КРЫШКА И DOHC (ДВА РАСПРЕДВАЛА)
    # =============================================================
    # Контур клапанной крышки: y = 16..36, x = 46..358
    fill_rect(46, 16, 358, 19, 1)  # верхняя горизонталь
    fill_rect(46, 19, 49, 36, 1)   # левая стенка
    fill_rect(355, 19, 358, 36, 1) # правая стенка

    # Ребра жесткости на клапанной крышке:
    for rx in [131, 205, 279]:
        fill_rect(rx - 2, 14, rx + 2, 16, 1)

    # Маслозаливная горловина с резьбовой крышкой:
    fill_rect(86, 12, 102, 15, 1)
    fill_rect(82, 9, 106, 11, 1) # ручка крышки
    fill_rect(80, 10, 82, 11, 1)
    fill_rect(106, 10, 108, 11, 1)

    # Два распределительных вала DOHC:
    # Вал впускных клапанов (y = 26..28) и вал выпускных клапанов (y = 26..28):
    fill_rect(42, 26, 354, 28, 1) # общая шина распредвалов в разрезе

    # Кулачки распредвалов и толкатели над каждым клапаном:
    for c in cyl_centers:
        in_valve_x = c - 11
        ex_valve_x = c + 11

        # Впускной кулачок:
        fill_rect(in_valve_x - 3, 23, in_valve_x + 3, 26, 1)
        fill_rect(in_valve_x - 2, 22, in_valve_x + 2, 23, 1)
        # Стаканчик толкателя (Bucket tappet):
        fill_rect(in_valve_x - 4, 29, in_valve_x + 4, 33, 1)

        # Выпускной кулачок:
        fill_rect(ex_valve_x - 3, 23, ex_valve_x + 3, 26, 1)
        fill_rect(ex_valve_x - 2, 22, ex_valve_x + 2, 23, 1)
        # Толкатель:
        fill_rect(ex_valve_x - 4, 29, ex_valve_x + 4, 33, 1)

    # Опоры (бугели) распредвалов:
    for bx in [52, 126, 200, 274, 348]:
        fill_rect(bx - 3, 29, bx + 3, 36, 1)

    # =============================================================
    # 2. ГОЛОВКА БЛОКА ЦИЛИНДРОВ (ГБЦ)
    # =============================================================
    # Верхняя плоскость ГБЦ: y = 37..39
    fill_rect(46, 37, 358, 39, 1)
    fill_rect(46, 39, 50, 74, 1)  # передний торец ГБЦ
    fill_rect(354, 39, 358, 74, 1)# задний торец ГБЦ

    # Межцилиндровые перегородки ГБЦ с каналами охлаждения:
    for i in range(len(cyl_centers) - 1):
        mid = (cyl_centers[i] + cyl_centers[i + 1]) // 2
        fill_rect(mid - 8, 40, mid + 8, 74, 1)
        # Канал охлаждающей жидкости в ГБЦ:
        fill_rect(mid - 4, 46, mid + 4, 60, 0)
        draw_rect(mid - 4, 46, mid + 4, 60, 1)
        # Шпилька / болт ГБЦ по центру:
        fill_rect(mid - 1, 40, mid + 1, 74, 1)

    # Камеры сгорания, клапаны, направляющие и свечи:
    for c in cyl_centers:
        in_x = c - 11
        ex_x = c + 11

        # Купольный свод камеры сгорания (pent-roof):
        # Заливаем массив металла ГБЦ выше свода
        for dx in range(-bore_half, bore_half + 1):
            roof_y = 65 + int(9 * (abs(dx) / bore_half) ** 1.3)
            fill_rect(c + dx, 40, c + dx, roof_y, 1)

        # Впускной канал (Intake port):
        fill_rect(c - 17, 48, c - 5, 63, 0)
        # Направляющая втулка впускного клапана:
        fill_rect(in_x - 2, 41, in_x + 2, 53, 1)
        # Шток клапана:
        fill_rect(in_x - 1, 33, in_x + 1, 66, 1)
        # Пружина клапана (витки):
        for sy in range(41, 53, 2):
            draw_line(in_x - 5, sy, in_x + 5, sy + 1, 1)
        # Тарелка клапана (Valve head) и седло:
        fill_rect(in_x - 6, 65, in_x + 6, 67, 1)

        # Выпускной канал (Exhaust port):
        fill_rect(c + 5, 48, c + 17, 63, 0)
        # Направляющая втулка выпускного клапана:
        fill_rect(ex_x - 2, 41, ex_x + 2, 53, 1)
        # Шток клапана:
        fill_rect(ex_x - 1, 33, ex_x + 1, 66, 1)
        # Пружина клапана:
        for sy in range(41, 53, 2):
            draw_line(ex_x - 5, sy, ex_x + 5, sy + 1, 1)
        # Тарелка клапана и седло:
        fill_rect(ex_x - 6, 65, ex_x + 6, 67, 1)

        # Свеча зажигания (Spark plug) строго по центру:
        # Керамический изолятор свечи с ребрами:
        fill_rect(c - 2, 28, c + 2, 54, 1)
        set_pixel(c - 3, 35, 1)
        set_pixel(c + 3, 35, 1)
        set_pixel(c - 3, 42, 1)
        set_pixel(c + 3, 42, 1)
        # Металлический шестигранник и резьба свечи:
        fill_rect(c - 4, 55, c + 4, 63, 1)
        # Центральный электрод:
        draw_line(c, 64, c, 67, 1)
        # Боковой электрод (L-образный загиб):
        draw_line(c + 2, 63, c + 2, 68, 1)
        draw_line(c + 1, 68, c, 68, 1)

    # Прокладка ГБЦ (Head Gasket): y = 74..75
    fill_rect(46, 74, 358, 75, 1)
    for c in cyl_centers:
        fill_rect(c - bore_half, 74, c + bore_half, 75, 0)

    # =============================================================
    # 3. БЛОК ЦИЛИНДРОВ (CYLINDER BLOCK)
    # =============================================================
    # Верхняя плита блока (Deck):
    fill_rect(46, 76, 358, 78, 1)
    for c in cyl_centers:
        fill_rect(c - bore_half, 76, c + bore_half, 78, 0)

    # Внешние стенки блока:
    fill_rect(46, 78, 52, 172, 1)   # передняя
    fill_rect(352, 78, 358, 172, 1) # задняя

    # Гильзы цилиндров (Cylinder liners): толщина 4 px
    for c in cyl_centers:
        # Левая стенка гильзы: x in [c - 26, c - 23]
        fill_rect(c - bore_half - 4, 78, c - bore_half - 1, 168, 1)
        # Правая стенка гильзы: x in [c + 23, c + 26]
        fill_rect(c + bore_half + 1, 78, c + bore_half + 4, 168, 1)

    # Рубашка охлаждения (Water Jackets) в верхней половине блока (y = 80..132):
    # Передний контур:
    fill_rect(53, 82, 66, 132, 0)
    draw_rect(53, 82, 66, 132, 1)
    # Задний контур:
    fill_rect(340, 82, 351, 132, 0)
    draw_rect(340, 82, 351, 132, 1)

    # Межцилиндровые перегородки блока:
    for i in range(len(cyl_centers) - 1):
        c1 = cyl_centers[i]
        c2 = cyl_centers[i + 1]
        x_left = c1 + bore_half + 5
        x_right = c2 - bore_half - 5
        # Массивный металл перегородки:
        fill_rect(x_left, 78, x_right, 172, 1)
        # Полость охлаждения в верхней части перегородки:
        fill_rect(x_left + 2, 82, x_right - 2, 132, 0)
        draw_rect(x_left + 2, 82, x_right - 2, 132, 1)
        # Канал смазки / слива масла в нижней части перегородки (y = 138..166):
        fill_rect(x_left + 5, 138, x_right - 5, 166, 0)
        draw_rect(x_left + 5, 138, x_right - 5, 166, 1)

    # =============================================================
    # 4. ПОРШНЕВАЯ ГРУППА (PISTONS)
    # =============================================================
    # Порядок работы:
    # Цилиндры 1 и 4 — ВМТ (TDC): днище на y = 78
    # Цилиндры 2 и 3 — НМТ (BDC): днище на y = 122
    for idx, c in enumerate(cyl_centers):
        is_tdc = (idx == 0 or idx == 3)
        py = piston_tdc_y if is_tdc else piston_bdc_y
        px1 = c - piston_w // 2
        px2 = c + piston_w // 2

        # Днище поршня (Crown) толщиной 5 px:
        fill_rect(px1, py, px2, py + 4, 1)
        # Выемки под впускной и выпускной клапаны в днище поршня:
        fill_rect(c - 14, py, c - 7, py + 1, 0)
        fill_rect(c + 7, py, c + 14, py + 1, 0)

        # Огневой пояс и 3 канавки под поршневые кольца:
        fill_rect(px1, py + 5, px2, py + 12, 1)
        # 1. Верхнее компрессионное кольцо:
        fill_rect(px1, py + 6, px1 + 2, py + 6, 0)
        fill_rect(px2 - 2, py + 6, px2, py + 6, 0)
        # 2. Второе компрессионное кольцо:
        fill_rect(px1, py + 8, px1 + 2, py + 8, 0)
        fill_rect(px2 - 2, py + 8, px2, py + 8, 0)
        # 3. Маслосъемное кольцо:
        fill_rect(px1, py + 10, px1 + 2, py + 10, 0)
        fill_rect(px2 - 2, py + 10, px2, py + 10, 0)

        # Юбка поршня (Piston skirt):
        # Наружные стенки юбки:
        fill_rect(px1, py + 13, px1 + 3, py + piston_h, 1)
        fill_rect(px2 - 3, py + 13, px2, py + piston_h, 1)
        # Фигурный нижний срез юбки (для свободного прохода шатуна и противовесов):
        for cut_x in range(px1, px1 + 4):
            set_pixel(cut_x, py + piston_h, 0)
        for cut_x in range(px2 - 3, px2 + 1):
            set_pixel(cut_x, py + piston_h, 0)

        # Болванка бобышки поршневого пальца (Wrist pin boss):
        pin_y = py + 15
        fill_rect(c - 8, pin_y - 4, c + 8, pin_y + 4, 1)
        # Отверстие бобышки:
        fill_circle(c, pin_y, 4, 0)
        draw_circle(c, pin_y, 4, 1)
        # Поршневой палец (полый стальной цилиндр):
        fill_circle(c, pin_y, 3, 1)
        set_pixel(c, pin_y, 0) # полость пальца

    # =============================================================
    # 5. ШАТУНЫ (CONNECTING RODS)
    # =============================================================
    # Цилиндры 1 и 4: кривошип ВВЕРХ (crankpin_y = 186 - 22 = 164)
    # Цилиндры 2 и 3: кривошип ВНИЗ (crankpin_y = 186 + 22 = 208)
    for idx, c in enumerate(cyl_centers):
        is_tdc = (idx == 0 or idx == 3)
        pin_y = (piston_tdc_y if is_tdc else piston_bdc_y) + 15
        crankpin_y = (crank_y - stroke_r) if is_tdc else (crank_y + stroke_r)

        # Верхняя головка шатуна (Small end) вокруг пальца:
        draw_circle(c, pin_y, 6, 1)
        fill_circle(c, pin_y, 4, 1)
        fill_circle(c, pin_y, 3, 0) # отверстие пальца

        # Стержень шатуна (I-beam rod shank):
        rod_top = pin_y + 6
        rod_bottom = crankpin_y - 8
        fill_rect(c - 3, rod_top, c + 3, rod_bottom, 1)
        # Двутавровая выемка облегчения:
        fill_rect(c - 1, rod_top + 3, c + 1, rod_bottom - 3, 0)

        # Нижняя головка шатуна (Big end) с крышкой:
        fill_circle(c, crankpin_y, 8, 1)
        # Вкладыш шатунного подшипника:
        draw_circle(c, crankpin_y, 5, 0)
        # Сама шейка коленвала:
        fill_circle(c, crankpin_y, 4, 1)
        set_pixel(c, crankpin_y, 0) # канал подачи смазки

        # Разъем крышки шатуна и болты:
        draw_line(c - 9, crankpin_y, c + 9, crankpin_y, 1)
        # Болты шатуна слева и справа:
        fill_rect(c - 7, crankpin_y - 6, c - 6, crankpin_y + 6, 1)
        fill_rect(c + 6, crankpin_y - 6, c + 7, crankpin_y + 6, 1)
        # Гайки / головки болтов:
        set_pixel(c - 7, crankpin_y + 7, 1)
        set_pixel(c - 6, crankpin_y + 7, 1)
        set_pixel(c + 6, crankpin_y + 7, 1)
        set_pixel(c + 7, crankpin_y + 7, 1)

    # =============================================================
    # 6. КОЛЕНЧАТЫЙ ВАЛ (CRANKSHAFT), ОПОРЫ И ПРОТИВОВЕСЫ
    # =============================================================
    # 5 коренных подшипников (Main bearings):
    main_bearings_x = [56, 131, 205, 279, 350]
    for mx in main_bearings_x:
        # Верхняя постель в блоке: y = 172..186
        fill_rect(mx - 8, 172, mx + 8, 185, 1)
        # Нижняя съемная крышка (Main bearing cap): y = 186..204
        fill_rect(mx - 9, 186, mx + 9, 204, 1)
        # Мощные коренные болты крышки:
        fill_rect(mx - 7, 180, mx - 6, 208, 1)
        fill_rect(mx + 6, 180, mx + 6, 208, 1)
        # Головки болтов коренной крышки:
        set_pixel(mx - 7, 209, 1)
        set_pixel(mx - 6, 209, 1)
        set_pixel(mx + 6, 209, 1)
        set_pixel(mx + 7, 209, 1)

        # Расточка подшипника и коренная шейка коленвала:
        fill_circle(mx, crank_y, 6, 0)
        fill_circle(mx, crank_y, 5, 1)
        set_pixel(mx, crank_y, 0) # масляный канал

    # Щеки коленвала (Crank webs) и противовесы (Counterweights):
    # Размещаются СЛЕВА и СПРАВА от каждого шатуна, чтобы не мешать его движению!
    for idx, c in enumerate(cyl_centers):
        is_tdc = (idx == 0 or idx == 3)
        crankpin_y = (crank_y - stroke_r) if is_tdc else (crank_y + stroke_r)

        # Щека слева от шатуна: x in [c - 20, c - 10]
        # Щека справа от шатуна: x in [c + 10, c + 20]
        left_web_x1, left_web_x2 = c - 20, c - 10
        right_web_x1, right_web_x2 = c + 10, c + 20

        # Соединяем ось вала (crank_y) с шатунной шейкой (crankpin_y):
        fill_rect(left_web_x1, min(crank_y, crankpin_y), left_web_x2, max(crank_y, crankpin_y), 1)
        fill_rect(right_web_x1, min(crank_y, crankpin_y), right_web_x2, max(crank_y, crankpin_y), 1)

        if is_tdc:
            # Для цилиндров 1 и 4 (шатун ВВЕРХ) — противовесы направлены ВНИЗ:
            cw_y1 = crank_y + 4
            cw_y2 = crank_y + 28
            # Левый противовес:
            fill_rect(left_web_x1 - 2, cw_y1, left_web_x2 + 2, cw_y2, 1)
            # Скругление низа левого противовеса:
            set_pixel(left_web_x1 - 2, cw_y2, 0)
            set_pixel(left_web_x2 + 2, cw_y2, 0)

            # Правый противовес:
            fill_rect(right_web_x1 - 2, cw_y1, right_web_x2 + 2, cw_y2, 1)
            # Скругление низа правого противовеса:
            set_pixel(right_web_x1 - 2, cw_y2, 0)
            set_pixel(right_web_x2 + 2, cw_y2, 0)
        else:
            # Для цилиндров 2 и 3 (шатун ВНИЗ) — противовесы направлены ВВЕРХ:
            cw_y1 = crank_y - 28
            cw_y2 = crank_y - 4
            # Левый противовес:
            fill_rect(left_web_x1 - 2, cw_y1, left_web_x2 + 2, cw_y2, 1)
            # Скругление верха левого противовеса:
            set_pixel(left_web_x1 - 2, cw_y1, 0)
            set_pixel(left_web_x2 + 2, cw_y1, 0)

            # Правый противовес:
            fill_rect(right_web_x1 - 2, cw_y1, right_web_x2 + 2, cw_y2, 1)
            # Скругление верха правого противовеса:
            set_pixel(right_web_x1 - 2, cw_y1, 0)
            set_pixel(right_web_x2 + 2, cw_y1, 0)

    # =============================================================
    # 7. МАСЛЯНЫЙ КАРТЕР И СИСТЕМА СМАЗКИ (OIL PAN & SUMP)
    # =============================================================
    # Нижняя юбка картера блока:
    fill_rect(46, 172, 52, 230, 1)
    fill_rect(352, 172, 358, 230, 1)

    # Привалочный фланец картера: y = 229..232
    fill_rect(44, 229, 360, 232, 1)

    # Стенки масляного поддона:
    # 1. Пологий спуск слева от (48, 232) до (145, 255):
    for x in range(48, 146):
        t = (x - 48) / (145 - 48)
        y = int(232 + t * (255 - 232))
        fill_rect(x, y, x, y + 2, 1)

    # 2. Глубокая ванна маслосборника: x in [145, 275], y in [255, 257]
    fill_rect(145, 255, 275, 257, 1)

    # 3. Подъем справа от (275, 255) до (356, 232):
    for x in range(275, 357):
        t = (x - 275) / (356 - 275)
        y = int(255 - t * (255 - 232))
        fill_rect(x, y, x, y + 2, 1)

    # Сливная пробка поддона с шестигранной головкой:
    fill_rect(266, 257, 274, 261, 1)
    fill_rect(268, 261, 272, 264, 1)

    # Маслоуспокоительная перфорированная перегородка (baffle plate):
    for bx in range(60, 342, 6):
        fill_rect(bx, 236, bx + 4, 237, 1)

    # Маслоприемник (Oil pickup tube):
    # Опускается от масляного насоса блока в самую глубокую точку поддона:
    fill_rect(180, 204, 183, 250, 1)
    # Воронка / сетка заборника масла:
    fill_rect(172, 250, 191, 253, 1)
    fill_rect(170, 253, 193, 254, 1) # перфорированная сетка

    # Масляный щуп (Dipstick tube) справа:
    draw_line(344, 60, 340, 246, 1)
    # Кольцо ручки щупа сверху:
    draw_circle(344, 56, 3, 1)

    # =============================================================
    # 8. ПРИВОД ГРМ (СЛЕВА / СПЕРЕДИ)
    # =============================================================
    # Ведущий зубчатый шкив коленвала:
    fill_circle(44, crank_y, 8, 1)
    fill_circle(44, crank_y, 4, 0)
    fill_circle(44, crank_y, 2, 1)

    # Ведомый шкив распредвала (DOHC pulley) вверху:
    fill_circle(44, 27, 10, 1)
    fill_circle(44, 27, 6, 0)
    fill_circle(44, 27, 2, 1)
    # Спицы шкива:
    draw_line(35, 27, 53, 27, 1)
    draw_line(44, 18, 44, 36, 1)

    # Ремень привода ГРМ:
    fill_rect(34, 27, 36, crank_y, 1) # левая натянутая ветвь
    fill_rect(52, 33, 54, 76, 1)     # правая верхняя ветвь
    fill_rect(52, 136, 54, crank_y, 1) # правая нижняя ветвь

    # Натяжной ролик ремня ГРМ (Tensioner pulley):
    fill_circle(49, 106, 6, 1)
    fill_circle(49, 106, 3, 0)
    fill_circle(49, 106, 1, 1)

    # Масляный насос на носке коленвала:
    fill_rect(46, 178, 54, 194, 1)
    fill_circle(50, 186, 5, 0)
    fill_circle(50, 186, 3, 1)

    # =============================================================
    # 9. МАХОВИК И СЦЕПЛЕНИЕ (СПРАВА / СЗАДИ)
    # =============================================================
    # Задний фланец коленвала и сальник:
    fill_rect(356, crank_y - 7, 362, crank_y + 7, 1)

    # Маховик (Flywheel): массивный стальной диск x in [363, 375], y in [146, 226]
    fill_rect(363, 146, 375, 226, 1)
    # Рабочая поверхность сцепления и углубление:
    fill_rect(367, 158, 375, 214, 0)
    fill_rect(370, 160, 375, 212, 1)

    # Зубчатый венец стартера (Ring gear teeth):
    for ty in range(146, 227, 2):
        set_pixel(376, ty, 1)
        set_pixel(377, ty, 1)

    # Стартер (Starter motor) закрепленный на блоке сверху:
    fill_rect(348, 128, 368, 142, 1)
    # Бендикс стартера, зацепленный за венец маховика:
    fill_rect(368, 132, 376, 140, 1)

    return grid, width, height

def export_scene(grid, width, height, output_json_path, output_world_path):
    os.makedirs(os.path.dirname(os.path.abspath(output_json_path)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(output_world_path)), exist_ok=True)

    # 1. Запись scene.json
    scene_data = {
        "Version": 19,
        "Scale": 0.25,
        "Gravity": 980.0,
        "BrushRadius": 18,
        "SpawnDensity": 0.82,
        "SolidGravity": False,
        "SelectedMaterialId": "core:metal",
        "SavedAt": datetime.now(timezone.utc).isoformat(),
        "HydraulicPressure": False,
        "PressureDestruction": False,
        "TemperatureSensors": [],
        "FilterSelection": 0,
        "OpenBoundaries": True,
        "Mode": "Sandbox",
        "MaterialPalette": [
            "core:empty",
            "core:metal"
        ]
    }

    with open(output_json_path, "w", encoding="utf-8") as f:
        json.dump(scene_data, f, indent=2, ensure_ascii=False)

    # 2. Запись scene.world (версия 19)
    magic = 0x5058594C
    version = 19
    cell_stride = 56
    grid_len = width * height * cell_stride
    oxidizer_len = 0

    header = struct.pack("<IIiiiii", magic, version, width, height, cell_stride, grid_len, oxidizer_len)

    empty_cell = struct.pack("<IffffIIIfffffI", 0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0)
    metal_cell = struct.pack("<IffffIIIfffffI", 1, 7.8, 0.0, 0.0, 0.0, 1, 0, 0, 30.0, 0.0, 0.0, 0.0, 0.0, 0)

    cell_bytes = bytearray()
    metal_count = 0
    for y in range(height):
        for x in range(width):
            if grid[y][x] == 1:
                cell_bytes.extend(metal_cell)
                metal_count += 1
            else:
                cell_bytes.extend(empty_cell)

    with open(output_world_path, "wb") as f:
        f.write(header)
        f.write(cell_bytes)
        # heatLength (4 байта = 0)
        f.write(struct.pack("<i", 0))
        # 4 секции: pending, pulse, air, motion (по 4 байта = 0)
        f.write(struct.pack("<iiii", 0, 0, 0, 0))

    print(f"Экспортировано {metal_count} пикселей металла в сцену {width}x{height}.")
    print(f"JSON: {output_json_path}")
    print(f"WORLD: {output_world_path}")

def export_preview_png(grid, width, height, output_png_path, scale=3):
    img = Image.new("RGB", (width * scale, height * scale), (20, 24, 28))
    pixels = img.load()

    metal_color = (142, 156, 166)
    metal_highlight = (185, 200, 210)
    metal_shadow = (95, 107, 115)

    for y in range(height):
        for x in range(width):
            if grid[y][x] == 1:
                top_free = (y > 0 and grid[y - 1][x] == 0)
                bottom_free = (y < height - 1 and grid[y + 1][x] == 0)
                col = metal_highlight if top_free else (metal_shadow if bottom_free else metal_color)

                for sy in range(scale):
                    for sx in range(scale):
                        pixels[x * scale + sx, y * scale + sy] = col

    os.makedirs(os.path.dirname(os.path.abspath(output_png_path)), exist_ok=True)
    img.save(output_png_path)
    print(f"Превью сохранено: {output_png_path} ({width*scale}x{height*scale})")

def main():
    print("Генерация 4-цилиндрового ДВС из металла...")
    grid, w, h = build_engine_scene()

    # 1. Сохраняем в папку проекта `scenes/inline4_engine.json` и `.world`
    project_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    scene_json = os.path.join(project_root, "scenes", "inline4_engine.json")
    scene_world = os.path.join(project_root, "scenes", "inline4_engine.world")
    export_scene(grid, w, h, scene_json, scene_world)

    # 2. Сохраняем также в пользовательский слот сохранения Phyxel в LocalAppData:
    local_app_data = os.environ.get("LOCALAPPDATA", os.path.expanduser("~\\AppData\\Local"))
    user_phyxel_dir = os.path.join(local_app_data, "Phyxel")
    user_scene_json = os.path.join(user_phyxel_dir, "scene.json")
    user_scene_world = os.path.join(user_phyxel_dir, "scene.world")
    export_scene(grid, w, h, user_scene_json, user_scene_world)

    # 3. Сохраняем изображение-превью в artifacts
    preview_png = os.path.join(project_root, "artifacts", "inline4_engine_preview.png")
    export_preview_png(grid, w, h, preview_png, scale=3)

if __name__ == "__main__":
    main()
