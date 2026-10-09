"""Small v19 controls for coal plumes; writes only a chosen artifact directory."""
import argparse
import json
import pathlib
import struct


def create(output: pathlib.Path):
    output.mkdir(parents=True, exist_ok=True)
    width, height, stride = 480, 300, 56
    for name, centers in (("single-plume", [240]), ("two-plumes", [140, 340]), ("finite-heat", [])):
        cells = bytearray(width * height * stride)

        def put(x, y, material, mass, temperature):
            offset = (y * width + x) * stride
            struct.pack_into("<If", cells, offset, material, mass)
            struct.pack_into("<I", cells, offset + 20, 1)
            struct.pack_into("<f", cells, offset + 32, temperature)

        if centers:
            for y in range(285, 289):
                for x in range(width):
                    put(x, y, 2, 7.8, 30)
        for center in centers:
            for x in range(center - 50, center + 51):
                top = 240 + abs(x - center) * 9 // 10
                for y in range(top, 285):
                    put(x, y, 1, 1, 900 if y < top + 2 else 30)
        heat = bytearray()
        if name == "finite-heat":
            for y in range((height + 3) // 4):
                for x in range((width + 3) // 4):
                    temperature = 900 if 50 <= x < 70 and 40 <= y < 60 else 20
                    heat += struct.pack("<ff", (temperature + 273.15) * .016, .016)
        metadata = dict(Version=19, Scale=.25, Gravity=980, BrushRadius=4, SpawnDensity=1,
                        SolidGravity=False, HydraulicPressure=False, PressureDestruction=False,
                        OpenBoundaries=True, Mode="Simulation", TemperatureSensors=[],
                        SelectedMaterialId="core:fire", MaterialPalette=["core:empty", "core:coal", "core:steel"])
        (output / (name + ".json")).write_text(json.dumps(metadata, ensure_ascii=False), encoding="utf-8")
        world = struct.pack("<7I", 0x5058594C, 19, width, height, stride, len(cells), 0) + cells
        world += struct.pack("<I", len(heat)) + heat + bytes(5 * 4)
        (output / (name + ".world")).write_bytes(world)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=pathlib.Path)
    create(parser.parse_args().output)
