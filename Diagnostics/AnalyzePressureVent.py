"""Inspect PV GPU replay packets; does not edit any user scene.

python Diagnostics/AnalyzePressureVent.py artifacts/pressure-vent-20261007
"""
import json
import math
from pathlib import Path
import struct
import sys

root = Path(sys.argv[1])
cell = struct.Struct("<IffffIIIfffffI")
report = {"vent": [], "closed": [], "furnace_powder": [], "ordinary": []}


def palette(folder):
    return {s.split(":", 1)[1]: int(s.split(":", 1)[0])
            for s in (folder / "runtime-palette.txt").read_text(encoding="utf-8").splitlines()}


def packets(folder, prefix):
    width, height = struct.unpack_from(">II", (folder / (prefix + ".png")).read_bytes(), 16)
    cells = list(cell.iter_unpack((folder / (prefix + "-grid.bin")).read_bytes()))
    air = list(struct.iter_unpack("<ffff", (folder / (prefix + "-air.bin")).read_bytes()))
    assert len(cells) == width * height
    return width, cells, air


for case in ("vent", "closed", "powder"):
    folder = root / ("final-" + case)
    rows = json.loads((folder / "measurements.json").read_text(encoding="utf-8"))
    pal = palette(folder)
    gas_ids = {pal["core:" + name] for name in ("fire", "smoke", "co2", "steam")}
    acceptance = [json.loads(line.split(" ", 1)[1]) for line in
                  (folder / "run.log").read_text(encoding="utf-8").splitlines()
                  if line.startswith("PHYXEL_VENT_ACCEPTANCE ")]
    assert len(acceptance) == 6 and all(v["pass"] for v in acceptance)
    for result in acceptance:
        fps, mode = result["fps"], result["mode"]
        prefix = ("furnace" if case == "powder" else "shell") + f"-{mode}-{fps}"
        row = next(v for v in rows if v["mode"] == mode and v["fps"] == fps and v["frame"] == fps)
        width, cells, air = packets(folder, prefix + "-" + str(fps))
        final = next(v for v in rows if v["mode"] == mode and v["fps"] == fps and v["frame"] == 2 * fps)
        assert final["powder"] < (65.12 if case != "powder" else 4), final
        if case == "vent":
            assert result["peakFragments"] == 0
            escaped = sum(1 for i, c in enumerate(cells) if c[5] and c[0] in gas_ids
                          and i // width < 95 and 184 <= i % width <= 264)
            assert escaped > 0
            aw = (width + 3) // 4
            up = -sum(air[23 * aw + x][2] for x in range(51, 61)) / 10
            assert up > 1, (result, up)
            report["vent"].append({**result, "gas_above_outlet_1s": escaped,
                                   "outlet_up_1s": up, "wave_max_1s": row["waveMax"]})
        elif case == "closed":
            assert row["coolFragments"] > 0 and row["outside"] > 0
            sectors = [0] * 8
            for i, c in enumerate(cells):
                if (c[5] and c[0] == pal["core:metal"] and c[6] & 0x40000000
                        and math.hypot(i % width - 224, i // width - 145) > 50):
                    sectors[int((math.atan2(i // width - 145, i % width - 224) + math.pi) * 4 / math.pi) % 8] += 1
            assert all(sectors), (result, sectors)
            report["closed"].append({**result, "cold_1s": row["coolFragments"],
                                     "outside_1s": row["outside"], "outside_sectors_1s": sectors})
        else:
            assert result["peakFragments"] == 0
            report["furnace_powder"].append(result)

folder = root / "final-ordinary"
for frame in (0, 30, 60, 120):
    for field in ("grid", "air", "motion"):
        file = f"furnace-Simulation-60-{frame}-{field}.bin"
        equal = (root / "before-ordinary-bound" / file).read_bytes() == (folder / file).read_bytes()
        assert equal, file
        report["ordinary"].append({"frame": frame, "field": field, "equal": equal})

report["furnace_30s"] = []
folder = root / "final-powder-long"
for mode in ("Sandbox", "Simulation"):
    result = next(json.loads(s.split(" ", 1)[1]) for s in
                  (folder / "run.log").read_text(encoding="utf-8").splitlines()
                  if s.startswith("PHYXEL_VENT_ACCEPTANCE ") and f'"mode":"{mode}"' in s)
    assert result["peakFragments"] == 0 and result["pass"]
    width, _, air = packets(folder, f"furnace-{mode}-60-1800")
    aw = (width + 3) // 4
    up = -sum(air[y * aw + x][2] for y in range(12, 55) for x in range(40, 45)) / (43 * 5)
    left = -sum(air[y * aw + x][1] for y in range(65, 68) for x in range(55, 115)) / (3 * 60)
    assert up > 1 and left > 1
    report["furnace_30s"].append({**result, "chimney_up": up, "under_boiler_left": left})

ledger = (root / "final-reaction/run.log").read_text(encoding="utf-8")
assert "PHYXEL_VENT_LEDGER pressureDebt=0" in ledger
assert "PHYXEL_REACTION_PULSE_SUCCESS pressure=2.000000" in ledger
assert "PHYXEL_SHELL_PROBES_PASS checks=76" in (root / "final-probes/run.log").read_text(encoding="utf-8")
(root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("PV PASS: vent 6/6 no fragments; closed 6/6 eight sectors; furnace powder 6/6; ordinary 12/12 bytes; ledger")
