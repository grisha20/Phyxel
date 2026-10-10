using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Diagnostic only: constant fresh air in a frozen copy of the user's furnace.
// No chemical source, moving occupancy or heat can explain concentration drift.
internal static class OxidizerProjectionRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!;
        Directory.CreateDirectory(dir);
        string source = Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SCENE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phyxel", "Проверка тяги и темпиратур.json");
        string worldPath = Path.ChangeExtension(source, ".world");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(worldPath));
        byte[] jsonHash = SHA256.HashData(File.ReadAllBytes(source));
        var settings = new SimulationSettings();
        var r = FurnaceSensorRegressionVerifier.LoadFixture(coordinator, registry, settings);
        settings.Mode = SimulationMode.Simulation;
        settings.Paused = false;
        // The save already contains a strong carrier. Do not advance it with
        // the candidate settings before freezing: every trial needs the exact
        // same input field, not a differently prepared one-second history.
        var grid = MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
        var table = registry.CreateGpuTable();
        var oxygen = new float[grid.Length];
        for (int i = 0; i < grid.Length; i++)
        {
            if (grid[i].IsActive == 0 || table[grid[i].MaterialIndex].SimulationKind == (uint)MaterialSimulationKind.Gas)
            { grid[i] = default; oxygen[i] = 1; }
        }
        r.Context.UpdateSubresource(grid, r.Grid.ReadBuffer);
        r.Context.UpdateSubresource(oxygen, r.Oxidizer.ReadBuffer);
        r.OxidizerCarrierWarm = false;
        File.WriteAllBytes(Path.Combine(dir, "grid.bin"), MemoryMarshal.AsBytes(grid.AsSpan()).ToArray());
        byte[] frozenAir = AirInventoryRegressionVerifier.Read(r, r.Air.Buffer);
        File.WriteAllBytes(Path.Combine(dir, "air.bin"), frozenAir);
        int ticks = int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_OXYGEN_TRACE_TICKS"), out int count)
            ? Math.Clamp(count, 1, 3600) : 600;
        var elapsed = Stopwatch.StartNew();
        for (int tick = 0; tick < ticks; tick++)
            SimulationDispatchCoordinator.DispatchOxidizer(r, 1f / 60, true, false, true);
        var after = MemoryMarshal.Cast<byte, float>(AirInventoryRegressionVerifier.Read(r, r.Oxidizer.ReadBuffer)).ToArray();
        elapsed.Stop(); // Includes submission and the final GPU readback.
        var faces = MemoryMarshal.Cast<byte, Vector4>(AirInventoryRegressionVerifier.Read(r, r.OxidizerCarrierFaces.Buffer)).ToArray();
        var psi = MemoryMarshal.Cast<byte, Vector2>(AirInventoryRegressionVerifier.Read(r, r.OxidizerCarrierPotential.ReadBuffer)).ToArray();
        double maxOutgoing = 0, maxDivergence = 0, sum = 0;
        int measured = 0, within = 0;
        for (int y = 161; y < 503; y++) for (int x = 280; x < 620; x++)
        {
            int i = y * r.Width + x;
            if (oxygen[i] == 0) continue;
            uint mask = (uint)faces[i].Z;
            float own = psi[i].X;
            double right = (mask & 1) != 0 ? faces[i].X - (psi[i + 1].X - own) : 0;
            double left = (mask & 2) != 0 ? faces[i - 1].X - (own - psi[i - 1].X) : 0;
            double down = (mask & 4) != 0 ? faces[i].Y - (psi[i + r.Width].X - own) : 0;
            double up = (mask & 8) != 0 ? faces[i - r.Width].Y - (own - psi[i - r.Width].X) : 0;
            maxOutgoing = Math.Max(maxOutgoing, Math.Max(0, right) + Math.Max(0, -left) + Math.Max(0, down) + Math.Max(0, -up));
            maxDivergence = Math.Max(maxDivergence, Math.Abs(right - left + down - up));
            measured++; sum += after[i];
            if (after[i] >= .95 && after[i] <= 1.05) within++;
        }
        double mean = sum / measured, fraction = (double)within / measured;
        bool valid = after.All(v => float.IsFinite(v) && v >= 0);
        bool pass = valid && mean >= .95 && mean <= 1.05 && fraction >= .99;
        var result = new { ticks, measured, region = new { x0 = 280, y0 = 161, x1Exclusive = 620, y1Exclusive = 503 },
            mean, withinFraction = fraction, max = after.Max(),
            maxOutgoing, maxDivergence, valid, pass, transportWallMs = elapsed.Elapsed.TotalMilliseconds,
            gridHash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(grid.AsSpan()))),
            airHash = Convert.ToHexString(SHA256.HashData(frozenAir)),
            sor = Environment.GetEnvironmentVariable("PHYXEL_OXYGEN_SOR") == "1",
            initialIterations = Environment.GetEnvironmentVariable("PHYXEL_OXYGEN_INITIAL_ITERATIONS") ?? "1024",
            warmIterations = Environment.GetEnvironmentVariable("PHYXEL_OXYGEN_WARM_ITERATIONS") ?? "64",
            transportSteps = Environment.GetEnvironmentVariable("PHYXEL_OXYGEN_TRANSPORT_STEPS") ?? "8" };
        File.WriteAllText(Path.Combine(dir, "measurements.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(Path.Combine(dir, "oxygen.bin"), MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
        File.WriteAllBytes(Path.Combine(dir, "potential.bin"), MemoryMarshal.AsBytes(psi.AsSpan()).ToArray());
        File.WriteAllBytes(Path.Combine(dir, "faces.bin"), MemoryMarshal.AsBytes(faces.AsSpan()).ToArray());
        Console.WriteLine("PHYXEL_OXYGEN_PROJECT " + JsonSerializer.Serialize(result));
        if (!SHA256.HashData(File.ReadAllBytes(worldPath)).AsSpan().SequenceEqual(hash))
            throw new InvalidOperationException("Source world changed.");
        if (!SHA256.HashData(File.ReadAllBytes(source)).AsSpan().SequenceEqual(jsonHash))
            throw new InvalidOperationException("Source metadata changed.");
        // Preserve the expected FAIL baseline without treating it as a successful test.
        if (!pass && Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE") != "1")
            throw new InvalidOperationException("Uniform oxygen changed in frozen carrier flow.");
    }
}
