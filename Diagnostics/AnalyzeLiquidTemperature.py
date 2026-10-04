"""Audit selected LT contracts; parcel transport and normal heating are separate."""
import argparse, json, pathlib, re
import numpy as np

p = argparse.ArgumentParser()
p.add_argument("root", type=pathlib.Path)
p.add_argument("--require-heated", action="store_true")
args = p.parse_args()
root = args.root
before = json.loads((root / "before-isolated/mix.json").read_text())
after = json.loads((root / "final/mix.json").read_text())
checks = []
def check(name, passed):
    checks.append(dict(name=name, passed=bool(passed)))
for fps in (30, 60, 100):
    a, b = before[f"core:water-{fps}"], after[f"core:water-{fps}"]
    check(f"water LT02 {fps}", b["upper"] >= a["upper"] - .01 and b["contrast"] <= a["contrast"] * .75)
    check(f"oil LT02 {fps}", after[f"core:oil-{fps}"]["upper"] >= 20.25 and after[f"core:oil-{fps}"]["contrast"] <= 1)
drains = json.loads((root / "final/drains.json").read_text())
def drain(mode, fps, t):
    return next(x["outMass"] for x in drains if (x["mode"],x["fps"],x["temperature"]) == (mode,fps,t))
for mode in ("Sandbox", "Simulation"):
    for fps in (30,60,100):
        cold, reference, hot = [drain(mode,fps,t) for t in (-20,20,100)]
        check(f"oil LT01 {mode} {fps}", cold < reference < hot and hot/cold > 1.3)
    for fps in (30,100):
        for t in (-20,20,100):
            check(f"oil FPS {mode} {fps} {t}", abs(drain(mode,fps,t)/drain(mode,60,t)-1) <= .25)
log = (root / "final/run.log").read_text()
check("GPU stable, walls, mass, latent energy, pause, reload, fixed ticks", "PHYXEL_LIQUID_TEMPERATURE_SUCCESS" in log and "PHYXEL_LT_FAIL" not in log)

dtype = np.dtype([(name,"<u4" if name in ("material","active","body","rest") else "<f4") for name in
    ("material","mass","vx","vy","pressure","active","body","rest","temperature","phase","moisture","moistureEnergy","fuel")])
heated = {}
for name, folder in (("before","heated-before"),("after","convection")):
    file = root / folder / "water_convection_heated-fps-60-air-0-hydro-0/final-grid.bin"
    if file.exists():
        grid = np.fromfile(file,dtype=dtype).reshape(-1,480)
        band = grid[101:111,41:130]["temperature"].astype(float)
        # The actual single-cell free/upper row, not a half-vessel mean.
        surface = band[0]
        heated[name] = dict(surfaceMean=float(surface.mean()),surfaceRange=float(np.ptp(surface)),
            surfaceStd=float(surface.std()),upperMean=float(band.mean()),upperStd=float(band.std()))
        np.savetxt(root / f"heated-surface-{name}.csv",np.c_[np.arange(41,130),surface],delimiter=",",header="x,temperature",comments="")
if args.require_heated:
    check("normal heated snapshots present", len(heated)==2)
    if len(heated)==2:
        check("normal heating surface spread improved",heated["after"]["surfaceStd"] < heated["before"]["surfaceStd"])
if len(heated)==2:
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig, ax = plt.subplots(figsize=(9,3.6),layout="constrained")
        for label in ("before","after"):
            data=np.loadtxt(root/f"heated-surface-{label}.csv",delimiter=",",skiprows=1)
            ax.plot(data[:,0],data[:,1],label=label)
        ax.set(xlabel="Surface cell x",ylabel="Temperature (C)",title="Same bottom heater, 60 simulated seconds")
        ax.legend(); ax.grid(alpha=.2); fig.savefig(root/"heated-surface.png",dpi=150);plt.close(fig)
    except ImportError:
        print("Optional matplotlib absent; measured surface CSVs retained.")
result=dict(scope="selected LT01-LT04; no full fluid solver or bubble turbulence",passed=all(x["passed"] for x in checks),
    checks=checks,heated=heated,mixBefore=before,mixAfter=after,drains=drains)
(root/"measurements.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
print(json.dumps(dict(passed=result["passed"],checks=len(checks),heated=heated)))
if not result["passed"]: raise SystemExit(1)
