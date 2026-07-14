using System;
using System.Collections.Generic;
using System.IO;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public sealed class AcceptanceRegressionHarness
{
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
            "water_drain" or "drain" => AcceptanceScenarioMode.WaterDrain,
            "communicating_vessels" or "vessels" or "hydrostatic" => AcceptanceScenarioMode.CommunicatingVessels,
            "pressure_tube" or "tube" => AcceptanceScenarioMode.PressureTube,
            "saved_pressure" => AcceptanceScenarioMode.SavedPressure,
            "saved_isolation" or "isolation" => AcceptanceScenarioMode.SavedIsolation,
            "saved_gravity" => AcceptanceScenarioMode.SavedGravity,
            "buoyancy" or "float" => AcceptanceScenarioMode.Buoyancy,
            "saved_sand_water" or "sand_basin" => AcceptanceScenarioMode.SavedSandWater,
            _ => AcceptanceScenarioMode.None
        };
    }

    public AcceptanceScenarioMode Mode { get; }
    public bool Active => Mode != AcceptanceScenarioMode.None;
    public bool RequiresNativeResolution => Mode == AcceptanceScenarioMode.WaterStress;
    public bool RequiresSavedScene => Mode is
        AcceptanceScenarioMode.SavedPressure or AcceptanceScenarioMode.SavedIsolation or
        AcceptanceScenarioMode.SavedGravity or AcceptanceScenarioMode.SavedSandWater;
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
                AcceptanceScenarioMode.WaterDrain => 1200,
                AcceptanceScenarioMode.CommunicatingVessels => 1200,
                AcceptanceScenarioMode.PressureTube => 1200,
                AcceptanceScenarioMode.SavedPressure => 2000,
                AcceptanceScenarioMode.SavedIsolation => 600,
                AcceptanceScenarioMode.SavedGravity => 400,
                AcceptanceScenarioMode.Buoyancy => 500,
                AcceptanceScenarioMode.SavedSandWater => 900,
                _ => uint.MaxValue
            };
        }
    }

    public IReadOnlyList<BrushDrawCommand> CreateCommands(uint frame)
    {
        return AcceptanceRegressionScenario.CreateCommands(Mode, frame);
    }

    public void ConfigureSettings(uint frame, SimulationSettings settings)
    {
        if (Mode is AcceptanceScenarioMode.SolidGravity or AcceptanceScenarioMode.Buoyancy)
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
            AcceptanceScenarioMode.SolidGravity when frame == 359 => "B_split_concrete",
            AcceptanceScenarioMode.Sand when frame == 189 => "C_pile_3s",
            AcceptanceScenarioMode.Hydro when frame == 125 => "D_equal_2s",
            AcceptanceScenarioMode.Hydro when frame == 15 => "D_waterfall",
            AcceptanceScenarioMode.Hydro when frame == 1199 => "D_rest",
            AcceptanceScenarioMode.Slope when frame == 20 => "E_slope_fall",
            AcceptanceScenarioMode.Slope when frame == 599 => "E_slope_rest",
            AcceptanceScenarioMode.Gas when frame == 30 => "F_gas_rise",
            AcceptanceScenarioMode.Gas when frame == 899 => "F_gas_spread",
            AcceptanceScenarioMode.WaterDrain when frame == 1199 => "G_water_drain",
            AcceptanceScenarioMode.CommunicatingVessels when frame == 125 => "H_vessels_2s",
            AcceptanceScenarioMode.CommunicatingVessels when frame == 1199 => "H_vessels_rest",
            AcceptanceScenarioMode.PressureTube when frame == 300 => "I_pressure_tube_fill",
            AcceptanceScenarioMode.PressureTube when frame == 1199 => "I_pressure_tube_rest",
            AcceptanceScenarioMode.SavedPressure when frame == 1199 => "J_saved_pressure",
            AcceptanceScenarioMode.SavedIsolation when frame == 599 => "K_saved_isolation",
            AcceptanceScenarioMode.SavedGravity when frame == 399 => "L_saved_gravity",
            AcceptanceScenarioMode.Buoyancy when frame == 499 => "M_buoyancy",
            AcceptanceScenarioMode.SavedSandWater when frame == 899 => "N_saved_sand_water",
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
        out string report)
    {
        bool passed = AcceptanceRegressionVerifier.Validate(
            Mode,
            snapshot,
            statistics,
            framesPerSecond,
            ArtifactDirectory,
            out report);
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
