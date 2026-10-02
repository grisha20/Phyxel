using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

internal static class ThermalDeviceAcceptance
{
    private static SimulationWorldSnapshot? initial;
    private static ThermalEnergyLedgerCell[] ledger = [];
    private static ulong ticks;
    private static TemperatureProbeResult? probe;
    public static bool Restarting => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHYXEL_DEVICE_RESTART"));
    public static uint FramesAt60(uint frames) => frames * (uint)(int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"), out int fps) ? fps : 60) / 60;
    public static void ObserveProbe(TemperatureProbeResult? value) { if (value is not null) probe = value; }
    public static SimulationWorldSnapshot Reload(MaterialRegistry materials) => Task.Run(async () =>
        (await new SimulationStateSerializer().LoadAsync(Environment.GetEnvironmentVariable("PHYXEL_DEVICE_RESTART")!, materials))?.World
        ?? throw new InvalidDataException("Missing saved device world.")).GetAwaiter().GetResult();
    public static void SetInitial(SimulationWorldSnapshot world) => initial = world;

    public static void FillDevices(Span<GridCell> cells, int width, MaterialRegistry materials,
        int left, int top, int right, int bottom, bool heating, float target, float power)
    {
        uint material = materials.GetRequiredRuntimeIndex(heating ? CoreMaterialIds.Heater : CoreMaterialIds.Cooler);
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
            cells[y * width + x] = new GridCell { MaterialIndex = material, Mass = 7.8f,
                Temperature = 20, IsActive = 1, DeviceTargetTemperature = target, DeviceMaximumPower = power };
    }

    public static void Populate(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        if (materials[CoreMaterialIds.Fixture].Properties.ThermalConductivity != 0)
            throw new InvalidOperationException("thermal_devices requires insulated fixture.");
        // Isolated devices distinguish power limitation, target clamping,
        // unilateral control and disabled operation without a thermal load.
        for (int i = 0; i < 8; i++)
        {
            int x = 180 + i * 30;
            for (int y = 119; y <= 121; y++) for (int xx = x - 1; xx <= x + 1; xx++)
                cells[y * width + xx] = new GridCell { IsActive = 1, Mass = 1, Temperature = 20,
                    MaterialIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture) };
            FillDevices(cells, width, materials, x, 120, x, 120, i != 1 && i != 3 && i != 6,
                i is 1 or 3 or 6 ? 20 : 100, i is 0 or 1 ? 1 : i == 4 ? 0 : 600);
            ref GridCell cell = ref cells[120 * width + x];
            cell.Temperature = i switch { 1 => 80, 2 => 200, 3 => 0, 4 => 55, 6 => 80, 7 => 37, _ => 20 };
            if (i == 7) cell = default; // Exercise creation, not only hand-built data.
        }
    }

    public static IReadOnlyList<BrushDrawCommand> Commands(uint frame, MaterialRegistry materials)
    {
        if (Restarting) return [];
        if (frame == FramesAt60(300)) frame = 300;
        else if (frame == FramesAt60(600)) frame = 600;
        else if (frame != 0) return [];
        BrushDrawCommand Configure(int x, bool heat, float target, float power) => new()
        {
            X = x, Y = 120, Radius = 0, Density = 1, Mode = BrushCommandMode.ThermalDevice,
            MaterialIndex = materials.GetRequiredRuntimeIndex(heat ? CoreMaterialIds.Heater : CoreMaterialIds.Cooler),
            TargetTemperature = target, Reserved = BitConverter.SingleToUInt32Bits(power)
        };
        return frame switch
        {
            0 => [Configure(390, true, 60, .5f), Configure(300, true, 120, 0),
                // A device brush must never replace an ordinary wall.
                new() { X = 179, Y = 119, Radius = 0, Mode = BrushCommandMode.ThermalDevice,
                    MaterialIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Heater), TargetTemperature = 100, Reserved = BitConverter.SingleToUInt32Bits(600) }],
            // Repainting preserves the actual temperature and mass; setting a
            // heater lower or cooler higher must not reverse their direction.
            300 => [Configure(330, true, 60, 600), Configure(360, false, 40, 600)],
            600 => [Configure(180, true, 100, 0), Configure(210, false, 20, 0), Configure(390, true, 60, 0)],
            _ => []
        };
    }

    // Called immediately before the matching grid capture, so the observer
    // and captured world describe the same completed simulation steps.
    public static void CaptureLedger(GpuSimulationResources resources, ulong thermalTicks)
    {
        if (resources.ThermalEnergyLedger is null || resources.ThermalEnergyStaging is null) return;
        ticks = thermalTicks;
        var context = resources.Context;
        context.CopyResource(resources.ThermalEnergyLedger.Buffer, resources.ThermalEnergyStaging);
        var mapped = context.MapSubresource(resources.ThermalEnergyStaging, 0, MapMode.Read, MapFlags.None);
        byte[] bytes = new byte[resources.Width * resources.Height * 8];
        try { Marshal.Copy(mapped.DataPointer, bytes, 0, bytes.Length); }
        finally { context.UnmapSubresource(resources.ThermalEnergyStaging, 0); }
        ledger = MemoryMarshal.Cast<byte, ThermalEnergyLedgerCell>(bytes).ToArray();
    }

    public static bool Audit(SimulationWorldSnapshot snapshot, MaterialRegistry materials,
        string directory, out string report)
    {
        if (initial is null || ledger.Length != snapshot.Width * snapshot.Height)
            throw new InvalidOperationException("Missing paired thermal energy observation.");
        MaterialProperties[] table = materials.CreateGpuTable();
        double Energy(byte[] bytes)
        {
            double e = 0;
            foreach (var c in MemoryMarshal.Cast<byte, GridCell>(bytes))
                if (c.IsActive != 0) e += c.Mass * (double)PhaseEnthalpy.SpecificEnergy(c, table);
            return e;
        }
        double before = Energy(initial.Grid), after = Energy(snapshot.Grid), heat = 0, ambient = 0, heating = 0, cooling = 0;
        foreach (var l in ledger) { heat += l.DeviceHeat; ambient += l.AmbientHeat; heating += Math.Max(0, l.DeviceHeat); cooling += Math.Min(0, l.DeviceHeat); }
        // Creation of one test block at room temperature is an
        // explicitly accounted input of matter, not device-generated heat.
        double creation = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_MODE") == "thermal_devices" && !Restarting
            ? 7.8 * .13 * 20 : 0;
        double residual = after - before - heat - ambient - creation;
        double scale = Math.Max(1, Math.Max(Math.Abs(before), Math.Abs(after)));
        // The world codec deliberately canonicalizes inactive cells to zero.
        // Compare every active byte and the oxidizer, not stale velocities in
        // empty cells left by cellular motion between thermal passes.
        byte[] normalizedGrid = (byte[])snapshot.Grid.Clone();
        var normalizedCells = MemoryMarshal.Cast<byte, GridCell>(normalizedGrid.AsSpan());
        for (int i = 0; i < normalizedCells.Length; i++)
            if (normalizedCells[i].IsActive == 0) normalizedCells[i] = default;
        bool saved = Task.Run(async () => {
            var serializer = new SimulationStateSerializer();
            string path = Path.Combine(directory, "final.scene.json");
            await serializer.SaveAsync(path, new SimulationSettings(),
                (ushort)materials.GetRequiredRuntimeIndex(CoreMaterialIds.Heater), snapshot, materials);
            var loaded = await serializer.LoadAsync(path, materials);
            return loaded?.World is { } world && world.Grid.AsSpan().SequenceEqual(normalizedGrid) &&
                (world.Oxidizer ?? []).AsSpan().SequenceEqual(snapshot.Oxidizer ?? []);
        }).GetAwaiter().GetResult();
        report = string.Create(CultureInfo.InvariantCulture,
            $"energyBefore={before:F6} energyAfter={after:F6} deviceHeat={heat:F6} heatingHeat={heating:F6} coolingHeat={cooling:F6} ambientHeat={ambient:F6} creationHeat={creation:F6} residual={residual:F6} relativeError={Math.Abs(residual)/scale:E4} saved={saved} thermalTicks={ticks}");
        File.WriteAllText(Path.Combine(directory, "energy-audit.txt"), report);
        File.WriteAllBytes(Path.Combine(directory, "final-grid.bin"), snapshot.Grid);
        return Math.Abs(residual) / scale < .0001 && saved;
    }

    public static bool ValidateDevices(SimulationWorldSnapshot snapshot, MaterialRegistry materials,
        string directory, IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, out string report)
    {
        bool pass = Audit(snapshot, materials, directory, out string audit);
        var cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        var before = MemoryMarshal.Cast<byte, GridCell>(initial!.Grid);
        using var csv = new StreamWriter(Path.Combine(directory, "devices.csv"));
        csv.WriteLine("device,temperature,target,power,heat");
        float[] expected = [0, 0, 200, 0, 55, 100, 20, 20];
        for (int i = 0; i < 8; i++)
        {
            int index = 120 * snapshot.Width + 180 + i * 30;
            GridCell c = cells[index];
            csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i},{c.Temperature:R},{c.Pressure},{c.Lifetime},{ledger[index].DeviceHeat:R}"));
            pass &= c.IsActive != 0 && Math.Abs(c.Mass - 7.8f) < .0001;
            if (Restarting)
            {
                GridCell b = before[index];
                pass &= c.Temperature == b.Temperature && c.Pressure == b.Pressure && c.Lifetime == b.Lifetime && ledger[index].DeviceHeat == 0;
            }
            else if (i < 2 || i == 7)
            {
                double observed = c.Mass * .13 * (c.Temperature - (i == 7 ? 20 : before[index].Temperature));
                double heat = ledger[index].DeviceHeat;
                // Nine active seconds before switch-off at 600 frames/60FPS.
                pass &= Math.Abs(observed - heat) < .003 && Math.Abs(heat) > (i == 7 ? 4.5 : 9) && Math.Abs(heat) < (i == 7 ? 5.05 : 10.1);
                pass &= c.Lifetime == 0;
            }
            else pass &= Math.Abs(c.Temperature - expected[i]) < .002;
            if (i is 5 or 6) pass &= c.Pressure == (i == 5 ? 60 : 40);
        }
        pass &= cells[119 * snapshot.Width + 179].MaterialIndex == materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        pass &= probe is { IsActive: 1, Reserved: 3732 } && Math.Abs(probe.Value.Temperature - cells[120 * snapshot.Width + 180].Temperature) < .002;
        if (!Restarting && checkpoints.Count > 0)
        {
            var paused = MemoryMarshal.Cast<byte, GridCell>(checkpoints[0].Snapshot.Grid);
            pass &= paused[120 * snapshot.Width + 180].Temperature == 20 &&
                paused[120 * snapshot.Width + 210].Temperature == 80 &&
                paused[120 * snapshot.Width + 300].Temperature == 55 && paused[120 * snapshot.Width + 300].Pressure == 120;
        }
        report = "PHYXEL_THERMAL_DEVICES " + audit + $" controlsPassed={pass}";
        return pass;
    }
}
