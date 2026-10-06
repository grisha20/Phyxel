using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class BulkHeatRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var settings = new SimulationSettings { Paused = true, AirSimulation = false };
        var r = coordinator.DispatchFrame(settings, [new() { X = 40, Y = 40, Radius = 1,
            Density = 1, MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }], 0);
        var table = registry.CreateGpuTable();
        foreach (ref var material in table.AsSpan()) material.AmbientCoolingRate = 0;
        uint metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),
            fixture = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        table[fixture].ThermalConductivity = 0; r.Materials.Upload(r.Context, table);
        int w = r.Width, failures = 0, checks = 0;
        GridCell[] Read() => MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
        double Energy(GridCell[] grid) => grid.Where(c => c.IsActive != 0).Sum(c =>
            (double)c.Mass * PhaseEnthalpy.SpecificEnergy(c, table));
        void Check(bool ok, string message)
        { checks++; if (!ok) { failures++; Console.WriteLine("PHYXEL_BULK_FAIL " + message); } }
        GridCell[] Bar(string control, bool phase = false)
        {
            var grid = new GridCell[w * r.Height];
            for (int y = 80; y < 90; y++) for (int x = 80; x < 176; x++)
                grid[y * w + x] = new() { IsActive = 1, MaterialIndex = metal, Mass = 7.8f,
                    Temperature = x >= 124 && x < 132 ? (phase ? 1500 : 800) : (phase ? 1000 : 20) };
            if (control is "gap" or "wall") for (int y = 80; y < 90; y++)
                grid[y * w + 140] = control == "gap" ? default : new()
                { IsActive = 1, MaterialIndex = fixture, Mass = 7.8f, Temperature = 20 };
            return grid;
        }
        GridCell[] Advance(GridCell[] grid, int ticks, string name)
        {
            r.Context.UpdateSubresource(grid, r.Grid.ReadBuffer); double before = Energy(grid);
            for (uint i = 0; i < ticks; i++) coordinator.DispatchThermalDiffusion(r, false, i, false);
            var after = Read(); double error = Math.Abs(Energy(after) - before);
            Check(error < Math.Max(.01, Math.Abs(before) * .00005), name + " energy");
            Check(after.Sum(c => (double)c.Mass) == grid.Sum(c => (double)c.Mass), name + " mass");
            Check(after.Where(c => c.IsActive != 0).All(c => float.IsFinite(c.Temperature) &&
                c.Temperature >= grid.Where(c => c.IsActive != 0).Min(c => c.Temperature) - .001 &&
                c.Temperature <= grid.Where(c => c.IsActive != 0).Max(c => c.Temperature) + .001), name + " range");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_BULK name={name} Qerror={error:F8} distantC={after[85*w+156].Temperature:F6}"));
            return after;
        }
        var hot = Bar("open"); var warm = Advance(hot, 120, "spread6s");
        Check(warm[85*w+156].Temperature > 21, "distant metal must warm across 24 cells");
        foreach (string control in new[] { "gap", "wall" })
        {
            var a = Advance(Bar(control), 120, control);
            Check(Enumerable.Range(80, 10).All(y => Enumerable.Range(141, 35).All(x =>
                Math.Abs(a[y*w+x].Temperature - 20) < .001)), control + " blocks heat");
        }
        float k = table[metal].ThermalConductivity;
        table[metal].ThermalConductivity = .5f; r.Materials.Upload(r.Context, table);
        var slow = Advance(hot, 120, "low-k");
        Check(slow[85*w+156].Temperature < 20.01f, "low conductivity must retain its local behavior");
        table[metal].ThermalConductivity = k; r.Materials.Upload(r.Context, table);
        var melted = Advance(Bar("open", true), 120, "phase");
        // At equal plateau temperatures latent heat has no gradient. Check
        // a direct bulk recipient eight cells beyond the hot region instead.
        Check(melted[85*w+140].Temperature == 1000 && melted[85*w+140].Lifetime > .01f,
            "bulk heat must enter melting plateau without changing its T");
        var serializer = new SimulationStateSerializer();
        var snapshot = new SimulationWorldSnapshot(r.Width, r.Height, MemoryMarshal.AsBytes(warm.AsSpan()).ToArray());
        serializer.ApplyWorldSnapshot(r, snapshot); coordinator.RestoreWorldActivity(r, true, true, false);
        coordinator.DispatchFrame(settings, [], .05f);
        Check(Read().AsSpan().SequenceEqual(warm), "paused heat must stay exact");
        string path = Path.Combine(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!, "bulk-phase.json");
        snapshot = snapshot with { Grid = MemoryMarshal.AsBytes(melted.AsSpan()).ToArray() };
        Task.Run(() => serializer.SaveAsync(path, settings, (ushort)metal, snapshot, registry)).GetAwaiter().GetResult();
        var loaded = Task.Run(() => serializer.LoadAsync(path, registry)).GetAwaiter().GetResult()!.World!;
        Check(loaded.Grid.AsSpan().SequenceEqual(snapshot.Grid), "partial phase save exact");
        serializer.ApplyWorldSnapshot(r, loaded); coordinator.DispatchThermalDiffusion(r, false, 0, false); var next = Read();
        serializer.ApplyWorldSnapshot(r, loaded); coordinator.DispatchThermalDiffusion(r, false, 0, false);
        Check(Read().AsSpan().SequenceEqual(next), "reload thermal continuation exact");
        Console.WriteLine($"PHYXEL_BULK_COMPLETE checks={checks} failures={failures}");
        if (failures > 0 && Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE") != "1")
            throw new InvalidOperationException("Bulk heating checks failed.");
    }
}
