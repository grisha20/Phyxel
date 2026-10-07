"""Accept the targeted FJ GPU runs, including the saved boiler's first roof cohort.

python Diagnostics/AnalyzeFragmentJet.py artifacts/fragment-jet-20261007
Artifacts remain ignored; this script only reads them and writes a report there.
"""
from collections import Counter
import json
import math
from pathlib import Path
import struct
import sys

root = Path(sys.argv[1])
cell = struct.Struct("<IffffIIIfffffI")


def records(folder, prefix):
    return [json.loads(line[len(prefix):]) for line in
            (root / folder / "run.log").read_text(encoding="utf-8").splitlines()
            if line.startswith(prefix)]


def log(folder):
    return (root / folder / "run.log").read_text(encoding="utf-8")


assert "PHYXEL_FRAGMENT_JET_COMPLETE checks=35 failures=0" in log("final-probes")
assert "PHYXEL_SHELL_PROBES_PASS checks=76" in log("final-mechanisms")
assert "PHYXEL_FRAGMENT_JET_COMPLETE checks=32 failures=13" in log("before-probes")
ordinary = []
for frame in (0, 30, 60, 120):
    for field in ("grid", "air", "motion"):
        name = f"furnace-Simulation-60-{frame}-{field}.bin"
        assert (root / "matched-before-ordinary" / name).read_bytes() == (root / "matched-final-ordinary" / name).read_bytes(), name
        ordinary.append({"frame": frame, "field": field, "equal": True})

flight = records("final-roof", "PHYXEL_FRAGMENT_FLIGHT ")
cohorts = records("final-roof", "PHYXEL_FRAGMENT_COHORT ")
assert len(cohorts) == 8 and cohorts[0]["age"] == 1 and cohorts[-1]["age"] == 181
assert all(r["tracked"] == cohorts[0]["tracked"] for r in cohorts), "Cohort disappeared; investigate export/phase before accepting"
first = [r for r in flight if r["age"] == 1]
last = [r for r in flight if r["age"] == 181]
assert all(r["vy"] < 0 and r["temperature"] < 200 for r in first)
roof_y = min(r["y"] for r in first)
assert all(r["y"] < roof_y for r in last), "First cohort returned inside within 3 seconds"
origins = records("final-roof", "PHYXEL_FRACTURE_ORIGIN ")
roof = [r for r in origins if 280 < r["x"] < 365 and 95 < r["y"] < 126]
births = Counter(r["tick"] for r in roof)
final_tick = 4800
final_cells = list(cell.iter_unpack((root / "final-roof/shell-Simulation-60-4800-grid.bin").read_bytes()))
remaining = [i for i, c in enumerate(final_cells) if c[5] and c[6] & 0x40000000
             and final_tick - (c[6] & 0x3fffffff) + 1 in births]
report = {"ordinary": ordinary, "first_roof_flight": flight,
          "roof_origins": len(roof), "roof_origins_upward": sum(r["vy"] < 0 for r in roof),
          "roof_cohort_remaining_at_80s": len(remaining),
          "roof_cohort_above_original_roof_at_80s": sum(i // 672 < roof_y for i in remaining),
          "roof_cohort_in_water_region_at_80s": sum(207 <= i % 672 <= 472 and 215 <= i // 672 <= 241 for i in remaining)}

for folder, opened in (("final-vent", True), ("final-original", False), ("final-powder", True)):
    accepted = records(folder, "PHYXEL_VENT_ACCEPTANCE ")
    assert len(accepted) == 6 and all(r["pass"] for r in accepted)
    rows = records(folder, "PHYXEL_SHELL_SAMPLE ")
    palette = {s.split(":", 1)[1]: int(s.split(":", 1)[0]) for s in
        (root / folder / "runtime-palette.txt").read_text(encoding="utf-8").splitlines()}
    for r in accepted:
        final = next(s for s in rows if s["mode"] == r["mode"] and s["fps"] == r["fps"] and s["frame"] == 2 * r["fps"])
        assert final["powder"] < (4 if folder == "final-powder" else 65.12)
        if opened:
            assert r["peakFragments"] == 0
        else:
            sample = next(s for s in rows if s["mode"] == r["mode"] and s["fps"] == r["fps"] and s["frame"] == r["fps"])
            assert sample["coolFragments"] > 0 and sample["outside"] > 0
        if folder != "final-powder":
            stamp = f'shell-{r["mode"]}-{r["fps"]}-{r["fps"]}'
            width, height = struct.unpack_from(">II", (root / folder / (stamp + ".png")).read_bytes(), 16)
            cells = list(cell.iter_unpack((root / folder / (stamp + "-grid.bin")).read_bytes()))
            assert len(cells) == width * height
            if opened:
                gas_ids = {palette["core:" + name] for name in ("fire", "smoke", "co2", "steam")}
                assert any(c[5] and c[0] in gas_ids and i // width < 95 and 184 <= i % width <= 264
                           for i, c in enumerate(cells))
                air = list(struct.iter_unpack("<ffff", (root / folder / (stamp + "-air.bin")).read_bytes()))
                aw = (width + 3) // 4
                assert -sum(air[23 * aw + x][2] for x in range(51, 61)) / 10 > 1
            else:
                sectors = [0] * 8
                for i, c in enumerate(cells):
                    x, y = i % width - 224, i // width - 145
                    if c[5] and c[0] == palette["core:metal"] and c[6] & 0x40000000 and math.hypot(x, y) > 50:
                        sectors[int((math.atan2(y, x) + math.pi) * 4 / math.pi) % 8] += 1
                assert all(sectors), (r, sectors)
                r["outside_sectors_1s"] = sectors
    report[folder] = accepted

edits = records("final-edits", "PHYXEL_PRESSURE_EDIT ")
assert len(edits) == 12 and all(r["pass"] and r["initial"] == 0 and r["peak"] == 0 and r["loaded"] > 0 for r in edits)
sandbox = records("final-sandbox", "PHYXEL_SHELL_SAMPLE ")
assert sandbox[-1]["mode"] == "Sandbox" and sandbox[-1]["frame"] == 4800
report["sandbox"] = sandbox
assert "PHYXEL_SHELL_ROUNDTRIP equal=True" in log("final-boiler-save")
long_runs = records("final-powder-long", "PHYXEL_VENT_ACCEPTANCE ")
assert len(long_runs) == 2 and all(r["pass"] and r["peakFragments"] == 0 for r in long_runs)
report["furnace_30s"] = []
for r in long_runs:
    air = list(struct.iter_unpack("<ffff", (root / "final-powder-long" /
        f'furnace-{r["mode"]}-60-1800-air.bin').read_bytes()))
    up = -sum(air[y * 168 + x][2] for y in range(12, 55) for x in range(40, 45)) / (43 * 5)
    left = -sum(air[y * 168 + x][1] for y in range(65, 68) for x in range(55, 115)) / (3 * 60)
    assert up > 1 and left > 1
    report["furnace_30s"].append({**r, "chimney_up": up, "under_boiler_left": left})
(root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("FJ PASS: 35 new GPU checks; 76 pressure checks; 12 edit controls; 18 scene cases; ordinary 12 byte comparisons")
print("Roof first/highest/3s y:", roof_y, min(r["y"] for r in flight), [r["y"] for r in last])
print("Roof remaining/above/in water region at 80s:", len(remaining), report["roof_cohort_above_original_roof_at_80s"], report["roof_cohort_in_water_region_at_80s"])
