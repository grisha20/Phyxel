using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class CoalFireAcceptance
{
    private static SimulationWorldSnapshot? initial;
    internal static int Fps => int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"), out int fps) ? fps : 60;
    private static int HoldSeconds => int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_COAL_FIRE_HOLD_SECONDS"), out int seconds) && seconds > 0 ? seconds : 5;
    internal static int ReleaseSecond => 1 + HoldSeconds;
    internal static int FinalSecond => ReleaseSecond + 24;
    internal static uint Frame(int seconds) => (uint)(seconds * Fps);
    internal static uint[] Checkpoints => [Frame(1) / 10, Frame(1) - 1, Frame(2) - 1,
        Frame(ReleaseSecond) - 1, Frame(ReleaseSecond + 4) - 1, Frame(ReleaseSecond + 14) - 1];

    internal static SimulationWorldSnapshot? Create(AcceptanceScenarioMode mode, int width, int height, MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.CoalFire) return null;
        byte[] grid = new byte[width * height * 40];
        float[] oxygen = new float[width * height];
        Array.Fill(oxygen, 1);
        void Put(int x, int y, string id, float temperature = 20)
        {
            var m = materials[id].Properties;
            MemoryMarshal.Cast<byte, GridCell>(grid.AsSpan())[y * width + x] = new()
            {
                IsActive = 1, MaterialIndex = materials.GetRequiredRuntimeIndex(id),
                Mass = m.SimulationKind == (uint)MaterialSimulationKind.Solid ? m.Density : 1,
                Temperature = temperature, Lifetime = m.MaximumLifetime
            };
            oxygen[y * width + x] = 0;
        }
        // Two cold, densely settled heaps; the tool must ignite them without
        // a temperature brush. The third heap is enclosed in pure CO2.
        for (int box = 0; box < 3; box++)
        {
            int center = 90 + box * 150;
            for (int x = center - 55; x <= center + 55; x++) Put(x, 246, CoreMaterialIds.Fixture);
            if (box == 2)
            {
                for (int y = 170; y < 246; y++)
                {
                    Put(center - 55, y, CoreMaterialIds.Fixture);
                    Put(center + 55, y, CoreMaterialIds.Fixture);
                    for (int x = center - 54; x < center + 55; x++) Put(x, y, CoreMaterialIds.Co2);
                }
                for (int x = center - 55; x <= center + 55; x++) Put(x, 170, CoreMaterialIds.Fixture);
            }
            for (int x = center - 50; x <= center + 50; x++)
                for (int y = 200 + Math.Abs(x - center) * 9 / 10; y < 246; y++)
                    Put(x, y, box == 1 ? "core:stone_coal" : CoreMaterialIds.Coal);
        }
        // An isolated fresh-air pocket with 35% of the normalized reserve.
        // Solid walls are not inert gas and must not dilute its concentration.
        for (int y = 79; y <= 81; y++) for (int x = 19; x <= 21; x++)
            if (x != 20 || y != 80) Put(x, y, CoreMaterialIds.Fixture);
        Put(20, 80, CoreMaterialIds.Fire, 420);
        oxygen[80 * width + 20] = .35f;
        return initial = new(width, height, grid, Oxidizer: MemoryMarshal.AsBytes(oxygen.AsSpan()).ToArray());
    }

    internal static IReadOnlyList<BrushDrawCommand> Commands(uint frame, MaterialRegistry materials)
    {
        if (frame < Frame(1) || frame >= Frame(ReleaseSecond)) return [];
        return new[] { 90, 240, 390 }.Select(x => new BrushDrawCommand
        {
            X = x, Y = 199, EndX = x, EndY = 199, Radius = 6,
            Density = .82f, MaterialIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
            Mode = BrushCommandMode.Material, Seed = 73001
        }).ToArray();
    }

    private static (double Mass, double MaxT, int Hot, int Partial, int Flames, double Oxygen) Measure(
        SimulationWorldSnapshot world, MaterialRegistry materials, int box)
    {
        var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        var oxygen = MemoryMarshal.Cast<byte, float>(world.Oxidizer!);
        uint fuel = materials.GetRequiredRuntimeIndex(box == 1 ? "core:stone_coal" : CoreMaterialIds.Coal);
        double mass = 0, maxT = 0, o = 0; int hot = 0, partial = 0, flames = 0;
        int center = 90 + box * 150;
        for (int y = 170; y < world.Height; y++) for (int x = center - 55; x <= center + 55; x++)
        {
            var c = cells[y * world.Width + x];
            if (c.IsActive != 0 && c.MaterialIndex == fuel)
            {
                mass += c.Mass; maxT = Math.Max(maxT, c.Temperature);
                if (c.Temperature > materials[fuel].Properties.IgnitionTemperature) hot++;
                if (c.Mass < .9999f) partial++;
            }
            if (c.IsActive != 0 && (materials[c.MaterialIndex].Properties.Flags & (uint)MaterialFlags.Flame) != 0) flames++;
            if (x >= center - 6 && x <= center + 6 && y >= 192 && y <= 199) o += oxygen[y * world.Width + x];
        }
        return (mass, maxT, hot, partial, flames, o);
    }

    internal static bool Validate(SimulationWorldSnapshot final, MaterialRegistry materials,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, string directory, out string report)
    {
        if (initial is null) throw new InvalidOperationException("No coal ignition fixture.");
        Directory.CreateDirectory(directory);
        List<string> csv = ["frame,box,mass,maxT,hot,partial,flames,sourceOxygen"];
        foreach (var sample in checkpoints.Select(c => (c.Frame, c.Snapshot)).Append((Frame(FinalSecond), final)))
            for (int box = 0; box < 3; box++)
            {
                var m = Measure(sample.Item2, materials, box);
                csv.Add(FormattableString.Invariant($"{sample.Item1},{box},{m.Mass:F6},{m.MaxT:F3},{m.Hot},{m.Partial},{m.Flames},{m.Oxygen:F6}"));
            }
        File.WriteAllLines(Path.Combine(directory, "coal-fire-checkpoints.csv"), csv);
        var held = checkpoints.Last(c => c.Frame < Frame(ReleaseSecond)).Snapshot;
        var release = checkpoints.Last(c => c.Frame < Frame(ReleaseSecond + 4) + 1).Snapshot;
        var a = Measure(held, materials, 0); var b = Measure(held, materials, 1);
        var later = Measure(release, materials, 0); var laterStone = Measure(release, materials, 1);
        var co2 = Measure(final, materials, 2);
        double charcoalUsed = Measure(initial, materials, 0).Mass - Measure(final, materials, 0).Mass;
        double stoneUsed = Measure(initial, materials, 1).Mass - Measure(final, materials, 1).Mass;
        bool pocket = MemoryMarshal.Cast<byte, GridCell>(checkpoints[0].Snapshot.Grid)[80 * final.Width + 20].MaterialIndex ==
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        bool pass = pocket && a.Partial >= 5 && b.Partial >= 5 && charcoalUsed > .05 && stoneUsed > .05 &&
            a.Mass - later.Mass > .005 && b.Mass - laterStone.Mass > .005 &&
            Math.Abs(co2.Mass - Measure(initial, materials, 2).Mass) < .0001;
        report = string.Create(CultureInfo.InvariantCulture,
            $"PHYXEL_COAL_FIRE pocketFlame={pocket} charcoalPartial={a.Partial} stonePartial={b.Partial} charcoalUsed={charcoalUsed:F6} stoneUsed={stoneUsed:F6} afterRelease={a.Mass-later.Mass:F6}/{b.Mass-laterStone.Mass:F6} heldMaxT={a.MaxT:F2}/{b.MaxT:F2} co2Mass={co2.Mass:F6} co2Initial={Measure(initial,materials,2).Mass:F6}");
        File.WriteAllText(Path.Combine(directory, "report.txt"), report);
        return pass;
    }
}
