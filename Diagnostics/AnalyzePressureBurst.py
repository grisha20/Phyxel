"""Inspect saved GPU packets from PB; no simulation or user saves are modified.

python Diagnostics/AnalyzePressureBurst.py artifacts/pressure-burst-20261007
"""
import json
import math
from pathlib import Path
import struct
import sys

root = Path(sys.argv[1])
matrix = root / "acceptance-synced"
rows = json.loads((matrix / "measurements.json").read_text(encoding="utf-8"))
palette = {line.split(":", 1)[1]: int(line.split(":", 1)[0])
           for line in (matrix / "runtime-palette.txt").read_text(encoding="utf-8").splitlines()}
metal = palette["core:metal"]
water = palette["core:water"]
solids = {palette["core:" + name] for name in ("metal", "steel", "cast_iron", "copper", "stone")}
cell = struct.Struct("<IffffIIIfffffI")
report = {"shell": [], "furnace_short": [], "furnace_long": {}}
for row in rows:
    if row["scene"] != "Питарда" or row["frame"] != row["fps"]:
        continue
    prefix = f'shell-{row["mode"]}-{row["fps"]}-{row["frame"]}'
    width, _ = struct.unpack_from(">II", (matrix / (prefix + ".png")).read_bytes(), 16)
    cells = list(cell.iter_unpack((matrix / (prefix + "-grid.bin")).read_bytes()))
    sectors = [0] * 8
    intact = 0
    for i, c in enumerate(cells):
        if not c[5] or c[0] != metal:
            continue
        if not c[6] & 0x40000000:
            intact += 1
            continue
        x, y = i % width - 224, i // width - 145
        if abs(x) > 50 or abs(y) > 50:
            sectors[int((math.atan2(y, x) + math.pi) * 4 / math.pi) % 8] += 1
    assert intact == 0 and all(sectors), (row, intact, sectors)
    assert row["coolFragments"] > 0 and row["lowerPowder"] < 1650, row
    final = next(v for v in rows if v["scene"] == row["scene"]
                 and v["mode"] == row["mode"] and v["fps"] == row["fps"]
                 and v["frame"] == 2 * row["fps"])
    assert final["powder"] < 65.12, final
    report["shell"].append({**row, "intact": intact, "outside_sectors": sectors})
assert len(report["shell"]) == 6

for frame in (0, 30, 60, 120):
    for field in ("grid", "air", "motion"):
        file = f"furnace-Simulation-60-{frame}-{field}.bin"
        equal = (root / "before" / file).read_bytes() == (matrix / file).read_bytes()
        assert equal, file
        report["furnace_short"].append({"frame": frame, "field": field, "equal": equal})

for frame in (0, 1800):
    prefix = f"furnace-Simulation-60-{frame}"
    folder = root / "furnace-long"
    width, _ = struct.unpack_from(">II", (folder / (prefix + ".png")).read_bytes(), 16)
    cells = list(cell.iter_unpack((folder / (prefix + "-grid.bin")).read_bytes()))
    air = list(struct.iter_unpack("<ffff", (folder / (prefix + "-air.bin")).read_bytes()))
    aw = (width + 3) // 4
    mean = lambda values: sum(values) / len(values)
    metrics = {
        "chimney_up": -mean([air[y * aw + x][2] for y in range(12, 55) for x in range(40, 45)]),
        "under_boiler_left": -mean([air[y * aw + x][1] for y in range(65, 68) for x in range(55, 115)]),
        "floor_temperature": mean([c[8] for i, c in enumerate(cells) if c[5] and c[0] in solids
                                   and 210 <= i % width <= 475 and 247 <= i // width <= 257]),
        "water_temperature": mean([c[8] for c in cells if c[5] and c[0] == water]),
        "fragments": sum(1 for c in cells if c[5] and c[0] in solids and c[6] & 0x40000000),
    }
    assert metrics["fragments"] == 0
    if frame:
        assert metrics["chimney_up"] > 1 and metrics["under_boiler_left"] > 1
    report["furnace_long"][str(frame)] = metrics

(root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("PB analysis PASS: shell 6/6, eight sectors, intact=0; furnace bytes 12/12; draft 30s")
