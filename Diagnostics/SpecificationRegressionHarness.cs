using System;
using System.Collections.Generic;
using System.IO;
using Phyxel.Graphics;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public enum SpecificationScenarioMode
{
    None,
    WaterRest,
    Funnel,
    MetalElastic,
    MetalPlastic,
    ConcreteCrack,
    ConcreteBreak,
    RestSand,
    RestWater,
    WaterSlope,
    SandSlope
}

public sealed class SpecificationRegressionHarness
{
    private readonly RestingPerformanceVerifier? performanceVerifier;
    private readonly bool loadedBeamCapture;

    public SpecificationRegressionHarness()
    {
        string value = Environment.GetEnvironmentVariable("PHYXEL_SPEC_SCENARIO") ?? string.Empty;
        loadedBeamCapture = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_SPEC_LOADED_CAPTURE"),
            "1",
            StringComparison.Ordinal);
        Mode = value.ToLowerInvariant() switch
        {
            "water_rest" => SpecificationScenarioMode.WaterRest,
            "funnel" => SpecificationScenarioMode.Funnel,
            "metal_elastic" => SpecificationScenarioMode.MetalElastic,
            "metal_plastic" => SpecificationScenarioMode.MetalPlastic,
            "concrete_crack" => SpecificationScenarioMode.ConcreteCrack,
            "concrete_break" => SpecificationScenarioMode.ConcreteBreak,
            "rest_sand" => SpecificationScenarioMode.RestSand,
            "rest_water" => SpecificationScenarioMode.RestWater,
            "water_slope" => SpecificationScenarioMode.WaterSlope,
            "sand_slope" => SpecificationScenarioMode.SandSlope,
            _ => SpecificationScenarioMode.None
        };
        if (Mode is SpecificationScenarioMode.RestSand or SpecificationScenarioMode.RestWater)
        {
            performanceVerifier = new RestingPerformanceVerifier(Mode.ToString());
        }
    }

    public SpecificationScenarioMode Mode { get; }
    public bool Active => Mode != SpecificationScenarioMode.None;
    public bool RequiresQuarterScale => Mode is SpecificationScenarioMode.MetalElastic or
        SpecificationScenarioMode.MetalPlastic or SpecificationScenarioMode.ConcreteCrack or
        SpecificationScenarioMode.ConcreteBreak;
    public uint CaptureFrame => loadedBeamCapture && Mode is SpecificationScenarioMode.MetalElastic or
        SpecificationScenarioMode.MetalPlastic
        ? 230
        : Mode switch
    {
        SpecificationScenarioMode.WaterRest => 240,
        SpecificationScenarioMode.Funnel => 360,
        SpecificationScenarioMode.MetalElastic or SpecificationScenarioMode.MetalPlastic => 420,
        SpecificationScenarioMode.ConcreteCrack or SpecificationScenarioMode.ConcreteBreak => 300,
        SpecificationScenarioMode.WaterSlope => 900,
        SpecificationScenarioMode.SandSlope => 600,
        _ => uint.MaxValue
    };

    public IReadOnlyList<BrushDrawCommand> CreateCommands(uint frameIndex, int width, int height)
    {
        return SpecificationRegressionScenario.CreateCommands(Mode, frameIndex, width, height);
    }

    public bool RecordPerformanceFrame(
        SimulationDispatchCoordinator coordinator,
        out bool passed,
        out string report)
    {
        if (performanceVerifier is null)
        {
            passed = false;
            report = string.Empty;
            return false;
        }

        return performanceVerifier.RecordFrame(
            coordinator.CellularSleeping,
            coordinator.FullGridPhysicsDispatches,
            out passed,
            out report);
    }

    public void CaptureScreenshot(GpuSimulationResources resources, uint frameIndex)
    {
        string? label = Mode switch
        {
            SpecificationScenarioMode.MetalElastic or SpecificationScenarioMode.MetalPlastic when frameIndex == 6 => "before",
            SpecificationScenarioMode.MetalElastic or SpecificationScenarioMode.MetalPlastic when frameIndex == 221 => "loaded",
            SpecificationScenarioMode.MetalElastic or SpecificationScenarioMode.MetalPlastic when frameIndex == 421 => "after",
            SpecificationScenarioMode.ConcreteCrack when frameIndex == 280 => "cracks",
            SpecificationScenarioMode.ConcreteBreak when frameIndex == 280 => "broken",
            SpecificationScenarioMode.WaterSlope when frameIndex == 29 => "flow",
            SpecificationScenarioMode.SandSlope when frameIndex == 570 => "angle",
            _ => null
        };
        if (label is null)
        {
            return;
        }

        string directory = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ??
            Path.Combine(Environment.CurrentDirectory, "artifacts", "specification");
        Directory.CreateDirectory(directory);
        SimulationScreenshotWriter.Save(resources, Path.Combine(directory, $"{Mode}_{label}.png"));
    }

    public bool Validate(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        SimulationDispatchCoordinator coordinator,
        out string report)
    {
        bool passed = SpecificationRegressionVerifier.Validate(
            Mode,
            loadedBeamCapture,
            snapshot,
            statistics,
            coordinator,
            out report);
        Console.WriteLine(report);
        Console.WriteLine(passed ? "PHYXEL_SPEC_REGRESSION_SUCCESS" : "PHYXEL_SPEC_REGRESSION_FAILED");
        return passed;
    }
}
