using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class WaterConvectionAcceptance
{
    private static SimulationWorldSnapshot? initial;
    internal static string? RestartPath => Environment.GetEnvironmentVariable("PHYXEL_CONVECTION_RESTART");
    internal static bool IsMode(AcceptanceScenarioMode mode) => mode is
        AcceptanceScenarioMode.WaterConvection or AcceptanceScenarioMode.WaterConvectionPause or
        AcceptanceScenarioMode.WaterConvectionHeated;
    internal static int Fps => int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"), out int fps) ? fps : 60;
    internal static SimulationWorldSnapshot? Create(AcceptanceScenarioMode mode, int width, int height, MaterialRegistry registry)
    {
        if (!IsMode(mode)) return null;
        if (!string.IsNullOrEmpty(RestartPath))
        {
            initial = Task.Run(async () => (await new SimulationStateSerializer().LoadAsync(RestartPath, registry))?.World
                ?? throw new InvalidDataException("Missing convection restart world.")).GetAwaiter().GetResult();
            ThermalDeviceAcceptance.SetInitial(initial);
            return initial;
        }
        if (width < 480 || height < 200) throw new InvalidOperationException("Convection fixtures require 480x200 cells.");
        if (registry[CoreMaterialIds.Fixture].Properties.ThermalConductivity != 0)
            throw new InvalidOperationException("Convection fixtures require insulated diagnostic walls.");
        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        void Put(int x, int y, string material, float temperature, uint tag = 0) =>
            MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan())[y * width + x] = new()
            { MaterialIndex = registry.GetRequiredRuntimeIndex(material), Mass = 1, Temperature = temperature,
                BodyId = tag, RestFrames = 60, IsActive = 1 };
        for (int box = 0; box < 4; box++)
        {
            int left = 40 + box * 110, right = left + 90;
            for (int y = 100; y <= 180; y++) for (int x = left; x <= right; x++)
            {
                if (x == left || x == right || y == 100 || y == 180)
                    Put(x, y, CoreMaterialIds.Fixture, 20);
                else
                {
                    bool hot = box == 1 ? y < 132 : box != 3 && y >= 149;
                    uint tag = box == 0 ? (hot ? 1u : 2u) :
                        box == 2 ? (y < 140 ? 3u : 4u) : (uint)(y * width + x + 1000);
                    Put(x, y, CoreMaterialIds.Water, box == 3 ? 50 : hot ? 80 : 20, tag);
                }
            }
            if (box == 2)
                for (int x = left + 1; x < right; x++) Put(x, 140, CoreMaterialIds.Metal, 20);
        }
        // A saturated packet carries unfinished boiling energy while rising.
        for (int y = 210; y <= 213; y++) for (int x = 200; x <= 203; x++)
        {
            if (x is 200 or 203 || y is 210 or 213) Put(x, y, CoreMaterialIds.Fixture, 20);
            else
            {
                Put(x, y, CoreMaterialIds.Water, y == 212 ? 100 : 20, y == 212 ? 5u : 6u);
                if (y == 212) MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan())[y * width + x].PhaseProgress = 1700;
            }
        }
        if (mode == AcceptanceScenarioMode.WaterConvectionHeated)
        {
            // Replace the initially warm layer with room-temperature water.
            for (int y = 101; y < 180; y++) for (int x = 41; x < 130; x++)
                Put(x, y, CoreMaterialIds.Water, 20, y >= 149 ? 1u : 2u);
            ThermalDeviceAcceptance.FillDevices(MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan()), width,
                registry, 41, 180, 129, 180, true, 80, 2000);
        }
        initial = new(width, height, bytes);
        ThermalDeviceAcceptance.SetInitial(initial);
        return initial;
    }

    private static string Metrics(SimulationWorldSnapshot world, uint water, out double warmY,
        out double coldY, out double upperTemperature, out int stableMoved, out int barrierCrossed,
        out int uniformMoved, out double mass, out int leaks, out int wallsChanged)
    {
        var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        var before = MemoryMarshal.Cast<byte, GridCell>(initial!.Grid);
        double warm = 0, cold = 0, heat = 0;
        int warmCount = 0, coldCount = 0, upperCount = 0;
        stableMoved = barrierCrossed = uniformMoved = leaks = wallsChanged = 0; mass = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            var c = cells[i]; int x = i % world.Width, y = i / world.Width;
            if (before[i].IsActive != 0 && before[i].MaterialIndex != water &&
                (c.IsActive == 0 || c.MaterialIndex != before[i].MaterialIndex || c.Mass != before[i].Mass)) wallsChanged++;
            if (c.IsActive == 0 || c.MaterialIndex != water) continue;
            mass += c.Mass;
            if (before[i].IsActive == 0 || before[i].MaterialIndex != water) leaks++;
            if (y < 101 || y >= 180) continue;
            if (x > 40 && x < 130)
            {
                if (c.BodyId == 1) { warm += y; warmCount++; }
                if (c.BodyId == 2) { cold += y; coldCount++; }
                if (y < 125) { heat += c.Temperature; upperCount++; }
            }
            else if (x > 150 && x < 240 && c.BodyId != before[i].BodyId) stableMoved++;
            else if (x > 260 && x < 350 && ((y < 140 && c.BodyId == 4) || (y > 140 && c.BodyId == 3)))
                barrierCrossed++;
            else if (x > 370 && x < 460 && c.BodyId != before[i].BodyId) uniformMoved++;
        }
        warmY = warm / Math.Max(1, warmCount); coldY = cold / Math.Max(1, coldCount);
        upperTemperature = heat / Math.Max(1, upperCount);
        return FormattableString.Invariant($"warmY={warmY:F3} coldY={coldY:F3} upperT={upperTemperature:F3} stableMoved={stableMoved} barrierCrossed={barrierCrossed} uniformMoved={uniformMoved} mass={mass:F6} leaks={leaks} wallsChanged={wallsChanged}");
    }

    internal static bool Validate(AcceptanceScenarioMode mode, SimulationWorldSnapshot final,
        MaterialRegistry registry, IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, string directory, out string report)
    {
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        string metrics = Metrics(final, water, out double warmY, out double coldY, out double upper,
            out int stable, out int crossed, out int uniform, out double mass, out int leaks, out int walls);
        Metrics(initial!, water, out double initialWarmY, out double initialColdY, out _, out _, out _, out _, out double initialMass, out _, out _);
        bool energy = ThermalDeviceAcceptance.Audit(final, registry, directory, out string audit);
        bool transport = mode == AcceptanceScenarioMode.WaterConvectionPause
            ? final.Grid.AsSpan().SequenceEqual(initial!.Grid)
            : !string.IsNullOrEmpty(RestartPath) ? warmY <= initialWarmY + 1 && coldY >= initialColdY - 1
            : warmY < initialWarmY - 12 && coldY > initialColdY + 7 && upper > 22;
        Dictionary<uint, int> Tags(byte[] bytes)
        {
            var result = new Dictionary<uint, int>();
            foreach (var c in MemoryMarshal.Cast<byte, GridCell>(bytes))
                if (c.IsActive != 0 && c.MaterialIndex == water)
                    result[c.BodyId] = result.GetValueOrDefault(c.BodyId) + 1;
            return result;
        }
        var initialTags = Tags(initial!.Grid); var finalTags = Tags(final.Grid);
        bool tags = initialTags.Count == finalTags.Count;
        foreach (var pair in initialTags) tags &= finalTags.GetValueOrDefault(pair.Key) == pair.Value;
        bool latent = true;
        if (registry[CoreMaterialIds.Water].Properties.ThermalConductivity == 0)
        {
            var cells = MemoryMarshal.Cast<byte, GridCell>(final.Grid);
            float progress = 0;
            for (int y = 211; y <= 212; y++) for (int x = 201; x <= 202; x++)
                progress += cells[y * final.Width + x].PhaseProgress;
            latent = Math.Abs(progress - 3400) < .001;
        }
        bool pass = energy && transport && latent && tags && stable == 0 && crossed == 0 && uniform == 0 && walls == 0 &&
            leaks == 0 && Math.Abs(mass - initialMass) < .0001;
        using var trace = new StreamWriter(Path.Combine(directory, "convection-checkpoints.csv"));
        trace.WriteLine("frame,thermalTicks,metrics");
        foreach (var sample in checkpoints)
        {
            trace.WriteLine($"{sample.Frame},{sample.ThermalTicks},{Metrics(sample.Snapshot, water, out _, out _, out _, out _, out _, out _, out _, out _, out _)}");
            File.WriteAllBytes(Path.Combine(directory, $"grid-tick-{sample.ThermalTicks}.bin"), sample.Snapshot.Grid);
        }
        report = $"PHYXEL_WATER_CONVECTION mode={mode} transport={transport} latent={latent} tags={tags} {metrics} {audit}";
        return pass;
    }
}
