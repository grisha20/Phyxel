using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class AcceptanceRegressionVerifier
{
    private static AcceptanceMaterialIndices materials = null!;

    private readonly record struct ComponentMetrics(
        int Count,
        int Largest,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY);

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
                out report),
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
                out report),
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
            AcceptanceScenarioMode.TemperatureProbeGpu => ThermalAcceptanceVerifier.Validate(
                mode,
                materialRegistry,
                snapshot,
                thermalTicks,
                temperatureProbe,
                thermalCheckpoints,
                temperatureProbeTrace,
                out report),
            _ => Fail(out report)
        };
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
        bool metalWhole = metal.Count == 3 && metal.Largest > 1800 && squareCells > 1800 &&
            supportedFragment > 150 && floorFragment > 150;
        bool stoneSplit = stone.Count == 2 && stone.Largest > 700 &&
            supportedStone > 650 && floorStone > 500 && landedWholeStone > 1400 &&
            stone.MinimumY <= 170 && stone.MaximumY >= 240;
        bool passed = metalWhole && stoneSplit && water > 500 && sand > 300 &&
            suspendedMetal > 1500 && suspendedStone > 1000 &&
            File.Exists(fallingImage) && File.Exists(splitImage) && colors.Red == 0;
        report = $"PHYXEL_B metalComponents={metal.Count} largestMetal={metal.Largest} square={squareCells} splitLevels={supportedFragment}/{floorFragment} stoneComponents={stone.Count} stoneCells={stone.Largest} stoneLevels={supportedStone}/{floorStone} landedWhole={landedWholeStone} water={water} sand={sand} suspendedMetal={suspendedMetal} suspendedStone={suspendedStone} red={colors.Red}";
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
            largest = Math.Max(largest, size);
        }
        return new ComponentMetrics(components, largest, minX, maxX, minY, maxY);
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
