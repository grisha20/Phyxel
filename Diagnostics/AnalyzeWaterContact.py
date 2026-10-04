"""Observe cardinal metal/water pairs in a diagnostic snapshot; never edit it."""
import json
import pathlib
import struct
import sys

root = pathlib.Path(sys.argv[1])
width = int(sys.argv[2]) if len(sys.argv) > 2 else 672
palette = {
    int(line.split(":", 1)[0]): line.split(":", 1)[1].strip()
    for line in (root / "runtime-palette.txt").read_text(encoding="utf-8").splitlines()
}
cells = list(struct.iter_unpack("<I4f3I2f", (root / "final-grid.bin").read_bytes()))
height = len(cells) // width
pairs = []
for index, metal in enumerate(cells):
    if not metal[5] or palette[metal[0]] != "core:metal":
        continue
    x, y = index % width, index // width
    for xx, yy in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
        if not (0 <= xx < width and 0 <= yy < height):
            continue
        water = cells[yy * width + xx]
        if water[5] and palette[water[0]] == "core:water":
            pairs.append({"metalX": x, "metalY": y, "waterX": xx, "waterY": yy,
                          "metalC": metal[8], "waterC": water[8],
                          "waterMass": water[1], "boilingEnergyPerMass": water[9]})
water = [c for c in cells if c[5] and palette[c[0]] == "core:water"]
report = {
    "contacts": len(pairs),
    "bulkWaterC": sum(c[1] * c[8] for c in water) / max(1e-12, sum(c[1] for c in water)),
    "waterMass": sum(c[1] for c in water),
    "steamMass": sum(c[1] for c in cells if c[5] and palette[c[0]] == "core:steam"),
    "liquidBoilingEnergy": sum(c[1] * c[9] for c in water),
    "hottestWetMetal": max(pairs, key=lambda p: p["metalC"], default=None),
    "metal190WaterBelow90": [p for p in pairs if p["metalC"] >= 190 and p["waterC"] < 90],
    "largestGaps": sorted(pairs, key=lambda p: p["metalC"] - p["waterC"], reverse=True)[:5],
}
(root / "water-contact.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
