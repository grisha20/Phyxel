using System;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class FurnaceSurfaceHeatVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var settings = new SimulationSettings { Paused = true, AirSimulation = false };
        var r = coordinator.DispatchFrame(settings, [new() { X = 40, Y = 40, Radius = 1,
            Density = 1, MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }], 0);
        var table = registry.CreateGpuTable();
        foreach (ref var material in table.AsSpan()) material.AmbientCoolingRate = 0;
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
            smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),
            metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),
            water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            wood = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood),
            fixture = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        table[fixture].ThermalConductivity = 0;
        r.Materials.Upload(r.Context, table);
        int w = r.Width, centre = 80 * w + 80, failures = 0, checks = 0;
        GridCell Cell(uint id, float t, float mass = 1) => new()
        { IsActive = 1, MaterialIndex = id, Mass = mass, Temperature = t };
        GridCell[] Read() => MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
        double Energy(GridCell[] grid) => grid.Where(c => c.IsActive != 0).Sum(c =>
            (double)c.Mass * PhaseEnthalpy.SpecificEnergy(c, table));
        void Check(bool ok, string message)
        { checks++; if (!ok) { failures++; Console.WriteLine("PHYXEL_SURFACE_FAIL " + message); } }
        GridCell[] Advance(GridCell[] grid, int ticks, string name)
        {
            r.Context.UpdateSubresource(grid, r.Grid.ReadBuffer);
            double before = Energy(grid);
            for (uint i = 0; i < ticks; i++) coordinator.DispatchThermalDiffusion(r, false, i, false);
            var after = Read(); double error = Math.Abs(Energy(after) - before);
            Check(error <= Math.Max(.01, Math.Abs(before) * .00005), name + " energy");
            Check(after.Where(c => c.IsActive != 0).All(c => float.IsFinite(c.Temperature) &&
                c.Temperature >= grid.Where(c => c.IsActive != 0).Min(c => c.Temperature) - .001 &&
                c.Temperature <= grid.Where(c => c.IsActive != 0).Max(c => c.Temperature) + .001), name + " bounded");
            Check(after.Sum(c => (double)c.Mass + c.MoistureMass + c.FuelMass) ==
                grid.Sum(c => (double)c.Mass + c.MoistureMass + c.FuelMass), name + " mass/stocks");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_SURFACE name={name} error={error:F8} receiverC={after[centre].Temperature:F6} progress={after[centre].Lifetime:F6}"));
            return after;
        }
        foreach (uint donor in new[] { fire, smoke })
        {
            var grid = new GridCell[w * r.Height];
            grid[centre] = Cell(metal, 20, 7.8f); grid[centre + 12 * w] = Cell(donor, 1000);
            var after = Advance(grid, 120, "gap/" + donor);
            Check(after[centre].Temperature > 21 && after[centre + 12 * w].Temperature < 999, "gap must heat and cool both endpoints");
        }
        uint iron=registry.GetRequiredRuntimeIndex("core:cast_iron");
        foreach(var (dx,dy) in new[]{(12,0),(0,12),(12,12)})
        {
            var grid=new GridCell[w*r.Height];
            int donor=centre+dx+dy*w;
            grid[centre]=Cell(iron,20,7.2f);grid[donor]=Cell(iron,1000,3.6f);
            var after=Advance(grid,120,$"surface/surface/{dx},{dy}");
            Check(after[centre].Temperature>21 && after[donor].Temperature<999,
                "Visible hot wall must heat the opposite wall without FIRE or hot gas");
        }
        foreach(string control in new[]{"equal","opaque","corner","range"})
        {
            var grid=new GridCell[w*r.Height];
            int donor=centre+(control=="range"?97:12)*w+(control=="corner"?12:0);
            grid[centre]=Cell(iron,20,7.2f);grid[donor]=Cell(iron,control=="equal"?20:1000,3.6f);
            if(control=="opaque")grid[centre+6*w]=Cell(fixture,20);
            if(control=="corner")grid[centre+1]=Cell(fixture,20);
            var after=Advance(grid,1,"surface/control/"+control);
            Check(after[centre].Temperature==20,"Surface radiation must respect "+control);
        }
        foreach (string control in new[] { "cold", "range", "wall", "water", "insulator" })
        {
            var grid = new GridCell[w * r.Height]; grid[centre] = Cell(metal, 20, 7.8f);
            grid[centre + (control == "range" ? 97 : 12) * w] = Cell(fire, control == "cold" ? 20 : 1000);
            if (control == "wall" || control == "water") grid[centre + 6 * w] = Cell(control == "wall" ? fixture : water, 20);
            float savedK = table[metal].ThermalConductivity;
            if (control == "insulator") { table[metal].ThermalConductivity = 0; r.Materials.Upload(r.Context, table); }
            var after = Advance(grid, 1, control);
            Check(after[centre].Temperature == 20, control + " recipient must stay cold");
            table[metal].ThermalConductivity = savedK; r.Materials.Upload(r.Context, table);
        }
        foreach (var (dx, dy) in new[] { (48, 0), (0, 96), (40, 40), (-40, 40), (40, -40), (-40, -40) })
        {
            var grid = new GridCell[w * r.Height]; grid[centre] = Cell(metal, 20, 7.8f);
            int donorIndex = centre + dx + dy * w;
            grid[donorIndex] = Cell(fire, 1000);
            var after = Advance(grid, 1, $"extended/{dx},{dy}");
            Check(after[centre].Temperature > 20 && after[donorIndex].Temperature < 1000, "extended reciprocal visible pair");
            if (dx != 0 && dy != 0)
            {
                grid[centre + Math.Sign(dx)] = Cell(fixture, 20);
                after = Advance(grid, 1, $"corner/{dx},{dy}");
                Check(after[centre].Temperature == 20 && after[donorIndex].Temperature == 1000, "corner occludes both endpoints");
            }
        }
        var diagonalRange = new GridCell[w * r.Height];
        diagonalRange[centre] = Cell(metal, 20, 7.8f);
        diagonalRange[centre + 68 + 68 * w] = Cell(fire, 1000);
        Check(Advance(diagonalRange, 1, "euclidean/range")[centre].Temperature == 20, "diagonal exceeds Euclidean 96 range");
        // A surface sees many emitters; the pair sum must remain bounded and
        // conserve heat even when the recipient starts on its melting plateau.
        var many = new GridCell[w * r.Height]; many[centre] = Cell(metal, 1000, 7.8f);
        for (int distance = 2; distance <= 24; distance++)
            foreach (int direction in new[] { -1, 1, -w, w, -w-1, -w+1, w-1, w+1 }) many[centre + direction * distance] = Cell(fire, 1500, .2f);
        var melted = Advance(many, 120, "many/melting");
        Check(melted[centre].Temperature == 1000 && melted[centre].Lifetime > 1, "metal melting plateau receives Q");
        Advance(many, 600, "many/long");
        var shells = new GridCell[w * r.Height]; shells[centre] = Cell(fire, 1500, .2f);
        foreach (int direction in new[] { -1, 1, -w, w, -w-1, -w+1, w-1, w+1 })
            shells[centre + direction * 48] = Cell(metal, 20, 7.8f);
        Advance(shells, 120, "eight-surfaces/budget");
        var serializer = new Phyxel.Serialization.SimulationStateSerializer();
        var snapshot = new Phyxel.Serialization.SimulationWorldSnapshot(w, r.Height,
            MemoryMarshal.AsBytes(melted.AsSpan()).ToArray());
        serializer.ApplyWorldSnapshot(r, snapshot);
        coordinator.RestoreWorldActivity(r, true, true, false);
        coordinator.DispatchFrame(settings, [], .05f);
        Check(Read().AsSpan().SequenceEqual(melted), "paused radiant phase exact");
        string path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!, "radiant-phase.json");
        System.Threading.Tasks.Task.Run(() => serializer.SaveAsync(path, settings, (ushort)metal, snapshot, registry)).GetAwaiter().GetResult();
        var loaded = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(path, registry)).GetAwaiter().GetResult()!.World!;
        Check(loaded.Grid.AsSpan().SequenceEqual(snapshot.Grid), "radiant partial phase save exact");
        serializer.ApplyWorldSnapshot(r, loaded); coordinator.DispatchThermalDiffusion(r, false, 0, false); var next = Read();
        serializer.ApplyWorldSnapshot(r, loaded); coordinator.DispatchThermalDiffusion(r, false, 0, false);
        Check(Read().AsSpan().SequenceEqual(next), "radiant degree scratch regenerated after reload");
        // Water receives heat from the visible metal by its existing contact;
        // the new distant transfer must not discard boiling enthalpy.
        var wet = new GridCell[w * r.Height]; wet[centre] = Cell(water, 100);
        wet[centre + w] = Cell(metal, 100, 7.8f); wet[centre + 13 * w] = Cell(fire, 1000, 5);
        var boiled = Advance(wet, 2, "water/contact/plateau");
        Check(boiled[centre].Temperature == 100 && boiled[centre].Lifetime > .01f, "water boiling plateau receives Q");
        var spent = Advance(wet, 600, "water/contact/after-latent");
        Check(spent[centre].Lifetime >= boiled[centre].Lifetime, "long transfer preserves spent boiling enthalpy");
        var porous = new GridCell[w * r.Height]; porous[centre] = Cell(wood, 100);
        porous[centre].MoistureMass = .15f; porous[centre].MoistureEnergy = 20;
        porous[centre + 12 * w] = Cell(fire, 1000, 5);
        var drying = Advance(porous, 120, "wood/drying");
        Check(drying[centre].Temperature == 100 && drying[centre].MoistureEnergy > 20.01f,
            "radiant heat enters retained water drying plateau");
        Console.WriteLine($"PHYXEL_SURFACE_COMPLETE checks={checks} failures={failures}");
        if (failures > 0 && Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE") != "1")
            throw new InvalidOperationException("Surface heating checks failed.");
    }
}
