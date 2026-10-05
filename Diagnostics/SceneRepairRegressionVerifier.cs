using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Phyxel.Diagnostics;

// Each yield returns to Update/Draw; no second simulator or blocked UI loop.
internal static class SceneRepairRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/scene-repair";
        Directory.CreateDirectory(dir);
        settings.ApplyScale(.25f); settings.Paused = true; settings.AirSimulation = false;
        settings.OpenBoundaries = false; settings.HydraulicPressure = false;
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            oil = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),
            metal = registry.GetRequiredRuntimeIndex("core:molten_metal"),
            coal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),
            powder = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
        var r = coordinator.DispatchFrame(settings, [new() { X = 20, Y = 20, Radius = 1,
            Density = 1, MaterialIndex = water }], 0);
        yield return r;
        int w = r.Width, h = r.Height, n = w * h, index = 120 * w + 220, failures = 0;
        var serializer = new SimulationStateSerializer(); var table = registry.CreateGpuTable();
        var results = new List<object>();
        void Record(string test, bool pass, object? details = null)
        {
            if (!pass) failures++;
            results.Add(new { test, pass, details });
            Console.WriteLine("PHYXEL_SCENE_REPAIR " + JsonSerializer.Serialize(results[^1]));
            File.WriteAllText(Path.Combine(dir, "measurements.json"),
                JsonSerializer.Serialize(new { failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        GridCell[] Read() => MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
        void Load(GridCell[] cells, uint[]? map = null)
        {
            serializer.ApplyWorldSnapshot(r, new(w, h, MemoryMarshal.AsBytes(cells.AsSpan()).ToArray(),
                Filters: map is null ? null : MemoryMarshal.AsBytes(map.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r, true, true, false);
            r.Materials.Upload(r.Context, table);
        }
        void Contact(int tick)
        {
            var constants = new ContactTransitionConstants { DeltaTime = .05f, Width = (uint)w, Height = (uint)h, TickIndex = (uint)tick };
            var ctx = r.Context; ctx.UpdateSubresource(ref constants, r.ContactTransitionConstants);
            ctx.ComputeShader.Set(r.MoistureShader); ctx.ComputeShader.SetConstantBuffer(0, r.ContactTransitionConstants);
            ctx.ComputeShader.SetShaderResource(0, r.Materials.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0, r.Grid.ReadUnorderedView, r.CellMaterials.UnorderedView,
                r.GasMotion.UnorderedView, r.ContactSummary.UnorderedView);
            ctx.Dispatch((w + 15) / 16, (h + 15) / 16, 1);
            ctx.ComputeShader.SetShaderResource(0, null);
            for (int i = 0; i < 4; i++) ctx.ComputeShader.SetUnorderedAccessView(i, null);
            ctx.ComputeShader.Set(null);
        }
        void Burn(SimulationMode mode, float oxygen, float dt)
        {
            var ctx = r.Context;
            ctx.UpdateSubresource(Enumerable.Repeat(oxygen, n).ToArray(), r.OxidizerAvailable.Buffer);
            ctx.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView, new SharpDX.Mathematics.Interop.RawInt4(-1, -1, -1, -1));
            var constants = new CombustionConstants { Width = (uint)w, Height = (uint)h, DeltaTime = dt,
                TickIndex = 1, MaterialCount = (uint)registry.Count,
                FiniteOxidizer = mode == SimulationMode.Simulation ? 1u : 0u, Reserved1 = 1 };
            ctx.UpdateSubresource(ref constants, r.CombustionConstants);
            ctx.ComputeShader.Set(r.CombustionShader); ctx.ComputeShader.SetConstantBuffer(0, r.CombustionConstants);
            ctx.ComputeShader.SetShaderResources(0, r.Materials.View, r.Emissions.View, r.OxidizerAvailable.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0, r.Grid.ReadUnorderedView, r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView, r.EmissionRequests.UnorderedView, r.OxidizerDemand.UnorderedView, r.ReactionPending.UnorderedView);
            ctx.Dispatch((w + 15) / 16, (h + 15) / 16, 1);
            for (int i = 0; i < 3; i++) ctx.ComputeShader.SetShaderResource(i, null);
            for (int i = 0; i < 6; i++) ctx.ComputeShader.SetUnorderedAccessView(i, null);
            ctx.ComputeShader.Set(null);
        }
        // All five porous hosts reject molten metal; water/oil remain supported.
        foreach (string id in new[] { CoreMaterialIds.Wood, CoreMaterialIds.Coal, CoreMaterialIds.WetCharcoal,
            CoreMaterialIds.Gunpowder, CoreMaterialIds.Sand })
        foreach (uint species in new[] { metal, oil, water })
        {
            uint host = registry.GetRequiredRuntimeIndex(id); var cells = new GridCell[n];
            cells[index] = new() { IsActive = 1, MaterialIndex = host, Mass = 1, Temperature = species == metal ? 1050 : 30 };
            cells[index + 1] = new() { IsActive = 1, MaterialIndex = species, Mass = .2f, Temperature = species == metal ? 1050 : 30 };
            Load(cells);
            double before = cells.Sum(c => (double)c.Mass + c.FuelMass + c.MoistureMass);
            double energy = cells.Where(c => c.IsActive != 0).Sum(c => c.Mass * (double)PhaseEnthalpy.SpecificEnergy(c, table));
            for (int tick = 0; tick < 16; tick++) { Contact(tick); if (tick % 4 == 3) yield return r; }
            var after = Read(); float stock = species == water ? after[index].MoistureMass : after[index].FuelMass;
            double massError = Math.Abs(before - after.Where(c => c.IsActive != 0).Sum(c => (double)c.Mass + c.FuelMass + c.MoistureMass));
            double qError = Math.Abs(energy - after.Where(c => c.IsActive != 0).Sum(c => c.Mass * (double)PhaseEnthalpy.SpecificEnergy(c, table))) / Math.Max(1, Math.Abs(energy));
            Record("pore-species", (species == metal ? stock == 0 && after[index + 1].Mass == .2f : stock > 0) && massError < 1e-5 && qError < 1e-4,
                new { id, species = registry[species].Id, stock, massError, qError });
        }
        // The opt-out is a property, including the primary moisture channel.
        {
            var cells = new GridCell[n]; uint sand = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
            cells[index] = new() { IsActive = 1, MaterialIndex = sand, Mass = 1, Temperature = 30 };
            cells[index + 1] = new() { IsActive = 1, MaterialIndex = water, Mass = .2f, Temperature = 30 };
            Load(cells); var excluded = table.ToArray(); excluded[water].Flags |= (uint)MaterialFlags.NonAbsorbableLiquid;
            r.Materials.Upload(r.Context, excluded);
            for (int tick = 0; tick < 8; tick++) Contact(tick); yield return r;
            Record("primary-liquid-opt-out", Read()[index].MoistureMass == 0 && Read()[index + 1].Mass == .2f);
        }
        // Recovery from the exact .0002 carrier floor of the failed live scene.
        foreach (var mode in Enum.GetValues<SimulationMode>())
        foreach (uint host in new[] { powder, coal })
        foreach (uint species in new[] { metal, oil })
        {
            var cells = new GridCell[n]; float initial = species == metal ? 1050 : 500;
            cells[index] = new() { IsActive = 1, MaterialIndex = host, Mass = .0002f,
                Temperature = initial, FuelMass = .04f, RetainedLiquidMaterialIndex = species };
            Load(cells); Burn(mode, 1, .1f); yield return r;
            var released = Read()[index];
            float cap = .0002f * table[host].HeatCapacity + .04f * table[species].HeatCapacity;
            float rise = host == powder ? .0002f * table[host].HeatPerMass / (.04f * table[species].HeatCapacity)
                : .0002f * table[host].HeatPerMass / Math.Max(.01f, cap);
            float expectedT = Math.Min(table[host].MaximumCombustionTemperature, initial + rise);
            double expectedQ = .04 * table[species].HeatCapacity * expectedT;
            double actualQ = released.Mass * (double)PhaseEnthalpy.SpecificEnergy(released, table);
            double qError = Math.Abs(actualQ - expectedQ) / Math.Max(1, Math.Abs(expectedQ));
            Record("burnout-release", released.IsActive == 1 && released.MaterialIndex == species &&
                released.Mass == .04f && released.FuelMass == 0 && released.RetainedLiquidMaterialIndex == 0 && qError < 1e-5,
                new { mode, host = registry[host].Id, species = registry[species].Id, released.Mass, released.Temperature, qError });
            string path = Path.Combine(dir, $"released-{mode}-{host}-{species}.json");
            var saving = serializer.SaveAsync(path, settings, (ushort)host,
                new(w, h, MemoryMarshal.AsBytes(Read().AsSpan()).ToArray()), registry);
            while (!saving.IsCompleted) yield return r;
            saving.GetAwaiter().GetResult();
            var loading = serializer.LoadAsync(path, registry); while (!loading.IsCompleted) yield return r;
            var saved = MemoryMarshal.Cast<byte, GridCell>(loading.GetAwaiter().GetResult()!.World!.Grid).ToArray()[index];
            Record("released-save-load", saved.Equals(released));
        }
        foreach (var mode in Enum.GetValues<SimulationMode>())
        {
            var cells = new GridCell[n]; uint wood = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood);
            cells[index] = new() { IsActive = 1, MaterialIndex = wood, Mass = table[coal].Density + .0002f,
                Temperature = 500, FuelMass = .1f, RetainedLiquidMaterialIndex = metal };
            Load(cells); Burn(mode, 1, .1f); yield return r;
            var residue = Read()[index];
            Record("wood-residue-keeps-stock", residue.MaterialIndex == coal && residue.FuelMass == .1f &&
                residue.RetainedLiquidMaterialIndex == metal && residue.Mass == table[coal].Density, new { mode });
        }
        // Excluded legacy species may stay in old saves but cannot spread to another grain.
        {
            var cells = new GridCell[n]; uint sand = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
            cells[index] = new() { IsActive = 1, MaterialIndex = sand, Mass = 1, Temperature = 1050,
                FuelMass = .1f, RetainedLiquidMaterialIndex = metal };
            cells[index + 1] = new() { IsActive = 1, MaterialIndex = sand, Mass = 1, Temperature = 1050 };
            Load(cells); for (int tick = 0; tick < 8; tick++) Contact(tick); yield return r;
            var after = Read();
            Record("legacy-metal-does-not-diffuse", after[index].FuelMass == .1f && after[index + 1].FuelMass == 0);
        }
        foreach (var mode in Enum.GetValues<SimulationMode>())
        foreach (uint host in new[] { powder, coal })
        {
            var cells = new GridCell[n];
            cells[index] = new() { IsActive = 1, MaterialIndex = host, Mass = .0002f, Temperature = 500 };
            Load(cells); Burn(mode, 1, .1f); yield return r;
            Record("dry-burnout-flame", Read()[index].MaterialIndex == registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
                new { mode, host = registry[host].Id });
        }
        foreach (var condition in new[] { "dry-no-oxygen", "wet-powder", "coal-no-oxygen" })
        {
            var cells = new GridCell[n]; uint host = condition == "coal-no-oxygen" ? coal : powder;
            cells[index] = new() { IsActive = 1, MaterialIndex = host, Mass = 1, Temperature = 500,
                MoistureMass = condition == "wet-powder" ? .1f : 0 };
            Load(cells); Burn(SimulationMode.Simulation, 0, .01f); yield return r;
            var after = Read()[index];
            Record(condition, condition == "dry-no-oxygen" ? after.Mass < 1 : after.Mass == 1);
        }
        // Real filter input goes through both the controller and GPU eraser.
        {
            var cells = new GridCell[n]; var map = new uint[n];
            cells[index] = new() { IsActive = 1, MaterialIndex = water, Mass = 1, Temperature = 30 };
            map[index] = FilterRules.AllParticles; map[index + 1] = FilterRules.Closed; Load(cells, map);
            settings.Width = w; settings.Height = h; settings.BrushRadius = 2;
            var input = new RawInputSnapshot(new Point(220, 120), 0, false, true, false, false, true, false, false, false, false, false, 0);
            var commands = new CanvasBrushController().CreateCommands(input, new Rectangle(0, 0, w, h), settings,
                (ushort)water, false, false, 30, false, filterTool: true, filterRule: FilterRules.Closed);
            coordinator.DispatchFrame(settings, commands.ToArray(), 0); yield return r;
            Record("filter-right-erases-all", commands[0].Mode == BrushCommandMode.Erase && Read()[index].IsActive == 0 && r.FilterCount == 0);
        }
        foreach (string view in new[] { "flat", "effects", "air" })
        {
            settings.RenderWithoutEffects = view == "flat"; settings.ShowAirField = view == "air";
            var cells = new GridCell[n]; var map = new uint[n]; var presets = Enum.GetValues<FilterSelection>();
            for (int brush = 0; brush < presets.Length; brush++)
                for (int y = 36; y <= 64; y++) for (int x = 24 + brush * 40; x <= 48 + brush * 40; x++)
                    map[y * w + x] = FilterRules.Select(presets[brush], registry, (ushort)coal);
            Load(cells, map); coordinator.DispatchFrame(settings, [], 0); yield return r;
            string path = Path.Combine(dir, "filter-colours-" + view + ".png"); SimulationScreenshotWriter.Save(r, path);
            using var bitmap = new Bitmap(path);
            int[] colours = Enumerable.Range(0, presets.Length).Select(i => bitmap.GetPixel(24 + i * 40, 40).ToArgb()).ToArray();
            Record("filter-colours", colours.Distinct().Count() == presets.Length, new { view, distinct = colours.Distinct().Count(), colours });
        }
        Console.WriteLine($"PHYXEL_SCENE_REPAIR_COMPLETE cases={results.Count} failures={failures}");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
