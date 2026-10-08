using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using Phyxel.UI;

namespace Phyxel.Diagnostics;

internal static class TemperatureSensorsRegressionVerifier
{
    internal static GpuSimulationResources Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry,
        SimulationSettings settings)
    {
        int checks = 0;
        void Check(bool ok, string reason) { checks++; if (!ok) throw new InvalidOperationException(reason); }
        settings.Paused = true;
        var r = coordinator.DispatchFrame(settings, [new() { X = 40, Y = 40, EndX = 40, EndY = 40,
            Radius = 1, Density = 1, Mode = BrushCommandMode.Material,
            MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }], 0);
        var grid = new GridCell[r.Width * r.Height];
        uint metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        grid[100 * r.Width + 100] = new() { IsActive = 1, MaterialIndex = metal, Mass = 1, Temperature = 245.5f };
        grid[140 * r.Width + 180] = new() { IsActive = 1, MaterialIndex = water, Mass = 1, Temperature = 83.25f };
        grid[160 * r.Width + 200] = new() { IsActive = 1, MaterialIndex = steam, Mass = .01f, Temperature = 123 };
        for (int y = 70; y <= 110; y++) grid[y * r.Width + 97] = new() { IsActive = 1, MaterialIndex = metal, Mass = 1, Temperature = 20 };
        var heat = new System.Numerics.Vector2[r.AirWidth * r.AirHeight];
        Array.Fill(heat, new(573.15f * .016f, .016f));
        heat[20 * r.AirWidth + 24] = new(1173.15f * .016f, .016f);
        r.Context.UpdateSubresource(grid, r.Grid.ReadBuffer);
        r.Context.UpdateSubresource(heat, r.AirThermal.Buffer);
        var air = new AirCell[heat.Length];
        Array.Fill(air, new AirCell { Pressure = -2.5f });
        air[20 * r.AirWidth + 24].Pressure = 17.25f;
        air[40 * r.AirWidth + 50].Pressure = 0;
        var wave = new System.Numerics.Vector4[heat.Length];
        wave[20 * r.AirWidth + 24].X = 3.5f;
        r.Context.UpdateSubresource(air, r.Air.Buffer);
        r.Context.UpdateSubresource(wave, r.ReactionPulse.ReadBuffer);
        byte[] beforeGrid = AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer);
        byte[] beforeHeat = AirInventoryRegressionVerifier.Read(r, r.AirThermal.Buffer);
        byte[] beforeAir = AirInventoryRegressionVerifier.Read(r, r.Air.Buffer);
        byte[] beforeWave = AirInventoryRegressionVerifier.Read(r, r.ReactionPulse.ReadBuffer);
        using var sensors = new GpuTemperatureSensors();
        List<TemperatureSensorPosition> points = [new(100, 100), new(180, 140), new(96, 82), new(98, 82), new(200,160)];
        void Read(bool air = true)
        {
            sensors.Update(r, points, air, .11f); r.Context.Flush();
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (sensors.Readings.Count != points.Count)
            {
                sensors.Update(r, points, air, 0);
                if (DateTime.UtcNow > until) throw new TimeoutException("Sensor GPU readback stalled.");
                Thread.Sleep(1);
            }
        }
        Read();
        Check(sensors.Readings[points[0]].Thermal.IsActive == 1 && sensors.Readings[points[0]].Thermal.Temperature == 245.5f, "Metal reading wrong.");
        Check(sensors.Readings[points[1]].Thermal.MaterialIndex == water && sensors.Readings[points[1]].Thermal.Temperature == 83.25f, "Water reading wrong.");
        Check(sensors.Readings[points[2]].Thermal.IsActive == 2 && Math.Abs(sensors.Readings[points[2]].Thermal.Temperature - 300) < .01f, "Air probe sampled through a thin wall.");
        Check(Math.Abs(sensors.Readings[points[3]].Thermal.Temperature - 900) < .01f, "Air Kelvin/energy conversion wrong.");
        Check(float.IsNaN(sensors.Readings[points[0]].Pressure) && float.IsNaN(sensors.Readings[points[1]].Pressure), "Solid/liquid supplied fake pressure.");
        Check(sensors.Readings[points[2]].Pressure == -2.5f && sensors.Readings[points[3]].Pressure == 17.25f, "Pressure sampled through thin wall or lost sign.");
        Check(sensors.Readings[points[3]].ReactionPressure == 3.5f, "Reaction pressure field read wrong.");
        Check(sensors.Readings[points[4]].Pressure == 0 && sensors.Readings[points[4]].Thermal.Temperature == 123, "Gas temperature/zero pressure wrong.");
        Check(beforeGrid.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)) &&
            beforeHeat.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r, r.AirThermal.Buffer)) &&
            beforeAir.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r, r.Air.Buffer)) &&
            beforeWave.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r, r.ReactionPulse.ReadBuffer)), "Sensors changed physics state.");
        Check(TemperatureSensorOverlay.Label(1, sensors.Readings[points[0]], registry).Contains("245,5 °C"), "Label/culture wrong.");
        Check(TemperatureSensorOverlay.Label(4, sensors.Readings[points[3]], registry).Contains("P: 17,25 игр. ед.") &&
            TemperatureSensorOverlay.Label(1, sensors.Readings[points[0]], registry).Contains("P: —"), "Pressure label/unit wrong.");
        Check(System.Runtime.InteropServices.Marshal.SizeOf<TemperatureProbeResult>() == 24 &&
            System.Runtime.InteropServices.Marshal.SizeOf<TemperatureSensorReading>() == 32, "Cursor/sensor ABI changed.");
        sensors.Reset(); Read(false);
        Check(sensors.Readings[points[2]].Thermal.IsActive == 0 && sensors.Readings[points[0]].Thermal.Temperature == 245.5f, "Disabled air supplied fake readings.");
        Check(sensors.Readings.Values.All(p => float.IsNaN(p.Pressure)), "Disabled air supplied fake pressure.");
        sensors.Reset(); sensors.Update(r, points, true, .11f);
        points = [new(180, 140)]; r.Context.Flush(); Read();
        Check(sensors.Readings.Count == 1 && sensors.Readings[points[0]].Thermal.MaterialIndex == water, "Old pending coordinates leaked after removal.");
        points = Enumerable.Range(0, 32).Select(i => new TemperatureSensorPosition(220 + i, 180)).ToList();
        Read(); Check(sensors.Readings.Count == 32 && sensors.Readings.Values.All(p => p.Thermal.IsActive == 2), "Full sensor batch wrong.");
        points = [new(-1, 0), new(r.Width, 0)]; Read();
        Check(sensors.Readings.Values.All(p => p.Thermal.IsActive == 0 && float.IsNaN(p.Pressure)), "Out of bounds sensors read data.");
        var testSettings = new SimulationSettings { Width = 100, Height = 100 };
        Rectangle bounds = new(300, -100, 600, 600);
        var click = default(RawInputSnapshot) with { MousePosition = new(543, 143), LeftPressed = true };
        Check(TemperatureSensorOverlay.Edit(click, bounds, testSettings) && testSettings.TemperatureSensors[0] == new TemperatureSensorPosition(40, 40), "Zoom/pan placement wrong.");
        Check(!TemperatureSensorOverlay.Edit(click, bounds, testSettings), "Duplicate sensor added.");
        Check(!TemperatureSensorOverlay.Edit(click with { MousePosition = new(2, 2), RightPressed = true, ShiftDown = true }, bounds, testSettings), "Outside input edited sensors.");
        Check(TemperatureSensorOverlay.Edit(click with { LeftPressed = false, RightPressed = true }, bounds, testSettings) && testSettings.TemperatureSensors.Count == 0, "RMB removal wrong.");
        testSettings.TemperatureSensors = Enumerable.Range(0, 32).Select(i => new TemperatureSensorPosition(i, 0)).ToList();
        Check(!TemperatureSensorOverlay.Edit(click, bounds, testSettings), "Sensor cap not enforced.");
        Check(TemperatureSensorOverlay.Edit(click with { LeftPressed = false, RightPressed = true, ShiftDown = true }, bounds, testSettings) && testSettings.TemperatureSensors.Count == 0, "Clear sensors wrong.");
        Check(TemperatureSensorPosition.Normalize([new(2, 3), new(2, 3), new(-1, 0), new(101, 0)], 100, 100).Count == 1, "Saved coordinate validation wrong.");
        SimulationStateSerializer.Apply(new(19, .25f, 980, 18, .82f, false, (ushort)metal, DateTimeOffset.Now), testSettings);
        Check(testSettings.TemperatureSensors.Count == 0, "Old scenes inherited sensors.");
        var cursor = new GpuTemperatureProbe();
        cursor.Update(r, new Point(100, 100), .11f); r.Context.Flush();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (cursor.Latest is null && DateTime.UtcNow < deadline) { cursor.Update(r, new Point(100, 100), 0); Thread.Sleep(1); }
        Check(cursor.Latest is { Temperature: 245.5f, IsActive: 1 }, "Existing cursor probe regressed.");
        Console.WriteLine($"PHYXEL_TEMPERATURE_SENSORS_PASS checks={checks} material=245.5 water=83.25 airLeft=300 airRight=900 physicsUnchanged=true");
        settings.TemperatureSensors = [new(100, 100), new(180, 140), new(260, 100)];
        return r;
    }
}
