"""Selected RS acceptance and explicit exceptions; ignored GPU artifacts required."""
import csv
import json
import struct
import sys
from pathlib import Path

root = Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/pressure-reliability-20261008")


def log(folder):
    raw = (root / folder / "run.log").read_bytes()
    try:
        return raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        return raw.decode("cp1251")


def records(folder, prefix):
    return [json.loads(line[len(prefix):]) for line in log(folder).splitlines() if line.startswith(prefix)]


controlled = json.loads((root / "acceptance/results.json").read_text())
assert len(controlled) == 36 and all(r["pass"] for r in controlled)
for r in controlled:
    assert r["maxFragments"] == {"weak": 14, "hot": 36}.get(r["variant"], 0)
    assert r["strongIntact"] >= .95 * r["strongInitial"]
assert "checks=43 failures=0" in log("jet")
assert "PHYXEL_SHELL_PROBES_PASS checks=76" in log("mechanisms")
assert "checks=26" in log("sensors") and "physicsUnchanged=true" in log("sensors")
assert "PHYXEL_UI_REGRESSION_SUCCESS" in log("ui")
assert "PHYXEL_SCENE_FILES_SUCCESS" in log("scene-files")

petard = {}
for variant in ("original", "vent", "off"):
    folder = "petard-" + variant
    rows = records(folder, "PHYXEL_SHELL_SAMPLE ")
    finals = [r for r in rows if r["frame"] == 2 * r["fps"]]
    assert len(finals) == 6
    assert log(folder).count("PHYXEL_SHELL_ROUNDTRIP equal=True") == 6
    assert all(r["powder"] <= 65.12 for r in finals)
    if variant == "original":
        assert all(r["fragments"] > 0 for r in finals)
        assert all(r["coolFragments"] > 0 and r["outside"] > 0 for r in rows if r["frame"] == r["fps"])
    else:
        acceptance = records(folder, "PHYXEL_VENT_ACCEPTANCE ")
        assert len(acceptance) == 6
        if variant == "off":
            assert all(r["peakFragments"] == 0 and r["pass"] for r in acceptance)
        else:
            # Keep the historical zero-fragments criterion FAIL visible.
            # One near-melting hot edge is NOT relabelled as a zero-fragment PASS.
            assert all(r["peakFragments"] <= 1 for r in acceptance)
            petard["vent_strict_failures"] = [r for r in acceptance if not r["pass"]]
    petard[variant] = finals

origins = records("vent-trace", "PHYXEL_FRACTURE_ORIGIN ")
assert len(origins) == 1 and origins[0]["temperature"] == 1000 and origins[0]["tick"] == 83

steam = []
for folder in ("steam-original", "steam-sprinkle", "steam-sandbox-sprinkle", "steam-steel"):
    rows = records(folder, "PHYXEL_SHELL_SAMPLE ")
    first, last = rows[0], rows[-1]
    assert last["frame"] == 14400
    assert max(r["furnaceFragments"] for r in rows) - first["furnaceFragments"] <= 100
    intact_pass = last["furnaceIntact"] >= .95 * first["furnaceIntact"]
    assert "PHYXEL_SHELL_ROUNDTRIP equal=True" in log(folder)
    if last["mode"] == "Simulation":
        assert max(r["fragments"] for r in rows) > first["fragments"]
    if folder != "steam-original":
        portions = records(folder, "PHYXEL_SHELL_SPRINKLE ")
        assert len(portions) == 12 and sum(r["requested"] for r in portions) == 156
    air = list(struct.iter_unpack("<ffff", (root / folder / f'shell-{last["mode"]}-60-14400-air.bin').read_bytes()))
    up = -sum(air[y * 168 + x][2] for y in range(12, 55) for x in range(40, 45)) / 215
    left = -sum(air[y * 168 + x][1] for y in range(65, 68) for x in range(55, 115)) / 180
    draft_pass = up > 1 and left > 1
    if folder == "steam-steel":
        assert intact_pass and draft_pass
        assert "PHYXEL_STEEL_BASE converted=" in log(folder)
    palette = dict(line.split(":", 1) for line in (root / folder / "runtime-palette.txt").read_text(encoding="utf-8-sig").splitlines())
    grid = list(struct.iter_unpack("<IffffIIIfffffI", (root / folder / f'shell-{last["mode"]}-60-14400-grid.bin').read_bytes()))
    molten_mass = sum(c[1] for i, c in enumerate(grid) if i // 672 >= 270 and c[5] and palette[str(c[0])] == "core:molten_metal")
    steam.append(dict(folder=folder, first=first, last=last, chimney_up=up, under_boiler_left=left,
                      intact95_pass=intact_pass, draft_pass=draft_pass, lower_molten_mass=molten_mass))

performance = {}
for folder in ("off-before", "on-before", "off-after", "on-after", "no-sensors-after"):
    rows = list(csv.DictReader((root / folder / "clock.csv").open(encoding="utf-8-sig")))
    windows = {}
    for start in (5, 20):
        a = next(r for r in rows if float(r["wallSeconds"]) >= start)
        b = rows[-1]
        elapsed = float(b["wallSeconds"]) - float(a["wallSeconds"])
        rate = {k: (float(b[k]) - float(a[k])) / elapsed for k in
                ("updates", "simulationSeconds", "airTicks", "gasMotionTicks", "thermalTicks", "combustionTicks")}
        assert .99 < rate["simulationSeconds"] < 1.01 and 59 < rate["airTicks"] < 61 and 19 < rate["thermalTicks"] < 21
        windows[str(start)] = rate
    performance[folder] = dict(windows=windows, final_gpu={k: float(rows[-1][k]) for k in rows[-1] if k.endswith("GpuMs")})

accepted = not petard["vent_strict_failures"] and all(r["intact95_pass"] and r["draft_pass"] for r in steam)
report = dict(accepted=accepted, controlled=controlled, petard=petard, vent_exception=origins[0], steam=steam, performance=performance)
(root / "analysis.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print("RS: controlled36, jet43, mechanisms76, sensors26, save/load and UI PASS; strict hot vent exception retained")
for r in steam:
    print(r["folder"], "lower intact", r["first"]["furnaceIntact"], "->", r["last"]["furnaceIntact"],
          "fragments", r["first"]["furnaceFragments"], "->", r["last"]["furnaceFragments"],
          "draft", r["chimney_up"], r["under_boiler_left"],
          "intact95", "PASS" if r["intact95_pass"] else "FAIL", "draft", "PASS" if r["draft_pass"] else "FAIL", "molten mass", r["lower_molten_mass"])
for folder, r in performance.items():
    print(folder, r["windows"])
print("RS overall:", "PASS" if accepted else "PARTIAL — retained strict vent/legacy melting FAILs; see analysis.json")
sys.exit(0 if accepted else 1)
