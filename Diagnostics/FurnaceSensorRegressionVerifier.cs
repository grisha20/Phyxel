using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

// Replays the complete user save. No injected heat, changed geometry or forced draft.
internal static class FurnaceSensorRegressionVerifier
{
    internal static GpuSimulationResources LoadFixture(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string path = Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SCENE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phyxel", "Проверка тяги и темпиратур.json");
        var serializer = new SimulationStateSerializer();
        var loaded = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(path, registry)).GetAwaiter().GetResult()!;
        var world = loaded.World!;
        SimulationStateSerializer.Apply(loaded.State, settings);
        settings.Width = world.Width; settings.Height = world.Height; settings.Paused = true;
        if (Enum.TryParse<SimulationMode>(Environment.GetEnvironmentVariable("PHYXEL_SENSOR_MODE"), out var mode)) settings.Mode = mode;
        if (Environment.GetEnvironmentVariable("PHYXEL_SENSOR_DESTRUCTION") is { } destruction)
            settings.PressureDestruction=destruction=="1";
        var r = coordinator.DispatchFrame(settings, [new() { X = 20, Y = 20, Radius = 1, Density = 1,
            MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal) }], 0);
        serializer.ApplyWorldSnapshot(r, world);
        coordinator.RestoreWorldActivity(r, true, true, settings.HydraulicPressure, true);
        if (Environment.GetEnvironmentVariable("PHYXEL_SENSOR_COLD_WALLS") == "1")
        {
            var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid).ToArray();
            uint castIron = registry.GetRequiredRuntimeIndex("core:cast_iron");
            foreach (ref var c in cells.AsSpan()) if (c.IsActive != 0 && c.MaterialIndex == castIron)
            { c.Temperature = 30; c.Lifetime = 0; }
            world = world with { Grid = MemoryMarshal.AsBytes(cells.AsSpan()).ToArray() };
            serializer.ApplyWorldSnapshot(r, world);
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_SENSOR_DISABLE") == "1") settings.TemperatureSensors.Clear();
        settings.Paused = Environment.GetEnvironmentVariable("PHYXEL_SENSOR_PAUSED") == "1";
        return r;
    }

    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string path = Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SCENE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phyxel", "Проверка тяги и темпиратур.json");
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/furnace-sensors";
        Directory.CreateDirectory(dir);
        var serializer = new SimulationStateSerializer();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(path, ".world"))));
        var jsonHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var r = LoadFixture(coordinator, registry, settings);
        if (r.Width != 968 || r.Height != 564)
            throw new InvalidDataException("Sensor measurement sections require the user's 968x564 furnace.");
        int fps = int.Parse(Environment.GetEnvironmentVariable("PHYXEL_SENSOR_FPS") ?? "60");
        int seconds = int.Parse(Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SECONDS") ?? "30");
        var points = settings.TemperatureSensors.ToArray();
        bool observationOnly = Environment.GetEnvironmentVariable("PHYXEL_SENSOR_OBSERVATION_ONLY") == "1";
        var thermalMaterials = registry.CreateGpuTable();
        uint waterIndex = registry.GetRequiredRuntimeIndex("core:water");
        uint steamIndex = registry.GetRequiredRuntimeIndex("core:steam");
        uint ironIndex = registry.GetRequiredRuntimeIndex("core:cast_iron");
        var rows = new List<object>();
        Console.WriteLine($"PHYXEL_SENSOR_REPLAY size={r.Width}x{r.Height} mode={settings.Mode} fps={fps} worldHash={hash}");
        var wallTime = Stopwatch.StartNew();
        using var timer = new GpuStageTimer(r.Device);
        for (int frame = 0; frame <= seconds * fps; frame++)
        {
            if (frame > 0)
            {
                timer.Begin(r.Context);
                coordinator.DispatchFrame(settings, [], 1f / fps);
                timer.End(r.Context);
            }
            if (frame % fps == 0)
            {
                var grid = MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
                var air = MemoryMarshal.Cast<byte, AirCell>(AirInventoryRegressionVerifier.Read(r, r.Air.Buffer)).ToArray();
                var thermal = MemoryMarshal.Cast<byte, System.Numerics.Vector2>(AirInventoryRegressionVerifier.Read(r, r.AirThermal.Buffer)).ToArray();
                var samples = points.Select((p, n) =>
                {
                    var cell = grid[p.Y * r.Width + p.X];
                    double nearbyGasMass = 0, nearbyGasHeat = 0; int count = 0; double temperature = 0;
                    for (int y = Math.Max(0, p.Y - 12); y <= Math.Min(r.Height - 1, p.Y + 12); y++)
                    for (int x = Math.Max(0, p.X - 12); x <= Math.Min(r.Width - 1, p.X + 12); x++)
                    {
                        var c = grid[y * r.Width + x];
                        if (c.IsActive == 0) continue;
                        if (registry[(ushort)c.MaterialIndex].Properties.SimulationKind == (uint)MaterialSimulationKind.Gas)
                        { nearbyGasMass += c.Mass; nearbyGasHeat += c.Mass * c.Temperature; }
                        if (c.MaterialIndex == cell.MaterialIndex) { count++; temperature += c.Temperature; }
                    }
                    return new { number = n + 1, p.X, p.Y, material = registry[(ushort)cell.MaterialIndex].Id,
                        cell.Temperature, wallMean = temperature / Math.Max(1, count), nearbyGasMass,
                        nearbyGasMean = nearbyGasHeat / Math.Max(1e-15, nearbyGasMass) };
                }).ToArray();
                object Section(string name, int x0, int y0, int x1, int y1)
                {
                    double vx = 0, vy = 0, e = 0, c = 0; int count = 0;
                    for (int y = y0 / 4; y <= y1 / 4; y++) for (int x = x0 / 4; x <= x1 / 4; x++)
                    {
                        int i = y * r.AirWidth + x;
                        if (air[i].Blocked > .5) continue;
                        vx += air[i].VelocityX; vy += air[i].VelocityY; e += thermal[i].X; c += thermal[i].Y; count++;
                    }
                    return new { name, vx = vx / Math.Max(1, count), vy = vy / Math.Max(1, count),
                        airC = c > 0 ? (double?)(e / c - 273.15) : null, capacity = c, count };
                }
                var row = new { seconds = frame / fps, samples, sections = new[] {
                    Section("turn1", 584, 350, 598, 420), Section("horizontal", 368, 332, 559, 362),
                    Section("turn5", 316, 316, 337, 360), Section("chimney", 312, 80, 332, 200) },
                    gpu = timer.Statistics, airGpu = coordinator.AirGpuTiming, airHeatGpu = coordinator.AirHeatGpuTiming,
                    gasGpu = coordinator.GasMotionGpuTiming, thermalGpu = coordinator.ThermalGpuTiming,
                    combustionGpu = coordinator.CombustionGpuTiming,
                    water = Inventory(waterIndex), steam = Inventory(steamIndex),
                    impactPlate = PlateInventory(),
                    coalMass = grid.Where(c => c.IsActive != 0 && registry[(ushort)c.MaterialIndex].Id is "core:coal" or "core:stone_coal").Sum(c => (double)c.Mass) };
                object Inventory(uint material)
                {
                    var cells = grid.Where(c => c.IsActive != 0 && c.MaterialIndex == material).ToArray();
                    double mass = cells.Sum(c => (double)c.Mass);
                    return new { cells = cells.Length, mass,
                        temperature = mass > 0 ? (double?)cells.Sum(c => (double)c.Mass * c.Temperature) / mass : null,
                        energy = cells.Sum(c => (double)c.Mass * PhaseEnthalpy.SpecificEnergy(c, thermalMaterials)),
                        latent = cells.Sum(c => (double)c.Mass * c.PhaseProgress) };
                }
                object PlateInventory()
                {
                    // Observation region under the user's drop; no geometry or heat is injected.
                    var cells = grid.Where((c, i) => c.IsActive != 0 && c.MaterialIndex == ironIndex &&
                        i % r.Width >= 450 && i % r.Width <= 510 && i / r.Width >= 306 && i / r.Width <= 323).ToArray();
                    return new { cells = cells.Length, temperature = cells.Length > 0 ? (double?)cells.Average(c => (double)c.Temperature) : null,
                        minimum = cells.Length > 0 ? (double?)cells.Min(c => c.Temperature) : null,
                        energy = cells.Sum(c => (double)c.Mass * PhaseEnthalpy.SpecificEnergy(c, thermalMaterials)) };
                }
                rows.Add(row); Console.WriteLine("PHYXEL_SENSOR_ROW " + JsonSerializer.Serialize(row));
                if (frame % (fps * 10) == 0)
                {
                    File.WriteAllBytes(Path.Combine(dir, $"grid-{frame / fps}.bin"), MemoryMarshal.AsBytes(grid.AsSpan()).ToArray());
                    SimulationScreenshotWriter.Save(r, Path.Combine(dir, $"world-{frame / fps}.png"));
                    settings.ShowAirField = true; coordinator.DispatchFrame(settings, [], 0);
                    SimulationScreenshotWriter.Save(r, Path.Combine(dir, $"air-{frame / fps}.png"));
                    settings.ShowAirField = false;
                    if (observationOnly)
                    {
                        Console.WriteLine("PHYXEL_SENSOR_OBSERVATION_ONLY save/load assertions not run");
                        if (frame % 4 == 0) yield return r;
                        continue;
                    }
                    // Explicit file output only to diagnostics, never the source scene.
                    serializer.BeginWorldCapture(r);
                    SimulationWorldSnapshot? snapshot;
                    while (!serializer.TryCompleteWorldCapture(r, out snapshot)) System.Threading.Thread.Yield();
                    System.Threading.Tasks.Task.Run(() => serializer.SaveAsync(Path.Combine(dir, $"snapshot-{frame / fps}.json"), settings,
                        registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal), snapshot!, registry)).GetAwaiter().GetResult();
                    var roundTrip = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(
                        Path.Combine(dir, $"snapshot-{frame / fps}.json"), registry)).GetAwaiter().GetResult()!;
                    var copy = roundTrip.World!;
                    bool Same(byte[]? a, byte[]? b) => (a ?? []).AsSpan().SequenceEqual(b ?? []);
                    byte[] CanonicalGrid(byte[] bytes)
                    {
                        // EncodeSceneSnapshot explicitly clears inactive parcels and
                        // the unused retained-liquid ID. Compare physical active
                        // fields exactly, not scratch left in an empty GPU parcel.
                        byte[] canonical=(byte[])bytes.Clone();
                        var cells=MemoryMarshal.Cast<byte,GridCell>(canonical);
                        int cleared=0;
                        foreach(ref var c in cells)
                        {
                            if(c.IsActive==0) {if(!c.Equals(default(GridCell)))cleared++;c=default;}
                            else if(c.FuelMass<=0)c.RetainedLiquidMaterialIndex=0;
                        }
                        Console.WriteLine($"PHYXEL_SENSOR_CANONICAL inactiveScratch={cleared}");
                        return canonical;
                    }
                    if (!Same(CanonicalGrid(snapshot!.Grid), CanonicalGrid(copy.Grid)) || !Same(snapshot.Air, copy.Air) ||
                        !Same(snapshot.GasMotion, copy.GasMotion) || !Same(snapshot.Oxidizer, copy.Oxidizer) ||
                        !Same(snapshot.AirThermal, copy.AirThermal) || !Same(snapshot.ReactionPending, copy.ReactionPending) ||
                        !Same(snapshot.ReactionPulse, copy.ReactionPulse) || !Same(snapshot.Filters, copy.Filters) ||
                        !settings.TemperatureSensors.SequenceEqual(roundTrip.State.TemperatureSensors ?? []))
                        throw new InvalidOperationException("Replay snapshot round trip changed fields or sensors.");
                }
            }
            if (frame % 4 == 0) yield return r;
        }
        File.WriteAllText(Path.Combine(dir, "measurements.json"), JsonSerializer.Serialize(new { hash, fps, mode = settings.Mode,
            size = new { r.Width, r.Height }, wallSeconds = wallTime.Elapsed.TotalSeconds, rows }, new JsonSerializerOptions { WriteIndented = true }));
        if (hash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(path, ".world")))))
            throw new InvalidOperationException("Original user world changed.");
        if (jsonHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            throw new InvalidOperationException("Original user metadata changed.");
        Console.WriteLine("PHYXEL_SENSOR_REPLAY_COMPLETE");
    }
}
