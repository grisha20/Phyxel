"""PC GPU replay acceptance; run after the cases in the PC results document."""
import json
import math
from pathlib import Path
import struct
import sys

root = Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/furnace-collapse-20261007")
cell = struct.Struct("<IffffIIIfffffI")


def log(folder):
    raw = (root / folder / "run.log").read_bytes()
    try:
        return raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        return raw.decode("cp1251")  # Legacy redirected Windows console names.


def records(folder, prefix):
    return [json.loads(line[len(prefix):]) for line in log(folder).splitlines() if line.startswith(prefix)]


def samples(folder):
    return records(folder, "PHYXEL_SHELL_SAMPLE ")


assert "checks=12 failures=0" in log("confinement-probes")
assert "checks=43 failures=0" in log("jet")
assert "PHYXEL_SHELL_PROBES_PASS checks=76" in log("mechanisms-acceptance")
assert "pass=False PC01 exterior open draft does not crush a closed cold hull" in log("exterior-before")
assert "PHYXEL_UI_REGRESSION_SUCCESS" in log("ui-final-pass")
assert "PHYXEL_SCENE_FILES_SUCCESS checks=56" in log("scene-files")
edits = records("edit", "PHYXEL_PRESSURE_EDIT ")
assert len(edits) == 12 and all(r["pass"] for r in edits)

ordinary = []
for frame in (0, 30, 60, 120):
    for field in ("grid", "air", "motion"):
        name = f"furnace-Simulation-60-{frame}-{field}.bin"
        assert (root / "ordinary-before-recheck" / name).read_bytes() == (root / "ordinary-final-recheck" / name).read_bytes(), name
        ordinary.append(name)

long_runs = []
for folder in ("final-original", "final-sprinkle", "final-sandbox-sprinkle"):
    rows = samples(folder)
    first, last = rows[0], rows[-1]
    assert last["frame"] == 14400
    assert max(r["furnaceFragments"] for r in rows) - first["furnaceFragments"] <= 100
    assert last["furnaceIntact"] >= first["furnaceIntact"] * .95
    assert "PHYXEL_SHELL_ROUNDTRIP equal=True" in log(folder)
    if last["mode"] == "Simulation":
        assert last["fragments"] > first["fragments"]
    if folder != "final-original":
        portions = records(folder, "PHYXEL_SHELL_SPRINKLE ")
        assert len(portions) == 12 and sum(r["requested"] for r in portions) == 156
    # The still-working exhaust is measured separately from fracture counts.
    air = list(struct.iter_unpack("<ffff", (root / folder / f'shell-{last["mode"]}-60-14400-air.bin').read_bytes()))
    up = -sum(air[y * 168 + x][2] for y in range(12, 55) for x in range(40, 45)) / (43 * 5)
    left = -sum(air[y * 168 + x][1] for y in range(65, 68) for x in range(55, 115)) / (3 * 60)
    assert up > 1 and left > 1, (folder, up, left)
    long_runs.append({"folder": folder, "first": first, "last": last, "chimney_up": up, "under_boiler_left": left})

scene_cases = []
for folder, opened, enabled in (("closed", False, True), ("vent-final", True, True), ("closed-off-final", False, False)):
    rows = samples(folder)
    finals = [r for r in rows if r["frame"] == 2 * r["fps"]]
    assert len(finals) == 6
    for r in finals:
        assert r["powder"] <= 65.12
        assert (r["fragments"] > 0) == (enabled and not opened)
        if enabled and not opened:
            at_one = next(s for s in rows if s["mode"] == r["mode"] and s["fps"] == r["fps"] and s["frame"] == r["fps"])
            assert at_one["coolFragments"] > 0 and at_one["outside"] > 0
            stamp = f'shell-{r["mode"]}-{r["fps"]}-{r["fps"]}'
            width, height = struct.unpack_from(">II", (root / folder / (stamp + ".png")).read_bytes(), 16)
            grid = list(cell.iter_unpack((root / folder / (stamp + "-grid.bin")).read_bytes()))
            assert len(grid) == width * height
            sectors = [0] * 8
            for i, c in enumerate(grid):
                x, y = i % width - 224, i // width - 145
                if c[5] and c[0] == 13 and c[6] & 0x40000000 and math.hypot(x, y) > 50:
                    sectors[int((math.atan2(y, x) + math.pi) * 4 / math.pi) % 8] += 1
            assert all(sectors), (r, sectors)
            r["sectors_1s"] = sectors
    if opened or not enabled:
        accepted = records(folder, "PHYXEL_VENT_ACCEPTANCE ")
        assert len(accepted) == 6 and all(r["pass"] and r["peakFragments"] == 0 for r in accepted)
    scene_cases.append({"folder": folder, "cases": finals})

origins = records("final-original", "PHYXEL_FRACTURE_ORIGIN ")
roof = [r for r in origins if 280 < r["x"] < 365 and 95 < r["y"] < 126]
assert roof and roof[0]["temperature"] < 200 and roof[0]["vy"] < 0
birth = roof[0]["tick"]
flights = [r for r in records("final-original", "PHYXEL_FRAGMENT_FLIGHT ") if r["tick"] - r["age"] + 1 == birth]
assert flights and all(r["y"] < roof[0]["y"] for r in flights if r["age"] == 181)
concurrent = {name: (root / "ordinary-before" / name).read_bytes() == (root / "ordinary-final" / name).read_bytes()
              for name in ordinary}
report = {"ordinary_byte_checks": ordinary, "concurrent_byte_matches": concurrent,
          "long_runs": long_runs, "scene_cases": scene_cases,
          "first_roof_origin": roof[0], "first_roof_flight": flights,
          "baseline_before": samples("before-long")[-1], "baseline_sprinkle": samples("before-sprinkle")[-1],
          "legacy_baseline": samples("legacy-long")[-1]}
assert report["baseline_before"]["fragments"] > 5000
assert report["baseline_sprinkle"]["furnaceFragments"] > 5000
assert report["legacy_baseline"]["furnaceFragments"] > 5000
(root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("PC PASS: confinement12, jet43, mechanics76, edit12; 18 scene cases; 3 four-minute runs; ordinary12 bytes; UI and scene-save checks")
for r in long_runs:
    print(r["folder"], "lower fragments", r["first"]["furnaceFragments"], "->", r["last"]["furnaceFragments"],
          "intact", r["last"]["furnaceIntact"], "exhaust", r["chimney_up"], r["under_boiler_left"])
print("Cold roof:", roof[0], "3s flight:", [r for r in flights if r["age"] == 181])
