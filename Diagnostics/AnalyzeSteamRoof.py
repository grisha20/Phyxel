"""Check the saved boiler diagnosis; run with artifacts/steam-fracture-20261007."""
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/steam-fracture-20261007")


def records(folder, prefix):
    return [json.loads(line[len(prefix):])
            for line in (root / folder / "run.log").read_text(encoding="utf-8").splitlines()
            if line.startswith(prefix)]


samples = records("before-long", "PHYXEL_SHELL_SAMPLE ")
for mode in ("Simulation", "Sandbox"):
    selected = [r for r in samples if r["mode"] == mode]
    first, last = selected[0], selected[-1]
    assert first["frame"] == 0 and last["frame"] == 7200
    assert first["fragments"] == 9 and all(r["waveMax"] == 0 for r in selected)
    assert (last["fragments"] > 9) if mode == "Simulation" else (last["fragments"] == 9)
    print(mode, "initial/final fragments", first["fragments"], last["fragments"])

origins = records("trace", "PHYXEL_FRACTURE_ORIGIN ")
roof = [r for r in origins if 280 < r["x"] < 365 and 95 < r["y"] < 126]
assert roof and roof[0]["vy"] < 0 and roof[0]["temperature"] < 200
assert all(r["temperature"] < 1000 for r in roof)
assert sum(r["vy"] < 0 for r in roof) > .9 * len(roof)
print("Roof origins", len(roof), "first time", roof[0]["tick"] / 60,
      "first temperature", roof[0]["temperature"], "upward", sum(r["vy"] < 0 for r in roof))

flight = records("flight", "PHYXEL_FRAGMENT_FLIGHT ")
assert flight[0]["age"] == 1 and flight[0]["vy"] < 0
assert min(r["y"] for r in flight) < flight[0]["y"] - 15
assert flight[-1]["age"] == 181 and flight[-1]["y"] > 200 and flight[-1]["vy"] > 0
print("Flight first/highest/final y", flight[0]["y"], min(r["y"] for r in flight), flight[-1]["y"])

edits = records("edit-thickness", "PHYXEL_PRESSURE_EDIT ")
assert len(edits) == 12 and all(r["pass"] and r["initial"] == 0 and r["peak"] == 0 and r["loaded"] > 0 for r in edits)
print("Uniform-pressure edit controls", len(edits), "PASS")
print("Diagnosis checked; steam entrainment is absent, not accepted by these checks.")
