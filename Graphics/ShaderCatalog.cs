using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Phyxel.Graphics;

internal readonly record struct ShaderProgram(string FileName, string EntryPoint = "CSMain");

internal static class ShaderCatalog
{
    // Build-time verification compares this list with every runtime call. A new
    // entry point must ship as bytecode instead of silently compiling on players' PCs.
    internal static readonly ShaderProgram[] Programs =
    [
        new("GasActiveTiles.hlsl"),
        new("AirThermal.hlsl", "CSExchange"), new("AirThermal.hlsl", "CSFlux"), new("AirThermal.hlsl", "CSTransport"),
        new("PressureConfinement.hlsl", "CSInitialize"), new("PressureConfinement.hlsl", "CSUnion"), new("PressureConfinement.hlsl", "CSCompress"),
        new("PressureFracture.hlsl", "CSRelease"), new("PowderFront.hlsl"),
        new("PressureFracture.hlsl", "CSMoveOnly"), new("PressureFracture.hlsl", "CSAdvectOnly"),
        new("PressureFracture.hlsl", "CSUpdate"), new("PressureFracture.hlsl", "CSPlan"), new("PressureFracture.hlsl", "CSApply"),
        new("ReactionPulse.hlsl", "CSGather"), new("ReactionPulse.hlsl", "CSClearMapped"),
        new("ReactionPulse.hlsl", "CSFaces"), new("ReactionPulse.hlsl", "CSCommit"),
        new("ReactionPulse.hlsl", "CSFastFaces"), new("ReactionPulse.hlsl", "CSFastCommit"),
        new("ThermalDiffusion.hlsl", "CSRadiantDegrees"), new("BulkHeatDegrees.hlsl"),
        new("BrushApplication.hlsl"), new("CellularAutomataSolver.hlsl"), new("LiquidSurfaceBalance.hlsl"), new("GasRedistribution.hlsl"),
        new("SteamGasStepObserver.hlsl"), new("SteamGasStepObserver.hlsl", "CSLateralBands"),
        new("SteamGasStepObserver.hlsl", "CSBlocking"), new("SteamGasStepObserver.hlsl", "CSBlockingFrame"),
        new("SteamGasStepObserver.hlsl", "CSDiagonalIntent"), new("SteamJetInjectionObserver.hlsl"),
        new("SteamJetInjectionObserver.hlsl", "CSDistribution"), new("SteamJetMotionObserver.hlsl"), new("SteamJetAirCouplingObserver.hlsl"),
        new("SolidComponents.hlsl", "InitializeComponents"), new("SolidComponents.hlsl", "UnionComponents"),
        new("SolidComponents.hlsl", "CompressComponents"), new("SolidComponents.hlsl", "FinalizeComponents"),
        new("SolidBodySolver.hlsl", "AnalyzeSolidGeometry"), new("SolidBalance.hlsl", "AnalyzeBalance"),
        new("SolidBalance.hlsl", "PlanRotation"), new("SolidBalance.hlsl", "ApplyRotation"),
        new("SolidBalance.hlsl", "InitializeOrigins"), new("SolidBalance.hlsl", "BuildRotationPlans"),
        new("SolidBodySolver.hlsl", "AnalyzeSolidBodies"), new("SolidBodySolver.hlsl", "PlanHullWaterDisplacement"),
        new("SolidBodySolver.hlsl", "MoveSolidBodies"), new("SolidBodySolver.hlsl", "ApplyHullWaterDisplacement"),
        new("RenderComposition.hlsl"), new("ThermalDiffusion.hlsl"), new("WaterConvection.hlsl"),
        new("OxidizerTransport.hlsl", "CSTransport"), new("OxidizerTransport.hlsl", "CSFlux"), new("OxidizerTransport.hlsl", "CSConsume"),
        new("OxidizerTransport.hlsl", "CSCarrierFaces"), new("OxidizerTransport.hlsl", "CSCarrierDivergence"), new("OxidizerTransport.hlsl", "CSCarrierJacobi"),
        new("AirSimulation.hlsl", "CSInject"), new("AirSimulation.hlsl", "CSFineMaterials"),
        new("AirSimulation.hlsl", "CSPressure"), new("AirSimulation.hlsl", "CSVelocity"),
        new("AirSimulation.hlsl", "CSAdvect"), new("AirSimulation.hlsl", "CSCommit"), new("AirSimulation.hlsl", "CSClear"),
        new("AirSimulation.hlsl", "CSDivergence"), new("AirSimulation.hlsl", "CSFaces"),
        new("AirSimulation.hlsl", "CSJacobiAB"), new("AirSimulation.hlsl", "CSJacobiBA"), new("AirSimulation.hlsl", "CSProject"),
        new("FireGlow.hlsl", "CSDeposit"), new("FireGlow.hlsl", "CSDiffuse"),
        new("FireGlow.hlsl", "CSCommitGlow"), new("FireGlow.hlsl", "CSClearGlow"),
        new("GasVisual.hlsl", "CSDeposit"), new("GasVisual.hlsl", "CSDiffuse"), new("GasVisual.hlsl", "CSCommit"),
        new("ContactTransitions.hlsl"), new("ContactTransitions.hlsl", "CSMoisture"), new("PhaseTransitions.hlsl"),
        new("Combustion.hlsl"), new("EmissionResolve.hlsl"), new("TransientLifecycle.hlsl"),
        new("TemperatureProbe.hlsl"), new("TemperatureSensors.hlsl")
    ];

    internal static void VerifyRuntimeCalls(string path)
    {
        string source = File.ReadAllText(path)
            .Replace("Environment.GetEnvironmentVariable(\"PHYXEL_GAS_TILE_OVERRIDE_SHADER\") ?? \"GasActiveTiles.hlsl\"", "\"GasActiveTiles.hlsl\"", StringComparison.Ordinal)
            .Replace("CompileShader(fragmentShader", "CompileShader(\"PressureFracture.hlsl\"", StringComparison.Ordinal)
            .Replace("CompileShader(transportShader", "CompileShader(\"PressureFracture.hlsl\"", StringComparison.Ordinal);
        MatchCollection matches = Regex.Matches(source, "CompileShader\\(\\s*\"([^\"]+)\"\\s*(?:,\\s*\"([^\"]+)\"\\s*)?\\)");
        // The remaining occurrence is the method declaration. Fail closed on a
        // new dynamic expression that this deliberately small scanner can't resolve.
        if (Regex.Matches(source, @"\bCompileShader\(").Count != matches.Count + 1)
            throw new InvalidDataException("Shader catalog: unresolved runtime CompileShader call.");
        var actual = new HashSet<ShaderProgram>(matches.Select(m => new ShaderProgram(m.Groups[1].Value,
            m.Groups[2].Success ? m.Groups[2].Value : "CSMain")));
        if (Programs.Length != Programs.Distinct().Count() || !actual.SetEquals(Programs))
            throw new InvalidDataException("Shader catalog differs from runtime entry points. Update ShaderCatalog.Programs.");
    }
}
