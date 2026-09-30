using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class AcceptanceRegressionVerifier
{
    private static AcceptanceMaterialIndices materials = null!;

    private static bool ValidateGasBrushFps(SimulationWorldSnapshot snapshot, uint gas, string artifactDirectory, out string report)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        if (Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_GAS_BRUSH_PAUSED") == "1")
        {
            int active = 0;
            foreach (GridCell cell in cells) active += cell.IsActive != 0 ? 1 : 0;
            int painted = CountColor(Path.Combine(artifactDirectory, "F_gas_paused_paint.png"),
                170, 90, 230, 150, color => color.B > 20 && color.B > color.G * 1.25);
            int erased = CountColor(Path.Combine(artifactDirectory, "F_gas_brush_fps.png"),
                170, 90, 230, 150, color => color.R > 20 || color.G > 20 || color.B > 20);
            report = $"PHYXEL_GAS_BRUSH_PAUSED activeAfterErase={active} paintedPixels={painted} erasedPixels={erased}";
            return active == 0 && painted > 50 && erased == 0;
        }
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? [] : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        if (motion.Length != cells.Length) return Fail(out report);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> sample = stackalloc byte[28];
        double mass = 0, x = 0, y = 0, xx = 0;
        int count = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            GridCell cell = cells[i];
            if (cell.IsActive == 0 || cell.MaterialIndex != gas) continue;
            count++;
            mass += cell.Mass;
            x += (i % snapshot.Width) * cell.Mass;
            y += (i / snapshot.Width) * cell.Mass;
            xx += Math.Pow(i % snapshot.Width, 2) * cell.Mass;
            BitConverter.TryWriteBytes(sample, i);
            BitConverter.TryWriteBytes(sample[4..], cell.Mass);
            BitConverter.TryWriteBytes(sample[8..], cell.Temperature);
            BitConverter.TryWriteBytes(sample[12..], motion[i].VelocityX);
            BitConverter.TryWriteBytes(sample[16..], motion[i].VelocityY);
            BitConverter.TryWriteBytes(sample[20..], motion[i].OffsetX);
            BitConverter.TryWriteBytes(sample[24..], motion[i].OffsetY);
            hash.AppendData(sample);
        }
        double sigmaX = Math.Sqrt(Math.Max(0, xx / Math.Max(1, mass) - Math.Pow(x / Math.Max(1, mass), 2)));
        report = $"PHYXEL_GAS_BRUSH_FPS cells={count} mass={mass:F6} centreX={x / Math.Max(1, mass):F6} centreY={y / Math.Max(1, mass):F6} sigmaX={sigmaX:F6} hash={Convert.ToHexString(hash.GetHashAndReset())}";
        return mass > 197 && Math.Abs(mass - count) < 0.01;
    }

    private readonly record struct ComponentMetrics(
        int Count,
        int SignificantCount,
        int Largest,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY);

    private readonly record struct OpenFlameSliceMetrics(
        int Components,
        int Width,
        int Occupied);

    internal static bool Validate(
        AcceptanceScenarioMode mode,
        MaterialRegistry? materialRegistry,
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        ulong thermalTicks,
        TemperatureProbeResult? temperatureProbe,
        IReadOnlyList<ThermalAcceptanceCheckpoint> thermalCheckpoints,
        TemperatureProbeAcceptanceTrace temperatureProbeTrace,
        ThermalGpuTimingStatistics thermalGpuTiming,
        ThermalGpuTimingStatistics phaseGpuTiming,
        ThermalGpuTimingStatistics combustionGpuTiming,
        ulong combustionDispatches,
        ulong combustionSummaryReadbacks,
        ulong phaseDispatches,
        ulong phaseSummaryReadbacks,
        ulong phaseFallbackWakeUps,
        int maximumPhaseDispatchesPerFrame,
        PhaseTransitionSummaryFlags phaseSummary,
        bool phasePresentationIsCurrent,
        IReadOnlyList<PhaseAcceptanceCheckpoint> phaseCheckpoints,
        string artifactDirectory,
        out string report)
    {
        if (materialRegistry is null)
        {
            return Fail(out report);
        }
        materials = new AcceptanceMaterialIndices(materialRegistry);
        return mode switch
        {
            AcceptanceScenarioMode.Bowl => ValidateBowl(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.SolidGravity => ValidateSolidGravity(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.Sand => ValidateSand(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.Hydro => ValidateHydro(
                snapshot,
                statistics,
                framesPerSecond,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.Slope => MaterialRegressionVerifier.ValidateSlope(
                snapshot,
                materials.Sand,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.Gas => MaterialRegressionVerifier.ValidateGas(
                snapshot,
                materials.Gas,
                artifactDirectory,
                "F_gas_rise.png",
                "F_gas_spread.png",
                out report,
                checkpoints: thermalCheckpoints),
            AcceptanceScenarioMode.GasBrushFps => ValidateGasBrushFps(snapshot, materials.Gas, artifactDirectory, out report),
            AcceptanceScenarioMode.WaterStress => ValidateWaterStress(
                snapshot,
                framesPerSecond,
                out report),
            AcceptanceScenarioMode.FlatSurface => ValidateFlatSurface(
                snapshot,
                framesPerSecond,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.WaterDrain => ValidateWaterDrain(snapshot, out report),
            AcceptanceScenarioMode.CommunicatingVessels => ValidateCommunicatingVessels(
                snapshot,
                statistics,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.PressureTube => ValidatePressureTube(
                snapshot,
                statistics,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.SavedPressure => ValidateSavedPressure(snapshot, out report),
            AcceptanceScenarioMode.SavedIsolation => ValidateSavedIsolation(
                snapshot,
                statistics,
                out report),
            AcceptanceScenarioMode.SavedGravity => ValidateSavedGravity(
                snapshot,
                statistics,
                framesPerSecond,
                out report),
            AcceptanceScenarioMode.Buoyancy => ValidateBuoyancy(snapshot, framesPerSecond, out report),
            AcceptanceScenarioMode.SavedSandWater => ValidateSavedSandWater(snapshot, out report),
            AcceptanceScenarioMode.ExternalGranular =>
                MaterialRegressionVerifier.ValidateGranularPile(
                    snapshot,
                    materials.Resolve("test:granular"),
                    artifactDirectory,
                    "Q_external_granular.png",
                    out report),
            AcceptanceScenarioMode.ExternalLiquid => ValidateExternalLiquid(
                snapshot,
                materials.Resolve("acceptance:liquid"),
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.ExternalGas => MaterialRegressionVerifier.ValidateGas(
                snapshot,
                materials.Resolve("acceptance:gas"),
                artifactDirectory,
                "S_external_gas_rise.png",
                "S_external_gas_spread.png",
                out report,
                heavyGas: false),
            AcceptanceScenarioMode.ExternalSolids => ValidateExternalSolids(
                snapshot,
                materials.Resolve("acceptance:solid_light"),
                materials.Resolve("acceptance:solid_heavy"),
                materials.Resolve("acceptance:fixture"),
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.UnderwaterGranularPile => ValidateUnderwaterGranularPile(
                snapshot,
                materials.Resolve("test:granular"),
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.GranularWaterDisplacement => ValidateGranularWaterDisplacement(
                snapshot,
                materials.Resolve("test:granular"),
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.GranularBarrier => ValidateGranularBarrier(
                snapshot,
                materials.Resolve("test:granular"),
                statistics,
                false,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.GranularBarrierHydraulic => ValidateGranularBarrier(
                snapshot,
                materials.Resolve("test:granular"),
                statistics,
                true,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.TemperatureBrush => ValidateTemperatureBrush(snapshot, out report),
            AcceptanceScenarioMode.TemperatureTool or
            AcceptanceScenarioMode.ThermalUniform or
            AcceptanceScenarioMode.ThermalContact or
            AcceptanceScenarioMode.ThermalCapacity or
            AcceptanceScenarioMode.ThermalConductivityCompare or
            AcceptanceScenarioMode.ThermalFast or
            AcceptanceScenarioMode.ThermalSlow or
            AcceptanceScenarioMode.ThermalInsulator or
            AcceptanceScenarioMode.ThermalVacuum or
            AcceptanceScenarioMode.ThermalGas or
            AcceptanceScenarioMode.SteamSelfCooling or
            AcceptanceScenarioMode.TemperatureProbeGpu => ThermalAcceptanceVerifier.Validate(
                mode,
                materialRegistry,
                snapshot,
                thermalTicks,
                temperatureProbe,
                thermalCheckpoints,
                temperatureProbeTrace,
                out report),
            AcceptanceScenarioMode.PhaseDispatchSmoke => ValidatePhaseDispatchSmoke(
                snapshot,
                materialRegistry,
                phaseGpuTiming,
                phaseDispatches,
                phaseFallbackWakeUps,
                maximumPhaseDispatchesPerFrame,
                phaseSummary,
                phasePresentationIsCurrent,
                out report),
            AcceptanceScenarioMode.CombustionChain => ValidateCombustionChain(
                snapshot,
                materialRegistry,
                combustionGpuTiming,
                combustionDispatches,
                combustionSummaryReadbacks,
                out report),
            AcceptanceScenarioMode.CombustionQuench => ValidateCombustionQuench(
                snapshot,
                materialRegistry,
                combustionGpuTiming,
                combustionDispatches,
                out report),
            AcceptanceScenarioMode.FireObstacle => ValidateFireObstacle(
                snapshot,
                materialRegistry,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.FireOpen => ValidateFireOpen(
                snapshot,
                materialRegistry,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.SteamPuff => ValidateSteamPuff(
                snapshot,
                materialRegistry,
                thermalCheckpoints,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.SteamJet => ValidateSteamJet(
                snapshot,
                materialRegistry,
                thermalCheckpoints,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.SteamObstacle => ValidateSteamJet(
                snapshot,
                materialRegistry,
                thermalCheckpoints,
                artifactDirectory,
                out report,
                steamObstacle: true),
            AcceptanceScenarioMode.Furnace => ValidateFurnace(
                snapshot,
                materialRegistry,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.MetalChimney => ValidateMetalChimney(
                snapshot,
                materialRegistry,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.BrushEmptyOnly => BrushEmptyOnlyAcceptanceVerifier.Validate(
                snapshot,
                materialRegistry,
                out report),
            AcceptanceScenarioMode.ContinuousBrushStroke =>
                ContinuousBrushStrokeAcceptanceVerifier.Validate(
                    snapshot,
                    materialRegistry,
                    out report),
            AcceptanceScenarioMode.CoalTypes => CoalTypesAcceptanceVerifier.Validate(
                snapshot,
                materialRegistry,
                thermalCheckpoints,
                out report),
            AcceptanceScenarioMode.GasUniformDistribution =>
                GasUniformDistributionAcceptanceVerifier.Validate(
                    snapshot,
                    materialRegistry,
                    thermalCheckpoints,
                    out report),
            AcceptanceScenarioMode.SteamDistributionAndCooling =>
                SteamDistributionAndCoolingAcceptanceVerifier.Validate(
                    snapshot,
                    materialRegistry,
                    thermalCheckpoints,
                    out report),
            AcceptanceScenarioMode.SteamCloudTemperature =>
                SteamCloudTemperatureAcceptanceVerifier.Validate(
                    snapshot,
                    materialRegistry,
                    thermalCheckpoints,
                    out report),
            AcceptanceScenarioMode.PhaseThresholds or
            AcceptanceScenarioMode.PhaseHysteresis or
            AcceptanceScenarioMode.PhaseSingleTransition or
            AcceptanceScenarioMode.PhaseNormalizationMatrix or
            AcceptanceScenarioMode.PhaseSummaryLiquidGas or
            AcceptanceScenarioMode.PhaseSummarySolidLiquid or
            AcceptanceScenarioMode.PhaseSummaryGasMovable or
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
            AcceptanceScenarioMode.PhasePauseContinue or
            AcceptanceScenarioMode.PhaseWakeGas or
            AcceptanceScenarioMode.PhaseWakeLiquid or
            AcceptanceScenarioMode.PhaseReadbackFallback or
            AcceptanceScenarioMode.PhaseExternalReorder or
            AcceptanceScenarioMode.PhaseDisabledRegistry or
            AcceptanceScenarioMode.PhaseEnergyContract or
            AcceptanceScenarioMode.PhaseV5RoundTrip or
            AcceptanceScenarioMode.PhasePerformanceSteady or
            AcceptanceScenarioMode.PhasePerformanceBurst or
            AcceptanceScenarioMode.WaterIceSteam or
            AcceptanceScenarioMode.WaterIceSteamMotion or
            AcceptanceScenarioMode.WaterIceSteamPause or
            AcceptanceScenarioMode.WaterIceSteamV5RoundTrip => PhaseAcceptanceVerifier.Validate(
                mode,
                materialRegistry,
                snapshot,
                phaseCheckpoints,
                thermalGpuTiming,
                phaseGpuTiming,
                phaseDispatches,
                phaseSummaryReadbacks,
                phaseFallbackWakeUps,
                maximumPhaseDispatchesPerFrame,
                phaseSummary,
                phasePresentationIsCurrent,
                out report),
            _ => Fail(out report)
        };
    }

    private static bool ValidateCombustionChain(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        ThermalGpuTimingStatistics timing,
        ulong dispatches,
        ulong summaryReadbacks,
        out string report)
    {
        uint wood = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood);
        uint coal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        int woodCount = 0;
        int coalCount = 0;
        int partiallyBurnedWood = 0;
        int flameCount = 0;
        int smokeCount = 0;
        int invalidFlameLifetime = 0;
        int hotWoodCount = 0;
        float minimumWoodMass = float.PositiveInfinity;
        float maximumWoodTemperature = float.NegativeInfinity;
        float maximumCoalTemperature = float.NegativeInfinity;
        foreach (GridCell cell in Cells(snapshot))
        {
            if (cell.IsActive == 0)
            {
                continue;
            }
            if (cell.MaterialIndex == wood)
            {
                woodCount++;
                minimumWoodMass = Math.Min(minimumWoodMass, cell.Mass);
                maximumWoodTemperature = Math.Max(maximumWoodTemperature, cell.Temperature);
                if (cell.Temperature > registry[CoreMaterialIds.Wood].Properties.IgnitionTemperature)
                {
                    hotWoodCount++;
                }
                if (cell.Mass < registry[CoreMaterialIds.Wood].Properties.Density)
                {
                    partiallyBurnedWood++;
                }
            }
            else if (cell.MaterialIndex == coal)
            {
                coalCount++;
                maximumCoalTemperature = Math.Max(maximumCoalTemperature, cell.Temperature);
            }
            else if (cell.MaterialIndex == fire)
            {
                flameCount++;
                if (cell.Lifetime <= 0 ||
                    cell.Lifetime > registry[CoreMaterialIds.Fire].Properties.MaximumLifetime)
                {
                    invalidFlameLifetime++;
                }
            }
            else if (cell.MaterialIndex == smoke)
            {
                smokeCount++;
            }
        }
        // Уголь больше не инертный остаток: он сыпучий и горючий. Свежий уголь
        // рождается внутри горящего дерева при ~900 °C, что выше его точки
        // воспламенения, поэтому он продолжает гореть после прогорания дерева.
        // Проверяем не инертность, а согласованность: уголь горит в пустоту и
        // не может превысить собственный температурный потолок.
        MaterialProperties coalProperties = registry[CoreMaterialIds.Coal].Properties;
        bool coalBurnsAway = coalProperties.BurnedIntoMaterialIndex ==
            registry.GetRequiredRuntimeIndex(CoreMaterialIds.Empty);
        bool coalTemperatureSane = coalCount == 0 ||
            maximumCoalTemperature <= coalProperties.MaximumCombustionTemperature + 0.5f;
        bool passed = partiallyBurnedWood > 100 &&
            flameCount > 0 && smokeCount > 0 && invalidFlameLifetime == 0 &&
            maximumWoodTemperature <= registry[CoreMaterialIds.Wood].Properties.MaximumCombustionTemperature + 0.5f &&
            coalBurnsAway && coalTemperatureSane &&
            dispatches > 0 && timing.Samples > 0;
        report = $"PHYXEL_COMBUSTION_ACCEPTANCE coal={coalCount} wood={woodCount} " +
            $"partialWood={partiallyBurnedWood} minimumWoodMass={minimumWoodMass:0.0000} " +
            $"hotWood={hotWoodCount} maxWoodTemp={maximumWoodTemperature:0.0} " +
            $"maxCoalTemp={maximumCoalTemperature:0.0} coalBurnsAway={coalBurnsAway} " +
            $"coalTempSane={coalTemperatureSane} " +
            $"flame={flameCount} smoke={smokeCount} invalidFlameLifetime={invalidFlameLifetime} " +
            $"dispatches={dispatches} summaryReadbacks={summaryReadbacks} " +
            $"gpuMs={timing.AverageMilliseconds:0.0000}/{timing.MinimumMilliseconds:0.0000}/" +
            $"{timing.MaximumMilliseconds:0.0000} samples={timing.Samples}";
        return passed;
    }

    private static bool ValidateCombustionQuench(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        ThermalGpuTimingStatistics timing,
        ulong dispatches,
        out string report)
    {
        uint wood = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood);
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        int woodCount = 0;
        int cooledWood = 0;
        int waterCount = 0;
        int steamCount = 0;
        double woodMass = 0;
        double woodTemperature = 0;
        foreach (GridCell cell in Cells(snapshot))
        {
            if (cell.IsActive == 0) continue;
            if (cell.MaterialIndex == wood)
            {
                woodCount++;
                woodMass += cell.Mass;
                woodTemperature += cell.Temperature;
                if (cell.Temperature <= registry[CoreMaterialIds.Wood].Properties.IgnitionTemperature)
                {
                    cooledWood++;
                }
            }
            else if (cell.MaterialIndex == water) waterCount++;
            else if (cell.MaterialIndex == steam) steamCount++;
        }
        double averageMass = woodMass / Math.Max(1, woodCount);
        double averageTemperature = woodTemperature / Math.Max(1, woodCount);
        bool passed = woodCount > 20 && cooledWood > 20 &&
            averageMass > registry[CoreMaterialIds.Coal].Properties.Density + 0.05 &&
            averageTemperature < registry[CoreMaterialIds.Wood].Properties.MaximumCombustionTemperature - 25 &&
            waterCount > 0 && steamCount > 0 && dispatches > 0 && timing.Samples > 0;
        report = $"PHYXEL_COMBUSTION_QUENCH wood={woodCount} cooled={cooledWood} " +
            $"averageMass={averageMass:0.000} averageTemperature={averageTemperature:0.0} " +
            $"water={waterCount} steam={steamCount} dispatches={dispatches} " +
            $"gpuSamples={timing.Samples}";
        return passed;
    }

    private static bool ValidateFireObstacle(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        string artifactDirectory,
        out string report,
        bool steamObstacle = false)
    {
        (int plateLeft, int plateRight) = AcceptanceRegressionScenario.GetFireObstaclePlateBounds();
        int plateCentre = (plateLeft + plateRight) / 2;
        const int surfaceTop = 107;
        const int surfaceBottom = 135;
        const float fireToSmokeTemperatureCelsius = 351.85f;
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        uint metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        int leftCells = 0;
        int rightCells = 0;
        int centreCells = 0;
        double fireSurfaceMass = 0;
        double sumFireSurfaceMassX = 0;
        int minimumX = snapshot.Width;
        int maximumX = -1;
        int minimumY = snapshot.Height;
        int contactGas = 0;
        int lateralLeft = 0;
        int lateralRight = 0;
        int blockedGas = 0;
        int globalMinimumX = snapshot.Width;
        int globalMaximumX = -1;
        int fireGlobalMinimumX = snapshot.Width;
        int fireGlobalMaximumX = -1;
        int globalSmoke = 0;
        int globalFire = 0;
        int smokeMinimumY = snapshot.Height;
        int smokeMaximumY = -1;
        int contactFire = 0;
        int contactSmoke = 0;
        int contactFireMinimumX = snapshot.Width;
        int contactFireMaximumX = -1;
        int smokeAboveLeft = 0;
        int smokeAboveRight = 0;
        int[] fireHistogram = new int[snapshot.Width];
        int[] fireSmokeHistogram = new int[snapshot.Width];
        int[] fireContactRowHistogram = new int[snapshot.Width];
        int[] smokeContactRowHistogram = new int[snapshot.Width];
        bool[] contactColumns = new bool[plateRight - plateLeft + 1];
        bool[] fireContactColumns = new bool[plateRight - plateLeft + 1];
        bool fireReachesLeftEnd = false;
        bool fireReachesRightEnd = false;
        List<double> fireLifetimeFrames = [];
        double sumFireTemperature = 0;
        double minimumFireTemperature = double.PositiveInfinity;
        int fireBelowSmokeTransition = 0;
        ReadOnlySpan<GasMotionState> gasMotion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        bool hasGasMotion = gasMotion.Length == grid.Length;
        int contactMotionCells = 0;
        int offsetXAtClamp = 0;
        int offsetYAtClamp = 0;
        int offsetXAtClampWithVelocity = 0;
        int offsetYAtClampWithVelocity = 0;
        double sumAbsVelocityXAtClamp = 0;
        double sumAbsVelocityYAtClamp = 0;
        int fullySurrounded = 0;
        double sumAbsOffsetX = 0;
        double sumAbsOffsetY = 0;
        double sumAbsVelocityX = 0;
        double sumAbsVelocityY = 0;
        double maximumAbsOffsetX = 0;
        double maximumAbsOffsetY = 0;
        double maximumAbsVelocityX = 0;
        double maximumAbsVelocityY = 0;
        int signedVelocityXLeftCells = 0;
        int signedVelocityXRightCells = 0;
        double sumSignedVelocityXLeft = 0;
        double sumSignedVelocityXRight = 0;
        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            if (cell.IsActive == 0 ||
                (cell.MaterialIndex != fire && cell.MaterialIndex != smoke))
            {
                continue;
            }
            int x = index % snapshot.Width;
            globalMinimumX = Math.Min(globalMinimumX, x);
            globalMaximumX = Math.Max(globalMaximumX, x);
            if (cell.MaterialIndex == smoke)
            {
                globalSmoke++;
                int y = index / snapshot.Width;
                smokeMinimumY = Math.Min(smokeMinimumY, y);
                smokeMaximumY = Math.Max(smokeMaximumY, y);
                if (y < 100)
                {
                    if (x < 225) smokeAboveLeft++;
                    if (x > 255) smokeAboveRight++;
                }
            }
            else if (cell.MaterialIndex == fire)
            {
                globalFire++;
                fireGlobalMinimumX = Math.Min(fireGlobalMinimumX, x);
                fireGlobalMaximumX = Math.Max(fireGlobalMaximumX, x);
            }
        }

        // The plate occupies y=94..106.  This deliberately scans the entire
        // world width: the former x=140..340 measuring window could itself
        // become the reported flame width.  Glow is excluded because it can
        // look wide while every physical gas cell remains in the centre column.
        for (int y = surfaceTop; y <= surfaceBottom; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 ||
                    (cell.MaterialIndex != fire && cell.MaterialIndex != smoke))
                {
                    continue;
                }
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                if (cell.MaterialIndex == fire)
                {
                    contactFire++;
                    fireSurfaceMass += cell.Mass;
                    sumFireSurfaceMassX += x * cell.Mass;
                    fireHistogram[x]++;
                    fireSmokeHistogram[x]++;
                    if (y <= 108 && x >= plateLeft && x <= plateRight)
                    {
                        fireContactRowHistogram[x]++;
                        contactColumns[x - plateLeft] = true;
                        fireContactColumns[x - plateLeft] = true;
                        contactFireMinimumX = Math.Min(contactFireMinimumX, x);
                        contactFireMaximumX = Math.Max(contactFireMaximumX, x);
                        fireReachesLeftEnd |= x < plateLeft + 10;
                        fireReachesRightEnd |= x > plateRight - 10;
                    }
                    if (y <= 108)
                    {
                        fireLifetimeFrames.Add(cell.Lifetime * 60.0);
                        sumFireTemperature += cell.Temperature;
                        minimumFireTemperature = Math.Min(minimumFireTemperature, cell.Temperature);
                        if (cell.Temperature < fireToSmokeTemperatureCelsius)
                        {
                            fireBelowSmokeTransition++;
                        }
                    }
                    if (x < plateCentre)
                    {
                        leftCells++;
                    }
                    else if (x > plateCentre)
                    {
                        rightCells++;
                    }
                    else
                    {
                        centreCells++;
                    }
                }
                else if (cell.MaterialIndex == smoke)
                {
                    contactSmoke++;
                    fireSmokeHistogram[x]++;
                    if (y <= 108 && x >= plateLeft && x <= plateRight)
                    {
                        smokeContactRowHistogram[x]++;
                        contactColumns[x - plateLeft] = true;
                    }
                }
                if (y <= 108)
                {
                    contactGas++;
                    if (cell.VelocityX < -0.01f) lateralLeft++;
                    if (cell.VelocityX > 0.01f) lateralRight++;
                    if (hasGasMotion)
                    {
                        GasMotionState motion = gasMotion[y * snapshot.Width + x];
                        double absOffsetX = Math.Abs(motion.OffsetX);
                        double absOffsetY = Math.Abs(motion.OffsetY);
                        double absVelocityX = Math.Abs(motion.VelocityX);
                        double absVelocityY = Math.Abs(motion.VelocityY);
                        contactMotionCells++;
                        sumAbsOffsetX += absOffsetX;
                        sumAbsOffsetY += absOffsetY;
                        sumAbsVelocityX += absVelocityX;
                        sumAbsVelocityY += absVelocityY;
                        if (x < plateCentre)
                        {
                            signedVelocityXLeftCells++;
                            sumSignedVelocityXLeft += motion.VelocityX;
                        }
                        else if (x > plateCentre)
                        {
                            signedVelocityXRightCells++;
                            sumSignedVelocityXRight += motion.VelocityX;
                        }
                        maximumAbsOffsetX = Math.Max(maximumAbsOffsetX, absOffsetX);
                        maximumAbsOffsetY = Math.Max(maximumAbsOffsetY, absOffsetY);
                        maximumAbsVelocityX = Math.Max(maximumAbsVelocityX, absVelocityX);
                        maximumAbsVelocityY = Math.Max(maximumAbsVelocityY, absVelocityY);
                        if (absOffsetX >= 7.999)
                        {
                            offsetXAtClamp++;
                            if (absVelocityX > 0.0001)
                            {
                                offsetXAtClampWithVelocity++;
                                sumAbsVelocityXAtClamp += absVelocityX;
                            }
                        }
                        if (absOffsetY >= 7.999)
                        {
                            offsetYAtClamp++;
                            if (absVelocityY > 0.0001)
                            {
                                offsetYAtClampWithVelocity++;
                                sumAbsVelocityYAtClamp += absVelocityY;
                            }
                        }
                        if (IsFullySurroundedByGasOrSolid(grid, registry, snapshot.Width, snapshot.Height, x, y))
                        {
                            fullySurrounded++;
                        }
                    }
                }
                GridCell above = grid[(y - 1) * snapshot.Width + x];
                if (above.IsActive != 0 && above.MaterialIndex == metal)
                {
                    blockedGas++;
                }
            }
        }

        int peakCount = fireHistogram.Max();
        double halfMaximum = peakCount * 0.5;
        int peakX = FindCentralPeak(fireHistogram, peakCount, plateCentre);
        (int halfLeft, int halfRight) = FindHalfMaximumSpan(fireHistogram, peakX, halfMaximum);
        double halfWidth = halfLeft >= 0 && halfRight >= halfLeft
            ? (halfRight - halfLeft + 1) * 0.5
            : 0;
        int fireSmokePeakCount = fireSmokeHistogram.Max();
        double fireSmokeHalfMaximum = fireSmokePeakCount * 0.5;
        int fireSmokePeakX = FindCentralPeak(fireSmokeHistogram, fireSmokePeakCount, plateCentre);
        (int fireSmokeHalfLeft, int fireSmokeHalfRight) =
            FindHalfMaximumSpan(fireSmokeHistogram, fireSmokePeakX, fireSmokeHalfMaximum);
        double fireSmokeHalfWidth = fireSmokeHalfLeft >= 0 && fireSmokeHalfRight >= fireSmokeHalfLeft
            ? (fireSmokeHalfRight - fireSmokeHalfLeft + 1) * 0.5
            : 0;
        // Deprecated: symmetryMass compressed two strongly time-dependent
        // lobes to one ratio and had sigma > 0.2 even on seven fixed seeds.
        // The signed, mass-weighted centre is directly interpretable: zero is
        // centred on the plate axis and the unit is a simulation cell.
        double fireCentreMassOffset = fireSurfaceMass > 0
            ? sumFireSurfaceMassX / fireSurfaceMass - plateCentre
            : 0;
        string histogram = FormatHistogram(fireHistogram);
        string pressureProfile = FormatAirPressureProfile(snapshot, surfaceTop, plateLeft, plateRight);
        int supportWidth = maximumX >= minimumX ? maximumX - minimumX + 1 : 0;
        double motionDenominator = Math.Max(1, contactMotionCells);
        double offsetXClampFraction = offsetXAtClamp / motionDenominator;
        double offsetYClampFraction = offsetYAtClamp / motionDenominator;
        double offsetXClampMovingFraction = offsetXAtClampWithVelocity / motionDenominator;
        double offsetYClampMovingFraction = offsetYAtClampWithVelocity / motionDenominator;
        double movingXClampDenominator = Math.Max(1, offsetXAtClampWithVelocity);
        double movingYClampDenominator = Math.Max(1, offsetYAtClampWithVelocity);
        double signedVelocityXLeft = sumSignedVelocityXLeft / Math.Max(1, signedVelocityXLeftCells);
        double signedVelocityXRight = sumSignedVelocityXRight / Math.Max(1, signedVelocityXRightCells);
        double fullySurroundedFraction = fullySurrounded / motionDenominator;
        double fireLifetimeMeanFrames = fireLifetimeFrames.Count > 0
            ? fireLifetimeFrames.Average()
            : 0;
        double fireLifetimeMedianFrames = Median(fireLifetimeFrames);
        double fireTemperatureMean = fireLifetimeFrames.Count > 0
            ? sumFireTemperature / fireLifetimeFrames.Count
            : 0;
        double fireTemperatureMinimum = double.IsPositiveInfinity(minimumFireTemperature)
            ? 0
            : minimumFireTemperature;
        double fireBelowSmokeTransitionFraction = fireBelowSmokeTransition /
            (double)Math.Max(1, fireLifetimeFrames.Count);
        int contactWidth = contactColumns.Count(occupied => occupied);
        double contactWidthFraction = contactWidth / (double)contactColumns.Length;
        int fireContactWidth = fireContactColumns.Count(occupied => occupied);
        double fireContactWidthFraction = fireContactWidth / (double)fireContactColumns.Length;
        double fireRangeCentreOffset = fireGlobalMaximumX >= fireGlobalMinimumX
            ? (fireGlobalMinimumX + fireGlobalMaximumX) * 0.5 - plateCentre
            : 0;
        int reportedContactFireMinimumX = contactFireMaximumX >= contactFireMinimumX
            ? contactFireMinimumX
            : -1;
        int reportedContactFireMaximumX = contactFireMaximumX >= contactFireMinimumX
            ? contactFireMaximumX
            : -1;
        WriteFireObstacleStateDump(snapshot, registry, artifactDirectory, fire, smoke);
        bool image = File.Exists(Path.Combine(artifactDirectory, "Y_fire_obstacle.png"));
        // This remains only a liveness gate for the diagnostic scenario.
        // jetHalfWidth is a projection of the vertical plume, not a measure
        // of surface spreading; contactWidth is the spreading measurement.
        bool passed = leftCells >= 8 && rightCells >= 8 && supportWidth >= 70;
        report = $"PHYXEL_FIRE_OBSTACLE fireCellsLeft={leftCells} fireCellsRight={rightCells} " +
            $"fireCellsCentre={centreCells} symmetryMetric=deprecated " +
            $"fireSurfaceMass={fireSurfaceMass:0.000} fireCentreMassOffset={fireCentreMassOffset:0.000} " +
            $"fireRangeCentreOffset={fireRangeCentreOffset:0.000} " +
            $"minX={minimumX} maxX={maximumX} minY={minimumY} contactGas={contactGas} " +
            $"blockedGas={blockedGas} lateralLeft={lateralLeft} lateralRight={lateralRight} " +
            $"contactFire={contactFire} contactSmoke={contactSmoke} " +
            $"smokeAboveLeft={smokeAboveLeft} smokeAboveRight={smokeAboveRight} " +
            $"peakCount={peakCount} peakX={peakX} jetHalfMaximum={halfMaximum:0.0} " +
            $"jetHalfMaximumLeft={halfLeft} jetHalfMaximumRight={halfRight} jetHalfWidth={halfWidth:0.0} " +
            $"fireSmokePeakCount={fireSmokePeakCount} fireSmokePeakX={fireSmokePeakX} " +
            $"fireSmokeJetHalfMaximum={fireSmokeHalfMaximum:0.0} fireSmokeJetHalfMaximumLeft={fireSmokeHalfLeft} " +
            $"fireSmokeJetHalfMaximumRight={fireSmokeHalfRight} fireSmokeJetHalfWidth={fireSmokeHalfWidth:0.0} " +
            $"plateLeft={plateLeft} plateRight={plateRight} plateWidth={contactColumns.Length} " +
            $"contactWidth={contactWidth} contactWidthFraction={contactWidthFraction:0.000000} " +
            $"fireContactWidth={fireContactWidth} fireContactWidthFraction={fireContactWidthFraction:0.000000} " +
            $"contactFireMinX={reportedContactFireMinimumX} contactFireMaxX={reportedContactFireMaximumX} " +
            $"fireReachesLeftEnd={(fireReachesLeftEnd ? 1 : 0)} fireReachesRightEnd={(fireReachesRightEnd ? 1 : 0)} " +
            $"fireLifetimeMeanFrames={fireLifetimeMeanFrames:0.000} fireLifetimeMedianFrames={fireLifetimeMedianFrames:0.000} " +
            $"fireTemperatureMean={fireTemperatureMean:0.000} fireTemperatureMinimum={fireTemperatureMinimum:0.000} " +
            $"fireBelowSmokeTransitionFraction={fireBelowSmokeTransitionFraction:0.000000} " +
            $"motionCells={contactMotionCells} offsetXClampFraction={offsetXClampFraction:0.000000} " +
            $"offsetXClampMovingFraction={offsetXClampMovingFraction:0.000000} " +
            $"meanAbsVelocityXAtClamp={sumAbsVelocityXAtClamp / movingXClampDenominator:0.000000} " +
            $"meanAbsOffsetX={sumAbsOffsetX / motionDenominator:0.000000} maxAbsOffsetX={maximumAbsOffsetX:0.000000} " +
            $"meanAbsVelocityX={sumAbsVelocityX / motionDenominator:0.000000} maxAbsVelocityX={maximumAbsVelocityX:0.000000} " +
            $"signedVelocityXLeft={signedVelocityXLeft:0.000000} signedVelocityXRight={signedVelocityXRight:0.000000} " +
            $"offsetYClampFraction={offsetYClampFraction:0.000000} offsetYClampMovingFraction={offsetYClampMovingFraction:0.000000} " +
            $"meanAbsVelocityYAtClamp={sumAbsVelocityYAtClamp / movingYClampDenominator:0.000000} " +
            $"meanAbsOffsetY={sumAbsOffsetY / motionDenominator:0.000000} " +
            $"maxAbsOffsetY={maximumAbsOffsetY:0.000000} meanAbsVelocityY={sumAbsVelocityY / motionDenominator:0.000000} " +
            $"maxAbsVelocityY={maximumAbsVelocityY:0.000000} fullySurroundedFraction={fullySurroundedFraction:0.000000} " +
            $"supportWidth={supportWidth} fireHistogram={histogram} airPressure={pressureProfile} globalMinX={globalMinimumX} " +
            $"globalMaxX={globalMaximumX} globalSmoke={globalSmoke} " +
            $"globalFire={globalFire} smokeMinY={smokeMinimumY} smokeMaxY={smokeMaximumY} " +
            $"contactFireProfile={FormatHistogramRange(fireContactRowHistogram, plateLeft, plateRight)} " +
            $"contactSmokeProfile={FormatHistogramRange(smokeContactRowHistogram, plateLeft, plateRight)} image={image}";
        return passed;
    }

    private static bool IsFullySurroundedByGasOrSolid(
        ReadOnlySpan<GridCell> grid,
        MaterialRegistry registry,
        int width,
        int height,
        int x,
        int y)
    {
        if (x == 0 || y == 0 || x + 1 >= width || y + 1 >= height)
        {
            return false;
        }

        return IsGasOrSolid(grid[(y - 1) * width + x], registry) &&
            IsGasOrSolid(grid[(y + 1) * width + x], registry) &&
            IsGasOrSolid(grid[y * width + x - 1], registry) &&
            IsGasOrSolid(grid[y * width + x + 1], registry);
    }

    private static bool IsGasOrSolid(GridCell cell, MaterialRegistry registry)
    {
        if (cell.IsActive == 0)
        {
            return false;
        }

        MaterialSimulationKind kind = (MaterialSimulationKind)registry[cell.MaterialIndex].Properties.SimulationKind;
        return kind is MaterialSimulationKind.Gas or MaterialSimulationKind.Solid;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) * 0.5
            : values[middle];
    }

    private static string FormatHistogramRange(int[] histogram, int left, int right)
    {
        return string.Join(';', Enumerable.Range(left, right - left + 1)
            .Select(x => $"{x}:{histogram[x]}"));
    }

    private static int FindCentralPeak(int[] histogram, int peakCount, int centreX)
    {
        int peakX = centreX;
        int nearestDistance = int.MaxValue;
        for (int x = 0; x < histogram.Length; x++)
        {
            if (histogram[x] != peakCount)
            {
                continue;
            }
            int distance = Math.Abs(x - centreX);
            if (distance < nearestDistance)
            {
                peakX = x;
                nearestDistance = distance;
            }
        }
        return peakX;
    }

    private static (int Left, int Right) FindHalfMaximumSpan(
        int[] histogram,
        int peakX,
        double halfMaximum)
    {
        if (histogram.Length == 0 || peakX < 0 || peakX >= histogram.Length || halfMaximum <= 0)
        {
            return (-1, -1);
        }

        int left = peakX;
        while (left > 0 && histogram[left - 1] >= halfMaximum)
        {
            left--;
        }
        int right = peakX;
        while (right + 1 < histogram.Length && histogram[right + 1] >= halfMaximum)
        {
            right++;
        }
        return (left, right);
    }

    private static string FormatHistogram(int[] histogram)
    {
        const int firstX = 20;
        const int lastX = 459;
        int start = Math.Min(firstX, histogram.Length - 1);
        int end = Math.Min(lastX, histogram.Length - 1);
        return $"x{start}-{end}:" + string.Join(',', histogram[start..(end + 1)]);
    }

    private static string FormatAirPressureProfile(
        SimulationWorldSnapshot snapshot,
        int fineY,
        int fineLeft,
        int fineRight)
    {
        if (snapshot.Air is null || snapshot.Air.Length == 0)
        {
            return "unavailable";
        }

        ReadOnlySpan<AirCell> air = MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        int airWidth = Math.Max(1, (snapshot.Width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        int airHeight = Math.Max(1, (snapshot.Height + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        if (air.Length != airWidth * airHeight)
        {
            return "invalid";
        }

        int airY = Math.Clamp(fineY / SimulationSettings.AirCellSize, 0, airHeight - 1);
        int airLeft = Math.Clamp(fineLeft / SimulationSettings.AirCellSize, 0, airWidth - 1);
        int airRight = Math.Clamp(fineRight / SimulationSettings.AirCellSize, 0, airWidth - 1);
        string[] samples = new string[airRight - airLeft + 1];
        for (int airX = airLeft; airX <= airRight; airX++)
        {
            float pressure = air[airY * airWidth + airX].Pressure;
            samples[airX - airLeft] = $"{airX}:{pressure:0.000}";
        }
        return $"y{airY};" + string.Join(';', samples);
    }

    private static void WriteFireObstacleStateDump(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        string artifactDirectory,
        uint fire,
        uint smoke)
    {
        Directory.CreateDirectory(artifactDirectory);
        WriteAirFieldDump(snapshot, artifactDirectory, "air-pressure.txt", cell => cell.Pressure);
        WriteAirFieldDump(snapshot, artifactDirectory, "air-velocity-x.txt", cell => cell.VelocityX);
        WriteAirFieldDump(snapshot, artifactDirectory, "air-velocity-y.txt", cell => cell.VelocityY);

        const int left = 100;
        const int right = 380;
        const int top = 90;
        const int bottom = 200;
        int firstX = Math.Clamp(left, 0, snapshot.Width - 1);
        int lastX = Math.Clamp(right, 0, snapshot.Width - 1);
        int firstY = Math.Clamp(top, 0, snapshot.Height - 1);
        int lastY = Math.Clamp(bottom, 0, snapshot.Height - 1);
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        StringBuilder map = new();
        map.AppendLine($"# x={firstX}..{lastX}; y={firstY}..{lastY}; .=empty F=fire S=smoke #=solid");
        for (int y = firstY; y <= lastY; y++)
        {
            for (int x = firstX; x <= lastX; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                char glyph = '.';
                if (cell.IsActive != 0)
                {
                    if (cell.MaterialIndex == fire)
                    {
                        glyph = 'F';
                    }
                    else if (cell.MaterialIndex == smoke)
                    {
                        glyph = 'S';
                    }
                    else if ((MaterialSimulationKind)registry[cell.MaterialIndex].Properties.SimulationKind ==
                        MaterialSimulationKind.Solid)
                    {
                        glyph = '#';
                    }
                }
                map.Append(glyph);
            }
            map.AppendLine();
        }
        File.WriteAllText(Path.Combine(artifactDirectory, "fire-obstacle-ascii.txt"), map.ToString());
    }

    private static void WriteFireOpenStateDump(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        string artifactDirectory,
        uint fire,
        uint smoke)
    {
        Directory.CreateDirectory(artifactDirectory);
        WriteAirFieldDump(snapshot, artifactDirectory, "air-pressure.txt", cell => cell.Pressure);
        WriteAirFieldDump(snapshot, artifactDirectory, "air-velocity-x.txt", cell => cell.VelocityX);
        WriteAirFieldDump(snapshot, artifactDirectory, "air-velocity-y.txt", cell => cell.VelocityY);

        const int left = 100;
        const int right = 380;
        const int top = 30;
        const int bottom = 200;
        int firstX = Math.Clamp(left, 0, snapshot.Width - 1);
        int lastX = Math.Clamp(right, 0, snapshot.Width - 1);
        int firstY = Math.Clamp(top, 0, snapshot.Height - 1);
        int lastY = Math.Clamp(bottom, 0, snapshot.Height - 1);
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        StringBuilder map = new();
        map.AppendLine($"# x={firstX}..{lastX}; y={firstY}..{lastY}; .=empty F=fire S=smoke #=solid");
        for (int y = firstY; y <= lastY; y++)
        {
            for (int x = firstX; x <= lastX; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                char glyph = '.';
                if (cell.IsActive != 0)
                {
                    if (cell.MaterialIndex == fire)
                    {
                        glyph = 'F';
                    }
                    else if (cell.MaterialIndex == smoke)
                    {
                        glyph = 'S';
                    }
                    else if ((MaterialSimulationKind)registry[cell.MaterialIndex].Properties.SimulationKind ==
                        MaterialSimulationKind.Solid)
                    {
                        glyph = '#';
                    }
                }
                map.Append(glyph);
            }
            map.AppendLine();
        }
        File.WriteAllText(Path.Combine(artifactDirectory, "fire-open-ascii.txt"), map.ToString());
    }

    private static void WriteAirFieldDump(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        string fileName,
        Func<AirCell, float> value)
    {
        if (snapshot.Air is null || snapshot.Air.Length == 0)
        {
            File.WriteAllText(Path.Combine(artifactDirectory, fileName), "# unavailable\n");
            return;
        }

        ReadOnlySpan<AirCell> air = MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        int airWidth = Math.Max(1, (snapshot.Width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        int airHeight = Math.Max(1, (snapshot.Height + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        if (air.Length != airWidth * airHeight)
        {
            File.WriteAllText(Path.Combine(artifactDirectory, fileName), "# invalid\n");
            return;
        }

        StringBuilder table = new();
        table.AppendLine($"# width={airWidth}; height={airHeight}; value per coarse air cell");
        for (int y = 0; y < airHeight; y++)
        {
            for (int x = 0; x < airWidth; x++)
            {
                if (x > 0)
                {
                    table.Append(' ');
                }
                table.Append(value(air[y * airWidth + x]).ToString("0.000000", CultureInfo.InvariantCulture));
            }
            table.AppendLine();
        }
        File.WriteAllText(Path.Combine(artifactDirectory, fileName), table.ToString());
    }

    private readonly record struct SteamPuffMetrics(
        int SteamCells,
        double TotalMass,
        double MinimumCellMass,
        double MaximumCellMass,
        double CentreOffsetX,
        double CentreOffsetY,
        int Width,
        int Height,
        double AspectRatio,
        double MeanAbsVelocityX,
        double MeanAbsVelocityY,
        double MeanTemperature,
        double MeanVelocityXLeft,
        double MeanVelocityYLeft,
        double MeanVelocityXRight,
        double MeanVelocityYRight,
        int LeftVelocityCells,
        int RightVelocityCells,
        double SigmaX,
        double SigmaY,
        double AspectSigma,
        double OffsetXClampFraction,
        double OffsetYClampFraction,
        int MinimumY,
        int MaximumY,
        bool ClippedTop);

    private readonly record struct SteamJetProfileSummary(
        int UpperEdgeY,
        int CandidateCapTransitionY,
        double CandidateSigmaXCurvature,
        int OccupiedBands);

    private readonly record struct SteamJetFrontRiseMetrics(
        double Rate,
        string UsedFrames,
        bool InsufficientSamples);

    private static bool ValidateSteamPuff(
        SimulationWorldSnapshot finalSnapshot,
        MaterialRegistry registry,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        string artifactDirectory,
        out string report,
        bool steamObstacle = false)
    {
        uint steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        const int sourceX = AcceptanceRegressionScenario.SteamPuffSourceX;
        const int sourceY = AcceptanceRegressionScenario.SteamPuffSourceY;
        int[] requestedFrames = [1, 30, 60, 90, 120, 150, 300];

        StringBuilder fields = new();
        bool hasInitialSample = checkpoints.Count > 0;
        for (int sample = 0; sample < requestedFrames.Length; sample++)
        {
            int targetFrame = requestedFrames[sample];
            if (sample >= checkpoints.Count)
            {
                fields.Append($" checkpoint{targetFrame}Missing=1");
                continue;
            }

            ThermalAcceptanceCheckpoint checkpoint = checkpoints[sample];
            SteamPuffMetrics metrics = MeasureSteamPuff(
                checkpoint.Snapshot,
                steam,
                sourceX,
                sourceY);
            AppendSteamPuffMetrics(fields, targetFrame, checkpoint.Frame, metrics);
            AppendSteamPuffMotionDistribution(fields, checkpoint.Snapshot, steam, targetFrame);
            AppendSteamPuffAirProfile(fields, checkpoint.Snapshot, metrics, targetFrame);
        }

        SteamPuffMetrics finalMetrics = MeasureSteamPuff(
            finalSnapshot,
            steam,
            sourceX,
            sourceY);
        AppendSteamPuffMetrics(fields, 600, 600, finalMetrics);
        AppendSteamPuffMotionDistribution(fields, finalSnapshot, steam, 600);
        AppendSteamPuffAirProfile(fields, finalSnapshot, finalMetrics, 600);
        double riseRate = CalculateSteamPuffRiseRate(checkpoints, steam, sourceX, sourceY);
        fields.Append($" riseRate={riseRate:0.000000}");
        WriteSteamStateDump(finalSnapshot, registry, artifactDirectory, steam, "steam-puff-ascii.txt", false);
        bool image = File.Exists(Path.Combine(artifactDirectory, "AA_steam_puff.png"));
        report = $"PHYXEL_STEAM_PUFF checkpoints={checkpoints.Count} image={image}{fields}";
        return hasInitialSample && finalMetrics.SteamCells > 0;
    }

    private static bool ValidateSteamJet(
        SimulationWorldSnapshot finalSnapshot,
        MaterialRegistry registry,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        string artifactDirectory,
        out string report,
        bool steamObstacle = false)
    {
        uint steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        int sourceX = AcceptanceRegressionScenario.GetSteamJetSourceX();
        int sourceY = AcceptanceRegressionScenario.GetSteamJetSourceY();
        int[] requestedFrames = steamObstacle ? [300, 600] :
            Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_COLLAPSE_TRACE") == "1"
                ? [60, 90, 120, 150, 200, 250, 300, 450]
                : [60, 90, 120, 150, 200, 250, 300];
        int finalFrame = steamObstacle ? 1200 : 600;
        StringBuilder fields = new();
        bool hasCheckpoints = checkpoints.Count >= requestedFrames.Length;

        for (int sample = 0; sample < requestedFrames.Length; sample++)
        {
            int targetFrame = requestedFrames[sample];
            if (sample >= checkpoints.Count)
            {
                fields.Append($" checkpoint{targetFrame}Missing=1");
                continue;
            }

            ThermalAcceptanceCheckpoint checkpoint = checkpoints[sample];
            SteamPuffMetrics metrics = MeasureSteamPuff(
                checkpoint.Snapshot,
                steam,
                sourceX,
                sourceY);
            AppendSteamPuffMetrics(fields, targetFrame, checkpoint.Frame, metrics);
            SteamJetProfileSummary profile = WriteSteamJetProfile(
                checkpoint.Snapshot,
                steam,
                artifactDirectory,
                targetFrame,
                sourceY);
            AppendSteamJetProfileSummary(fields, targetFrame, profile);
            if (steamObstacle) AppendSteamObstacleMetrics(fields, checkpoint.Snapshot, registry, targetFrame);
            WriteSteamStateDump(
                checkpoint.Snapshot,
                registry,
                artifactDirectory,
                steam,
                $"steam-jet-ascii-{targetFrame}.txt",
                true);
        }

        SteamPuffMetrics finalMetrics = MeasureSteamPuff(finalSnapshot, steam, sourceX, sourceY);
        AppendSteamPuffMetrics(fields, finalFrame, (uint)finalFrame, finalMetrics);
        SteamJetProfileSummary finalProfile = WriteSteamJetProfile(
            finalSnapshot,
            steam,
            artifactDirectory,
            finalFrame,
            sourceY);
        AppendSteamJetProfileSummary(fields, finalFrame, finalProfile);
        if (steamObstacle) AppendSteamObstacleMetrics(fields, finalSnapshot, registry, finalFrame);
        WriteSteamStateDump(
            finalSnapshot,
            registry,
            artifactDirectory,
            steam,
            $"steam-jet-ascii-{finalFrame}.txt",
            true);
        SteamJetFrontRiseMetrics frontRise = CalculateSteamJetFrontRiseRate(
            checkpoints,
            finalSnapshot,
            steam,
            sourceX,
            sourceY);
        fields.Append($" steamJetFrontRiseRate={frontRise.Rate:0.000000}");
        fields.Append($" steamJetFrontRiseFrames={frontRise.UsedFrames}");
        fields.Append($" steamJetFrontRiseInsufficient={(frontRise.InsufficientSamples ? 1 : 0)}");
        string imageStem = steamObstacle ? "AA_steam_obstacle" : "AA_steam_jet";
        bool images = requestedFrames.All(frame => File.Exists(Path.Combine(artifactDirectory, $"{imageStem}_{frame}.png"))) &&
            File.Exists(Path.Combine(artifactDirectory, $"{imageStem}_{finalFrame}.png"));
        report = $"PHYXEL_STEAM_JET checkpoints={checkpoints.Count} images={images}{fields}";
        return hasCheckpoints && finalMetrics.SteamCells > 0;
    }

    private static void AppendSteamObstacleMetrics(StringBuilder fields, SimulationWorldSnapshot snapshot, MaterialRegistry registry, int frame)
    {
        const int left = 241, right = 371, top = 225, bottom = 231, axis = 306;
        uint steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        ReadOnlySpan<GridCell> cells = Cells(snapshot);
        int steamCount = 0, waterCount = 0, above = 0, min = int.MaxValue, max = -1, midN = 0, edgeN = 0;
        int outsideLeft = 0, outsideRight = 0, plateContactCount = 0;
        SteamObstacleLayerMetrics contactLayer = new(bottom + 1, bottom + 6);
        SteamObstacleLayerMetrics nearLayer = new(bottom + 7, bottom + 20);
        SteamObstacleLayerMetrics lowerLayer = new(bottom + 21, bottom + 40);
        double steamX = 0, waterY = 0, midT = 0, edgeT = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            GridCell cell = cells[i]; if (cell.IsActive == 0) continue;
            int x = i % snapshot.Width, y = i / snapshot.Width;
            if (cell.MaterialIndex == steam)
            {
                steamCount++;
                steamX += x;
                if (y < top) above++;
                if (x < left) outsideLeft++;
                if (x > right) outsideRight++;
                if (y > bottom && y <= bottom + 6)
                {
                    min = Math.Min(min, x);
                    max = Math.Max(max, x);
                    if (x >= left && x <= right) plateContactCount++;
                }
                contactLayer.Add(x, y);
                nearLayer.Add(x, y);
                lowerLayer.Add(x, y);
            }
            else if (cell.MaterialIndex == water) { waterCount++; waterY += y; }
            else if (cell.MaterialIndex == metal && x >= left && x <= right && y >= top && y <= bottom)
            { if (x >= left + 44 && x <= right - 44) { midT += cell.Temperature; midN++; } else if (x < left + 22 || x > right - 22) { edgeT += cell.Temperature; edgeN++; } }
        }
        int coverage = max < min ? 0 : Math.Max(0, Math.Min(right, max) - Math.Max(left, min) + 1);
        // The historical coverage metric is only a clipped min..max range. A
        // single outlier can make it look full, so retain it for continuity
        // but flag it as unsuitable for assessing steam spreading.
        fields.Append($" obstacleSteamCells{frame}={steamCount} obstacleCoverage{frame}={coverage / 131.0:0.000000} obstacleCoverageRangeOnly{frame}=1 obstacleCoverageUnsuitable{frame}=1 obstacleSteamAboveFraction{frame}={above / (double)Math.Max(1, steamCount):0.000000} obstacleWaterCells{frame}={waterCount} obstacleWaterCentreY{frame}={waterY / Math.Max(1, waterCount):0.000000} obstacleSteamCentreOffsetX{frame}={steamX / Math.Max(1, steamCount) - axis:0.000000} obstaclePlateMiddleTemperature{frame}={midT / Math.Max(1, midN):0.000000} obstaclePlateEdgeTemperature{frame}={edgeT / Math.Max(1, edgeN):0.000000}");
        AppendSteamObstacleLayerMetrics(fields, frame, "0to6", contactLayer);
        AppendSteamObstacleLayerMetrics(fields, frame, "6to20", nearLayer);
        AppendSteamObstacleLayerMetrics(fields, frame, "20to40", lowerLayer);
        // Fixed geometry makes these comparable across runs. The historical
        // occupancy denominator follows min/max outliers and can change even
        // when the contact particle count is effectively identical.
        fields.Append($" obstaclePlateContactCount{frame}={plateContactCount}" +
            $" obstaclePlateContactOccupancy{frame}={plateContactCount / (131.0 * 6):0.000000}" +
            $" obstacleWorldContactOccupancy{frame}={contactLayer.Count / (snapshot.Width * 6.0):0.000000}");
        fields.Append($" obstacleContactLayerWidth{frame}={contactLayer.Width} obstacleSteamOutsideLeftFraction{frame}={outsideLeft / (double)Math.Max(1, steamCount):0.000000} obstacleSteamOutsideRightFraction{frame}={outsideRight / (double)Math.Max(1, steamCount):0.000000}");
    }

    private static void AppendSteamObstacleLayerMetrics(
        StringBuilder fields,
        int frame,
        string name,
        SteamObstacleLayerMetrics layer)
    {
        fields.Append($" obstacleLayer{name}Width{frame}={layer.Width} obstacleLayer{name}Occupancy{frame}={layer.Occupancy:0.000000} obstacleLayer{name}SteamCells{frame}={layer.Count}");
    }

    private sealed class SteamObstacleLayerMetrics
    {
        private readonly int minimumY;
        private readonly int maximumY;
        private int minimumX = int.MaxValue;
        private int maximumX = -1;

        public SteamObstacleLayerMetrics(int minimumY, int maximumY)
        {
            this.minimumY = minimumY;
            this.maximumY = maximumY;
        }

        public int Count { get; private set; }
        public int Height => maximumY - minimumY + 1;
        public int Width => maximumX < minimumX ? 0 : maximumX - minimumX + 1;
        public double Occupancy => Count / (double)Math.Max(1, Width * Height);

        public void Add(int x, int y)
        {
            if (y < minimumY || y > maximumY)
            {
                return;
            }
            Count++;
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
        }
    }

    private static void AppendSteamJetProfileSummary(
        StringBuilder fields,
        int frame,
        SteamJetProfileSummary profile)
    {
        fields.Append($" steamJetUpperEdgeY{frame}={profile.UpperEdgeY}");
        fields.Append($" steamJetOccupiedBands{frame}={profile.OccupiedBands}");
        fields.Append($" steamJetCandidateCapTransitionY{frame}={profile.CandidateCapTransitionY}");
        fields.Append($" steamJetCandidateSigmaXCurvature{frame}={profile.CandidateSigmaXCurvature:0.000000}");
    }

    private static SteamJetProfileSummary WriteSteamJetProfile(
        SimulationWorldSnapshot snapshot,
        uint steam,
        string artifactDirectory,
        int frame,
        int sourceY)
    {
        const int bandHeight = 20;
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int minimumY = snapshot.Height;
        int maximumY = -1;
        for (int index = 0; index < grid.Length; index++)
        {
            if (grid[index].IsActive == 0 || grid[index].MaterialIndex != steam)
            {
                continue;
            }
            int y = index / snapshot.Width;
            minimumY = Math.Min(minimumY, y);
            maximumY = Math.Max(maximumY, y);
        }

        string path = Path.Combine(artifactDirectory, $"steam-jet-profile-{frame}.csv");
        Directory.CreateDirectory(artifactDirectory);
        if (maximumY < minimumY)
        {
            File.WriteAllText(path, "heightAboveSourceStart,heightAboveSourceEnd,worldYTop,worldYBottom,steamCells,sigmaX\n");
            return new SteamJetProfileSummary(-1, -1, 0, 0);
        }

        // The TPT reference is measured in 20-cell strips from the source upward.
        // Retaining both relative and world coordinates makes the dump directly
        // comparable without hiding a possible collision with a world boundary.
        int firstDistance = (sourceY - maximumY) / bandHeight * bandHeight;
        int lastDistance = (sourceY - minimumY) / bandHeight * bandHeight;
        int bandCount = (lastDistance - firstDistance) / bandHeight + 1;
        int[] cells = new int[bandCount];
        double[] mass = new double[bandCount];
        double[] sumX = new double[bandCount];
        double[] sumXSquare = new double[bandCount];
        double[] sumOffsetX = new double[bandCount];
        double[] sumOffsetXSquare = new double[bandCount];
        int[] offsetAboveHalf = new int[bandCount];
        int[] offsetAtStep = new int[bandCount];
        int[] neighborOccupancy = new int[bandCount];
        double[] sumVelocityX = new double[bandCount];
        double[] sumVelocityXSquare = new double[bandCount];
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? [] : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            if (cell.IsActive == 0 || cell.MaterialIndex != steam)
            {
                continue;
            }
            int x = index % snapshot.Width;
            int y = index / snapshot.Width;
            int heightAboveSource = sourceY - y;
            int band = (heightAboveSource / bandHeight * bandHeight - firstDistance) / bandHeight;
            cells[band]++;
            mass[band] += cell.Mass;
            sumX[band] += x * cell.Mass;
            sumXSquare[band] += x * x * cell.Mass;
            if (motion.Length == grid.Length)
            {
                GasMotionState state = motion[index];
                double offset = state.OffsetX;
                sumOffsetX[band] += offset;
                sumOffsetXSquare[band] += offset * offset;
                if (Math.Abs(offset) > 0.5) offsetAboveHalf[band]++;
                if (Math.Abs(offset) >= 1.0) offsetAtStep[band]++;
                sumVelocityX[band] += state.VelocityX;
                sumVelocityXSquare[band] += state.VelocityX * state.VelocityX;
            }
            foreach ((int dx, int dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < snapshot.Width && ny < snapshot.Height &&
                    grid[ny * snapshot.Width + nx].IsActive != 0) neighborOccupancy[band]++;
            }
        }

        List<(int HeightAboveSource, double SigmaX)> occupied = [];
        StringBuilder csv = new("heightAboveSourceStart,heightAboveSourceEnd,worldYTop,worldYBottom,steamCells,sigmaX,meanOffsetX,sigmaOffsetX,offsetXAboveHalfFraction,offsetXAtStepFraction,meanOccupiedNeighbors,sigmaVelocityX\n");
        for (int band = 0; band < bandCount; band++)
        {
            if (cells[band] == 0 || mass[band] <= 0)
            {
                continue;
            }
            double centreX = sumX[band] / mass[band];
            double variance = Math.Max(0, sumXSquare[band] / mass[band] - centreX * centreX);
            double sigmaX = Math.Sqrt(variance);
            double meanOffsetX = sumOffsetX[band] / cells[band];
            double sigmaOffsetX = Math.Sqrt(Math.Max(0, sumOffsetXSquare[band] / cells[band] - meanOffsetX * meanOffsetX));
            double meanVelocityX = sumVelocityX[band] / cells[band];
            double sigmaVelocityX = Math.Sqrt(Math.Max(0, sumVelocityXSquare[band] / cells[band] - meanVelocityX * meanVelocityX));
            int heightAboveSource = firstDistance + band * bandHeight;
            int worldYBottom = sourceY - heightAboveSource;
            int worldYTop = worldYBottom - bandHeight + 1;
            csv.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{heightAboveSource},{heightAboveSource + bandHeight - 1},{worldYTop},{worldYBottom},{cells[band]},{sigmaX:0.000000},{meanOffsetX:0.000000},{sigmaOffsetX:0.000000},{offsetAboveHalf[band] / (double)cells[band]:0.000000},{offsetAtStep[band] / (double)cells[band]:0.000000},{neighborOccupancy[band] / (double)cells[band]:0.000000},{sigmaVelocityX:0.000000}"));
            occupied.Add((heightAboveSource, sigmaX));
        }
        File.WriteAllText(path, csv.ToString());

        int transitionY = -1;
        double greatestCurvature = 0;
        for (int index = 1; index + 1 < occupied.Count; index++)
        {
            double curvature = Math.Abs(
                occupied[index + 1].SigmaX - 2 * occupied[index].SigmaX + occupied[index - 1].SigmaX);
            if (curvature > greatestCurvature)
            {
                greatestCurvature = curvature;
                transitionY = sourceY - occupied[index].HeightAboveSource;
            }
        }
        return new SteamJetProfileSummary(minimumY, transitionY, greatestCurvature, occupied.Count);
    }

    private static SteamJetFrontRiseMetrics CalculateSteamJetFrontRiseRate(
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        SimulationWorldSnapshot finalSnapshot,
        uint steam,
        int sourceX,
        int sourceY)
    {
        List<(double Frame, double TopY)> samples = [];
        foreach (ThermalAcceptanceCheckpoint checkpoint in checkpoints)
        {
            if (checkpoint.Frame > 300)
            {
                continue;
            }
            SteamPuffMetrics metrics = MeasureSteamPuff(checkpoint.Snapshot, steam, sourceX, sourceY);
            if (!metrics.ClippedTop)
            {
                samples.Add((checkpoint.Frame, metrics.MinimumY));
            }
        }
        SteamPuffMetrics finalMetrics = MeasureSteamPuff(finalSnapshot, steam, sourceX, sourceY);
        if (!finalMetrics.ClippedTop)
        {
            samples.Add((600, finalMetrics.MinimumY));
        }
        string frames = samples.Count == 0
            ? "none"
            : string.Join(',', samples.Select(sample => ((int)sample.Frame).ToString(CultureInfo.InvariantCulture)));
        if (samples.Count < 3)
        {
            return new SteamJetFrontRiseMetrics(double.NaN, frames, true);
        }

        double count = samples.Count;
        double sumFrame = 0;
        double sumTop = 0;
        double sumFrameSquare = 0;
        double sumFrameTop = 0;
        foreach ((double frame, double topY) in samples)
        {
            sumFrame += frame;
            sumTop += topY;
            sumFrameSquare += frame * frame;
            sumFrameTop += frame * topY;
        }
        double denominator = count * sumFrameSquare - sumFrame * sumFrame;
        double rate = Math.Abs(denominator) < double.Epsilon
            ? double.NaN
            : -(count * sumFrameTop - sumFrame * sumTop) / denominator;
        return new SteamJetFrontRiseMetrics(rate, frames, false);
    }

    private static void AppendSteamPuffMetrics(
        StringBuilder fields,
        int targetFrame,
        uint capturedFrame,
        SteamPuffMetrics metrics)
    {
        string suffix = targetFrame.ToString(CultureInfo.InvariantCulture);
        fields.Append($" capturedFrame{suffix}={capturedFrame}");
        fields.Append($" steamCells{suffix}={metrics.SteamCells}");
        fields.Append($" steamMass{suffix}={metrics.TotalMass:0.000000}");
        fields.Append($" steamMinimumCellMass{suffix}={metrics.MinimumCellMass:0.000000}");
        fields.Append($" steamMaximumCellMass{suffix}={metrics.MaximumCellMass:0.000000}");
        fields.Append($" centreOffsetX{suffix}={metrics.CentreOffsetX:0.000000}");
        fields.Append($" centreOffsetY{suffix}={metrics.CentreOffsetY:0.000000}");
        fields.Append($" cloudWidth{suffix}={metrics.Width}");
        fields.Append($" cloudHeight{suffix}={metrics.Height}");
        fields.Append($" cloudAspect{suffix}={metrics.AspectRatio:0.000000}");
        fields.Append($" meanAbsVelocityX{suffix}={metrics.MeanAbsVelocityX:0.000000}");
        fields.Append($" meanAbsVelocityY{suffix}={metrics.MeanAbsVelocityY:0.000000}");
        fields.Append($" meanTemperature{suffix}={metrics.MeanTemperature:0.000000}");
        fields.Append($" meanVelocityXLeft{suffix}={metrics.MeanVelocityXLeft:0.000000}");
        fields.Append($" meanVelocityYLeft{suffix}={metrics.MeanVelocityYLeft:0.000000}");
        fields.Append($" meanVelocityXRight{suffix}={metrics.MeanVelocityXRight:0.000000}");
        fields.Append($" meanVelocityYRight{suffix}={metrics.MeanVelocityYRight:0.000000}");
        fields.Append($" leftVelocityCells{suffix}={metrics.LeftVelocityCells}");
        fields.Append($" rightVelocityCells{suffix}={metrics.RightVelocityCells}");
        fields.Append($" sigmaX{suffix}={metrics.SigmaX:0.000000}");
        fields.Append($" sigmaY{suffix}={metrics.SigmaY:0.000000}");
        fields.Append($" aspectSigma{suffix}={metrics.AspectSigma:0.000000}");
        fields.Append($" offsetXClampFraction{suffix}={metrics.OffsetXClampFraction:0.000000}");
        fields.Append($" offsetYClampFraction{suffix}={metrics.OffsetYClampFraction:0.000000}");
        fields.Append($" minimumY{suffix}={metrics.MinimumY}");
        fields.Append($" maximumY{suffix}={metrics.MaximumY}");
        fields.Append($" clippedTop{suffix}={(metrics.ClippedTop ? 1 : 0)}");
    }

    private static void AppendSteamPuffMotionDistribution(
        StringBuilder fields,
        SimulationWorldSnapshot snapshot,
        uint steam,
        int frame)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        if (motion.Length != grid.Length)
        {
            fields.Append($" steamMotion{frame}Missing=1");
            return;
        }

        int cells = 0;
        double velocityXSum = 0;
        double velocityXSquareSum = 0;
        int velocityXPositive = 0;
        double velocityYSum = 0;
        double velocityYSquareSum = 0;
        int velocityYPositive = 0;
        double offsetYSum = 0;
        double offsetYSquareSum = 0;
        int offsetYAboveHalf = 0;
        for (int index = 0; index < grid.Length; index++)
        {
            if (grid[index].IsActive == 0 || grid[index].MaterialIndex != steam)
            {
                continue;
            }
            GasMotionState state = motion[index];
            cells++;
            velocityXSum += state.VelocityX;
            velocityXSquareSum += state.VelocityX * state.VelocityX;
            velocityXPositive += state.VelocityX > 0 ? 1 : 0;
            velocityYSum += state.VelocityY;
            velocityYSquareSum += state.VelocityY * state.VelocityY;
            velocityYPositive += state.VelocityY > 0 ? 1 : 0;
            offsetYSum += state.OffsetY;
            offsetYSquareSum += state.OffsetY * state.OffsetY;
            offsetYAboveHalf += Math.Abs(state.OffsetY) > 0.5f ? 1 : 0;
        }

        double divisor = Math.Max(1, cells);
        double velocityXMean = velocityXSum / divisor;
        double velocityYMean = velocityYSum / divisor;
        double offsetYMean = offsetYSum / divisor;
        double velocityXSigma = Math.Sqrt(Math.Max(0, velocityXSquareSum / divisor - velocityXMean * velocityXMean));
        double velocityYSigma = Math.Sqrt(Math.Max(0, velocityYSquareSum / divisor - velocityYMean * velocityYMean));
        double offsetYSigma = Math.Sqrt(Math.Max(0, offsetYSquareSum / divisor - offsetYMean * offsetYMean));
        fields.Append($" meanVelocityX{frame}={velocityXMean:0.000000}");
        fields.Append($" sigmaVelocityX{frame}={velocityXSigma:0.000000}");
        fields.Append($" velocityXPositiveFraction{frame}={velocityXPositive / divisor:0.000000}");
        fields.Append($" meanVelocityY{frame}={velocityYMean:0.000000}");
        fields.Append($" sigmaVelocityY{frame}={velocityYSigma:0.000000}");
        fields.Append($" velocityYPositiveFraction{frame}={velocityYPositive / divisor:0.000000}");
        fields.Append($" meanOffsetY{frame}={offsetYMean:0.000000}");
        fields.Append($" sigmaOffsetY{frame}={offsetYSigma:0.000000}");
        fields.Append($" offsetYAboveHalfFraction{frame}={offsetYAboveHalf / divisor:0.000000}");
    }

    private static SteamPuffMetrics MeasureSteamPuff(
        SimulationWorldSnapshot snapshot,
        uint steam,
        int sourceX,
        int sourceY)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        bool hasMotion = motion.Length == grid.Length;
        int cells = 0;
        int minimumX = snapshot.Width;
        int maximumX = -1;
        int minimumY = snapshot.Height;
        int maximumY = -1;
        double sumX = 0;
        double sumY = 0;
        double totalMass = 0;
        double minimumCellMass = double.PositiveInfinity;
        double maximumCellMass = 0;
        double sumAbsVelocityX = 0;
        double sumAbsVelocityY = 0;
        double sumTemperature = 0;
        double sumVelocityXLeft = 0;
        double sumVelocityYLeft = 0;
        double sumVelocityXRight = 0;
        double sumVelocityYRight = 0;
        int leftVelocityCells = 0;
        int rightVelocityCells = 0;
        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            if (cell.IsActive == 0 || cell.MaterialIndex != steam)
            {
                continue;
            }

            int x = index % snapshot.Width;
            int y = index / snapshot.Width;
            cells++;
            totalMass += cell.Mass;
            sumX += x * cell.Mass;
            sumY += y * cell.Mass;
            minimumCellMass = Math.Min(minimumCellMass, cell.Mass);
            maximumCellMass = Math.Max(maximumCellMass, cell.Mass);
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumY = Math.Max(maximumY, y);
            sumTemperature += cell.Temperature;
            if (hasMotion)
            {
                sumAbsVelocityX += Math.Abs(motion[index].VelocityX);
                sumAbsVelocityY += Math.Abs(motion[index].VelocityY);
            }
        }

        if (cells == 0)
        {
            return new SteamPuffMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);
        }

        double centreX = totalMass > 0 ? sumX / totalMass : sourceX;
        double centreY = totalMass > 0 ? sumY / totalMass : sourceY;
        double varianceX = 0;
        double varianceY = 0;
        int offsetXClamped = 0;
        int offsetYClamped = 0;
        if (hasMotion)
        {
            for (int index = 0; index < grid.Length; index++)
            {
                GridCell cell = grid[index];
                if (cell.IsActive == 0 || cell.MaterialIndex != steam)
                {
                    continue;
                }
                GasMotionState velocity = motion[index];
                int x = index % snapshot.Width;
                int y = index / snapshot.Width;
                varianceX += cell.Mass * Math.Pow(x - centreX, 2);
                varianceY += cell.Mass * Math.Pow(y - centreY, 2);
                offsetXClamped += Math.Abs(velocity.OffsetX) >= 7.999f ? 1 : 0;
                offsetYClamped += Math.Abs(velocity.OffsetY) >= 7.999f ? 1 : 0;
                if (x < centreX)
                {
                    sumVelocityXLeft += velocity.VelocityX;
                    sumVelocityYLeft += velocity.VelocityY;
                    leftVelocityCells++;
                }
                else
                {
                    sumVelocityXRight += velocity.VelocityX;
                    sumVelocityYRight += velocity.VelocityY;
                    rightVelocityCells++;
                }
            }
        }

        if (!hasMotion)
        {
            for (int index = 0; index < grid.Length; index++)
            {
                GridCell cell = grid[index];
                if (cell.IsActive == 0 || cell.MaterialIndex != steam)
                {
                    continue;
                }
                int x = index % snapshot.Width;
                int y = index / snapshot.Width;
                varianceX += cell.Mass * Math.Pow(x - centreX, 2);
                varianceY += cell.Mass * Math.Pow(y - centreY, 2);
            }
        }

        double sigmaX = totalMass > 0 ? Math.Sqrt(varianceX / totalMass) : 0;
        double sigmaY = totalMass > 0 ? Math.Sqrt(varianceY / totalMass) : 0;

        int width = maximumX - minimumX + 1;
        int height = maximumY - minimumY + 1;
        return new SteamPuffMetrics(
            cells,
            totalMass,
            minimumCellMass,
            maximumCellMass,
            centreX - sourceX,
            totalMass > 0 ? sumY / totalMass - sourceY : 0,
            width,
            height,
            height > 0 ? width / (double)height : 0,
            sumAbsVelocityX / cells,
            sumAbsVelocityY / cells,
            sumTemperature / cells,
            leftVelocityCells > 0 ? sumVelocityXLeft / leftVelocityCells : 0,
            leftVelocityCells > 0 ? sumVelocityYLeft / leftVelocityCells : 0,
            rightVelocityCells > 0 ? sumVelocityXRight / rightVelocityCells : 0,
            rightVelocityCells > 0 ? sumVelocityYRight / rightVelocityCells : 0,
            leftVelocityCells,
            rightVelocityCells,
            sigmaX,
            sigmaY,
            sigmaY > 0 ? sigmaX / sigmaY : 0,
            offsetXClamped / (double)cells,
            offsetYClamped / (double)cells,
            minimumY,
            maximumY,
            minimumY <= 2);
    }

    private static double CalculateSteamPuffRiseRate(
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        uint steam,
        int sourceX,
        int sourceY)
    {
        const uint firstFrame = 60;
        const uint lastFrame = 150;
        double count = 0;
        double sumFrame = 0;
        double sumOffsetY = 0;
        double sumFrameSquared = 0;
        double sumFrameOffsetY = 0;
        foreach (ThermalAcceptanceCheckpoint checkpoint in checkpoints)
        {
            if (checkpoint.Frame < firstFrame || checkpoint.Frame > lastFrame)
            {
                continue;
            }

            SteamPuffMetrics metrics = MeasureSteamPuff(
                checkpoint.Snapshot,
                steam,
                sourceX,
                sourceY);
            double frame = checkpoint.Frame;
            count++;
            sumFrame += frame;
            sumOffsetY += metrics.CentreOffsetY;
            sumFrameSquared += frame * frame;
            sumFrameOffsetY += frame * metrics.CentreOffsetY;
        }

        double denominator = count * sumFrameSquared - sumFrame * sumFrame;
        if (count < 2 || Math.Abs(denominator) < double.Epsilon)
        {
            return double.NaN;
        }

        // Screen/world Y grows downward, so a negative centre-of-mass slope
        // is reported as a positive upward rate in simulation cells per frame.
        return -(count * sumFrameOffsetY - sumFrame * sumOffsetY) / denominator;
    }

    private static void AppendSteamPuffAirProfile(
        StringBuilder fields,
        SimulationWorldSnapshot snapshot,
        SteamPuffMetrics metrics,
        int frame)
    {
        ReadOnlySpan<AirCell> air = snapshot.Air is null ? [] : MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        int airWidth = Math.Max(1, (snapshot.Width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        int airHeight = Math.Max(1, (snapshot.Height + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        if (metrics.SteamCells == 0 || air.Length != airWidth * airHeight)
        {
            fields.Append($" airProfile{frame}=unavailable");
            return;
        }

        int centreFineX = (int)Math.Round(AcceptanceRegressionScenario.SteamPuffSourceX + metrics.CentreOffsetX);
        int centreFineY = (int)Math.Round(AcceptanceRegressionScenario.SteamPuffSourceY + metrics.CentreOffsetY);
        int centreAirX = Math.Clamp(centreFineX / SimulationSettings.AirCellSize, 0, airWidth - 1);
        int centreAirY = Math.Clamp(centreFineY / SimulationSettings.AirCellSize, 0, airHeight - 1);
        int aboveAirY = Math.Clamp((centreFineY - 10) / SimulationSettings.AirCellSize, 0, airHeight - 1);
        fields.Append($" centreAirVelocityY{frame}={air[centreAirY * airWidth + centreAirX].VelocityY:0.000000}");
        int[] distances = [-6, -3, -2, -1, 0, 1, 2, 3, 6];
        StringBuilder profile = new($"centre=[{centreAirX},{centreAirY}];above10=[{centreAirX},{aboveAirY}];");
        foreach (int airY in new[] { centreAirY, aboveAirY })
        {
            profile.Append(airY == centreAirY ? "level(" : "above10(");
            bool first = true;
            foreach (int distance in distances)
            {
                int airX = Math.Clamp(centreAirX + distance, 0, airWidth - 1);
                AirCell cell = air[airY * airWidth + airX];
                if (!first)
                {
                    profile.Append(',');
                }
                profile.Append($"d{distance}:{cell.VelocityX:0.000}/{cell.VelocityY:0.000}/{cell.Pressure:0.000}");
                first = false;
            }
            profile.Append(");");
        }
        fields.Append($" airProfile{frame}=\"{profile}\"");
    }

    private static void WriteSteamStateDump(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        string artifactDirectory,
        uint steam,
        string fileName,
        bool fullWidth)
    {
        Directory.CreateDirectory(artifactDirectory);
        int left = fullWidth ? 0 : 100;
        int right = fullWidth ? snapshot.Width - 1 : 380;
        const int top = 0;
        const int bottom = 269;
        int firstX = Math.Clamp(left, 0, snapshot.Width - 1);
        int lastX = Math.Clamp(right, 0, snapshot.Width - 1);
        int firstY = Math.Clamp(top, 0, snapshot.Height - 1);
        int lastY = Math.Clamp(bottom, 0, snapshot.Height - 1);
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        StringBuilder map = new();
        map.AppendLine($"# x={firstX}..{lastX}; y={firstY}..{lastY}; .=empty V=steam #=solid");
        for (int y = firstY; y <= lastY; y++)
        {
            for (int x = firstX; x <= lastX; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                char glyph = '.';
                if (cell.IsActive != 0)
                {
                    if (cell.MaterialIndex == steam)
                    {
                        glyph = 'V';
                    }
                    else if ((MaterialSimulationKind)registry[cell.MaterialIndex].Properties.SimulationKind ==
                        MaterialSimulationKind.Solid)
                    {
                        glyph = '#';
                    }
                }
                map.Append(glyph);
            }
            map.AppendLine();
        }
        File.WriteAllText(Path.Combine(artifactDirectory, fileName), map.ToString());
    }

    private static bool ValidateFireOpen(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        int fireCells = 0;
        int minimumX = snapshot.Width;
        int maximumX = -1;
        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            if (cell.IsActive == 0 || cell.MaterialIndex != fire)
            {
                continue;
            }
            int x = index % snapshot.Width;
            fireCells++;
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
        }

        const int brushWidth = AcceptanceRegressionScenario.FireBrushWidth;
        const int sourceX = 240;
        const int sourceY = 170;
        int[] heightsAboveSource = [20, 40, 60, 80];
        StringBuilder slices = new();
        foreach (int heightAboveSource in heightsAboveSource)
        {
            int y = sourceY - heightAboveSource;
            OpenFlameSliceMetrics fireSlice = MeasureOpenFlameSlice(
                grid, snapshot.Width, snapshot.Height, y, fire, smoke, false);
            OpenFlameSliceMetrics fireSmokeSlice = MeasureOpenFlameSlice(
                grid, snapshot.Width, snapshot.Height, y, fire, smoke, true);
            slices.Append($" fireComponentsH{heightAboveSource}={fireSlice.Components}" +
                $" fireWidthH{heightAboveSource}={fireSlice.Width}" +
                $" fireWidthBrushRatioH{heightAboveSource}={fireSlice.Width / (double)brushWidth:0.000}" +
                $" fireOccupiedH{heightAboveSource}={fireSlice.Occupied}" +
                $" fireSmokeComponentsH{heightAboveSource}={fireSmokeSlice.Components}" +
                $" fireSmokeWidthH{heightAboveSource}={fireSmokeSlice.Width}" +
                $" fireSmokeWidthBrushRatioH{heightAboveSource}={fireSmokeSlice.Width / (double)brushWidth:0.000}" +
                $" fireSmokeOccupiedH{heightAboveSource}={fireSmokeSlice.Occupied}");
        }

        GasMotionRatioMetrics fireGasRatio = MeasureFireGasMotionRatio(
            snapshot, grid, fire, sourceY - 40);
        SignedGasVelocityMetrics fireVelocityH20 = MeasureSignedFireVelocity(
            snapshot, grid, fire, sourceX, sourceY - 20);
        SignedGasVelocityMetrics fireVelocityH40 = MeasureSignedFireVelocity(
            snapshot, grid, fire, sourceX, sourceY - 40);
        string fireColumnsH20 = FormatFireSliceColumns(snapshot, grid, fire, sourceY - 20);
        string fireColumnsH40 = FormatFireSliceColumns(snapshot, grid, fire, sourceY - 40);
        string fireAirPairsH20 = FormatFireAirVelocityPairs(
            snapshot, grid, fire, sourceY - 20);
        string fireAirPairsH40 = FormatFireAirVelocityPairs(
            snapshot, grid, fire, sourceY - 40);
        string airProfiles = FormatOpenAirProfiles(snapshot, sourceX, sourceY, [5, 10, 20, 40]);
        CoreAirPressureMaximum corePressureMaximum = FindOpenCoreAirPressureMaximum(
            snapshot, sourceX, sourceY, 5, 40, 3);
        double fireRangeCentreOffset = maximumX >= minimumX
            ? (minimumX + maximumX) * 0.5 - sourceX
            : 0;
        WriteFireOpenStateDump(snapshot, registry, artifactDirectory, fire, smoke);
        bool image = File.Exists(Path.Combine(artifactDirectory, "Y_fire_open.png"));
        // Fine y=164 is the coarse cell immediately above the source brush.
        string pressureProfile = FormatAirPressureProfile(snapshot, 164, 140, 340);
        report = $"PHYXEL_FIRE_OPEN fireCells={fireCells} minX={minimumX} maxX={maximumX} " +
            $"fireRangeCentreOffset={fireRangeCentreOffset:0.000} brushWidth={brushWidth}{slices} " +
            $"fireGasRatioH40Count={fireGasRatio.Count} " +
            $"fireGasRatioH40Mean={fireGasRatio.Mean:0.000000} " +
            $"fireGasRatioH40StdDev={fireGasRatio.StandardDeviation:0.000000} " +
            $"fireGasRatioH40Min={fireGasRatio.Minimum:0.000000} " +
            $"fireGasRatioH40Max={fireGasRatio.Maximum:0.000000} " +
            $"fireSignedVelocityXH20Left={fireVelocityH20.LeftMean:0.000000} " +
            $"fireSignedVelocityXH20Right={fireVelocityH20.RightMean:0.000000} " +
            $"fireSignedVelocityXH20LeftCount={fireVelocityH20.LeftCount} " +
            $"fireSignedVelocityXH20RightCount={fireVelocityH20.RightCount} " +
            $"fireSignedVelocityXH40Left={fireVelocityH40.LeftMean:0.000000} " +
            $"fireSignedVelocityXH40Right={fireVelocityH40.RightMean:0.000000} " +
            $"fireSignedVelocityXH40LeftCount={fireVelocityH40.LeftCount} " +
            $"fireSignedVelocityXH40RightCount={fireVelocityH40.RightCount} " +
            $"airCorePressureMax={corePressureMaximum.Pressure:0.000000} " +
            $"airCorePressureHeight={corePressureMaximum.HeightAboveSource} " +
            $"airCorePressureDistance={corePressureMaximum.DistanceFromAxis} " +
            $"airCorePressureX={corePressureMaximum.AirX} " +
            $"airCorePressureY={corePressureMaximum.AirY} " +
            $"fireColumnsH20={fireColumnsH20} fireColumnsH40={fireColumnsH40} " +
            $"image={image} airProfiles={airProfiles} airPressure={pressureProfile} " +
            $"fireAirPairsH20={fireAirPairsH20} fireAirPairsH40={fireAirPairsH40}";
        return fireCells > 0 && pressureProfile is not "unavailable" and not "invalid";
    }

    private readonly record struct GasMotionRatioMetrics(
        int Count,
        double Mean,
        double StandardDeviation,
        double Minimum,
        double Maximum);

    private readonly record struct SignedGasVelocityMetrics(
        int LeftCount,
        int RightCount,
        double LeftMean,
        double RightMean);

    private readonly record struct CoreAirPressureMaximum(
        float Pressure,
        int HeightAboveSource,
        int DistanceFromAxis,
        int AirX,
        int AirY);

    private static SignedGasVelocityMetrics MeasureSignedFireVelocity(
        SimulationWorldSnapshot snapshot,
        ReadOnlySpan<GridCell> grid,
        uint fire,
        int axisX,
        int y)
    {
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        if (motion.Length != grid.Length || y < 0 || y >= snapshot.Height)
        {
            return new SignedGasVelocityMetrics(0, 0, 0, 0);
        }

        int leftCount = 0;
        int rightCount = 0;
        double leftSum = 0;
        double rightSum = 0;
        for (int x = 0; x < snapshot.Width; x++)
        {
            int index = y * snapshot.Width + x;
            if (grid[index].IsActive == 0 || grid[index].MaterialIndex != fire)
            {
                continue;
            }
            if (x < axisX)
            {
                leftCount++;
                leftSum += motion[index].VelocityX;
            }
            else if (x > axisX)
            {
                rightCount++;
                rightSum += motion[index].VelocityX;
            }
        }
        return new SignedGasVelocityMetrics(
            leftCount,
            rightCount,
            leftSum / Math.Max(1, leftCount),
            rightSum / Math.Max(1, rightCount));
    }

    private static string FormatFireSliceColumns(
        SimulationWorldSnapshot snapshot,
        ReadOnlySpan<GridCell> grid,
        uint fire,
        int y)
    {
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        if (motion.Length != grid.Length || y < 0 || y >= snapshot.Height)
        {
            return "unavailable";
        }

        List<string> columns = [];
        for (int x = 0; x < snapshot.Width; x++)
        {
            int index = y * snapshot.Width + x;
            if (grid[index].IsActive != 0 && grid[index].MaterialIndex == fire)
            {
                GasMotionState velocity = motion[index];
                columns.Add($"{x}:{velocity.VelocityX:0.000000}/{velocity.VelocityY:0.000000}");
            }
        }
        return columns.Count == 0 ? "none" : string.Join(';', columns);
    }

    // Uses precisely the same fine-to-coarse lookup as FlameAirDrift in CellularAutomataSolver.hlsl.
    private static string FormatFireAirVelocityPairs(
        SimulationWorldSnapshot snapshot,
        ReadOnlySpan<GridCell> grid,
        uint fire,
        int y)
    {
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        ReadOnlySpan<AirCell> air = snapshot.Air is null
            ? []
            : MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        int airWidth = Math.Max(1, (snapshot.Width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        int airHeight = Math.Max(1, (snapshot.Height + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        if (motion.Length != grid.Length || air.Length != airWidth * airHeight || y < 0 || y >= snapshot.Height)
        {
            return "unavailable";
        }

        List<string> pairs = [];
        int airY = Math.Clamp(y / SimulationSettings.AirCellSize, 0, airHeight - 1);
        for (int x = 0; x < snapshot.Width; x++)
        {
            int index = y * snapshot.Width + x;
            if (grid[index].IsActive == 0 || grid[index].MaterialIndex != fire)
            {
                continue;
            }

            int airX = Math.Clamp(x / SimulationSettings.AirCellSize, 0, airWidth - 1);
            GasMotionState gas = motion[index];
            AirCell sourceAir = air[airY * airWidth + airX];
            pairs.Add($"x{x}:gasVx={gas.VelocityX:0.000000},air[{airX},{airY}].Vx={sourceAir.VelocityX:0.000000}");
        }
        return pairs.Count == 0 ? "none" : string.Join(';', pairs);
    }

    private static CoreAirPressureMaximum FindOpenCoreAirPressureMaximum(
        SimulationWorldSnapshot snapshot,
        int sourceFineX,
        int sourceFineY,
        int minimumHeightAboveSource,
        int maximumHeightAboveSource,
        int coreRadiusCoarseCells)
    {
        ReadOnlySpan<AirCell> air = snapshot.Air is null
            ? []
            : MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        int airWidth = Math.Max(1, (snapshot.Width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        int airHeight = Math.Max(1, (snapshot.Height + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        if (air.Length != airWidth * airHeight)
        {
            return new CoreAirPressureMaximum(0, 0, 0, 0, 0);
        }

        int axisX = Math.Clamp(sourceFineX / SimulationSettings.AirCellSize, 0, airWidth - 1);
        bool found = false;
        CoreAirPressureMaximum maximum = new(float.NegativeInfinity, 0, 0, 0, 0);
        for (int airY = 0; airY < airHeight; airY++)
        {
            int heightAboveSource = sourceFineY - airY * SimulationSettings.AirCellSize;
            if (heightAboveSource < minimumHeightAboveSource || heightAboveSource > maximumHeightAboveSource)
            {
                continue;
            }

            for (int distance = -coreRadiusCoarseCells; distance <= coreRadiusCoarseCells; distance++)
            {
                int airX = axisX + distance;
                if (airX < 0 || airX >= airWidth)
                {
                    continue;
                }

                float pressure = air[airY * airWidth + airX].Pressure;
                if (!found || pressure > maximum.Pressure)
                {
                    maximum = new CoreAirPressureMaximum(
                        pressure, heightAboveSource, distance, airX, airY);
                    found = true;
                }
            }
        }
        return found ? maximum : new CoreAirPressureMaximum(0, 0, 0, 0, 0);
    }

    private static GasMotionRatioMetrics MeasureFireGasMotionRatio(
        SimulationWorldSnapshot snapshot,
        ReadOnlySpan<GridCell> grid,
        uint fire,
        int y)
    {
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        if (motion.Length != grid.Length || y < 0 || y >= snapshot.Height)
        {
            return new GasMotionRatioMetrics(0, 0, 0, 0, 0);
        }

        List<double> ratios = [];
        for (int x = 0; x < snapshot.Width; x++)
        {
            int index = y * snapshot.Width + x;
            GridCell cell = grid[index];
            if (cell.IsActive == 0 || cell.MaterialIndex != fire)
            {
                continue;
            }

            GasMotionState velocity = motion[index];
            double absY = Math.Abs(velocity.VelocityY);
            if (absY > 0.0001)
            {
                ratios.Add(Math.Abs(velocity.VelocityX) / absY);
            }
        }

        if (ratios.Count == 0)
        {
            return new GasMotionRatioMetrics(0, 0, 0, 0, 0);
        }

        double mean = ratios.Average();
        double variance = ratios.Sum(ratio => Math.Pow(ratio - mean, 2)) / ratios.Count;
        return new GasMotionRatioMetrics(
            ratios.Count,
            mean,
            Math.Sqrt(variance),
            ratios.Min(),
            ratios.Max());
    }

    private static string FormatOpenAirProfiles(
        SimulationWorldSnapshot snapshot,
        int sourceFineX,
        int sourceFineY,
        IReadOnlyList<int> heightsAboveSource)
    {
        if (snapshot.Air is null || snapshot.Air.Length == 0)
        {
            return "unavailable";
        }

        ReadOnlySpan<AirCell> air = MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        int airWidth = Math.Max(1, (snapshot.Width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        int airHeight = Math.Max(1, (snapshot.Height + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        if (air.Length != airWidth * airHeight)
        {
            return "invalid";
        }

        int axisX = Math.Clamp(sourceFineX / SimulationSettings.AirCellSize, 0, airWidth - 1);
        const int profileRadius = 15;
        StringBuilder profiles = new();
        for (int heightIndex = 0; heightIndex < heightsAboveSource.Count; heightIndex++)
        {
            int heightAboveSource = heightsAboveSource[heightIndex];
            int airY = Math.Clamp(
                (sourceFineY - heightAboveSource) / SimulationSettings.AirCellSize,
                0,
                airHeight - 1);
            if (heightIndex > 0)
            {
                profiles.Append('|');
            }
            profiles.Append($"h{heightAboveSource}[y{airY}](");
            bool first = true;
            for (int distance = -profileRadius; distance <= profileRadius; distance++)
            {
                int airX = axisX + distance;
                if (airX < 0 || airX >= airWidth)
                {
                    continue;
                }
                if (!first)
                {
                    profiles.Append(',');
                }
                AirCell cell = air[airY * airWidth + airX];
                profiles.Append($"d{distance}:{cell.VelocityX:0.000000}/" +
                    $"{cell.VelocityY:0.000000}/{cell.Pressure:0.000000}");
                first = false;
            }
            profiles.Append(')');
        }
        return profiles.ToString();
    }

    private static OpenFlameSliceMetrics MeasureOpenFlameSlice(
        ReadOnlySpan<GridCell> grid,
        int width,
        int height,
        int y,
        uint fire,
        uint smoke,
        bool includeSmoke)
    {
        if (y < 0 || y >= height)
        {
            return new OpenFlameSliceMetrics(0, 0, 0);
        }

        int components = 0;
        int occupied = 0;
        int minimumX = width;
        int maximumX = -1;
        bool wasOccupied = false;
        int row = y * width;
        for (int x = 0; x < width; x++)
        {
            GridCell cell = grid[row + x];
            bool isOccupied = cell.IsActive != 0 &&
                (cell.MaterialIndex == fire || (includeSmoke && cell.MaterialIndex == smoke));
            if (isOccupied)
            {
                if (!wasOccupied)
                {
                    components++;
                }
                occupied++;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
            }
            wasOccupied = isOccupied;
        }
        int sliceWidth = maximumX >= minimumX ? maximumX - minimumX + 1 : 0;
        return new OpenFlameSliceMetrics(components, sliceWidth, occupied);
    }

    private static bool ValidateFurnace(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        int chimneySmoke = 0;
        int chimneyFire = 0;
        int chamberFire = 0;
        int chamberSmoke = 0;
        int topSmoke = 0;
        for (int y = 20; y <= 134; y++)
        {
            for (int x = 111; x <= 179; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0) continue;
                if (cell.MaterialIndex == smoke)
                {
                    chimneySmoke++;
                    if (y < 70) topSmoke++;
                }
                else if (cell.MaterialIndex == fire)
                {
                    chimneyFire++;
                }
            }
        }
        for (int y = 140; y <= 214; y++)
        {
            for (int x = 226; x <= 374; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0) continue;
                if (cell.MaterialIndex == smoke) chamberSmoke++;
                else if (cell.MaterialIndex == fire) chamberFire++;
            }
        }
        bool image = File.Exists(Path.Combine(artifactDirectory, "Z_furnace.png"));
        bool passed = chimneySmoke >= 8 && topSmoke >= 2 && chimneySmoke > chimneyFire * 3 &&
            chamberFire > 0 && image;
        report = $"PHYXEL_FURNACE chimneySmoke={chimneySmoke} chimneyFire={chimneyFire} " +
            $"topSmoke={topSmoke} chamberFire={chamberFire} chamberSmoke={chamberSmoke} image={image}";
        return passed;
    }

    private static bool ValidateMetalChimney(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        ReadOnlySpan<GasMotionState> motion = snapshot.GasMotion is null
            ? []
            : MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        ReadOnlySpan<AirCell> air = snapshot.Air is null
            ? []
            : MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        int airWidth = Math.Max(1, (snapshot.Width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize);
        uint smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        int[] tops = [180, 120, 60];
        StringBuilder bands = new();
        int outletSmoke = 0;
        for (int band = 0; band < tops.Length; band++)
        {
            int fromY = tops[band];
            int toY = fromY + 19;
            int gasCount = 0;
            double velocityY = 0;
            int clamped = 0;
            double airVelocityY = 0;
            int airCount = 0;
            for (int y = fromY; y <= toY; y++)
            for (int x = 221; x <= 258; x++)
            {
                int index = y * snapshot.Width + x;
                GridCell cell = grid[index];
                if (cell.IsActive != 0 &&
                    (MaterialSimulationKind)registry[cell.MaterialIndex].Properties.SimulationKind == MaterialSimulationKind.Gas &&
                    motion.Length == grid.Length)
                {
                    gasCount++;
                    velocityY += motion[index].VelocityY;
                    clamped += Math.Abs(motion[index].OffsetY) >= 7.999f ? 1 : 0;
                    if (band == 2 && cell.MaterialIndex == smoke) outletSmoke++;
                }
                int airX = x / 4;
                int airY = y / 4;
                if (air.Length > 0)
                {
                    airVelocityY += air[airY * airWidth + airX].VelocityY;
                    airCount++;
                }
            }
            bands.Append($" band{band}Y={fromY}..{toY}" +
                $" gasCells={gasCount} meanGasVelocityY={velocityY / Math.Max(1, gasCount):0.000000}" +
                $" offsetYClampFraction={clamped / (double)Math.Max(1, gasCount):0.000000}" +
                $" meanAirVelocityY={airVelocityY / Math.Max(1, airCount):0.000000}");
        }
        bool image = File.Exists(Path.Combine(artifactDirectory, "Z_metal_chimney.png"));
        report = $"PHYXEL_METAL_CHIMNEY outletSmoke={outletSmoke} image={image}{bands}";
        return image;
    }

    private static bool ValidatePhaseDispatchSmoke(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry registry,
        ThermalGpuTimingStatistics timing,
        ulong dispatches,
        ulong fallbackWakeUps,
        int maximumDispatchesPerFrame,
        PhaseTransitionSummaryFlags summary,
        bool presentationIsCurrent,
        out string report)
    {
        uint source = registry.GetRequiredRuntimeIndex("acceptance:phase_source");
        uint target = registry.GetRequiredRuntimeIndex("acceptance:phase_target");
        int sourceCount = 0;
        int targetCount = 0;
        int normalizedCount = 0;
        int massCount = 0;
        int temperatureCount = 0;
        int velocityCount = 0;
        int pressureCount = 0;
        int bodyCount = 0;
        int restCount = 0;
        foreach (GridCell cell in Cells(snapshot))
        {
            sourceCount += cell.IsActive != 0 && cell.MaterialIndex == source ? 1 : 0;
            if (cell.IsActive == 0 || cell.MaterialIndex != target)
            {
                continue;
            }
            targetCount++;
            massCount += cell.Mass == 2 ? 1 : 0;
            temperatureCount += cell.Temperature == 150 ? 1 : 0;
            velocityCount += cell.VelocityX == 0 && cell.VelocityY == 0 ? 1 : 0;
            pressureCount += cell.Pressure == 0 ? 1 : 0;
            bodyCount += cell.BodyId == 0 ? 1 : 0;
            restCount += cell.RestFrames == 2 ? 1 : 0;
            if (cell.Mass == 2 && cell.Temperature == 150 &&
                cell.VelocityX == 0 && cell.VelocityY == 0 && cell.Pressure == 0 &&
                cell.BodyId == 0 && cell.RestFrames == 2)
            {
                normalizedCount++;
            }
        }
        PhaseTransitionSummaryFlags expected = PhaseTransitionSummaryFlags.PhaseOccurred |
            PhaseTransitionSummaryFlags.TouchesLiquid |
            PhaseTransitionSummaryFlags.TouchesSolid;
        bool passed = sourceCount == 0 && targetCount == 256 && normalizedCount == targetCount &&
            dispatches >= 40 && fallbackWakeUps == 0 && maximumDispatchesPerFrame == 1 &&
            (summary & expected) == expected && timing.Samples > 0 && presentationIsCurrent;
        report = passed
            ? $"PHASE_DISPATCH_SMOKE_OK target={targetCount} dispatches={dispatches} " +
                $"fallbackWakeUps={fallbackWakeUps} summary=0x{(uint)summary:X} " +
                $"timingSamples={timing.Samples} compositionCurrent=true"
            : $"PHASE_DISPATCH_SMOKE_FAILED source={sourceCount} target={targetCount} " +
                $"normalized={normalizedCount} dispatches={dispatches} fallbackWakeUps={fallbackWakeUps} " +
                $"fields={massCount}/{temperatureCount}/{velocityCount}/{pressureCount}/{bodyCount}/{restCount} " +
                $"maxPerFrame={maximumDispatchesPerFrame} " +
                $"summary=0x{(uint)summary:X} timingSamples={timing.Samples} " +
                $"compositionCurrent={presentationIsCurrent}";
        return passed;
    }

    private static bool ValidateTemperatureBrush(
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        uint probeIndex = materials.Resolve("acceptance:temperature_probe");
        int sandCount = 0;
        int probeCount = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0)
            {
                continue;
            }
            if (cell.MaterialIndex == materials.Sand)
            {
                sandCount++;
                if (cell.Temperature != 20.0f)
                {
                    report = $"TEMPERATURE_BRUSH_FAILED sand_temperature={cell.Temperature:R}";
                    return false;
                }
            }
            else if (cell.MaterialIndex == probeIndex)
            {
                probeCount++;
                if (cell.Temperature != 123.5f)
                {
                    report = $"TEMPERATURE_BRUSH_FAILED probe_temperature={cell.Temperature:R}";
                    return false;
                }
            }
        }

        GridCell erased = grid[60 * snapshot.Width + 300];
        bool erasedIsDefault = erased.MaterialIndex == 0 && erased.Mass == 0 &&
            erased.VelocityX == 0 && erased.VelocityY == 0 && erased.Pressure == 0 &&
            erased.IsActive == 0 && erased.BodyId == 0 && erased.RestFrames == 0 &&
            erased.Temperature == 0;
        bool passed = sandCount > 0 && probeCount > 0 && erasedIsDefault;
        report = passed
            ? $"TEMPERATURE_BRUSH_OK sand={sandCount} probe={probeCount} erasedDefault=true"
            : $"TEMPERATURE_BRUSH_FAILED sand={sandCount} probe={probeCount} erasedDefault={erasedIsDefault}";
        return passed;
    }

    private static bool ValidateUnderwaterGranularPile(
        SimulationWorldSnapshot snapshot,
        uint granularIndex,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int granular = 0;
        int liquid = 0;
        int settled = 0;
        int minimumX = snapshot.Width;
        int maximumX = -1;
        int minimumY = snapshot.Height;
        int maximumY = -1;
        double granularMass = 0;
        double liquidMass = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                if (cell.MaterialIndex == granularIndex)
                {
                    granular++;
                    granularMass += cell.Mass;
                    settled += cell.RestFrames >= 30 ? 1 : 0;
                    minimumX = Math.Min(minimumX, x);
                    maximumX = Math.Max(maximumX, x);
                    minimumY = Math.Min(minimumY, y);
                    maximumY = Math.Max(maximumY, y);
                }
                else if (cell.MaterialIndex == materials.Water)
                {
                    liquid++;
                    liquidMass += cell.Mass;
                }
            }
        }

        int expectedGranular = ExpectedCircleCells(snapshot.Width, snapshot.Height, 240, 80, 20);
        int expectedLiquid = ExpectedFillCells(snapshot.Width, snapshot.Height, 50, 125, 430, 230, 15, 11);
        int width = maximumX >= minimumX ? maximumX - minimumX + 1 : 0;
        int height = maximumY >= minimumY ? maximumY - minimumY + 1 : 0;
        bool conserved = granular == expectedGranular && liquid == expectedLiquid &&
            Math.Abs(granularMass - expectedGranular) < 0.1 &&
            Math.Abs(liquidMass - expectedLiquid) < 0.1;
        bool image = File.Exists(Path.Combine(artifactDirectory, "U_underwater_granular.png"));
        bool passed = conserved && width >= 55 && height <= 45 && maximumY >= 240 &&
            settled >= granular * 0.98 && image;
        report =
            $"PHYXEL_UNDERWATER_GRANULAR granular={granular}/{expectedGranular} granularMass={granularMass:0.0} " +
            $"liquid={liquid}/{expectedLiquid} liquidMass={liquidMass:0.0} bounds={minimumX},{minimumY}-{maximumX},{maximumY} " +
            $"width={width} height={height} settled={settled}";
        return passed;
    }

    private static bool ValidateGranularWaterDisplacement(
        SimulationWorldSnapshot snapshot,
        uint granularIndex,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int granular = 0;
        int liquid = 0;
        int minimumLiquidY = snapshot.Height;
        int shoulderLiquidTop = snapshot.Height;
        int outsideLiquidTop = snapshot.Height;
        int highTrail = 0;
        int upwardFast = 0;
        double granularMass = 0;
        double liquidMass = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                if (cell.MaterialIndex == granularIndex)
                {
                    granular++;
                    granularMass += cell.Mass;
                }
                else if (cell.MaterialIndex == materials.Water)
                {
                    liquid++;
                    liquidMass += cell.Mass;
                    minimumLiquidY = Math.Min(minimumLiquidY, y);
                    if (x is >= 190 and <= 290)
                    {
                        shoulderLiquidTop = Math.Min(shoulderLiquidTop, y);
                    }
                    else if (x < 170 || x > 310)
                    {
                        outsideLiquidTop = Math.Min(outsideLiquidTop, y);
                    }
                    highTrail += x is >= 225 and <= 255 && y < 90 ? 1 : 0;
                    upwardFast += cell.VelocityY < -8 ? 1 : 0;
                }
            }
        }

        int expectedGranular = ExpectedCircleCells(snapshot.Width, snapshot.Height, 240, 80, 20);
        int expectedLiquid = ExpectedFillCells(snapshot.Width, snapshot.Height, 50, 125, 430, 230, 15, 11);
        bool conserved = granular == expectedGranular && liquid == expectedLiquid &&
            Math.Abs(granularMass - expectedGranular) < 0.1 &&
            Math.Abs(liquidMass - expectedLiquid) < 0.1;
        bool image = File.Exists(Path.Combine(artifactDirectory, "V_granular_displacement.png"));
        int surfaceRise = outsideLiquidTop - shoulderLiquidTop;
        bool passed = conserved && minimumLiquidY >= 90 && surfaceRise is >= 5 and <= 35 &&
            highTrail == 0 && upwardFast == 0 && image;
        report =
            $"PHYXEL_GRANULAR_DISPLACEMENT granular={granular}/{expectedGranular} granularMass={granularMass:0.0} " +
            $"liquid={liquid}/{expectedLiquid} liquidMass={liquidMass:0.0} liquidTop={minimumLiquidY} " +
            $"shoulderTop={shoulderLiquidTop} outsideTop={outsideLiquidTop} rise={surfaceRise} " +
            $"highTrail={highTrail} upwardFast={upwardFast}";
        return passed;
    }

    private static bool ValidateGranularBarrier(
        SimulationWorldSnapshot snapshot,
        uint granularIndex,
        SimulationStatistics statistics,
        bool hydraulics,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int granular = 0;
        int liquid = 0;
        int settledGranular = 0;
        int rightLiquid = 0;
        int minimumGranularY = snapshot.Height;
        int minimumLiquidY = snapshot.Height;
        double granularMass = 0;
        double liquidMass = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                if (cell.MaterialIndex == granularIndex)
                {
                    granular++;
                    granularMass += cell.Mass;
                    settledGranular += cell.RestFrames >= 30 ? 1 : 0;
                    minimumGranularY = Math.Min(minimumGranularY, y);
                }
                else if (cell.MaterialIndex == materials.Water)
                {
                    liquid++;
                    liquidMass += cell.Mass;
                    minimumLiquidY = Math.Min(minimumLiquidY, y);
                    rightLiquid += x >= 280 ? 1 : 0;
                }
            }
        }

        int expectedGranular = ExpectedFillCells(snapshot.Width, snapshot.Height, 210, 90, 270, 240, 7, 5);
        int expectedLiquid = ExpectedFillCells(snapshot.Width, snapshot.Height, 50, 175, 175, 238, 7, 5);
        bool conserved = granular == expectedGranular && liquid == expectedLiquid &&
            Math.Abs(granularMass - expectedGranular) < 0.1 &&
            Math.Abs(liquidMass - expectedLiquid) < 0.1;
        string imageName = hydraulics ? "X_granular_barrier_on.png" : "W_granular_barrier_off.png";
        bool image = File.Exists(Path.Combine(artifactDirectory, imageName));
        bool passed = conserved && rightLiquid == 0 && minimumLiquidY >= 155 &&
            minimumGranularY < minimumLiquidY && settledGranular >= granular * 0.98 &&
            (!hydraulics || statistics.FarColumnMoves == 0) && image;
        report =
            $"PHYXEL_GRANULAR_BARRIER hydraulics={(hydraulics ? 1 : 0)} granular={granular}/{expectedGranular} " +
            $"granularMass={granularMass:0.0} liquid={liquid}/{expectedLiquid} liquidMass={liquidMass:0.0} " +
            $"granularTop={minimumGranularY} liquidTop={minimumLiquidY} rightLiquid={rightLiquid} " +
            $"settled={settledGranular} pressureMoves={statistics.PressureMoves} farColumnMoves={statistics.FarColumnMoves}";
        return passed;
    }

    private static int ExpectedCircleCells(
        int width,
        int height,
        int centerX,
        int centerY,
        int radius)
    {
        bool[] cells = new bool[width * height];
        MarkExpectedBrush(cells, width, height, centerX, centerY, radius);
        return cells.Count(value => value);
    }

    private static int ExpectedFillCells(
        int width,
        int height,
        int left,
        int top,
        int right,
        int bottom,
        int spacing,
        int radius)
    {
        bool[] cells = new bool[width * height];
        for (int y = top; y <= bottom; y += spacing)
        {
            for (int x = left; x <= right; x += spacing)
            {
                MarkExpectedBrush(cells, width, height, x, y, radius);
            }
        }
        return cells.Count(value => value);
    }

    private static void MarkExpectedBrush(
        bool[] cells,
        int width,
        int height,
        int centerX,
        int centerY,
        int radius)
    {
        for (int offsetY = -radius; offsetY <= radius; offsetY++)
        {
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                if (offsetX * offsetX + offsetY * offsetY > radius * radius)
                {
                    continue;
                }
                int x = centerX + offsetX;
                int y = centerY + offsetY;
                if (x >= 0 && y >= 0 && x < width && y < height)
                {
                    cells[y * width + x] = true;
                }
            }
        }
    }

    private static bool ValidateExternalLiquid(
        SimulationWorldSnapshot snapshot,
        uint liquidIndex,
        string artifactDirectory,
        out string report)
    {
        int cells = 0;
        int resting = 0;
        int moving = 0;
        int minimumX = snapshot.Width;
        int maximumX = 0;
        double mass = 0;
        foreach (GridCell cell in Cells(snapshot))
        {
            if (cell.IsActive == 0 || cell.MaterialIndex != liquidIndex)
            {
                continue;
            }
            cells++;
            mass += cell.Mass;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialIndex == liquidIndex)
                {
                    minimumX = Math.Min(minimumX, x);
                    maximumX = Math.Max(maximumX, x);
                }
            }
        }
        bool image = File.Exists(Path.Combine(artifactDirectory, "R_external_liquid.png"));
        bool passed = cells >= 1000 && mass >= 1000 && maximumX - minimumX >= 180 &&
            resting >= cells * 0.98 && moving == 0 && image;
        report = $"PHYXEL_EXTERNAL_LIQUID cells={cells} mass={mass:0.0} resting={resting} moving={moving} width={maximumX - minimumX}";
        return passed;
    }

    private static bool ValidateExternalSolids(
        SimulationWorldSnapshot snapshot,
        uint lightIndex,
        uint heavyIndex,
        uint fixtureIndex,
        string artifactDirectory,
        out string report)
    {
        int light = 0;
        int heavy = 0;
        int fixture = 0;
        int moving = 0;
        int lightBottom = 0;
        int heavyBottom = 0;
        int fixtureBodyCells = 0;
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            if (cell.IsActive == 0)
            {
                continue;
            }
            int y = index / snapshot.Width;
            if (cell.MaterialIndex == lightIndex)
            {
                light++;
                lightBottom = Math.Max(lightBottom, y);
                moving += cell.RestFrames < 2 ? 1 : 0;
                if (Math.Abs(cell.Mass - 4f) > 0.01f) return Fail(out report);
            }
            else if (cell.MaterialIndex == heavyIndex)
            {
                heavy++;
                heavyBottom = Math.Max(heavyBottom, y);
                moving += cell.RestFrames < 2 ? 1 : 0;
                if (Math.Abs(cell.Mass - 12f) > 0.01f) return Fail(out report);
            }
            else if (cell.MaterialIndex == fixtureIndex)
            {
                fixture++;
                fixtureBodyCells += cell.BodyId != 0 ? 1 : 0;
            }
        }
        bool image = File.Exists(Path.Combine(artifactDirectory, "T_external_solids.png"));
        bool passed = light >= 500 && heavy >= 500 && fixture >= 400 &&
            lightBottom >= 235 && heavyBottom >= 235 && moving == 0 &&
            fixtureBodyCells == 0 && image;
        report = $"PHYXEL_EXTERNAL_SOLIDS light={light}@{lightBottom} heavy={heavy}@{heavyBottom} fixture={fixture} fixtureBodies={fixtureBodyCells} moving={moving}";
        return passed;
    }

    private static bool ValidateWaterStress(
        SimulationWorldSnapshot snapshot,
        double framesPerSecond,
        out string report)
    {
        int water = 0;
        int resting = 0;
        int moving = 0;
        foreach (GridCell cell in Cells(snapshot))
        {
            if (cell.IsActive == 0 || cell.MaterialIndex != materials.Water)
            {
                continue;
            }
            water++;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        report = $"PHYXEL_STRESS_WATER water={water} resting={resting} moving={moving} fps={framesPerSecond:0.0}";
        return water >= 500000;
    }

    private static bool ValidateFlatSurface(
        SimulationWorldSnapshot snapshot,
        double framesPerSecond,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 25, 190, 100, 250);
        int rightTop = SurfaceTop(grid, snapshot.Width, 405, 455, 100, 250);
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        int minimumLeakX = snapshot.Width;
        int maximumLeakX = 0;
        int minimumLeakY = snapshot.Height;
        int maximumLeakY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != materials.Water)
                {
                    continue;
                }
                water++;
                resting += cell.RestFrames >= 60 ? 1 : 0;
                moving += Speed(cell) > 0.02f ? 1 : 0;
                if (x < 8 || x > 472 || y > 256)
                {
                    leaks++;
                    minimumLeakX = Math.Min(minimumLeakX, x);
                    maximumLeakX = Math.Max(maximumLeakX, x);
                    minimumLeakY = Math.Min(minimumLeakY, y);
                    maximumLeakY = Math.Max(maximumLeakY, y);
                }
            }
        }
        int difference = leftTop > 0 && rightTop > 0
            ? Math.Abs(leftTop - rightTop)
            : snapshot.Height;
        string[] streamImages =
        [
            "O_flat_stream_early.png",
            "O_flat_stream_mid1.png",
            "O_flat_stream_mid2.png",
            "O_flat_stream_late.png"
        ];
        int minimumFallingWater = int.MaxValue;
        int sideWater = 0;
        int stripedWater = 0;
        foreach (string streamImageName in streamImages)
        {
            string streamImage = Path.Combine(artifactDirectory, streamImageName);
            minimumFallingWater = Math.Min(
                minimumFallingWater,
                CountColor(streamImage, 225, 20, 255, 100, IsBlue));
            sideWater +=
                CountColor(streamImage, 15, 20, 215, 100, IsBlue) +
                CountColor(streamImage, 265, 20, 465, 100, IsBlue);
            stripedWater +=
                CountVerticalColorRuns(streamImage, 15, 20, 215, 100, 8, IsBlue) +
                CountVerticalColorRuns(streamImage, 265, 20, 465, 100, 8, IsBlue);
        }
        // A conserved cellular liquid may have one partially filled final row,
        // but a broad multi-pixel wave is never an acceptable resting surface.
        bool passed = water > 10000 && difference <= 1 && leaks == 0 &&
            moving == 0 &&
            minimumFallingWater > 100 && stripedWater == 0 && framesPerSecond >= 55;
        report = $"PHYXEL_FLAT_SURFACE water={water} levels={leftTop}/{rightTop} difference={difference} resting={resting} moving={moving} streamMin={minimumFallingWater} striped={stripedWater} stray={sideWater} leaks={leaks} leakBounds={minimumLeakX},{minimumLeakY}-{maximumLeakX},{maximumLeakY} fps={framesPerSecond:0.0}";
        return passed;
    }

    private static bool ValidateWaterDrain(
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 20, 170, 20, 250);
        int rightTop = SurfaceTop(grid, snapshot.Width, 310, 460, 20, 250);
        int referenceTop = Math.Max(leftTop, rightTop);
        int water = 0;
        int sand = 0;
        int hangingWater = 0;
        int movingWater = 0;
        int unsettledWater = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                if (cell.MaterialIndex == materials.Sand)
                {
                    sand++;
                    continue;
                }
                if (cell.MaterialIndex != materials.Water)
                {
                    continue;
                }
                water++;
                movingWater += Speed(cell) > 0.02f ? 1 : 0;
                unsettledWater += cell.RestFrames < 60 ? 1 : 0;
                if (referenceTop > 0 && y + 4 < referenceTop)
                {
                    hangingWater++;
                }
            }
        }
        report = $"PHYXEL_WATER_DRAIN water={water} sand={sand} leftTop={leftTop} rightTop={rightTop} hanging={hangingWater} moving={movingWater} unsettled={unsettledWater}";
        return water > 5000 && sand > 1000 && Math.Abs(leftTop - rightTop) <= 2 &&
            hangingWater <= water / 100 && movingWater == 0 && unsettledWater <= 16;
    }

    private static bool ValidateCommunicatingVessels(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 35, 95, 30, 247);
        int centerTop = SurfaceTop(grid, snapshot.Width, 115, 265, 30, 247);
        int rightTop = SurfaceTop(grid, snapshot.Width, 285, 445, 30, 247);
        int finalRange = Math.Max(leftTop, Math.Max(centerTop, rightTop)) -
            Math.Min(leftTop, Math.Min(centerTop, rightTop));
        string twoSecondImage = Path.Combine(artifactDirectory, "H_vessels_2s.png");
        int imageLeft = ImageSurfaceTop(twoSecondImage, 35, 95, 30, 247);
        int imageCenter = ImageSurfaceTop(twoSecondImage, 115, 265, 30, 247);
        int imageRight = ImageSurfaceTop(twoSecondImage, 285, 445, 30, 247);
        int imageRange = Math.Max(imageLeft, Math.Max(imageCenter, imageRight)) -
            Math.Min(imageLeft, Math.Min(imageCenter, imageRight));
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || cell.MaterialIndex != materials.Water)
            {
                continue;
            }
            water++;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialIndex == materials.Water &&
                    (x < 20 || x > 460 || y > 251))
                {
                    leaks++;
                }
            }
        }
        int unsettled = water - resting;
        bool passed = water > 10000 && leftTop > 0 && centerTop > 0 && rightTop > 0 &&
            imageLeft > 0 && imageCenter > 0 && imageRight > 0 &&
            finalRange <= 4 && imageRange >= 16 && unsettled <= 16 && moving == 0 &&
            statistics.PressureMoves == 0 && statistics.FarColumnMoves == 0 && leaks == 0;
        report = $"PHYXEL_H_VESSELS water={water} final={leftTop}/{centerTop}/{rightTop} finalRange={finalRange} image2s={imageLeft}/{imageCenter}/{imageRight} imageRange={imageRange} resting={resting} unsettled={unsettled} moving={moving} pressureMoves={statistics.PressureMoves} farColumnMoves={statistics.FarColumnMoves} leaks={leaks}";
        return passed;
    }

    private static bool ValidatePressureTube(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int tubeTop = SurfaceTop(grid, snapshot.Width, 236, 254, 60, 245);
        int tankTop = SurfaceTop(grid, snapshot.Width, 340, 450, 60, 250);
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != materials.Water)
                {
                    continue;
                }
                water++;
                resting += cell.RestFrames >= 60 ? 1 : 0;
                moving += Speed(cell) > 0.02f ? 1 : 0;
                leaks += y > 254 || x > 475 ? 1 : 0;
            }
        }
        string fillImage = Path.Combine(artifactDirectory, "I_pressure_tube_fill.png");
        int imageTubeTop = ImageSurfaceTop(fillImage, 236, 254, 60, 245);
        int imageTankTop = ImageSurfaceTop(fillImage, 340, 450, 60, 250);
        bool passed = water > 10000 && tubeTop > 0 && tankTop > 0 &&
            imageTubeTop > 0 && imageTankTop > 0 &&
            Math.Abs(tubeTop - tankTop) <= 2 &&
            Math.Abs(imageTubeTop - imageTankTop) <= 2 &&
            resting == water && moving == 0 && statistics.PressureMoves == 0 &&
            statistics.FarColumnMoves == 0 && leaks == 0;
        report = $"PHYXEL_I_PRESSURE_TUBE water={water} final={tubeTop}/{tankTop} image300={imageTubeTop}/{imageTankTop} resting={resting} moving={moving} pressureMoves={statistics.PressureMoves} farColumnMoves={statistics.FarColumnMoves} leaks={leaks}";
        return passed;
    }

    private static bool ValidateSavedPressure(
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int tubeTop = CurvedTubeSurfaceTop(
            grid,
            snapshot.Width,
            1120,
            1240,
            620,
            790,
            1190,
            out int tubeWidth);
        int tankTop = SurfaceTop(grid, snapshot.Width, 1080, 1130, 450, 900);
        int water = 0;
        int moving = 0;
        int routed = 0;
        float minimumHead = float.MaxValue;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || cell.MaterialIndex != materials.Water)
            {
                continue;
            }
            water++;
            moving += Speed(cell) > 0.02f ? 1 : 0;
            if (cell.Pressure > 0)
            {
                routed++;
                minimumHead = Math.Min(minimumHead, cell.Pressure - 1);
            }
        }
        bool passed = water > 100000 && tubeTop > 0 && tankTop > 0 &&
            Math.Abs(tubeTop - tankTop) <= 3 && moving == 0;
        report = $"PHYXEL_J_SAVED_PRESSURE water={water} tubeTop={tubeTop} tubeWidth={tubeWidth} tankTop={tankTop} difference={Math.Abs(tubeTop - tankTop)} moving={moving} routed={routed} minHead={(minimumHead == float.MaxValue ? -1 : minimumHead):0}";
        return passed;
    }

    private static bool ValidateSavedIsolation(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int water = CountMaterial(
            grid,
            snapshot.Width,
            0,
            snapshot.Width - 1,
            0,
            snapshot.Height - 1,
            materials.Water);
        int tankTop = SurfaceTop(grid, snapshot.Width, 1100, 1450, 100, 350);
        int leftSpiralTop = SurfaceTop(grid, snapshot.Width, 580, 730, 300, 760);
        int outerRise = CountMaterial(
            grid, snapshot.Width, 620, 700, 390, 508, materials.Water);
        int firstBend = CountMaterial(
            grid, snapshot.Width, 730, 780, 480, 510, materials.Water);
        int innerRise = CountMaterial(
            grid, snapshot.Width, 1040, 1100, 500, 612, materials.Water);
        int bottomWater = CountMaterial(
            grid, snapshot.Width, 0, snapshot.Width - 1, 1000, snapshot.Height - 1, materials.Water);
        int upperWater = water - bottomWater;
        GridCell[] componentCells = grid.ToArray();
        int outerComponent = ConnectedMaterialSize(
            componentCells, snapshot.Width, snapshot.Height, 1200, 300, materials.Water);
        int innerComponent = ConnectedMaterialSize(
            componentCells, snapshot.Width, snapshot.Height, 1050, 600, materials.Water);
        int bottomComponent = ConnectedMaterialSize(
            componentCells, snapshot.Width, snapshot.Height, 100, 1070, materials.Water);
        int moving = 0;
        int leftRouted = 0;
        float leftMinimumHead = float.MaxValue;
        for (int y = 300; y <= 760; y++)
        {
            for (int x = 580; x <= 730; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (IsMaterial(cell, materials.Water) && cell.Pressure > 0)
                {
                    leftRouted++;
                    leftMinimumHead = Math.Min(leftMinimumHead, cell.Pressure - 1);
                }
            }
        }
        foreach (GridCell cell in grid)
        {
            moving += IsMaterial(cell, materials.Water) && Speed(cell) > 0.02f ? 1 : 0;
        }

        // This capture contains three deliberately disconnected pools: the
        // outer vessel, the inner spiral, and the strip along the floor.  A
        // pressure route may rearrange water inside one pool, but it must not
        // transfer mass between those components merely because their columns
        // share the same x coordinate.
        bool passed = water is >= 325000 and <= 325150 &&
            outerComponent is >= 255300 and <= 255450 &&
            innerComponent is >= 40200 and <= 40300 &&
            bottomComponent is >= 29350 and <= 29480 &&
            upperWater is >= 295600 and <= 295750 &&
            tankTop is >= 180 and <= 260 &&
            leftSpiralTop is > 0 and <= 650 && leftRouted >= 1000 &&
            leftMinimumHead <= tankTop + 4 && statistics.PressureMoves > 0 &&
            statistics.FarColumnMoves == 0;
        report = $"PHYXEL_K_SAVED_SPIRAL water={water} components={outerComponent}/{innerComponent}/{bottomComponent} upperWater={upperWater} bottomWater={bottomWater} tankTop={tankTop} leftTop={leftSpiralTop} leftRoute={leftRouted}/{(leftMinimumHead == float.MaxValue ? -1 : leftMinimumHead):0} outerRise={outerRise} firstBend={firstBend} innerRise={innerRise} moving={moving} pressureMoves={statistics.PressureMoves} pressurePlans={statistics.PressurePlans} farColumnMoves={statistics.FarColumnMoves}";
        return passed;
    }

    private static bool ValidateSavedGravity(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int lowerMetal = CountMaterial(
            grid, snapshot.Width, 0, snapshot.Width - 1, 850, snapshot.Height - 1, materials.Metal);
        int lowerStone = CountMaterial(
            grid, snapshot.Width, 0, snapshot.Width - 1, 850, snapshot.Height - 1, materials.Stone);
        int lowerSolids = lowerMetal + lowerStone;
        Dictionary<uint, (int Count, int Top, int Bottom)> bodies = [];
        int water = 0;
        int waterTop = snapshot.Height;
        int movingSolids = 0;
        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            int y = index / snapshot.Width;
            if (IsMaterial(cell, materials.Water))
            {
                water++;
                waterTop = Math.Min(waterTop, y);
            }
            bool solidMaterial = cell.MaterialIndex == materials.Metal ||
                cell.MaterialIndex == materials.Stone;
            if (cell.IsActive != 0 && solidMaterial &&
                cell.RestFrames < 2)
            {
                movingSolids++;
            }
            if (cell.IsActive != 0 && cell.BodyId != 0 && solidMaterial)
            {
                bodies.TryGetValue(cell.BodyId, out (int Count, int Top, int Bottom) body);
                body.Count++;
                body.Top = body.Count == 1 ? y : Math.Min(body.Top, y);
                body.Bottom = Math.Max(body.Bottom, y);
                bodies[cell.BodyId] = body;
            }
        }
        (int Count, int Top, int Bottom) largestBody = default;
        foreach ((int Count, int Top, int Bottom) body in bodies.Values)
        {
            if (body.Count > largestBody.Count)
            {
                largestBody = body;
            }
        }
        int draft = waterTop < snapshot.Height ? largestBody.Bottom - waterTop : 0;
        bool floating = water > 1000 && largestBody.Count > 1000 && draft >= 20;
        bool passed = (lowerSolids > 1000 || floating) && movingSolids == 0;
        report = $"PHYXEL_L_SAVED_GRAVITY lowerSolids={lowerSolids} body={largestBody.Count} bounds={largestBody.Top}-{largestBody.Bottom} water={water} waterTop={waterTop} draft={draft} movingSolids={movingSolids} statsFrame={statistics.FrameIndex} statsMoving={statistics.MovingCells} statsMovingSolids={statistics.MovingSolidCells} pressureMoves={statistics.PressureMoves} fps={framesPerSecond:0.0}";
        return passed;
    }

    private static bool ValidateBuoyancy(
        SimulationWorldSnapshot snapshot,
        double framesPerSecond,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int waterTop = SurfaceTop(grid, snapshot.Width, 20, 45, 130, 250);
        int closedBottom = MaterialBottom(grid, snapshot.Width, 45, 195, 60, 250, materials.Metal);
        int openBottom = MaterialBottom(grid, snapshot.Width, 205, 340, 60, 250, materials.Metal);
        int blockBottom = MaterialBottom(grid, snapshot.Width, 365, 435, 60, 250, materials.Metal);
        int closedMetal = CountMaterial(
            grid, snapshot.Width, 45, 195, 60, 250, materials.Metal);
        int openMetal = CountMaterial(
            grid, snapshot.Width, 205, 340, 60, 250, materials.Metal);
        int loadedSand = CountMaterial(
            grid, snapshot.Width, 205, 340, 60, 250, materials.Sand);
        int sunkBlock = CountMaterial(
            grid, snapshot.Width, 365, 435, waterTop + 3, 250, materials.Metal);
        int closedWater = CountMaterial(
            grid, snapshot.Width, 70, 170, Math.Max(0, waterTop - 80), closedBottom - 10, materials.Water);
        int openWater = CountMaterial(
            grid, snapshot.Width, 230, 315, Math.Max(0, waterTop - 80), openBottom - 10, materials.Water);
        int deepHull = CountMaterial(
            grid, snapshot.Width, 45, 340, waterTop + 64, 250, materials.Metal);
        int totalWater = 0;
        int movingSolids = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive != 0 && cell.MaterialIndex == materials.Water)
            {
                totalWater++;
            }
            if (cell.IsActive != 0 && cell.MaterialIndex == materials.Metal &&
                cell.RestFrames < 2)
            {
                movingSolids++;
            }
        }
        int closedDraft = closedBottom - waterTop;
        int loadedDraft = openBottom - waterTop;
        bool passed = waterTop > 0 && closedMetal > 2400 && openMetal > 1300 &&
            loadedSand > 2500 && sunkBlock > 1000 &&
            closedDraft is >= 15 and <= 35 &&
            loadedDraft >= closedDraft + 10 && loadedDraft <= 64 &&
            blockBottom >= 245 && closedWater == 0 && openWater == 0 && deepHull == 0 &&
            totalWater is >= 32300 and <= 32450 && movingSolids == 0;
        report = $"PHYXEL_M_BUOYANCY waterTop={waterTop} drafts={closedDraft}/{loadedDraft} bottoms={closedBottom}/{openBottom}/{blockBottom} metal={closedMetal}/{openMetal} sandLoad={loadedSand} sunkBlock={sunkBlock} closedWater={closedWater} openWater={openWater} deepHull={deepHull} water={totalWater} movingSolids={movingSolids} fps={framesPerSecond:0.0}";
        return passed;
    }

    private static bool ValidateSavedSandWater(
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int surfaceTop = SurfaceTop(grid, snapshot.Width, 300, 1600, 550, 1000);
        int water = 0;
        int fringeWater = 0;
        int fringeAdjacentSand = 0;
        int movingFringe = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                int index = y * snapshot.Width + x;
                GridCell cell = grid[index];
                if (cell.IsActive == 0 || cell.MaterialIndex != materials.Water)
                {
                    continue;
                }
                water++;
                if (surfaceTop < 0 || y + 10 >= surfaceTop)
                {
                    continue;
                }
                fringeWater++;
                movingFringe += cell.RestFrames < 60 ? 1 : 0;
                bool adjacentSand =
                    (x > 0 && IsMaterial(grid[index - 1], materials.Sand)) ||
                    (x + 1 < snapshot.Width && IsMaterial(grid[index + 1], materials.Sand)) ||
                    (y > 0 && IsMaterial(grid[index - snapshot.Width], materials.Sand)) ||
                    (y + 1 < snapshot.Height && IsMaterial(grid[index + snapshot.Width], materials.Sand));
                fringeAdjacentSand += adjacentSand ? 1 : 0;
            }
        }
        bool passed = surfaceTop > 0 && water is >= 192300 and <= 192360 &&
            fringeWater <= 100 && fringeAdjacentSand <= 80 && movingFringe <= 40;
        report = $"PHYXEL_N_SAVED_SAND_WATER surface={surfaceTop} water={water} fringe={fringeWater} adjacentSand={fringeAdjacentSand} movingFringe={movingFringe}";
        return passed;
    }

    private static bool ValidateBowl(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int metal = 0;
        int water = 0;
        int sand = 0;
        int leakedWater = 0;
        int movingWater = 0;
        int movingSand = 0;
        double waterY = 0;
        double sandY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                uint material = cell.MaterialIndex;
                if (material == materials.Metal) metal++;
                if (material == materials.Water)
                {
                    water++;
                    waterY += y;
                    leakedWater += x < 108 || x > 331 || y > 231 ? 1 : 0;
                    movingWater += Speed(cell) > 0.02f ? 1 : 0;
                }
                if (material == materials.Sand)
                {
                    sand++;
                    sandY += y;
                    movingSand += Speed(cell) > 0.02f ? 1 : 0;
                }
            }
        }
        int wallGaps = 0;
        for (int y = 115; y <= 229; y++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, 108, 120, y, materials.Metal) ? 0 : 1;
            wallGaps += HasMaterial(grid, snapshot.Width, 319, 331, y, materials.Metal) ? 0 : 1;
        }
        for (int x = 110; x <= 329; x++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, x, x, 220, 230, materials.Metal) ? 0 : 1;
        }
        string waterImage = Path.Combine(artifactDirectory, "A_water_2s.png");
        string finalImage = Path.Combine(artifactDirectory, "A_water_sand.png");
        WaterVisualMetrics waterVisual = AnalyzeWater(waterImage, 121, 318, 120, 218);
        ColorMetrics colors = AnalyzeColors(finalImage);
        bool passed = metal > 3500 && water > 1000 && sand > 5000 && leakedWater == 0 && wallGaps == 0 &&
            movingWater == 0 && movingSand == 0 && sandY / sand > waterY / water &&
            waterVisual.Columns > 190 && waterVisual.Gaps == 0 && waterVisual.SurfaceRange <= 2 &&
            File.Exists(waterImage) && colors.Red == 0 && colors.Blue > 500 && colors.Yellow > 500 && colors.Metal > 500;
        report = $"PHYXEL_A bowlMetal={metal} water={water} sand={sand} leaked={leakedWater} gaps={wallGaps} movingWater={movingWater} movingSand={movingSand} waterY={waterY / Math.Max(1, water):0.0} sandY={sandY / Math.Max(1, sand):0.0} imageColumns={waterVisual.Columns} imageGaps={waterVisual.Gaps} surfaceRange={waterVisual.SurfaceRange} red={colors.Red}";
        return passed;
    }

    private static bool ValidateSolidGravity(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        GridCell[] componentCells = grid.ToArray();
        ComponentMetrics metal = Components(componentCells, snapshot.Width, snapshot.Height, materials.Metal);
        ComponentMetrics stone = Components(componentCells, snapshot.Width, snapshot.Height, materials.Stone);
        string offImage = Path.Combine(artifactDirectory, "B_gravity_off.png");
        string fallingImage = Path.Combine(artifactDirectory, "B_falling.png");
        string landedImage = Path.Combine(artifactDirectory, "B_landed.png");
        string splitImage = Path.Combine(artifactDirectory, "B_split_stone.png");
        int suspendedMetal = CountColor(offImage, 50, 15, 120, 85, IsMetal);
        int suspendedStone = CountColor(offImage, 205, 45, 425, 75, IsStone);
        ColorMetrics colors = AnalyzeColors(splitImage);
        int squareCells = CountMaterial(grid, snapshot.Width, 50, 120, 180, 245, materials.Metal);
        int supportedFragment = CountMaterial(grid, snapshot.Width, 125, 165, 90, 115, materials.Metal);
        int floorFragment = CountMaterial(grid, snapshot.Width, 170, 210, 225, 245, materials.Metal);
        int supportedStone = CountMaterial(grid, snapshot.Width, 210, 310, 155, 185, materials.Stone);
        int floorStone = CountMaterial(grid, snapshot.Width, 320, 410, 225, 245, materials.Stone);
        int landedWholeStone = CountColor(landedImage, 205, 155, 425, 185, IsStone);
        int water = CountMaterial(grid, snapshot.Width, 0, snapshot.Width - 1, 0, snapshot.Height - 1, materials.Water);
        int sand = CountMaterial(grid, snapshot.Width, 0, snapshot.Width - 1, 0, snapshot.Height - 1, materials.Sand);
        bool metalWhole = metal.SignificantCount == 3 &&
            metal.Largest > 1800 && squareCells > 1800 &&
            supportedFragment > 150 && floorFragment > 150;
        bool stoneSplit = stone.SignificantCount == 2 && stone.Largest > 700 &&
            supportedStone > 650 && floorStone > 500 && landedWholeStone > 1400 &&
            stone.MinimumY <= 170 && stone.MaximumY >= 240;
        bool passed = metalWhole && stoneSplit && water > 500 && sand > 300 &&
            suspendedMetal > 1500 && suspendedStone > 1000 &&
            File.Exists(fallingImage) && File.Exists(splitImage) && colors.Red == 0;
        report = $"PHYXEL_B metalComponents={metal.Count}/{metal.SignificantCount} " +
            $"largestMetal={metal.Largest} square={squareCells} " +
            $"splitLevels={supportedFragment}/{floorFragment} " +
            $"stoneComponents={stone.Count}/{stone.SignificantCount} stoneCells={stone.Largest} " +
            $"stoneLevels={supportedStone}/{floorStone} landedWhole={landedWholeStone} " +
            $"water={water} sand={sand} suspendedMetal={suspendedMetal} " +
            $"suspendedStone={suspendedStone} red={colors.Red}";
        return passed;
    }

    private static bool ValidateSand(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int[] surface = new int[snapshot.Width];
        Array.Fill(surface, -1);
        int sand = 0;
        int resting = 0;
        int moving = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != materials.Sand)
                {
                    continue;
                }
                sand++;
                surface[x] = surface[x] < 0 ? y : surface[x];
                resting += cell.RestFrames >= 30 ? 1 : 0;
                moving += Speed(cell) > 0.02f ? 1 : 0;
            }
        }
        int left = Array.FindIndex(surface, value => value >= 0);
        int right = Array.FindLastIndex(surface, value => value >= 0);
        int peakX = left;
        for (int x = Math.Max(0, left); x <= right; x++)
        {
            if (surface[x] >= 0 && surface[x] < surface[peakX]) peakX = x;
        }
        float leftAngle = Angle(surface, left, peakX);
        float rightAngle = Angle(surface, right, peakX);
        float roughness = 0;
        int samples = 0;
        int gaps = 0;
        for (int x = Math.Max(0, left + 1); x <= right; x++)
        {
            if (surface[x] < 0 || surface[x - 1] < 0)
            {
                gaps++;
                continue;
            }
            roughness += Math.Abs(surface[x] - surface[x - 1]);
            samples++;
        }
        roughness /= Math.Max(1, samples);
        ColorMetrics colors = AnalyzeColors(Path.Combine(artifactDirectory, "C_pile_3s.png"));
        bool passed = sand is >= 900 and <= 1100 && leftAngle is >= 30 and <= 45 &&
            rightAngle is >= 30 and <= 45 && roughness <= 1.5f && gaps == 0 &&
            resting == sand && moving == 0 && colors.Red == 0 && colors.Yellow > 500;
        report = $"PHYXEL_C sand={sand} resting={resting} moving={moving} width={right - left + 1} leftAngle={leftAngle:0.0} rightAngle={rightAngle:0.0} roughness={roughness:0.00} gaps={gaps} red={colors.Red}";
        return passed;
    }

    private static bool ValidateHydro(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 25, 128, 100, 245);
        int rightTop = SurfaceTop(grid, snapshot.Width, 148, 250, 100, 245);
        int waterfallTop = SurfaceTop(grid, snapshot.Width, 300, 455, 100, 245);
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        double leakedMass = 0;
        int minimumLeakY = snapshot.Height;
        int maximumLeakY = 0;
        int minimumLeakX = snapshot.Width;
        int maximumLeakX = 0;
        int wallGaps = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || cell.MaterialIndex != materials.Water)
            {
                continue;
            }
            water++;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialIndex == materials.Water &&
                    ((x < 10 || x > 470) || y > 249))
                {
                    leaks++;
                    leakedMass += cell.Mass;
                    minimumLeakY = Math.Min(minimumLeakY, y);
                    maximumLeakY = Math.Max(maximumLeakY, y);
                    minimumLeakX = Math.Min(minimumLeakX, x);
                    maximumLeakX = Math.Max(maximumLeakX, x);
                }
            }
        }
        for (int y = 95; y <= 254; y++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, 10, 20, y, materials.Metal) ? 0 : 1;
            wallGaps += HasMaterial(grid, snapshot.Width, 255, 265, y, materials.Metal) ? 0 : 1;
        }
        for (int x = 15; x <= 260; x++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, x, x, 245, 255, materials.Metal) ? 0 : 1;
        }
        string equalImage = Path.Combine(artifactDirectory, "D_equal_2s.png");
        string waterfallImage = Path.Combine(artifactDirectory, "D_waterfall.png");
        int imageLeft = ImageSurfaceTop(equalImage, 25, 128, 100, 245);
        int imageRight = ImageSurfaceTop(equalImage, 148, 250, 100, 245);
        WaterVisualMetrics leftVisual = AnalyzeWater(equalImage, 25, 128, 100, 245);
        WaterVisualMetrics rightVisual = AnalyzeWater(equalImage, 148, 250, 100, 245);
        int fallingWater = CountColor(waterfallImage, 330, 80, 415, 220, IsBlue);
        ColorMetrics colors = AnalyzeColors(Path.Combine(artifactDirectory, "D_rest.png"));
        bool passed = water > 5000 && Math.Abs(leftTop - rightTop) <= 3 &&
            imageLeft > 0 && imageRight > 0 && Math.Abs(imageLeft - imageRight) >= 4 &&
            waterfallTop > 0 &&
            leftVisual.Gaps == 0 && rightVisual.Gaps == 0 &&
            resting >= water * 0.99 && moving == 0 && leaks == 0 &&
            framesPerSecond >= 55 && colors.Red == 0 && colors.Blue > 500 &&
            fallingWater > 100;
        report = $"PHYXEL_D water={water} leftTop={leftTop} rightTop={rightTop} image2s={imageLeft}/{imageRight} waterfallTop={waterfallTop} fallingWater={fallingWater} resting={resting} moving={moving} leaks={leaks} leakMass={leakedMass:0.000} leakBounds={minimumLeakX},{minimumLeakY}-{maximumLeakX},{maximumLeakY} wallGaps={wallGaps} fps={framesPerSecond:0.0} statsMoving={statistics.MovingCells} red={colors.Red}";
        return passed;
    }

    private static ComponentMetrics Components(
        GridCell[] grid,
        int width,
        int height,
        uint material)
    {
        bool[] visited = new bool[grid.Length];
        int[] queue = new int[grid.Length];
        int components = 0;
        int significantComponents = 0;
        int largest = 0;
        int minX = width;
        int maxX = 0;
        int minY = height;
        int maxY = 0;
        for (int start = 0; start < grid.Length; start++)
        {
            if (visited[start] || grid[start].IsActive == 0 || grid[start].MaterialIndex != material)
            {
                continue;
            }
            components++;
            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            int size = 0;
            while (head < tail)
            {
                int index = queue[head++];
                int x = index % width;
                int y = index / width;
                size++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                Enqueue(grid, visited, queue, ref tail, index - 1, x > 0, material);
                Enqueue(grid, visited, queue, ref tail, index + 1, x + 1 < width, material);
                Enqueue(grid, visited, queue, ref tail, index - width, y > 0, material);
                Enqueue(grid, visited, queue, ref tail, index + width, y + 1 < height, material);
            }
            if (size >= 32)
            {
                significantComponents++;
            }
            largest = Math.Max(largest, size);
        }
        return new ComponentMetrics(
            components, significantComponents, largest, minX, maxX, minY, maxY);
    }

    private static int ConnectedMaterialSize(
        GridCell[] grid,
        int width,
        int height,
        int seedX,
        int seedY,
        uint material)
    {
        if (seedX < 0 || seedY < 0 || seedX >= width || seedY >= height)
        {
            return 0;
        }
        int seed = seedY * width + seedX;
        if (!IsMaterial(grid[seed], material))
        {
            return 0;
        }
        bool[] visited = new bool[grid.Length];
        int[] queue = new int[grid.Length];
        int head = 0;
        int tail = 0;
        int size = 0;
        visited[seed] = true;
        queue[tail++] = seed;
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            size++;
            Enqueue(grid, visited, queue, ref tail, index - 1, x > 0, material);
            Enqueue(grid, visited, queue, ref tail, index + 1, x + 1 < width, material);
            Enqueue(grid, visited, queue, ref tail, index - width, y > 0, material);
            Enqueue(grid, visited, queue, ref tail, index + width, y + 1 < height, material);
        }
        return size;
    }

    private static void Enqueue(
        GridCell[] grid,
        bool[] visited,
        int[] queue,
        ref int tail,
        int index,
        bool valid,
        uint material)
    {
        if (!valid || visited[index] || grid[index].IsActive == 0 || grid[index].MaterialIndex != material)
        {
            return;
        }
        visited[index] = true;
        queue[tail++] = index;
    }

    private static float Angle(int[] surface, int edge, int peak)
    {
        int horizontal = Math.Max(1, Math.Abs(peak - edge));
        int vertical = Math.Max(0, surface[edge] - surface[peak]);
        return MathF.Atan2(vertical, horizontal) * 180 / MathF.PI;
    }

    private static int SurfaceTop(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom)
    {
        List<int> tops = [];
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                GridCell cell = grid[y * width + x];
                if (cell.IsActive != 0 && cell.MaterialIndex == materials.Water)
                {
                    tops.Add(y);
                    break;
                }
            }
        }
        if (tops.Count == 0) return -1;
        tops.Sort();
        return tops[tops.Count / 2];
    }

    private static int MaterialBottom(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        uint material)
    {
        for (int y = bottom; y >= top; y--)
        {
            if (HasMaterial(grid, width, left, right, y, material))
            {
                return y;
            }
        }
        return -1;
    }

    private static int CurvedTubeSurfaceTop(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        int expectedCenter,
        out int filledWidth)
    {
        filledWidth = 0;
        for (int y = top; y <= bottom; y++)
        {
            List<(int Start, int End)> walls = [];
            int runStart = -1;
            for (int x = left; x <= right + 1; x++)
            {
                bool solid = x <= right && grid[y * width + x].IsActive != 0 &&
                    grid[y * width + x].MaterialIndex != materials.Water;
                if (solid && runStart < 0)
                {
                    runStart = x;
                }
                else if (!solid && runStart >= 0)
                {
                    if (x - runStart >= 4)
                    {
                        walls.Add((runStart, x - 1));
                    }
                    runStart = -1;
                }
            }
            int interiorLeft = -1;
            int interiorRight = -1;
            int bestDistance = int.MaxValue;
            for (int wall = 0; wall + 1 < walls.Count; wall++)
            {
                int gapLeft = walls[wall].End + 1;
                int gapRight = walls[wall + 1].Start - 1;
                int gapWidth = gapRight - gapLeft + 1;
                if (gapWidth is < 3 or > 45)
                {
                    continue;
                }
                int distance = Math.Abs((gapLeft + gapRight) / 2 - expectedCenter);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    interiorLeft = gapLeft;
                    interiorRight = gapRight;
                }
            }
            if (interiorLeft < 0)
            {
                continue;
            }
            int water = CountMaterial(
                grid,
                width,
                interiorLeft,
                interiorRight,
                y,
                y,
                materials.Water);
            int interiorWidth = interiorRight - interiorLeft + 1;
            if (water * 2 >= interiorWidth)
            {
                filledWidth = water;
                return y;
            }
        }
        return -1;
    }

    private static int ImageSurfaceTop(string path, int left, int right, int top, int bottom)
    {
        if (!File.Exists(path)) return -1000;
        using Bitmap bitmap = new(path);
        List<int> tops = [];
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (IsBlue(bitmap.GetPixel(x, y)))
                {
                    tops.Add(y);
                    break;
                }
            }
        }
        if (tops.Count == 0) return -1000;
        tops.Sort();
        return tops[tops.Count / 2];
    }

    private static ColorMetrics AnalyzeColors(string path)
    {
        if (!File.Exists(path)) return default;
        using Bitmap bitmap = new(path);
        int red = 0;
        int blue = 0;
        int yellow = 0;
        int metal = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                red += color.R > 120 && color.R > color.G * 1.35 && color.R > color.B * 1.35 ? 1 : 0;
                blue += IsBlue(color) ? 1 : 0;
                yellow += IsYellow(color) ? 1 : 0;
                metal += IsMetal(color) ? 1 : 0;
            }
        }
        return new ColorMetrics(red, blue, yellow, metal);
    }

    private static WaterVisualMetrics AnalyzeWater(
        string path,
        int left,
        int right,
        int top,
        int bottom)
    {
        if (!File.Exists(path)) return default;
        using Bitmap bitmap = new(path);
        int columns = 0;
        int gaps = 0;
        int minimumTop = bottom;
        int maximumTop = top;
        for (int x = left; x <= right; x++)
        {
            int first = -1;
            int last = -1;
            for (int y = top; y <= bottom; y++)
            {
                if (!IsBlue(bitmap.GetPixel(x, y))) continue;
                first = first < 0 ? y : first;
                last = y;
            }
            if (first < 0) continue;
            columns++;
            minimumTop = Math.Min(minimumTop, first);
            maximumTop = Math.Max(maximumTop, first);
            for (int y = first; y <= last; y++)
            {
                gaps += IsBlue(bitmap.GetPixel(x, y)) ? 0 : 1;
            }
        }
        return new WaterVisualMetrics(columns, gaps, maximumTop - minimumTop);
    }

    private static int CountColor(
        string path,
        int left,
        int top,
        int right,
        int bottom,
        Func<Color, bool> predicate)
    {
        if (!File.Exists(path)) return 0;
        using Bitmap bitmap = new(path);
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                count += predicate(bitmap.GetPixel(x, y)) ? 1 : 0;
            }
        }
        return count;
    }

    private static int CountVerticalColorRuns(
        string path,
        int left,
        int top,
        int right,
        int bottom,
        int minimumLength,
        Func<Color, bool> predicate)
    {
        if (!File.Exists(path)) return 0;
        using Bitmap bitmap = new(path);
        int runs = 0;
        for (int x = left; x <= right; x++)
        {
            int length = 0;
            for (int y = top; y <= bottom; y++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    length++;
                    continue;
                }
                runs += length >= minimumLength ? 1 : 0;
                length = 0;
            }
            runs += length >= minimumLength ? 1 : 0;
        }
        return runs;
    }

    private static bool HasMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int y,
        uint material)
    {
        for (int x = left; x <= right; x++)
        {
            GridCell cell = grid[y * width + x];
            if (cell.IsActive != 0 && cell.MaterialIndex == material) return true;
        }
        return false;
    }

    private static bool IsMaterial(GridCell cell, uint material) =>
        cell.IsActive != 0 && cell.MaterialIndex == material;

    private static int CountMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        uint material)
    {
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = grid[y * width + x];
                count += cell.IsActive != 0 && cell.MaterialIndex == material ? 1 : 0;
            }
        }
        return count;
    }

    private static bool HasMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int x,
        int ignored,
        int top,
        int bottom,
        uint material)
    {
        for (int y = top; y <= bottom; y++)
        {
            GridCell cell = grid[y * width + x];
            if (cell.IsActive != 0 && cell.MaterialIndex == material) return true;
        }
        return false;
    }

    private static ReadOnlySpan<GridCell> Cells(SimulationWorldSnapshot snapshot)
    {
        return MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
    }

    private static float Speed(GridCell cell)
    {
        return Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY);
    }

    private static bool IsBlue(Color color) => color.B > color.G + 35 && color.G > color.R + 35;
    private static bool IsYellow(Color color) => color.R > 160 && color.G > 130 && color.B < 130;
    private static bool IsMetal(Color color) => color.R is >= 125 and <= 160 && color.G is >= 140 and <= 175 && color.B is >= 145 and <= 185;
    private static bool IsStone(Color color) => color.R is >= 75 and <= 110 && color.G is >= 80 and <= 115 && color.B is >= 85 and <= 125;
    private static bool Fail(out string report)
    {
        report = "PHYXEL_ACCEPTANCE_MODE_MISSING";
        return false;
    }

    private readonly record struct ColorMetrics(int Red, int Blue, int Yellow, int Metal);
    private readonly record struct WaterVisualMetrics(int Columns, int Gaps, int SurfaceRange);
}
