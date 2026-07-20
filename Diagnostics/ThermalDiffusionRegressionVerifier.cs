using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.UI;

namespace Phyxel.Diagnostics;

internal static class ThermalDiffusionRegressionVerifier
{
    private const float MinimumMass = 0.0001f;
    private const float MaximumExchangeFraction = 0.80f;

    public static int Run()
    {
        try
        {
            VerifyLayoutsAndShader();
            VerifyBrushCommands();
            VerifyFixedStepScheduler();
            VerifyNumerics();
            VerifyProbeMappingAndFormatting();
            Console.WriteLine("PHYXEL_THERMAL_DIFFUSION_SUCCESS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYXEL_THERMAL_DIFFUSION_FAILED {exception}");
            return 1;
        }
    }

    private static void VerifyLayoutsAndShader()
    {
        Require(Marshal.SizeOf<ThermalSimulationConstants>() == 16,
            "ThermalSimulationConstants must be 16 bytes.");
        Require(Marshal.SizeOf<TemperatureProbeConstants>() == 16,
            "TemperatureProbeConstants must be 16 bytes.");
        Require(Marshal.SizeOf<TemperatureProbeResult>() == 16,
            "TemperatureProbeResult must be 16 bytes.");
        Require(Marshal.SizeOf<BrushDrawCommand>() == 36,
            "BrushDrawCommand must be 36 bytes.");
        Require(Enum.GetUnderlyingType(typeof(BrushCommandMode)) == typeof(uint) &&
            (uint)BrushCommandMode.Material == 0 && (uint)BrushCommandMode.Erase == 1 &&
            (uint)BrushCommandMode.SetTemperature == 2,
            "BrushCommandMode C# values/layout are incorrect.");
        string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders");
        string shared = File.ReadAllText(Path.Combine(shaderDirectory, "PhysicsShared.hlsli"));
        RequireOrdered(
            shared,
            "struct BrushDrawCommand",
            "int X;",
            "int Y;",
            "uint MaterialIndex;",
            "float Radius;",
            "float Density;",
            "uint Mode;",
            "uint Seed;",
            "uint Reserved;",
            "float TargetTemperature;");
        Require(shared.Contains("BrushCommandModeMaterial = 0", StringComparison.Ordinal) &&
            shared.Contains("BrushCommandModeErase = 1", StringComparison.Ordinal) &&
            shared.Contains("BrushCommandModeSetTemperature = 2", StringComparison.Ordinal),
            "Brush command HLSL mode constants do not match C#.");
        string brush = File.ReadAllText(Path.Combine(shaderDirectory, "BrushApplication.hlsl"));
        int temperatureBranch = brush.IndexOf(
            "command.Mode == BrushCommandModeSetTemperature",
            StringComparison.Ordinal);
        int materialLookup = brush.IndexOf(
            "MaterialProperties material = Materials[command.MaterialIndex]",
            StringComparison.Ordinal);
        Require(temperatureBranch >= 0 && materialLookup > temperatureBranch &&
            brush.Contains("existing.Temperature = clamp(command.TargetTemperature, -273.15, 5000.0)",
                StringComparison.Ordinal),
            "Temperature brush is missing, unclamped, or reads its unused material first.");
        string thermal = File.ReadAllText(Path.Combine(shaderDirectory, "ThermalDiffusion.hlsl"));
        RequireOrdered(
            thermal,
            "cbuffer ThermalConstants",
            "float ThermalDeltaTime;",
            "float ThermalExchangeRate;",
            "uint ThermalWidth;",
            "uint ThermalHeight;");
        Require(thermal.Contains("StructuredBuffer<GridCell> SourceGrid", StringComparison.Ordinal) &&
            thermal.Contains("RWStructuredBuffer<GridCell> DestinationGrid", StringComparison.Ordinal),
            "Thermal shader is not a Jacobi source/destination pass.");
        Require(!thermal.Contains("Interlocked", StringComparison.Ordinal),
            "Thermal shader must not use atomic temperature updates.");
        Require(thermal.Contains("2 * conductivityA * conductivityB / conductivitySum", StringComparison.Ordinal),
            "Thermal shader harmonic contact conductivity is missing.");
        Require(thermal.Contains("IsSameGas", StringComparison.Ordinal) &&
            thermal.Contains("SameGasConductivityFloor", StringComparison.Ordinal) &&
            thermal.Contains("AmbientSurfaceExposure", StringComparison.Ordinal) &&
            thermal.Contains("immediateEmptyNeighbors", StringComparison.Ordinal) &&
            thermal.Contains("localEmptyNeighbors", StringComparison.Ordinal),
            "Thermal shader is missing same-gas equalization or surface-aware ambient cooling.");
        Require(thermal.Contains("DestinationGrid[index] = (GridCell)0", StringComparison.Ordinal),
            "Thermal shader does not normalize inactive cells.");
        string gas = File.ReadAllText(Path.Combine(shaderDirectory, "GasRedistribution.hlsl"));
        Require(gas.Contains("RedistributeSameGas", StringComparison.Ordinal) &&
            gas.Contains("GasDiffusion", StringComparison.Ordinal) &&
            gas.Contains("GasBuoyancy", StringComparison.Ordinal) &&
            gas.Contains("firstMass * heatCapacity * first.Temperature", StringComparison.Ordinal) &&
            !gas.Contains("Interlocked", StringComparison.Ordinal),
            "Gas redistribution is not a race-safe, mass-carrying continuum pass.");
        string probe = File.ReadAllText(Path.Combine(shaderDirectory, "TemperatureProbe.hlsl"));
        RequireOrdered(
            probe,
            "cbuffer ProbeConstants",
            "uint ProbeX;",
            "uint ProbeY;",
            "uint ProbeWidth;",
            "uint ProbeHeight;");
        RequireOrdered(
            probe,
            "struct TemperatureProbeResult",
            "uint IsActive;",
            "uint MaterialIndex;",
            "float Temperature;",
            "uint Reserved;");
    }

    private static void VerifyBrushCommands()
    {
        SimulationSettings settings = new();
        Rectangle canvas = new(0, 0, settings.Width, settings.Height);
        RawInputSnapshot left = new(
            new Point(100, 100), 0, true, false, true, false, false, false,
            false, false, false, false, 1f / 60);
        BrushDrawCommand temperature = new CanvasBrushController().CreateCommands(
            left, canvas, settings, 1, true, true, 500, false)[0];
        Require(temperature.Mode == BrushCommandMode.SetTemperature &&
            temperature.TargetTemperature == 500,
            "Left temperature-tool command is incorrect.");

        RawInputSnapshot right = left with
        {
            LeftDown = false,
            RightDown = true,
            LeftPressed = false,
            RightPressed = true
        };
        BrushDrawCommand erase = new CanvasBrushController().CreateCommands(
            right, canvas, settings, 1, false, true, 500, false)[0];
        Require(erase.Mode == BrushCommandMode.Erase,
            "Right button did not override the temperature tool with erase.");

        BrushDrawCommand material = new CanvasBrushController().CreateCommands(
            left, canvas, settings, 1, false, false, 500, false)[0];
        Require(material.Mode == BrushCommandMode.Material && material.TargetTemperature == 0,
            "Ordinary material brush command changed semantics.");

        GpuCommandEncoder encoder = new();
        BrushDrawCommand tooHot = temperature;
        tooHot.TargetTemperature = 6000;
        ReadOnlySpan<BrushDrawCommand> encoded = encoder.Encode([tooHot]);
        Require(encoded[0].TargetTemperature == MaterialRegistry.MaximumInitialTemperature,
            "CPU command encoder did not clamp target temperature.");
        BrushDrawCommand invalid = temperature;
        invalid.TargetTemperature = float.NaN;
        bool rejected = false;
        try
        {
            encoder.Encode([invalid]);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Require(rejected, "CPU command encoder accepted NaN target temperature.");
    }

    private static void VerifyFixedStepScheduler()
    {
        ulong ticks30 = RunScheduler(30, 3);
        ulong ticks60 = RunScheduler(60, 3);
        ulong ticks100 = RunScheduler(100, 3);
        Require(ticks30 == 60 && ticks60 == 60 && ticks100 == 60,
            $"Fixed-step ticks differ by FPS: {ticks30}/{ticks60}/{ticks100}.");

        FixedStepThermalScheduler scheduler = new();
        for (int frame = 0; frame < 60; frame++) scheduler.Advance(1d / 60, false, true);
        ulong beforePause = scheduler.TotalTicks;
        for (int frame = 0; frame < 600; frame++) scheduler.Advance(1d / 60, true, true);
        Require(scheduler.TotalTicks == beforePause, "Pause accumulated thermal ticks.");
        int resumed = scheduler.Advance(0.05, false, true);
        Require(resumed == 1, "Resume performed catch-up thermal ticks.");
        scheduler.Reset();
        Require(scheduler.Advance(10, false, true) == 4,
            "Long frame exceeded or missed the four-tick cap.");
        scheduler.Reset();
        Require(scheduler.Advance(1, false, false) == 0 && scheduler.TotalTicks == 0,
            "Inactive thermal system accumulated ticks.");
    }

    private static ulong RunScheduler(int framesPerSecond, int seconds)
    {
        FixedStepThermalScheduler scheduler = new();
        for (int frame = 0; frame < framesPerSecond * seconds; frame++)
        {
            scheduler.Advance(1d / framesPerSecond, false, true);
        }
        return scheduler.TotalTicks;
    }

    private static void VerifyNumerics()
    {
        float[] uniform = [123.5f, 123.5f];
        Step(uniform, [1f, 1f], [0.8f, 0.8f], [true, true], 60);
        Require(uniform[0] == 123.5f && uniform[1] == 123.5f,
            "Uniform temperature changed.");

        float[] contact = [400, 0];
        double initialEnergy = Energy(contact, [1f, 1f]);
        float previousHot = contact[0];
        float previousCold = contact[1];
        for (int tick = 0; tick < 200; tick++)
        {
            Step(contact, [1f, 1f], [0.8f, 0.8f], [true, true], 1);
            Require(contact[0] <= previousHot && contact[1] >= previousCold && contact[0] >= contact[1],
                "Contact temperatures are non-monotonic or crossed.");
            previousHot = contact[0];
            previousCold = contact[1];
        }
        Require(RelativeError(initialEnergy, Energy(contact, [1f, 1f])) <= 1e-6,
            "Reference diffusion did not conserve energy.");
        Require(contact[0] is >= 0 and <= 400 && contact[1] is >= 0 and <= 400,
            "Reference diffusion exceeded its initial range.");

        float[] capacity = [400, 0];
        Step(capacity, [0.25f, 4f], [0.8f, 0.8f], [true, true], 20);
        Require(400 - capacity[0] > capacity[1],
            "Lower-capacity material did not change faster.");
        double equilibrium = (400 * 0.25) / 4.25;
        Step(capacity, [0.25f, 4f], [0.8f, 0.8f], [true, true], 2000);
        Require(Math.Abs(capacity[0] - equilibrium) < 0.1 && Math.Abs(capacity[1] - equilibrium) < 0.1,
            "Capacity-weighted equilibrium is incorrect.");

        float[] fast = [400, 0];
        float[] slow = [400, 0];
        Step(fast, [1f, 1f], [1f, 1f], [true, true], 20);
        Step(slow, [1f, 1f], [0.1f, 0.1f], [true, true], 20);
        Require(400 - fast[0] > 400 - slow[0], "Fast conductivity did not transfer more heat.");

        float[] insulated = [400, 20, 0];
        Step(insulated, [1f, 1f, 1f], [0.8f, 0, 0.8f], [true, true, true], 100);
        Require(insulated[0] == 400 && insulated[1] == 20 && insulated[2] == 0,
            "Zero-conductivity insulator transferred heat.");
        float[] vacuum = [400, 0, 0];
        Step(vacuum, [1f, 1f, 1f], [0.8f, 0, 0.8f], [true, false, true], 100);
        Require(vacuum[0] == 400 && vacuum[1] == 0 && vacuum[2] == 0,
            "Vacuum transferred heat or changed its default temperature.");
    }

    private static void Step(
        float[] temperatures,
        float[] capacities,
        float[] conductivities,
        bool[] active,
        int ticks)
    {
        float[] destination = new float[temperatures.Length];
        for (int tick = 0; tick < ticks; tick++)
        {
            for (int index = 0; index < temperatures.Length; index++)
            {
                if (!active[index]) { destination[index] = 0; continue; }
                float heat = 0;
                if (index > 0 && active[index - 1])
                    heat += Flow(index, index - 1, temperatures, capacities, conductivities);
                if (index + 1 < temperatures.Length && active[index + 1])
                    heat += Flow(index, index + 1, temperatures, capacities, conductivities);
                destination[index] = temperatures[index] + heat / Math.Max(capacities[index], MinimumMass);
            }
            (temperatures, destination) = (destination, temperatures);
        }
        if (ticks % 2 != 0)
        {
            Array.Copy(temperatures, destination, temperatures.Length);
        }
    }

    private static float Flow(
        int first,
        int second,
        float[] temperatures,
        float[] capacities,
        float[] conductivities)
    {
        float firstConductivity = conductivities[first];
        float secondConductivity = conductivities[second];
        if (firstConductivity <= 0 || secondConductivity <= 0) return 0;
        float contact = 2 * firstConductivity * secondConductivity /
            (firstConductivity + secondConductivity);
        float fraction = Math.Min(
            MaximumExchangeFraction,
            SimulationDispatchCoordinator.ThermalExchangeRate *
            SimulationDispatchCoordinator.FixedThermalStep);
        float coefficient = Math.Min(capacities[first], capacities[second]) *
            contact * fraction / 4;
        return coefficient * (temperatures[second] - temperatures[first]);
    }

    private static double Energy(float[] temperatures, float[] capacities)
    {
        double energy = 0;
        for (int index = 0; index < temperatures.Length; index++)
            energy += temperatures[index] * capacities[index];
        return energy;
    }

    private static void VerifyProbeMappingAndFormatting()
    {
        Rectangle bounds = new(10, 20, 100, 50);
        Require(GpuTemperatureProbe.MapPointerToCell(new Point(10, 20), bounds, 480, 270) == new Point(0, 0),
            "Probe top-left mapping failed.");
        Require(GpuTemperatureProbe.MapPointerToCell(new Point(109, 69), bounds, 480, 270) == new Point(475, 264),
            "Probe bottom-right mapping failed.");
        Require(GpuTemperatureProbe.MapPointerToCell(new Point(110, 69), bounds, 480, 270) is null,
            "Probe accepted a pointer outside worldBounds.");

        string directory = Path.Combine(Path.GetTempPath(), $"phyxel-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            MaterialRegistry materials = new(directory);
            uint sand = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
            string active = SandboxUiCoordinator.FormatTemperatureProbe(
                materials,
                new TemperatureProbeResult { IsActive = 1, MaterialIndex = sand, Temperature = 20 });
            string empty = SandboxUiCoordinator.FormatTemperatureProbe(
                materials,
                new TemperatureProbeResult());
            string outside = SandboxUiCoordinator.FormatTemperatureProbe(materials, null);
            Require(active.Contains("20,0 °C", StringComparison.Ordinal),
                "Probe temperature is not formatted with one decimal and comma.");
            Require(empty.Contains("Температура: —", StringComparison.Ordinal) &&
                outside == "Температура: —", "Empty/outside probe formatting is incorrect.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static double RelativeError(double expected, double actual) =>
        Math.Abs(actual - expected) / Math.Max(1, Math.Abs(expected));

    private static void RequireOrdered(string source, string start, params string[] fields)
    {
        int position = source.IndexOf(start, StringComparison.Ordinal);
        Require(position >= 0, $"HLSL declaration '{start}' is missing.");
        foreach (string field in fields)
        {
            int next = source.IndexOf(field, position + 1, StringComparison.Ordinal);
            Require(next > position, $"HLSL field '{field}' is missing or out of order.");
            position = next;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
