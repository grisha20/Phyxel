using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class SteamEnergyAcceptance
{
    // Run with diagnostic copies of the core JSONs: ambient cooling disabled,
    // fixture conductivity zero. There are no temperature brushes or sources.
    public static void Populate(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        if (materials[CoreMaterialIds.Fixture].Properties.ThermalConductivity != 0 ||
            materials[CoreMaterialIds.Steam].Properties.AmbientCoolingRate != 0)
            throw new InvalidOperationException("steam_energy requires insulated diagnostic materials.");
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        for (int i = 0; i < 4; i++)
            Box(cells, width, materials, 179 + i * 30, 119, 181 + i * 30, 121);
        Set(cells, width, 180, 120, water, 110, 0);
        Set(cells, width, 210, 120, water, 100, 2256);
        Set(cells, width, 240, 120, steam, 80, 0);
        Set(cells, width, 270, 120, steam, 97.99f, 2260.2f);
        // A finite cold metal reservoir absorbs condensation heat.
        Box(cells, width, materials, 298, 118, 302, 122);
        for (int y = 119; y <= 121; y++) for (int x = 299; x <= 301; x++)
            Set(cells, width, x, y, materials.GetRequiredRuntimeIndex(CoreMaterialIds.Metal), 20, 0, 78);
        Set(cells, width, 300, 120, steam, 122, 0);
        // Liquid transport/merging carries different latent progress. No heat
        // escapes these boxes; the vapour box also exercises gas movement.
        Box(cells, width, materials, 100, 170, 150, 220);
        Set(cells, width, 120, 180, water, 100, 1700, .6f);
        Set(cells, width, 121, 180, water, 60, 0, .4f);
        Box(cells, width, materials, 330, 170, 380, 220);
        Set(cells, width, 350, 205, steam, 98, 500);
        Set(cells, width, 351, 205, steam, 98, 1500);
    }

    private static void Box(Span<GridCell> cells, int width, MaterialRegistry materials,
        int left, int top, int right, int bottom)
    {
        uint wall = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
            if (x == left || x == right || y == top || y == bottom)
                Set(cells, width, x, y, wall, 20, 0);
    }

    private static void Set(Span<GridCell> cells, int width, int x, int y,
        uint material, float temperature, float progress, float mass = 1) =>
        cells[y * width + x] = new GridCell { MaterialIndex = material, Temperature = temperature,
            PhaseProgress = progress, Mass = mass, IsActive = 1, RestFrames = 2 };

    public static bool Validate(SimulationWorldSnapshot snapshot, MaterialRegistry materials,
        string directory, out string report)
    {
        byte[] initialBytes = new byte[snapshot.Grid.Length];
        Populate(MemoryMarshal.Cast<byte, GridCell>(initialBytes.AsSpan()), snapshot.Width, materials);
        MaterialProperties[] table = materials.CreateGpuTable();
        double before = Energy(initialBytes, table), after = Energy(snapshot.Grid, table);
        double error = Math.Abs(after - before) / before;
        int[] lefts = [179, 209, 239, 269, 298, 100, 330];
        int[] rights = [181, 211, 241, 271, 302, 150, 380];
        bool regionsConserved = true;
        using (var writer = new StreamWriter(Path.Combine(directory, "energy-regions.csv")))
        {
            writer.WriteLine("region,initial,final,absoluteError");
            for (int i = 0; i < lefts.Length; i++)
            {
                double regionBefore = RegionEnergy(initialBytes, snapshot.Width, table, lefts[i], rights[i]);
                double regionAfter = RegionEnergy(snapshot.Grid, snapshot.Width, table, lefts[i], rights[i]);
                double regionError = Math.Abs(regionAfter - regionBefore);
                regionsConserved &= regionError < .01;
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i},{regionBefore:R},{regionAfter:R},{regionError:R}"));
            }
        }
        var cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        GridCell pending = cells[120 * snapshot.Width + 180];
        GridCell boiled = cells[120 * snapshot.Width + 210];
        GridCell cooling = cells[120 * snapshot.Width + 240];
        GridCell condensed = cells[120 * snapshot.Width + 270];
        GridCell surface = cells[120 * snapshot.Width + 300];
        double mass = 0;
        foreach (var c in cells)
            if (c.IsActive != 0 && (c.MaterialIndex == water || c.MaterialIndex == steam)) mass += c.Mass;
        string path = Path.Combine(directory, "energy.scene.json");
        bool saved = Task.Run(async () => {
            var serializer = new SimulationStateSerializer();
            await serializer.SaveAsync(path, new SimulationSettings(), (ushort)water, snapshot, materials);
            var loaded = await serializer.LoadAsync(path, materials);
            return loaded?.World is { } world && world.Grid.AsSpan().SequenceEqual(snapshot.Grid);
        }).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(directory, "final-grid.bin"), snapshot.Grid);
        report = string.Create(CultureInfo.InvariantCulture,
            $"PHYXEL_STEAM_ENERGY initial={before:F6} final={after:F6} relativeError={error:E4} mass={mass:F6} pendingT={pending.Temperature:F4} pendingHeat={pending.PhaseProgress:F4} boiled={boiled.MaterialIndex}/{boiled.Temperature:F4} coolingT={cooling.Temperature:F4} coolingHeat={cooling.PhaseProgress:F4} condensed={condensed.MaterialIndex}/{condensed.Temperature:F4} surface={surface.MaterialIndex}/{surface.Temperature:F4} saved={saved}");
        return error < .00005 && regionsConserved && Math.Abs(mass - 8) < .0001 && saved &&
            pending.MaterialIndex == water && Math.Abs(pending.Temperature - 100) < .001 &&
            Math.Abs(pending.PhaseProgress - 41.8) < .002 && boiled.MaterialIndex == steam &&
            Math.Abs(boiled.Temperature - 100) < .001 && cooling.MaterialIndex == steam &&
            Math.Abs(cooling.Temperature - 98) < .001 && Math.Abs(cooling.PhaseProgress - 37.44) < .002 &&
            condensed.MaterialIndex == water && Math.Abs(condensed.Temperature - 98) < .02 &&
            surface.MaterialIndex == water;
    }

    private static double Energy(byte[] bytes, MaterialProperties[] table)
    {
        double energy = 0;
        foreach (var cell in MemoryMarshal.Cast<byte, GridCell>(bytes))
            if (cell.IsActive != 0) energy += cell.Mass * (double)PhaseEnthalpy.SpecificEnergy(cell, table);
        return energy;
    }

    private static double RegionEnergy(byte[] bytes, int width, MaterialProperties[] table, int left, int right)
    {
        double energy = 0;
        var cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        for (int i = 0; i < cells.Length; i++)
            if (i % width >= left && i % width <= right && cells[i].IsActive != 0)
                energy += cells[i].Mass * (double)PhaseEnthalpy.SpecificEnergy(cells[i], table);
        return energy;
    }
}
