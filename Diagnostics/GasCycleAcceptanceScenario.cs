using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Graphics;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

internal static class GasCycleAcceptanceScenario
{
    private static readonly List<(uint Frame, uint Evap, uint Cond, uint Cold, uint Hot)> Events = [];
    public static SimulationWorldSnapshot? CreateInitialWorld(AcceptanceScenarioMode mode,
        int width, int height, MaterialRegistry materials)
    {
        if (mode is not (AcceptanceScenarioMode.Co2Layer or AcceptanceScenarioMode.SteamCycle or AcceptanceScenarioMode.SteamSurface or AcceptanceScenarioMode.SteamEnergy)) return null;
        if (width < 480 || height < 270) throw new InvalidOperationException("Gas cycle requires 480x270.");
        Events.Clear();
        byte[] bytes = new byte[width * height * Marshal.SizeOf<GridCell>()];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan());
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        if (mode == AcceptanceScenarioMode.Co2Layer)
        {
            Fill(cells, width, 120, 80, 359, 83, fixture, 20, 1);
            Fill(cells, width, 120, 240, 359, 243, fixture, 20, 1);
            Fill(cells, width, 120, 80, 123, 243, fixture, 20, 1);
            Fill(cells, width, 356, 80, 359, 243, fixture, 20, 1);
            Fill(cells, width, 228, 180, 251, 219,
                materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2), 20, 1);
        }
        else if (mode == AcceptanceScenarioMode.SteamEnergy)
        {
            SteamEnergyAcceptance.Populate(cells, width, materials);
        }
        else if (mode == AcceptanceScenarioMode.SteamSurface)
        {
            Pocket(cells, width, materials, 200, 122, 20);
            // A finite condenser must have capacity for the vapour's latent
            // energy; the old 8*7.8 mass pocket only tested instant conversion.
            uint coldMetal = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
            for (int y = 119; y <= 121; y++) for (int x = 199; x <= 201; x++)
                if (cells[y * width + x].MaterialIndex == coldMetal)
                    cells[y * width + x].Mass = 78;
            Pocket(cells, width, materials, 300, 122, 122);
            Pocket(cells, width, materials, 400, 200, 180);
        }
        else
        {
            Fill(cells, width, 180, 110, 183, 245, fixture, 20, 1);
            Fill(cells, width, 296, 110, 299, 245, fixture, 20, 1);
            uint metal = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
            Fill(cells, width, 184, 110, 295, 115, metal, 20, 7.8f);
            Fill(cells, width, 184, 240, 295, 245, metal, 240, 7.8f);
            Fill(cells, width, 236, 234, 243, 239,
                materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water), 95, 1);
        }
        var world = new SimulationWorldSnapshot(width, height, bytes);
        if (mode == AcceptanceScenarioMode.SteamCycle)
        {
            string directory = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/gas-cycle";
            // Export on a worker: the game thread owns a synchronization
            // context, so synchronously awaiting file I/O there deadlocks.
            Task.Run(() => new SimulationStateSerializer().SaveAsync(Path.Combine(directory, "steam-cycle.scene.json"),
                new SimulationSettings(), (ushort)materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
                world, materials)).GetAwaiter().GetResult();
        }
        return world;
    }

    private static void Pocket(Span<GridCell> cells, int width, MaterialRegistry materials,
        int x, float steamTemperature, float metalTemperature)
    {
        Fill(cells, width, x - 1, 119, x + 1, 121, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Metal), metalTemperature, 7.8f);
        Fill(cells, width, x - 2, 119, x - 2, 121, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture), 20, 1);
        Fill(cells, width, x, 120, x, 120, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam), steamTemperature, 1);
    }

    // The temperature tool touches only the outside three metal rows. No
    // material is spawned, and neither water nor steam is directly heated.
    public static IReadOnlyList<BrushDrawCommand> CreateCommands() =>
    [
        new() { X = 184, Y = 112, EndX = 295, EndY = 112, Shape = BrushCommandShape.Segment,
            Radius = 1, Density = 1, Mode = BrushCommandMode.SetTemperature, TargetTemperature = 20 },
        new() { X = 184, Y = 243, EndX = 295, EndY = 243, Shape = BrushCommandShape.Segment,
            Radius = 1, Density = 1, Mode = BrushCommandMode.SetTemperature, TargetTemperature = 240 }
    ];

    public static void RecordCounters(uint frame, GpuSimulationResources resources)
    {
        if (resources.PhaseEventCounters is null || resources.PhaseEventStaging is null) return;
        var context = resources.Context;
        context.CopyResource(resources.PhaseEventCounters.Buffer, resources.PhaseEventStaging);
        var mapped = context.MapSubresource(resources.PhaseEventStaging, 0, MapMode.Read, MapFlags.None);
        int[] counts = new int[4];
        Marshal.Copy(mapped.DataPointer, counts, 0, 4);
        context.UnmapSubresource(resources.PhaseEventStaging, 0);
        Events.Add((frame, (uint)counts[0], (uint)counts[1], (uint)counts[2], (uint)counts[3]));
        if (frame % 1800 == 0)
            Console.WriteLine($"PHYXEL_STEAM_CYCLE_PROGRESS frame={frame} evaporations={counts[0]} condensations={counts[1]} upper={counts[2]}");
    }

    public static bool Validate(AcceptanceScenarioMode mode, SimulationWorldSnapshot snapshot,
        MaterialRegistry materials, IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        string directory, out string report)
    {
        uint gas = materials.GetRequiredRuntimeIndex(mode == AcceptanceScenarioMode.Co2Layer
            ? CoreMaterialIds.Co2 : CoreMaterialIds.Steam);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        var cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        Directory.CreateDirectory(directory);
        if (mode == AcceptanceScenarioMode.SteamEnergy)
            return SteamEnergyAcceptance.Validate(snapshot, materials, directory, out report);
        if (mode == AcceptanceScenarioMode.SteamSurface)
        {
            GridCell cold = cells[120 * snapshot.Width + 200], warm = cells[120 * snapshot.Width + 300];
            GridCell energyGas = cells[120 * snapshot.Width + 400];
            double initialEnergy = 200 * materials[CoreMaterialIds.Steam].Properties.HeatCapacity +
                8 * 180 * 7.8 * materials[CoreMaterialIds.Metal].Properties.HeatCapacity +
                3 * 20 * materials[CoreMaterialIds.Fixture].Properties.HeatCapacity;
            double finalEnergy = 0;
            for (int y = 119; y <= 121; y++) for (int x = 398; x <= 401; x++)
            {
                var c = cells[y * snapshot.Width + x];
                if (c.IsActive != 0) finalEnergy += c.Mass * materials[c.MaterialIndex].Properties.HeatCapacity * c.Temperature;
            }
            double energyError = Math.Abs(finalEnergy - initialEnergy) / initialEnergy;
            report = string.Create(CultureInfo.InvariantCulture,
                $"PHYXEL_STEAM_SURFACE coldMaterial={cold.MaterialIndex} coldTemperature={cold.Temperature:F3} warmMaterial={warm.MaterialIndex} warmTemperature={warm.Temperature:F3} energyError={energyError:F6}");
            File.WriteAllBytes(Path.Combine(directory, "final-grid.bin"), snapshot.Grid);
            return cold.MaterialIndex == water && warm.MaterialIndex == gas && energyGas.MaterialIndex == gas && energyError < .003;
        }
        double mass = 0, gasMass = 0, bottomMass = 0, xSum = 0, xxSum = 0;
        int leaks = 0;
        int[] bins = new int[8];
        for (int i = 0; i < cells.Length; i++)
        {
            GridCell cell = cells[i];
            if (cell.IsActive == 0 || (cell.MaterialIndex != gas &&
                !(mode == AcceptanceScenarioMode.SteamCycle && cell.MaterialIndex == water))) continue;
            int x = i % snapshot.Width, y = i / snapshot.Width;
            mass += cell.Mass;
            if (cell.MaterialIndex == gas) gasMass += cell.Mass;
            xSum += x * cell.Mass; xxSum += x * x * cell.Mass;
            if (y >= 220) bottomMass += cell.Mass;
            if (mode == AcceptanceScenarioMode.Co2Layer)
            {
                if (x < 124 || x > 355 || y < 84 || y > 239) leaks++;
                else bins[Math.Min(7, (x - 124) * 8 / 232)]++;
            }
            else if (x < 184 || x > 295 || y < 116 || y > 239) leaks++;
        }
        double sigma = Math.Sqrt(Math.Max(0, xxSum / Math.Max(1, mass) - Math.Pow(xSum / Math.Max(1, mass), 2)));
        File.WriteAllBytes(Path.Combine(directory, "final-grid.bin"), snapshot.Grid);
        using (var writer = new StreamWriter(Path.Combine(directory, "cycle-checkpoints.csv")))
        {
            writer.WriteLine("frame,waterMass,steamMass");
            foreach (var checkpoint in checkpoints)
            {
                double w = 0, s = 0;
                foreach (var c in MemoryMarshal.Cast<byte, GridCell>(checkpoint.Snapshot.Grid))
                    if (c.IsActive != 0) { if (c.MaterialIndex == water) w += c.Mass; if (c.MaterialIndex == gas) s += c.Mass; }
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{checkpoint.Frame},{w},{s}"));
            }
        }
        if (mode == AcceptanceScenarioMode.Co2Layer)
        {
            report = string.Create(CultureInfo.InvariantCulture,
                $"PHYXEL_CO2_LAYER mass={mass} sigmaX={sigma:F3} bottomFraction={bottomMass / Math.Max(1, mass):F3} bins={string.Join(',', bins)} leaks={leaks}");
            // Reject a central heap even when a few outliers reach the ends.
            double binMean = mass / bins.Length;
            return Math.Abs(mass - 960) < 0.01 && leaks == 0 && sigma >= 60 &&
                bins.Min() >= binMean * .75 && bins.Max() <= binMean * 1.25 && bottomMass >= mass * 0.8;
        }
        using (var writer = new StreamWriter(Path.Combine(directory, "phase-events.csv")))
        {
            writer.WriteLine("frame,evaporations,condensations,coldCondensations,hotEvaporations");
            foreach (var e in Events) writer.WriteLine($"{e.Frame},{e.Evap},{e.Cond},{e.Cold},{e.Hot}");
        }
        var last = Events.LastOrDefault();
        report = string.Create(CultureInfo.InvariantCulture,
            $"PHYXEL_STEAM_CYCLE mass={mass} steam={gasMass} evaporations={last.Evap} condensations={last.Cond} coldCondensations={last.Cold} hotEvaporations={last.Hot} leaks={leaks}");
        bool conservedCheckpoints = checkpoints.All(checkpoint =>
            WaterSteamMass(checkpoint.Snapshot, water, gas) is > 47.99 and < 48.01);
        return Math.Abs(mass - 48) < 0.01 && conservedCheckpoints && leaks == 0 &&
            last.Cold > 48 && last.Hot > 48 && last.Cond > 48;
    }

    private static double WaterSteamMass(SimulationWorldSnapshot snapshot, uint water, uint steam)
    {
        double mass = 0;
        foreach (var cell in MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid))
            if (cell.IsActive != 0 && (cell.MaterialIndex == water || cell.MaterialIndex == steam))
                mass += cell.Mass;
        return mass;
    }

    private static void Fill(Span<GridCell> cells, int width, int left, int top, int right, int bottom,
        uint material, float temperature, float mass)
    {
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
            cells[y * width + x] = new GridCell
            {
                MaterialIndex = material,
                Temperature = temperature,
                Mass = mass,
                IsActive = 1,
                RestFrames = 2
            };
    }
}
