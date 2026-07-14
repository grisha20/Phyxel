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
            _ => AcceptanceScenarioMode.None
        };
    }

    public AcceptanceScenarioMode Mode { get; }
    public bool Active => Mode != AcceptanceScenarioMode.None;
    public uint CaptureFrame => Mode switch
    {
        AcceptanceScenarioMode.Bowl => 420,
        AcceptanceScenarioMode.SolidGravity => 360,
        AcceptanceScenarioMode.Sand => 190,
        AcceptanceScenarioMode.Hydro => 360,
        AcceptanceScenarioMode.Slope => 300,
        AcceptanceScenarioMode.Gas => 240,
        _ => uint.MaxValue
    };

    public IReadOnlyList<BrushDrawCommand> CreateCommands(uint frame)
    {
        return AcceptanceRegressionScenario.CreateCommands(Mode, frame);
    }

    public void ConfigureSettings(uint frame, SimulationSettings settings)
    {
        settings.SolidGravity = Mode == AcceptanceScenarioMode.SolidGravity && frame >= 60;
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
            AcceptanceScenarioMode.Bowl when frame == 419 => "A_water_sand",
            AcceptanceScenarioMode.SolidGravity when frame == 59 => "B_gravity_off",
            AcceptanceScenarioMode.SolidGravity when frame == 120 => "B_falling",
            AcceptanceScenarioMode.SolidGravity when frame == 190 => "B_landed",
            AcceptanceScenarioMode.SolidGravity when frame == 359 => "B_split_concrete",
            AcceptanceScenarioMode.Sand when frame == 189 => "C_pile_3s",
            AcceptanceScenarioMode.Hydro when frame == 125 => "D_equal_2s",
            AcceptanceScenarioMode.Hydro when frame == 15 => "D_waterfall",
            AcceptanceScenarioMode.Hydro when frame == 359 => "D_rest",
            AcceptanceScenarioMode.Slope when frame == 20 => "E_slope_fall",
            AcceptanceScenarioMode.Slope when frame == 299 => "E_slope_rest",
            AcceptanceScenarioMode.Gas when frame == 30 => "F_gas_rise",
            AcceptanceScenarioMode.Gas when frame == 239 => "F_gas_spread",
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
        Console.WriteLine(report);
        Console.WriteLine(passed ? "PHYXEL_ACCEPTANCE_SUCCESS" : "PHYXEL_ACCEPTANCE_FAILED");
        return passed;
    }

    private static string ArtifactDirectory =>
        Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ??
        Path.Combine(Environment.CurrentDirectory, "artifacts", "powder-toy-acceptance");
}
