using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public sealed class AcceptanceRegressionHarness
{
    private MaterialRegistry? materialRegistry;

    public AcceptanceRegressionHarness()
    {
        string? requested = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_MODE") ??
            Environment.GetEnvironmentVariable("PHYXEL_SPEC_SCENARIO");
        Mode = requested?.Trim().ToLowerInvariant() switch
        {
            "bowl" or "acceptance_bowl" => AcceptanceScenarioMode.Bowl,
            "solid_gravity" or "acceptance_solid_gravity" => AcceptanceScenarioMode.SolidGravity,
            "sand" or "acceptance_sand" => AcceptanceScenarioMode.Sand,
            "hydro" or "acceptance_hydro" => AcceptanceScenarioMode.Hydro,
            "slope" or "acceptance_slope" => AcceptanceScenarioMode.Slope,
            "gas" or "acceptance_gas" => AcceptanceScenarioMode.Gas,
            "water_stress" or "stress_water" => AcceptanceScenarioMode.WaterStress,
            "flat_surface" or "surface" => AcceptanceScenarioMode.FlatSurface,
            "water_drain" or "drain" => AcceptanceScenarioMode.WaterDrain,
            "communicating_vessels" or "vessels" or "hydrostatic" => AcceptanceScenarioMode.CommunicatingVessels,
            "pressure_tube" or "tube" => AcceptanceScenarioMode.PressureTube,
            "saved_pressure" => AcceptanceScenarioMode.SavedPressure,
            "saved_isolation" or "isolation" => AcceptanceScenarioMode.SavedIsolation,
            "saved_gravity" => AcceptanceScenarioMode.SavedGravity,
            "buoyancy" or "float" => AcceptanceScenarioMode.Buoyancy,
            "saved_sand_water" or "sand_basin" => AcceptanceScenarioMode.SavedSandWater,
            "external_granular" => AcceptanceScenarioMode.ExternalGranular,
            "external_liquid" => AcceptanceScenarioMode.ExternalLiquid,
            "external_gas" => AcceptanceScenarioMode.ExternalGas,
            "external_solids" => AcceptanceScenarioMode.ExternalSolids,
            "underwater_granular" or "underwater_pile" => AcceptanceScenarioMode.UnderwaterGranularPile,
            "granular_displacement" or "soft_displacement" => AcceptanceScenarioMode.GranularWaterDisplacement,
            "granular_barrier" or "granular_barrier_off" => AcceptanceScenarioMode.GranularBarrier,
            "granular_barrier_hydraulic" or "granular_barrier_on" => AcceptanceScenarioMode.GranularBarrierHydraulic,
            "temperature_brush" => AcceptanceScenarioMode.TemperatureBrush,
            "thermal_uniform" => AcceptanceScenarioMode.ThermalUniform,
            "thermal_contact" => AcceptanceScenarioMode.ThermalContact,
            "thermal_capacity" => AcceptanceScenarioMode.ThermalCapacity,
            "thermal_fast" => AcceptanceScenarioMode.ThermalFast,
            "thermal_slow" => AcceptanceScenarioMode.ThermalSlow,
            "thermal_insulator" => AcceptanceScenarioMode.ThermalInsulator,
            "thermal_vacuum" => AcceptanceScenarioMode.ThermalVacuum,
            "thermal_gas" => AcceptanceScenarioMode.ThermalGas,
            _ => AcceptanceScenarioMode.None
        };
    }

    public AcceptanceScenarioMode Mode { get; }
    public bool Active => Mode != AcceptanceScenarioMode.None;
    public bool RequiresNativeResolution => Mode == AcceptanceScenarioMode.WaterStress;
    public bool RequiresSavedScene => Mode is
        AcceptanceScenarioMode.SavedPressure or AcceptanceScenarioMode.SavedIsolation or
        AcceptanceScenarioMode.SavedGravity or AcceptanceScenarioMode.SavedSandWater;

    public void ConfigureMaterials(MaterialRegistry registry)
    {
        materialRegistry = registry;
    }
    public uint CaptureFrame
    {
        get
        {
            if (uint.TryParse(
                Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_CAPTURE_FRAME"),
                out uint requestedFrame) && requestedFrame > 0)
            {
                return requestedFrame;
            }
            return Mode switch
            {
                AcceptanceScenarioMode.Bowl => 1000,
                AcceptanceScenarioMode.SolidGravity => 360,
                AcceptanceScenarioMode.Sand => 190,
                AcceptanceScenarioMode.Hydro => 1200,
                AcceptanceScenarioMode.Slope => 600,
                AcceptanceScenarioMode.Gas => 900,
                AcceptanceScenarioMode.WaterStress => 180,
                AcceptanceScenarioMode.FlatSurface => 1200,
                AcceptanceScenarioMode.WaterDrain => 1800,
                AcceptanceScenarioMode.CommunicatingVessels => 3600,
                AcceptanceScenarioMode.PressureTube => 1800,
                AcceptanceScenarioMode.SavedPressure => 2000,
                AcceptanceScenarioMode.SavedIsolation => 1200,
                AcceptanceScenarioMode.SavedGravity => 400,
                AcceptanceScenarioMode.Buoyancy => 500,
                AcceptanceScenarioMode.SavedSandWater => 900,
                AcceptanceScenarioMode.ExternalGranular => 190,
                AcceptanceScenarioMode.ExternalLiquid => 600,
                AcceptanceScenarioMode.ExternalGas => 900,
                AcceptanceScenarioMode.ExternalSolids => 300,
                AcceptanceScenarioMode.UnderwaterGranularPile => 720,
                AcceptanceScenarioMode.GranularWaterDisplacement => 13,
                AcceptanceScenarioMode.GranularBarrier => 900,
                AcceptanceScenarioMode.GranularBarrierHydraulic => 900,
                AcceptanceScenarioMode.TemperatureBrush => 3,
                AcceptanceScenarioMode.ThermalGas => 120,
                AcceptanceScenarioMode.ThermalUniform or
                AcceptanceScenarioMode.ThermalContact or
                AcceptanceScenarioMode.ThermalCapacity or
                AcceptanceScenarioMode.ThermalFast or
                AcceptanceScenarioMode.ThermalSlow or
                AcceptanceScenarioMode.ThermalInsulator or
                AcceptanceScenarioMode.ThermalVacuum => 300,
                _ => uint.MaxValue
            };
        }
    }

    public IReadOnlyList<BrushDrawCommand> CreateCommands(uint frame)
    {
        return AcceptanceRegressionScenario.CreateCommands(Mode, frame, materialRegistry);
    }

    public SimulationWorldSnapshot? CreateInitialWorld(int width, int height) =>
        materialRegistry is null
            ? null
            : ThermalAcceptanceScenario.Create(Mode, width, height, materialRegistry);

    public Point? ProbeCoordinate => Mode == AcceptanceScenarioMode.ThermalUniform
        ? new Point(200, 120)
        : null;

    public void ConfigureSettings(uint frame, SimulationSettings settings)
    {
        if (!Active)
        {
            return;
        }

        bool scenarioHydraulics = Mode is
            AcceptanceScenarioMode.Hydro or
            AcceptanceScenarioMode.WaterDrain or
            AcceptanceScenarioMode.CommunicatingVessels or
            AcceptanceScenarioMode.PressureTube or
            AcceptanceScenarioMode.SavedPressure or
            AcceptanceScenarioMode.SavedIsolation or
            AcceptanceScenarioMode.GranularBarrierHydraulic;
        settings.HydraulicPressure = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_HYDRAULICS") switch
        {
            "0" => false,
            "1" => true,
            _ => scenarioHydraulics
        };
        if (Mode is AcceptanceScenarioMode.SolidGravity or AcceptanceScenarioMode.Buoyancy or
            AcceptanceScenarioMode.ExternalSolids)
        {
            settings.SolidGravity = frame >= 60;
        }
        else if (Mode == AcceptanceScenarioMode.SavedGravity)
        {
            settings.SolidGravity = frame >= 30;
        }
    }

    public void CaptureScreenshot(GpuSimulationResources resources, uint frame)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_RECORD") == "1" && frame % 4 == 0)
        {
            string frameDirectory = Path.Combine(
                ArtifactDirectory,
                "recording",
                Mode.ToString().ToLowerInvariant());
            Directory.CreateDirectory(frameDirectory);
            SimulationScreenshotWriter.Save(
                resources,
                Path.Combine(frameDirectory, $"{frame / 4:D4}.png"));
        }
        string? label = Mode switch
        {
            AcceptanceScenarioMode.Bowl when frame == 125 => "A_water_2s",
            AcceptanceScenarioMode.Bowl when frame == 999 => "A_water_sand",
            AcceptanceScenarioMode.SolidGravity when frame == 59 => "B_gravity_off",
            AcceptanceScenarioMode.SolidGravity when frame == 120 => "B_falling",
            AcceptanceScenarioMode.SolidGravity when frame == 190 => "B_landed",
            AcceptanceScenarioMode.SolidGravity when frame == 359 => "B_split_stone",
            AcceptanceScenarioMode.Sand when frame == 189 => "C_pile_3s",
            AcceptanceScenarioMode.Hydro when frame == 125 => "D_equal_2s",
            AcceptanceScenarioMode.Hydro when frame == 15 => "D_waterfall",
            AcceptanceScenarioMode.Hydro when frame == 1199 => "D_rest",
            AcceptanceScenarioMode.Slope when frame == 20 => "E_slope_fall",
            AcceptanceScenarioMode.Slope when frame == 599 => "E_slope_rest",
            AcceptanceScenarioMode.Gas when frame == 30 => "F_gas_rise",
            AcceptanceScenarioMode.Gas when frame == 899 => "F_gas_spread",
            AcceptanceScenarioMode.FlatSurface when frame == 15 => "O_flat_stream_early",
            AcceptanceScenarioMode.FlatSurface when frame == 100 => "O_flat_stream_mid1",
            AcceptanceScenarioMode.FlatSurface when frame == 200 => "O_flat_stream_mid2",
            AcceptanceScenarioMode.FlatSurface when frame == 299 => "O_flat_stream_late",
            AcceptanceScenarioMode.FlatSurface when frame + 1 == CaptureFrame => "O_flat_surface",
            AcceptanceScenarioMode.WaterDrain when frame == 1799 => "G_water_drain",
            AcceptanceScenarioMode.CommunicatingVessels when frame == 125 => "H_vessels_2s",
            AcceptanceScenarioMode.CommunicatingVessels when frame == 3599 => "H_vessels_rest",
            AcceptanceScenarioMode.PressureTube when frame == 300 => "I_pressure_tube_fill",
            AcceptanceScenarioMode.PressureTube when frame == 1799 => "I_pressure_tube_rest",
            AcceptanceScenarioMode.SavedPressure when frame == 1199 => "J_saved_pressure",
            AcceptanceScenarioMode.SavedIsolation when frame + 1 == CaptureFrame => "K_saved_isolation",
            AcceptanceScenarioMode.SavedGravity when frame == 399 => "L_saved_gravity",
            AcceptanceScenarioMode.Buoyancy when frame == 499 => "M_buoyancy",
            AcceptanceScenarioMode.SavedSandWater when frame == 899 => "N_saved_sand_water",
            AcceptanceScenarioMode.ExternalGranular when frame == 189 => "Q_external_granular",
            AcceptanceScenarioMode.ExternalLiquid when frame == 599 => "R_external_liquid",
            AcceptanceScenarioMode.ExternalGas when frame == 30 => "S_external_gas_rise",
            AcceptanceScenarioMode.ExternalGas when frame == 899 => "S_external_gas_spread",
            AcceptanceScenarioMode.ExternalSolids when frame == 299 => "T_external_solids",
            AcceptanceScenarioMode.UnderwaterGranularPile when frame == 719 => "U_underwater_granular",
            AcceptanceScenarioMode.GranularWaterDisplacement when frame == 12 => "V_granular_displacement",
            AcceptanceScenarioMode.GranularBarrier when frame == 899 => "W_granular_barrier_off",
            AcceptanceScenarioMode.GranularBarrierHydraulic when frame == 899 => "X_granular_barrier_on",
            _ => null
        };
        if (label is null)
        {
            return;
        }
        Directory.CreateDirectory(ArtifactDirectory);
        SimulationScreenshotWriter.Save(resources, Path.Combine(ArtifactDirectory, $"{label}.png"));
    }

    public bool Validate(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        ulong thermalTicks,
        TemperatureProbeResult? temperatureProbe,
        ThermalGpuTimingStatistics thermalGpuTiming,
        ThermalGpuTimingStatistics probeGpuTiming,
        out string report)
    {
        bool passed = AcceptanceRegressionVerifier.Validate(
            Mode,
            materialRegistry,
            snapshot,
            statistics,
            framesPerSecond,
            thermalTicks,
            temperatureProbe,
            ArtifactDirectory,
            out report);
        report += Environment.NewLine +
            $"PHYXEL_ACCEPTANCE_METRICS size={snapshot.Width}x{snapshot.Height} fps={framesPerSecond:0.0} " +
            $"thermalGpuMs={thermalGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{thermalGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{thermalGpuTiming.MaximumMilliseconds:0.0000} samples={thermalGpuTiming.Samples} " +
            $"probeGpuMs={probeGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{probeGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{probeGpuTiming.MaximumMilliseconds:0.0000} probeSamples={probeGpuTiming.Samples}";
        Directory.CreateDirectory(ArtifactDirectory);
        File.WriteAllText(
            Path.Combine(ArtifactDirectory, "acceptance-report.txt"),
            report + Environment.NewLine +
            (passed ? "PHYXEL_ACCEPTANCE_SUCCESS" : "PHYXEL_ACCEPTANCE_FAILED") + Environment.NewLine);
        Console.WriteLine(report);
        Console.WriteLine(passed ? "PHYXEL_ACCEPTANCE_SUCCESS" : "PHYXEL_ACCEPTANCE_FAILED");
        return passed;
    }

    private static string ArtifactDirectory =>
        Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ??
        Path.Combine(Environment.CurrentDirectory, "artifacts", "powder-toy-acceptance");
}
