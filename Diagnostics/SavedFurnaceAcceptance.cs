using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

// Observes the user's saved furnace without drawing replacement geometry.
internal static class SavedFurnaceAcceptance
{
    internal static IReadOnlyList<BrushDrawCommand> Commands(uint frame, MaterialRegistry registry)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_FURNACE_TORCH_SECONDS"), out int seconds) ||
            seconds <= 0 || frame < Fps || frame >= (seconds + 1) * Fps) return [];
        return [new BrushDrawCommand { X = 443, Y = 338, EndX = 443, EndY = 338,
            Radius = 6, Density = .82f, MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
            Mode = BrushCommandMode.Material, Seed = 73001 }];
    }
    internal static int Fps => int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"), out int n) ? n : 60;
    private static int Seconds => int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_FURNACE_DURATION_SECONDS"), out int n)
        ? Math.Clamp(n, 60, 180) : 60;
    internal static uint FinalFrame => (uint)(Seconds * Fps);
    internal static uint[] Frames
    {
        get
        {
            List<uint> frames = [(uint)Fps];
            for (int second = 5; second < Seconds; second += 5) frames.Add((uint)(second * Fps));
            if (Environment.GetEnvironmentVariable("PHYXEL_FURNACE_COAL_TIMING") == "1")
            {
                // Resolve early coal burnout to one second. Five-second bins
                // cannot establish the passport's two-second FPS tolerance.
                for (int second = 10; second <= 30; second++)
                {
                    uint frame = (uint)(second * Fps);
                    if (!frames.Contains(frame)) frames.Add(frame);
                }
                frames.Sort();
            }
            return frames.ToArray();
        }
    }
    private static bool SealedIntake => Environment.GetEnvironmentVariable("PHYXEL_FURNACE_SEALED_INTAKE") == "1";

    internal static bool Validate(SimulationWorldSnapshot world, MaterialRegistry registry,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, string directory, out string report)
    {
        if (world.Width != 672 || world.Height != 378)
        {
            report = "PHYXEL_SAVED_FURNACE wrongSceneSize expected=672x378";
            return false;
        }
        var rows = new List<string> { "frame,chimneySmoke,chimneyCO2,escapedCO2,farFloorT,nearFloorT,chimneyT,waterT,coalMass,fireCells" };
        (int Smoke, int Co2, int Escaped, double Floor, double Near, double Chimney, double Water, double Fuel, int Fire, int BurningTop, double Co2Mass) Measure(SimulationWorldSnapshot s)
        {
            var grid = MemoryMarshal.Cast<byte, GridCell>(s.Grid);
            int smoke = 0, co2 = 0, escaped = 0, fc = 0, nc = 0, cc = 0, wc = 0, fire = 0;
            double floor = 0, near = 0, chimney = 0, water = 0, fuel = 0, co2Mass = 0;
            int top = s.Height;
            for (int y = 0; y < s.Height; y++) for (int x = 0; x < s.Width; x++)
            {
                var c = grid[y * s.Width + x]; if (c.IsActive == 0) continue;
                string id = registry[c.MaterialIndex].Id;
                if (x >= 154 && x <= 184 && y >= 20 && y <= 150)
                {
                    if (id == CoreMaterialIds.Smoke) smoke++;
                    if (id == CoreMaterialIds.Co2) { co2++; co2Mass += c.Mass; }
                }
                if (x >= 494 && y >= 310 && id == CoreMaterialIds.Co2) escaped++;
                if (id == CoreMaterialIds.Metal)
                {
                    if (x >= 215 && x <= 270 && y >= 235 && y <= 250) { floor += c.Temperature; fc++; }
                    if (x >= 400 && x <= 440 && y >= 235 && y <= 250) { near += c.Temperature; nc++; }
                    // Observe both walls: the bend guides the initial hot
                    // plume along the right wall, not the left one alone.
                    if (((x >= 133 && x <= 153) || (x >= 185 && x <= 205)) && y >= 60 && y <= 140)
                    { chimney += c.Temperature; cc++; }
                }
                if (id == CoreMaterialIds.Water) { water += c.Temperature; wc++; }
                if (id == CoreMaterialIds.Coal) { fuel += c.Mass; if (c.Lifetime > 0) top = Math.Min(top, y); }
                if (id == CoreMaterialIds.Fire) fire++;
            }
            return (smoke, co2, escaped, floor / Math.Max(1, fc), near / Math.Max(1, nc), chimney / Math.Max(1, cc), water / Math.Max(1, wc), fuel, fire, top, co2Mass);
        }
        var final = Measure(world); int maxSmoke = final.Smoke, maxCo2 = final.Co2;
        double maxCo2Mass = final.Co2Mass;
        int peakIgnitionTop = final.BurningTop;
        bool lateFlow = true;
        (double Pipe, double Intake, double Up, double In, double Speed95, double GasPipe, double GasUp, int GasCells) Flow(SimulationWorldSnapshot s)
        {
            if (s.Air is null) return (0, 0, 0, 0, double.PositiveInfinity, 0, 0, 0);
            var air = MemoryMarshal.Cast<byte, AirCell>(s.Air);
            int width = (s.Width + 3) / 4, height = (s.Height + 3) / 4;
            double pipe = 0, intake = 0; int pc = 0, ic = 0, up = 0, inward = 0;
            List<double> speeds = [];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var c = air[y * width + x]; if (c.Blocked > .5) continue;
                int fx = x * 4 + 2, fy = y * 4 + 2;
                bool inPipe = fx >= 154 && fx <= 184 && fy >= 20 && fy <= 230;
                bool inIntake = fx >= 473 && fx <= 494 && fy >= 330 && fy <= 346;
                if (inPipe) { pipe += c.VelocityY; pc++; if (c.VelocityY < 0) up++; }
                if (inIntake) { intake += c.VelocityX; ic++; if (c.VelocityX < 0) inward++; }
                if (inPipe || inIntake || (fx >= 154 && fx <= 472 && fy >= 252 && fy <= 346))
                    speeds.Add(Math.Sqrt(c.VelocityX * c.VelocityX + c.VelocityY * c.VelocityY));
            }
            speeds.Sort();
            // A closed-bottom chimney can return cool air along one wall.
            // Observe the carrier where gas actually is, rather than treating
            // that empty-air return as failed smoke transport. The straight
            // saved pipe maps edge x184 to the visible node at x182, matching
            // AirFineNodeFor instead of sampling metal at native center x186.
            var cells = MemoryMarshal.Cast<byte, GridCell>(s.Grid);
            double gasPipe = 0; int gasCells = 0, gasUp = 0;
            for (int y = 20; y <= 230; y++) for (int x = 154; x <= 184; x++)
            {
                var c = cells[y * s.Width + x];
                if (c.IsActive == 0 || registry[c.MaterialIndex].Properties.SimulationKind != (uint)MaterialSimulationKind.Gas) continue;
                var carrier = air[y / 4 * width + Math.Clamp(x / 4, 38, 45)];
                gasPipe += carrier.VelocityY; gasCells++;
                if (carrier.VelocityY < 0) gasUp++;
            }
            return (pipe / Math.Max(1, pc), intake / Math.Max(1, ic),
                (double)up / Math.Max(1, pc), (double)inward / Math.Max(1, ic),
                speeds.Count == 0 ? double.PositiveInfinity : speeds[(int)((speeds.Count - 1) * .95)],
                gasPipe / Math.Max(1, gasCells), (double)gasUp / Math.Max(1, gasCells), gasCells);
        }
        bool Directed(SimulationWorldSnapshot s)
        {
            var f = Flow(s);
            return f.Pipe < -.1 && f.Speed95 <= 4 && (SealedIntake
                ? f.GasCells >= 20 && f.GasPipe < -.1 && f.GasUp >= .8
                : f.Up >= .8 && f.Intake < -.1 && f.In >= .8);
        }
        foreach (var sample in checkpoints)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"grid-{sample.Frame}.bin"), sample.Snapshot.Grid);
            if (sample.Snapshot.Oxidizer is not null)
                File.WriteAllBytes(Path.Combine(directory, $"oxygen-{sample.Frame}.bin"), sample.Snapshot.Oxidizer);
            if (sample.Snapshot.Air is not null)
                File.WriteAllBytes(Path.Combine(directory, $"air-{sample.Frame}.bin"), sample.Snapshot.Air);
            var m = Measure(sample.Snapshot); maxSmoke = Math.Max(maxSmoke, m.Smoke); maxCo2 = Math.Max(maxCo2, m.Co2);
            maxCo2Mass = Math.Max(maxCo2Mass,m.Co2Mass);
            peakIgnitionTop = Math.Min(peakIgnitionTop, m.BurningTop);
            if (sample.Frame >= 40 * Fps) lateFlow &= Directed(sample.Snapshot);
            rows.Add(FormattableString.Invariant($"{sample.Frame},{m.Smoke},{m.Co2},{m.Escaped},{m.Floor:F5},{m.Near:F5},{m.Chimney:F5},{m.Water:F5},{m.Fuel:F5},{m.Fire}"));
        }
        rows.Add(FormattableString.Invariant($"{FinalFrame},{final.Smoke},{final.Co2},{final.Escaped},{final.Floor:F5},{final.Near:F5},{final.Chimney:F5},{final.Water:F5},{final.Fuel:F5},{final.Fire}"));
        Directory.CreateDirectory(directory); File.WriteAllLines(Path.Combine(directory, "saved-furnace.csv"), rows);
        File.WriteAllBytes(Path.Combine(directory, "final-grid.bin"), world.Grid);
        if (world.Oxidizer is not null)
            File.WriteAllBytes(Path.Combine(directory, "final-oxygen.bin"), world.Oxidizer);
        if (world.Air is not null) File.WriteAllBytes(Path.Combine(directory, "final-air.bin"), world.Air);
        var palette = new List<string>(); foreach (var m in registry.Materials) palette.Add($"{m.RuntimeIndex}:{m.Id}");
        File.WriteAllLines(Path.Combine(directory, "runtime-palette.txt"), palette);
        var finalFlow = Flow(world);
        lateFlow &= Directed(world);
        bool sealedWall = true;
        if (SealedIntake)
        {
            // Require the entire actual lower aperture to remain metal, so
            // this case cannot pass by accidentally retaining an air opening.
            foreach (var sample in checkpoints) sealedWall &= IntakeIsMetal(sample.Snapshot);
            sealedWall &= IntakeIsMetal(world);
        }
        bool IntakeIsMetal(SimulationWorldSnapshot s)
        {
            var cells = MemoryMarshal.Cast<byte, GridCell>(s.Grid);
            uint metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
            for (int y = 330; y <= 347; y++) for (int x = 473; x <= 493; x++)
                if (cells[y * s.Width + x].IsActive == 0 || cells[y * s.Width + x].MaterialIndex != metal) return false;
            return true;
        }
        report = string.Create(CultureInfo.InvariantCulture, $"PHYXEL_SAVED_FURNACE sealedIntake={SealedIntake} sealedWall={sealedWall} chimneySmoke={maxSmoke} chimneyCO2={maxCo2} escapedCO2={final.Escaped} farFloorT={final.Floor:F5} nearFloorT={final.Near:F5} chimneyT={final.Chimney:F5} waterT={final.Water:F5} coalMass={final.Fuel:F5} fireCells={final.Fire} burningTop={final.BurningTop} peakIgnitionTop={peakIgnitionTop} pipeVy={finalFlow.Pipe:F4} intakeVx={finalFlow.Intake:F4} pipeUp={finalFlow.Up:F3} intakeIn={finalFlow.In:F3} gasCarrierVy={finalFlow.GasPipe:F4} gasCarrierUp={finalFlow.GasUp:F3} gasCarrierCells={finalFlow.GasCells} speed95={finalFlow.Speed95:F3} lateFlow={lateFlow}");
        File.WriteAllText(Path.Combine(directory, "report.txt"), report);
        // Require visible heating and continuing combustion, not merely one
        // particle in the tube or a few thousandths of a degree of drift.
        // Consolidated partial CO2 packets have fewer cells for the same mass.
        // Require a real amount in the tube instead of rewarding fragmentation.
        return maxSmoke >= 10 && maxCo2Mass >= .01 && final.Floor >= 40 && final.Chimney >= 22 &&
            final.Fire >= 20 && peakIgnitionTop <= 270 && lateFlow && sealedWall;
    }
}
