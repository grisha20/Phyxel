using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Graphics;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class GasFlowAcceptance
{
    private static int Shift => int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_AIR_WALL_SHIFT"), out int n) ? n : 0;
    private static bool Horizontal => Environment.GetEnvironmentVariable("PHYXEL_AIR_WALL_HORIZONTAL") == "1";
    internal static SimulationWorldSnapshot? Create(AcceptanceScenarioMode mode, int width, int height, MaterialRegistry registry)
    {
        if (mode is not (AcceptanceScenarioMode.AirWall or AcceptanceScenarioMode.Co2Thermal or AcceptanceScenarioMode.TransientHeat or AcceptanceScenarioMode.GasCoFlow)) return null;
        byte[] bytes = new byte[width * height * 40];
        void Put(int x, int y, string id, float t = 20)
        {
            var m = registry[id].Properties;
            MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan())[y * width + x] = new()
            { IsActive = 1, MaterialIndex = registry.GetRequiredRuntimeIndex(id), Mass = m.Density,
                Temperature = t, Lifetime = m.MaximumLifetime };
        }
        if (mode == AcceptanceScenarioMode.AirWall)
        {
            if (Horizontal) for (int x = 0; x < width; x++) Put(x, 132 + Shift, CoreMaterialIds.Metal);
            else for (int y = 0; y < height; y++) Put(240 + Shift, y, CoreMaterialIds.Metal);
        }
        else if (mode == AcceptanceScenarioMode.GasCoFlow)
        {
            for (int y = 99; y <= 140; y++) { Put(199, y, CoreMaterialIds.Fixture); Put(240, y, CoreMaterialIds.Fixture); }
            for (int x = 199; x <= 240; x++) { Put(x, 99, CoreMaterialIds.Fixture); Put(x, 140, CoreMaterialIds.Fixture); }
            for (int y = 100; y < 140; y++) for (int x = 200; x < 240; x++) Put(x, y, CoreMaterialIds.Smoke);
            Put(210, 120, CoreMaterialIds.Co2);
            for (int x = 300; x <= 320; x++) Put(x, 150, CoreMaterialIds.Fixture);
            for (int x = 309; x <= 311; x++) Put(x, 148, CoreMaterialIds.Fixture);
            Put(309, 149, CoreMaterialIds.Fixture);
            Put(311, 149, CoreMaterialIds.Fixture);
            Put(310, 149, CoreMaterialIds.Co2);
        }
        else if (mode == AcceptanceScenarioMode.TransientHeat)
        {
            Put(100, 100, CoreMaterialIds.Fire, 600);
            Put(200, 100, CoreMaterialIds.Fire, 50);
            var grid = MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan());
            grid[100 * width + 100].Mass = .12f;
            grid[100 * width + 100].Lifetime = .01f;
            grid[100 * width + 200].Mass = .24f;
            grid[100 * width + 200].Lifetime = .01f;
        }
        else
        {
            for (int box = 0; box < 2; box++)
            {
                int left = 60 + box * 240;
                for (int y = 40; y <= 240; y++) { Put(left, y, CoreMaterialIds.Fixture); Put(left + 150, y, CoreMaterialIds.Fixture); }
                for (int x = left; x <= left + 150; x++) { Put(x, 40, CoreMaterialIds.Fixture); Put(x, 240, CoreMaterialIds.Fixture); }
                for (int y = 115; y <= 153; y += 2) for (int x = left + 36; x <= left + 104; x += 2)
                    Put(x, y, CoreMaterialIds.Co2, box == 0 ? 20 : 500);
            }
        }
        return new(width, height, bytes);
    }
    internal static void InitializeFields(AcceptanceScenarioMode mode, GpuSimulationResources resources)
    {
        if (mode != AcceptanceScenarioMode.GasCoFlow) return;
        var air = new AirCell[resources.AirWidth * resources.AirHeight];
        // A closed box admits circulation, not uniform upward throughflow.
        // Seed a stream-function vortex with zero normal velocity at its walls;
        // pressure projection must retain this rising branch, while removing
        // the old fixture's nonphysical source at the floor and sink at the lid.
        for (int y = 0; y < resources.AirHeight; y++) for (int x = 0; x < resources.AirWidth; x++)
        {
            int fx = x * 4 + 2, fy = y * 4 + 2;
            if (fx < 200 || fx >= 240 || fy < 100 || fy >= 140) continue;
            double sx = Math.PI * (fx - 200) / 40, sy = Math.PI * (fy - 100) / 40;
            air[y * resources.AirWidth + x].VelocityX = (float)(2 * Math.Sin(sx) * Math.Sin(sx) * Math.Sin(2 * sy));
            air[y * resources.AirWidth + x].VelocityY = (float)(-2 * Math.Sin(2 * sx) * Math.Sin(sy) * Math.Sin(sy));
        }
        resources.Context.UpdateSubresource(air, resources.Air.Buffer);
        var motion = new GasMotionState[resources.Width * resources.Height];
        // Both probes start with a pending collision. A small stable-gas
        // relaxation increment alone might not cross a pixel this first tick,
        // so it would test free acceleration instead of contact resolution.
        motion[120 * resources.Width + 210] = new() { VelocityY = -2, OffsetY = -1 };
        motion[149 * resources.Width + 310] = new() { VelocityY = 2, OffsetY = 1 };
        resources.Context.UpdateSubresource(motion, resources.GasMotion.Buffer);
    }
    internal static IReadOnlyList<BrushDrawCommand> Commands(AcceptanceScenarioMode mode, uint frame, MaterialRegistry registry)
    {
        if (mode != AcceptanceScenarioMode.AirWall || frame == 0 || frame > 180) return [];
        int x = Horizontal ? 240 : 220 + Shift, y = Horizontal ? 152 + Shift : 135;
        return [new() { X = x, EndX = x, Y = y, EndY = y, Radius = 4, Density = 1,
            Mode = BrushCommandMode.Material, MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire), Seed = 73601 }];
    }
    internal static bool Validate(AcceptanceScenarioMode mode, SimulationWorldSnapshot world, MaterialRegistry registry, string directory, out string report)
    {
        if (mode == AcceptanceScenarioMode.GasCoFlow)
        {
            var motion = MemoryMarshal.Cast<byte, GasMotionState>(world.GasMotion!);
            var gas = motion[120 * world.Width + 210];
            var wall = motion[149 * world.Width + 310];
            var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
            int count = 0; double mass = 0;
            foreach (var c in cells) if (c.IsActive != 0 && c.MaterialIndex == registry.GetRequiredRuntimeIndex(CoreMaterialIds.Co2)) { count++; mass += c.Mass; }
            report = string.Create(CultureInfo.InvariantCulture, $"PHYXEL_GAS_COFLOW gasVy={gas.VelocityY:R} gasOffsetY={gas.OffsetY:R} wallVy={wall.VelocityY:R} wallOffsetY={wall.OffsetY:R} co2Count={count} co2Mass={mass:R}");
            return gas.VelocityY < -.1 && gas.OffsetY == 0 && wall.VelocityY < 0 && wall.OffsetY == 0 && count == 2 && Math.Abs(mass - .24) < 1e-6;
        }
        if (mode == AcceptanceScenarioMode.TransientHeat)
        {
            int smoke = 0, fire = 0; double mass = 0, heat = 0;
            foreach (var c in MemoryMarshal.Cast<byte, GridCell>(world.Grid))
            {
                if (c.IsActive == 0) continue;
                if (c.MaterialIndex == registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire)) fire++;
                if (c.MaterialIndex != registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke)) continue;
                smoke++; mass += c.Mass;
                heat += c.Mass * registry[c.MaterialIndex].Properties.HeatCapacity * (c.Temperature - 20);
            }
            double initialHeat = (.12 * 580 + .24 * 30) * registry[CoreMaterialIds.Fire].Properties.HeatCapacity;
            report = string.Create(CultureInfo.InvariantCulture, $"PHYXEL_TRANSIENT_HEAT smoke={smoke} fire={fire} mass={mass:R} heat={heat:R} expectedHeat={initialHeat:R}");
            return smoke == 2 && fire == 0 && Math.Abs(mass - .36) < 1e-6 && Math.Abs(heat - initialHeat) < 1e-4;
        }
        if (mode == AcceptanceScenarioMode.AirWall)
        {
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "grid.bin"), world.Grid);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "air.bin"), world.Air!);
            var air = MemoryMarshal.Cast<byte, AirCell>(world.Air!);
            int width = (world.Width + 3) / 4; double leak = 0, drive = 0; int leakIndex = 0;
            for (int i = 0; i < air.Length; i++)
            {
                int x = i % width * 4 + 2, y = i / width * 4 + 2;
                bool far = Horizontal ? y < 132 + Shift : x > 240 + Shift;
                double value = Math.Abs(air[i].Pressure) + Math.Abs(air[i].VelocityX) + Math.Abs(air[i].VelocityY);
                if (far && value > leak) { leak = value; leakIndex = i; } else if (!far) drive = Math.Max(drive, value);
            }
            int wall = 0, crossedGas = 0;
            var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].IsActive == 0) continue;
                int x = i % world.Width, y = i / world.Width;
                bool onWall = Horizontal ? y == 132 + Shift : x == 240 + Shift;
                bool far = Horizontal ? y < 132 + Shift : x > 240 + Shift;
                if (onWall && cells[i].MaterialIndex == registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal)) wall++;
                if (far && registry[cells[i].MaterialIndex].Properties.SimulationKind == (uint)MaterialSimulationKind.Gas) crossedGas++;
            }
            report = string.Create(CultureInfo.InvariantCulture, $"PHYXEL_AIR_WALL horizontal={Horizontal} shift={Shift} wallCells={wall} crossedGas={crossedGas} leak={leak:R} drive={drive:R} leakX={leakIndex%width*4+2} leakY={leakIndex/width*4+2}");
            return leak == 0 && drive > .01 && wall == (Horizontal ? world.Width : world.Height) && crossedGas == 0;
        }
        var grid = MemoryMarshal.Cast<byte, GridCell>(world.Grid); double coldY = 0, hotY = 0; int cold = 0, hot = 0;
        for (int i = 0; i < grid.Length; i++) if (grid[i].IsActive != 0 && grid[i].MaterialIndex == registry.GetRequiredRuntimeIndex(CoreMaterialIds.Co2))
        {
            if (i % world.Width < 240) { coldY += i / world.Width; cold++; }
            else { hotY += i / world.Width; hot++; }
        }
        coldY /= Math.Max(1, cold); hotY /= Math.Max(1, hot);
        report = string.Create(CultureInfo.InvariantCulture, $"PHYXEL_CO2_THERMAL coldCount={cold} hotCount={hot} initialY=134 coldY={coldY:F5} hotY={hotY:F5}");
        return cold == 700 && hot == 700 && coldY > 136 && hotY < 132;
    }
}
