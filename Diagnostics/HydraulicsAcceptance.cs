using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class HydraulicsAcceptance
{
    private static SimulationWorldSnapshot? initial;
    internal static SimulationWorldSnapshot? Create(AcceptanceScenarioMode mode, int width, int height, MaterialRegistry registry)
    {
        if (mode is not (AcceptanceScenarioMode.HydraulicSurface or AcceptanceScenarioMode.HydraulicBalance)) return null;
        if (width < 480 || height < 260) throw new InvalidOperationException("Hydraulic fixtures require 480x260 cells.");
        byte[] grid = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        void Put(int x, int y, string id)
        {
            var material = registry[id];
            MemoryMarshal.Cast<byte, GridCell>(grid.AsSpan())[y * width + x] = new()
            {
                MaterialIndex = material.RuntimeIndex, Mass = 1, IsActive = 1, Temperature = 20,
                RestFrames = id == CoreMaterialIds.Water ? 0u : 30u
            };
        }
        void Box(int left, int right)
        {
            for (int x = left; x <= right; x++) Put(x, 245, CoreMaterialIds.Fixture);
            for (int y = 80; y < 245; y++)
            {
                Put(left, y, CoreMaterialIds.Fixture); Put(right, y, CoreMaterialIds.Fixture);
            }
        }
        if (mode == AcceptanceScenarioMode.HydraulicSurface)
        {
            Box(30, 450);
            for (int x = 135; x <= 335; x++)
                for (int y = 145 + Math.Abs(x - 235); y < 245; y++) Put(x, y, CoreMaterialIds.Sand);
            for (int y = 180; y < 245; y++) for (int x = 31; x < 235; x++)
                if (MemoryMarshal.Cast<byte, GridCell>(grid.AsSpan())[y * width + x].IsActive == 0)
                    Put(x, y, CoreMaterialIds.Water);
            // One-cell film resting on a slope and joined to the pool below.
            for (int x = 200; x <= 235; x++) Put(x, 380 - x, CoreMaterialIds.Water);
            // A separate droplet on a flat shelf must retain its location.
            for (int x = 370; x <= 420; x++) Put(x, 100, CoreMaterialIds.Fixture);
            Put(395, 99, CoreMaterialIds.Water);
        }
        else
        {
            Box(30, 250); Box(270, 450);
            for (int y = 80; y <= 230; y++) Put(140, y, CoreMaterialIds.Fixture);
            for (int y = 80; y < 245; y++) Put(360, y, CoreMaterialIds.Fixture);
            for (int y = 140; y < 245; y++) for (int x = 31; x < 140; x++) Put(x, y, CoreMaterialIds.Water);
            for (int y = 220; y < 245; y++) for (int x = 141; x < 250; x++) Put(x, y, CoreMaterialIds.Water);
            for (int y = 231; y < 245; y++) Put(140, y, CoreMaterialIds.Water);
            for (int y = 160; y < 245; y++) for (int x = 271; x < 360; x++) Put(x, y, CoreMaterialIds.Water);
            for (int y = 210; y < 245; y++) for (int x = 361; x < 450; x++) Put(x, y, CoreMaterialIds.Water);
        }
        return initial = new(width, height, grid);
    }

    internal static uint[] Checkpoints(AcceptanceScenarioMode mode) => mode switch
    {
        AcceptanceScenarioMode.Hydro => [3, 15, 30, 60, 125],
        AcceptanceScenarioMode.GranularBarrierHydraulic => [3, 30, 120, 300, 600],
        AcceptanceScenarioMode.HydraulicSurface or AcceptanceScenarioMode.HydraulicBalance => [1, 15, 60, 125, 300, 600],
        _ => []
    };

    internal static void WriteTrace(AcceptanceScenarioMode mode, SimulationWorldSnapshot final,
        MaterialRegistry registry, IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, string directory)
    {
        if (Checkpoints(mode).Length == 0) return;
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        List<string> csv = ["frame,mass,cells,leftMass,rightMass,top,highWater,highDeepWater"];
        foreach (var sample in checkpoints.Select(c => (c.Frame, c.Snapshot)).Append((uint.MaxValue, final)))
        {
            var cells = MemoryMarshal.Cast<byte, GridCell>(sample.Item2.Grid);
            double mass = 0, left = 0, right = 0;
            int count = 0, top = final.Height, high = 0, deep = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].IsActive == 0 || cells[i].MaterialIndex != water) continue;
                int x = i % final.Width, y = i / final.Width;
                mass += cells[i].Mass; count++; top = Math.Min(top, y);
                if (x <= 128) left += cells[i].Mass;
                if (x >= 148 && x <= 250) right += cells[i].Mass;
                if (y < 175 && x >= 170 && x <= 270)
                {
                    high++;
                    if (y + 3 < final.Height && cells[i + final.Width].MaterialIndex == water &&
                        cells[i + 2 * final.Width].MaterialIndex == water &&
                        cells[i + 3 * final.Width].MaterialIndex == water) deep++;
                }
            }
            csv.Add(FormattableString.Invariant($"{sample.Item1},{mass:F6},{count},{left:F6},{right:F6},{top},{high},{deep}"));
        }
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "hydraulic-checkpoints.csv"), csv);
        File.WriteAllBytes(Path.Combine(directory, "hydraulic-final-grid.bin"), final.Grid);
        var finalCells = MemoryMarshal.Cast<byte, GridCell>(final.Grid);
        List<string> highCells = ["x,y,mass,pressure,vx,vy,rest"];
        for (int i = 0; i < finalCells.Length; i++)
        {
            var c = finalCells[i];
            if (c.IsActive != 0 && c.MaterialIndex == water && i / final.Width < 175)
                highCells.Add(FormattableString.Invariant($"{i % final.Width},{i / final.Width},{c.Mass:R},{c.Pressure:R},{c.VelocityX:R},{c.VelocityY:R},{c.RestFrames}"));
        }
        File.WriteAllLines(Path.Combine(directory, "hydraulic-high-water.csv"), highCells);
    }

    internal static bool Validate(AcceptanceScenarioMode mode, SimulationWorldSnapshot final,
        MaterialRegistry registry, IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, out string report)
    {
        if (initial is null) throw new InvalidOperationException("Missing hydraulic fixture.");
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        var before = MemoryMarshal.Cast<byte, GridCell>(initial.Grid);
        var after = MemoryMarshal.Cast<byte, GridCell>(final.Grid);
        double initialMass = 0, finalMass = 0, isolatedLeftMass = 0, isolatedRightMass = 0;
        int leaks = 0, film = 0, materialChanges = 0;
        for (int i = 0; i < after.Length; i++)
        {
            if (before[i].IsActive != 0 && before[i].MaterialIndex == water) initialMass += before[i].Mass;
            if (after[i].IsActive != 0 && after[i].MaterialIndex == water)
            {
                int x = i % final.Width, y = i / final.Width;
                finalMass += after[i].Mass;
                if (x > 270 && x < 360) isolatedLeftMass += after[i].Mass;
                if (x > 360 && x < 450) isolatedRightMass += after[i].Mass;
                if (x < 30 || x > 450 || y > 245) leaks++;
                if (x >= 150 && x <= 300 && y < 177) film++;
            }
            if (before[i].IsActive != 0 && before[i].MaterialIndex != water &&
                (after[i].IsActive == 0 || after[i].MaterialIndex != before[i].MaterialIndex || after[i].Mass != before[i].Mass))
                materialChanges++;
        }
        bool conserved = Math.Abs(initialMass - finalMass) < .0001 && leaks == 0 && materialChanges == 0;
        if (mode == AcceptanceScenarioMode.HydraulicSurface)
        {
            var droplet = after[99 * final.Width + 395];
            bool isolated = droplet.IsActive != 0 && droplet.MaterialIndex == water && droplet.Mass == 1;
            report = FormattableString.Invariant($"PHYXEL_HYDRAULIC_SURFACE film={film} isolated={isolated} mass={initialMass:F6}/{finalMass:F6} leaks={leaks} materialChanges={materialChanges}");
            return conserved && film == 0 && isolated;
        }
        int left = BulkTop(final, water, 31, 139), right = BulkTop(final, water, 141, 249);
        int isolatedLeft = BulkTop(final, water, 271, 359), isolatedRight = BulkTop(final, water, 361, 449);
        int earlyLeft = BulkTop(checkpoints[0].Snapshot, water, 31, 139);
        int earlyRight = BulkTop(checkpoints[0].Snapshot, water, 141, 249);
        report = FormattableString.Invariant($"PHYXEL_HYDRAULIC_BALANCE initial={earlyLeft}/{earlyRight} final={left}/{right} isolated={isolatedLeft}/{isolatedRight} isolatedMass={isolatedLeftMass:F0}/{isolatedRightMass:F0} mass={initialMass:F6}/{finalMass:F6} leaks={leaks} materialChanges={materialChanges}");
        return conserved && earlyRight - earlyLeft > 40 && Math.Abs(left - right) <= 3 &&
            isolatedLeft == 160 && isolatedRight == 210 && isolatedLeftMass == 7565 && isolatedRightMass == 3115;
    }

    internal static bool ValidateHydroTrace(SimulationWorldSnapshot final, uint water,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, out string report)
    {
        if (checkpoints.Count < 5) { report = "hydraulicTrace=missing"; return false; }
        double Mass(SimulationWorldSnapshot world, int left, int right)
        {
            var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
            double mass = 0;
            for (int y = 0; y < world.Height; y++) for (int x = left; x <= right; x++)
            {
                var c = cells[y * world.Width + x];
                if (c.IsActive != 0 && c.MaterialIndex == water) mass += c.Mass;
            }
            return mass;
        }
        // Include pockets inside the thick painted walls. x=280 separates
        // this entire vessel from the independent waterfall fixture.
        double initialPan = Mass(checkpoints[0].Snapshot, 0, 279);
        double initialLeft = Mass(checkpoints[0].Snapshot, 0, 130);
        double initialRight = Mass(checkpoints[0].Snapshot, 146, 279);
        double middleRight = Mass(checkpoints[2].Snapshot, 146, 279);
        double finalRight = Mass(final, 146, 279);
        double finalPan = Mass(final, 0, 279);
        double residual = checkpoints.Max(c => Math.Abs(Mass(c.Snapshot, 0, 279) - initialPan));
        residual = Math.Max(residual, Math.Abs(finalPan - initialPan));
        bool pass = initialPan > 5000 && initialLeft > initialPan * .85 && initialRight < initialPan * .1 &&
            middleRight > initialRight + 100 && middleRight < initialPan * .4 &&
            finalRight > initialPan * .35 && residual < .001;
        report = FormattableString.Invariant($"hydraulicTrace={pass} panMass={initialPan:F6}/{finalPan:F6} massResidual={residual:F6} rightMass={initialRight:F0}/{middleRight:F0}/{finalRight:F0}");
        return pass;
    }

    private static int BulkTop(SimulationWorldSnapshot world, uint water, int left, int right)
    {
        var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        for (int y = 80; y < 245; y++)
        {
            int count = 0;
            for (int x = left; x <= right; x++)
                if (cells[y * world.Width + x].IsActive != 0 && cells[y * world.Width + x].MaterialIndex == water) count++;
            if (count > (right - left) / 2) return y;
        }
        return 0;
    }
}
