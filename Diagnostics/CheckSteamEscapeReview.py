"""Check SE metrics for the documented 968x564 large-fill/small-drop fixtures.

RunSteamEscapeReview.ps1 produces replay data; this checks physical criteria.
GPU comparison requires a fresh baseline on the same computer. These are game
units, not SI or an FPS promise for another GPU. No third-party Python packages.
"""
import argparse
import json
import math
from pathlib import Path
import struct


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(ok, message):
    if not ok:
        raise ValueError(message)


def check_grid(path):
    # GridCell layout: fourteen 32-bit fields, active at 5, temperature at 8.
    cell = struct.Struct("<I4f3I5fI")
    for c in cell.iter_unpack(path.read_bytes()):
        if c[5]:
            require(c[1] > 0 and all(math.isfinite(c[i]) for i in (1, 2, 3, 4, 8, 9, 10, 11, 12)),
                    f"Non-finite/invalid active cell: {path}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--accepted-root", required=True, type=Path,
                        help="QR fixture runs: final-large/small-<mode>-<fps>, dry60")
    parser.add_argument("--baseline-large", required=True, type=Path,
                        help="Fresh before-large/measurements.json on this computer")
    parser.add_argument("--prefix", default="")
    args = parser.parse_args()
    baseline = read(args.baseline_large)["rows"][-1]
    summary = []
    for scene in ("large", "small"):
        for mode in ("Simulation", "Sandbox"):
            for fps in (30, 60, 100):
                key = f"{scene}-{mode}-{fps}"
                directory = args.root / (args.prefix + key)
                # Existing artifact runs use lower-case mode names.
                if not directory.exists():
                    directory = args.root / (args.prefix + key.lower())
                data = read(directory / "measurements.json")
                row = data["rows"][-1]
                require(row["seconds"] == 30, f"Incomplete replay: {key}")
                check_grid(directory / "grid-30.bin")
                if scene == "small":
                    require(row["water"]["mass"] <= 1, f"Drop not evaporated: {key}")
                else:
                    old = read(args.accepted_root / ("final-" + key.lower()) / "measurements.json")["rows"][-1]
                    require(abs(row["impactPlate"]["temperature"] - old["impactPlate"]["temperature"]) <= 25,
                            f"Accepted plate cooling changed: {key}")
                    require(row["water"]["mass"] > 12000 and row["water"]["cells"] >= .95 * row["water"]["mass"],
                            f"Large bath disappeared or remained sparse: {key}")
                    if mode == "Simulation" and fps == 60:
                        early = next(r for r in data["rows"] if r["seconds"] == 2)
                        require(early["steamInPool"] < 193, "Internal steam not reduced at 2s")
                        require(row["steam"]["cells"] < 28075.5, "Steam still accumulated at 30s")
                        require(row["gpu"]["AverageMilliseconds"] < 1.2 * baseline["gpu"]["AverageMilliseconds"],
                                "Large bath GPU cost worsened >20%")
                summary.append(dict(name=key, plate=row["impactPlate"]["temperature"],
                                    water=row["water"], steam=row["steam"], steamInPool=row["steamInPool"],
                                    gpu=row["gpu"]["AverageMilliseconds"]))
    dry = args.root / (args.prefix + "dry-Simulation-60")
    if not dry.exists():
        dry = args.root / (args.prefix + "dry60")
    new = read(dry / "measurements.json")
    old = {r["seconds"]: r for r in read(args.accepted_root / "dry60/measurements.json")["rows"]}
    delta = max(abs(a["Temperature"] - b["Temperature"]) for row in new["rows"] if row["seconds"] in old
                for a, b in zip(row["samples"], old[row["seconds"]]["samples"]))
    require(delta <= 2, f"Dry furnace changed: {delta}")
    sections = {s["name"]: s for s in new["rows"][-1]["sections"]}
    require(sections["horizontal"]["vx"] < 0 and sections["chimney"]["vy"] < 0, "Dry draft reversed")
    check_grid(dry / "grid-30.bin")
    result = dict(cases=summary, dryMaxDelta=delta, passed=True)
    (args.root / "summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
