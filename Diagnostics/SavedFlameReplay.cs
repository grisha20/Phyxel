using System;
using System.Collections.Generic;
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

// Observation only: replay a user world and reset initial motion on a control copy.
// This is not a regression PASS or validation of physical flame attachment.
internal static class SavedFlameReplay
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string path = Environment.GetEnvironmentVariable("PHYXEL_FLAME_SCENE")
            ?? throw new InvalidOperationException("PHYXEL_FLAME_SCENE is required.");
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/saved-flame";
        Directory.CreateDirectory(dir);
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        string jsonHash = Hash(path), worldPath = Path.ChangeExtension(path, ".world"), worldHash = Hash(worldPath);
        var serializer = new SimulationStateSerializer();
        var loaded = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(path, registry)).GetAwaiter().GetResult()!;
        var source = loaded.World ?? throw new InvalidDataException("World is missing.");
        SimulationStateSerializer.Apply(loaded.State, settings);
        settings.Width = source.Width; settings.Height = source.Height; settings.Paused = true;
        var r = coordinator.DispatchFrame(settings, [new() { X = 20, Y = 20, Radius = 1, Density = 1,
            MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand) }], 0);
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        var results = new List<object>();
        foreach (var mode in new[] { SimulationMode.Sandbox, SimulationMode.Simulation })
        foreach (string control in new[] { "saved", "still", "cold", "mirror-cold" })
        {
            settings.Mode = mode; settings.Paused = true;
            byte[]? Mirror(byte[]? bytes)
            {
                if (bytes is null || bytes.Length == 0) return bytes;
                int count = source.Width * source.Height;
                if (bytes.Length % count != 0) throw new InvalidDataException("Expected fine-grid field.");
                int stride = bytes.Length / count;
                byte[] copy = new byte[bytes.Length];
                for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                    bytes.AsSpan((y * source.Width + x) * stride, stride).CopyTo(
                        copy.AsSpan((y * source.Width + source.Width - 1 - x) * stride, stride));
                return copy;
            }
            var world = control == "saved" ? source : source with { Air = null, GasMotion = null };
            if (control is "cold" or "mirror-cold")
                world = world with { AirThermal = null, Oxidizer = null, ReactionPending = null, ReactionPulse = null };
            if (control == "mirror-cold")
                world = world with { Grid = Mirror(source.Grid)!, Filters = Mirror(source.Filters) };
            serializer.ApplyWorldSnapshot(r, world);
            coordinator.RestoreWorldActivity(r, true, true, settings.HydraulicPressure, true);
            settings.Paused = false;
            string label = $"{mode}-{control}";
            var rows = new List<object>();
            for (int frame = 0; frame <= 600; frame++)
            {
                if (frame > 0) coordinator.DispatchFrame(settings, [], 1f / 60);
                if (frame % 60 == 0)
                {
                    var grid = MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
                    var air = MemoryMarshal.Cast<byte, AirCell>(AirInventoryRegressionVerifier.Read(r, r.Air.Buffer)).ToArray();
                    var motion = MemoryMarshal.Cast<byte, GasMotionState>(AirInventoryRegressionVerifier.Read(r, r.GasMotion.Buffer)).ToArray();
                    var indices = Enumerable.Range(0, grid.Length).Where(i => grid[i].IsActive != 0 && grid[i].MaterialIndex == fire).ToArray();
                    double Mean(Func<int, double> f) => indices.Length > 0 ? indices.Average(f) : 0;
                    var row = new { seconds = frame / 60, fireCount = indices.Length,
                        fireX = Mean(i => i % r.Width), fireY = Mean(i => i / r.Width),
                        fireVx = Mean(i => motion[i].VelocityX), fireVy = Mean(i => motion[i].VelocityY),
                        fireAirVx = Mean(i => air[(i / r.Width / 4) * r.AirWidth + i % r.Width / 4].VelocityX),
                        fireAirVy = Mean(i => air[(i / r.Width / 4) * r.AirWidth + i % r.Width / 4].VelocityY),
                        oilStock = grid.Where(c => c.IsActive != 0).Sum(c => (double)c.FuelMass),
                        airMaxSpeed = air.Max(a => Math.Sqrt(a.VelocityX * a.VelocityX + a.VelocityY * a.VelocityY)) };
                    rows.Add(row);
                    Console.WriteLine("PHYXEL_FLAME_ROW " + label + " " + JsonSerializer.Serialize(row));
                    if (frame is 0 or 180 or 600)
                    {
                        SimulationScreenshotWriter.Save(r, Path.Combine(dir, $"{label}-{frame / 60}.png"));
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / 60}-grid.bin"), MemoryMarshal.AsBytes(grid.AsSpan()).ToArray());
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / 60}-air.bin"), MemoryMarshal.AsBytes(air.AsSpan()).ToArray());
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / 60}-motion.bin"), MemoryMarshal.AsBytes(motion.AsSpan()).ToArray());
                    }
                }
                if (frame % 4 == 0) yield return r;
            }
            results.Add(new { label, rows });
        }
        if (jsonHash != Hash(path) || worldHash != Hash(worldPath))
            throw new InvalidOperationException("Source scene changed during observation.");
        File.WriteAllText(Path.Combine(dir, "measurements.json"), JsonSerializer.Serialize(
            new { jsonHash, worldHash, source.Width, source.Height, tickRate = 60, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PHYXEL_FLAME_OBSERVATION_COMPLETE sourceUnchanged=true");
    }
}
