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
        foreach (string control in new[] { "cold", "range", "wall", "water", "insulator" })
        {
            var grid = new GridCell[w * r.Height]; grid[centre] = Cell(metal, 20, 7.8f);
            grid[centre + (control == "range" ? 25 : 12) * w] = Cell(fire, control == "cold" ? 20 : 1000);
            if (control == "wall" || control == "water") grid[centre + 6 * w] = Cell(control == "wall" ? fixture : water, 20);
            float savedK = table[metal].ThermalConductivity;
            if (control == "insulator") { table[metal].ThermalConductivity = 0; r.Materials.Upload(r.Context, table); }
            var after = Advance(grid, 1, control);
            Check(after[centre].Temperature == 20, control + " recipient must stay cold");
            table[metal].ThermalConductivity = savedK; r.Materials.Upload(r.Context, table);
        }
        // A surface sees many emitters; the pair sum must remain bounded and
        // conserve heat even when the recipient starts on its melting plateau.
        var many = new GridCell[w * r.Height]; many[centre] = Cell(metal, 1000, 7.8f);
        for (int distance = 2; distance <= 24; distance++)
            foreach (int direction in new[] { -1, 1, -w, w }) many[centre + direction * distance] = Cell(fire, 1500, .2f);
        var melted = Advance(many, 120, "many/melting");
        Check(melted[centre].Temperature == 1000 && melted[centre].Lifetime > 1, "metal melting plateau receives Q");
        Advance(many, 600, "many/long");
        // Water receives heat from the visible metal by its existing contact;
        // the new distant transfer must not discard boiling enthalpy.
        var wet = new GridCell[w * r.Height]; wet[centre] = Cell(water, 100);
        wet[centre + w] = Cell(metal, 100, 7.8f); wet[centre + 13 * w] = Cell(fire, 1000, 5);
        var boiled = Advance(wet, 600, "water/contact");
        Check(boiled[centre].Temperature == 100 && boiled[centre].Lifetime > .01f, "water boiling plateau receives Q");
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
