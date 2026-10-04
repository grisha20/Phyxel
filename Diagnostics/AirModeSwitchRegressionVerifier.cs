using System;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Diagnostics;

internal static class AirModeSwitchRegressionVerifier
{
    public static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var settings = new SimulationSettings { Paused = true, Mode = SimulationMode.Simulation };
        BrushDrawCommand[] commands = [new()
        {
            X = 40, EndX = 40, Y = 40, EndY = 40, Radius = 2, Density = 1,
            Mode = BrushCommandMode.Material,
            MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke), Seed = 8711
        }];
        var resources = coordinator.DispatchFrame(settings, commands, 0);
        var air = new AirCell[resources.AirWidth * resources.AirHeight];
        for (int i = 0; i < air.Length; i++)
            air[i] = new() { Pressure = .125f, VelocityX = .5f, VelocityY = -.75f };
        resources.Context.UpdateSubresource(air, resources.Air.Buffer);
        var oxygen = new float[resources.Width * resources.Height];
        Array.Fill(oxygen, .375f);
        resources.Context.UpdateSubresource(oxygen, resources.Oxidizer.ReadBuffer);
        byte[] expectedAir = Read(resources, resources.Air.Buffer);
        byte[] expectedGrid = Read(resources, resources.Grid.ReadBuffer);
        byte[] expectedOxygen = Read(resources, resources.Oxidizer.ReadBuffer);
        var heat=new System.Numerics.Vector2[resources.AirWidth*resources.AirHeight];
        Array.Fill(heat,new System.Numerics.Vector2(12,.016f));
        resources.Context.UpdateSubresource(heat,resources.AirThermal.Buffer);
        byte[] expectedHeat=Read(resources,resources.AirThermal.Buffer);
        ulong expectedAirTicks = coordinator.AirTicks;
        ulong expectedGasTicks = coordinator.GasMotionTicks;
        ulong expectedThermalTicks = coordinator.ThermalTicks;
        ulong expectedFireTicks = coordinator.CombustionDispatches;
        foreach (SimulationMode mode in new[] { SimulationMode.Sandbox, SimulationMode.Simulation, SimulationMode.Sandbox })
        {
            settings.Mode = mode;
            coordinator.DispatchFrame(settings, [], 1f / 60);
            if (!Read(resources,resources.AirThermal.Buffer).AsSpan().SequenceEqual(expectedHeat))
                throw new InvalidOperationException("Mode switch cleared carrier heat.");
            if (!Read(resources, resources.Air.Buffer).AsSpan().SequenceEqual(expectedAir))
                throw new InvalidOperationException($"Switch to {mode} discarded existing pressure or flow.");
            if (!Read(resources, resources.Grid.ReadBuffer).AsSpan().SequenceEqual(expectedGrid) ||
                !Read(resources, resources.Oxidizer.ReadBuffer).AsSpan().SequenceEqual(expectedOxygen))
                throw new InvalidOperationException($"Switch to {mode} changed paused particles, heat or oxygen.");
            if (coordinator.AirTicks != expectedAirTicks || coordinator.GasMotionTicks != expectedGasTicks ||
                coordinator.ThermalTicks != expectedThermalTicks || coordinator.CombustionDispatches != expectedFireTicks)
                throw new InvalidOperationException("Switch advanced or reset paused physics clocks.");
        }
        Console.WriteLine("PHYXEL_AIR_MODE_SWITCH_SUCCESS switches=3 airPreserved=True heatPreserved=True oxygenPreserved=True clocksPreserved=True");
    }

    private static byte[] Read(GpuSimulationResources resources, Buffer source)
    {
        using var staging = new Buffer(resources.Device, new BufferDescription
        {
            SizeInBytes = source.Description.SizeInBytes, Usage = ResourceUsage.Staging,
            CpuAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });
        resources.Context.CopyResource(source, staging);
        var mapping = resources.Context.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            byte[] bytes = new byte[source.Description.SizeInBytes];
            Marshal.Copy(mapping.DataPointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { resources.Context.UnmapSubresource(staging, 0); }
    }
}
