using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class BrushEmptyOnlyAcceptanceVerifier
{
    public static bool Validate(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        out string report)
    {
        SimulationWorldSnapshot initial = BrushEmptyOnlyAcceptanceScenario.CreateInitialWorld(
            AcceptanceScenarioMode.BrushEmptyOnly, snapshot.Width, snapshot.Height, materials) ??
            throw new InvalidOperationException("Missing brush_empty_only fixture.");
        ReadOnlySpan<GridCell> before = MemoryMarshal.Cast<byte, GridCell>(initial.Grid);
        ReadOnlySpan<GridCell> after = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        List<string> errors = [];
        int preservedCells = 0;
        double initialMass = 0;
        double finalMass = 0;
        int ignitedWood = 0;
        uint wood = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Wood);
        float ignitionTemperature = materials[CoreMaterialIds.Wood].Properties.IgnitionTemperature + 1;

        foreach ((string id, int centerX, int centerY) in BrushEmptyOnlyAcceptanceScenario.PreservationBlocks)
        {
            for (int y = centerY - BrushEmptyOnlyAcceptanceScenario.BlockHalfSize;
                y <= centerY + BrushEmptyOnlyAcceptanceScenario.BlockHalfSize;
                y++)
            {
                for (int x = centerX - BrushEmptyOnlyAcceptanceScenario.BlockHalfSize;
                    x <= centerX + BrushEmptyOnlyAcceptanceScenario.BlockHalfSize;
                    x++)
                {
                    int index = y * snapshot.Width + x;
                    GridCell expected = before[index];
                    GridCell actual = after[index];
                    if (expected.MaterialIndex == wood)
                    {
                        expected.Temperature = Math.Max(expected.Temperature, ignitionTemperature);
                        if (SameBits(actual.Temperature, expected.Temperature))
                        {
                            ignitedWood++;
                        }
                    }
                    Require(CellEquals(expected, actual),
                        $"occupied {id} changed at {x},{y}: expected={Describe(expected)} actual={Describe(actual)}",
                        errors);
                    initialMass += before[index].Mass;
                    finalMass += actual.Mass;
                    preservedCells++;
                }
            }
        }

        double largeSandInitialMass = 0;
        double largeSandFinalMass = 0;
        int largeSandCells = 0;
        for (int y = BrushEmptyOnlyAcceptanceScenario.LargeSandTop;
            y <= BrushEmptyOnlyAcceptanceScenario.LargeSandBottom;
            y++)
        {
            for (int x = BrushEmptyOnlyAcceptanceScenario.LargeSandLeft;
                x <= BrushEmptyOnlyAcceptanceScenario.LargeSandRight;
                x++)
            {
                int index = y * snapshot.Width + x;
                Require(CellEquals(before[index], after[index]),
                    $"sand field changed under gas/steam/fire brushes at {x},{y}", errors);
                largeSandInitialMass += before[index].Mass;
                largeSandFinalMass += after[index].Mass;
                largeSandCells++;
            }
        }

        GridCell erased = after[BrushEmptyOnlyAcceptanceScenario.EraserY * snapshot.Width +
            BrushEmptyOnlyAcceptanceScenario.EraserX];
        Require(erased.IsActive == 0 && erased.MaterialIndex == 0,
            $"eraser did not clear its probe: {Describe(erased)}", errors);

        int temperatureIndex = BrushEmptyOnlyAcceptanceScenario.TemperatureY * snapshot.Width +
            BrushEmptyOnlyAcceptanceScenario.TemperatureX;
        GridCell temperatureExpected = before[temperatureIndex];
        temperatureExpected.Temperature = 500;
        Require(CellEquals(temperatureExpected, after[temperatureIndex]),
            $"temperature tool changed non-temperature fields: expected={Describe(temperatureExpected)} " +
            $"actual={Describe(after[temperatureIndex])}", errors);

        GridCell emptyDraw = after[BrushEmptyOnlyAcceptanceScenario.EmptyDrawY * snapshot.Width +
            BrushEmptyOnlyAcceptanceScenario.EmptyDrawX];
        Require(emptyDraw.IsActive != 0 &&
            emptyDraw.MaterialIndex == materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            $"material brush did not create water in empty: {Describe(emptyDraw)}", errors);

        GridCell overlap = after[BrushEmptyOnlyAcceptanceScenario.OverlapY * snapshot.Width +
            BrushEmptyOnlyAcceptanceScenario.OverlapX];
        Require(overlap.IsActive != 0 &&
            overlap.MaterialIndex == materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2),
            $"overlapping commands were not deterministic first-writer-wins: {Describe(overlap)}", errors);
        Require(ignitedWood == 13 * 13,
            $"flame brush did not ignite wood in place expected=169 actual={ignitedWood}", errors);
        Require(Math.Abs(initialMass - finalMass) <= 0.0001,
            $"occupied-region mass changed before={initialMass:F4} after={finalMass:F4}", errors);
        Require(Math.Abs(largeSandInitialMass - largeSandFinalMass) <= 0.0001,
            $"large sand mass changed before={largeSandInitialMass:F4} after={largeSandFinalMass:F4}", errors);

        report = $"PHYXEL_BRUSH_EMPTY_ONLY preservedCells={preservedCells} " +
            $"initialMass={initialMass:F3} finalMass={finalMass:F3} " +
            $"sandCells={largeSandCells} sandMass={largeSandInitialMass:F3}/{largeSandFinalMass:F3} " +
            $"ignitedWood={ignitedWood} emptyDraw={materials[emptyDraw.MaterialIndex].Id} " +
            $"overlap={materials[overlap.MaterialIndex].Id}";
        if (errors.Count == 0)
        {
            return true;
        }
        report += Environment.NewLine + "PHYXEL_BRUSH_EMPTY_ONLY_FAILURE " +
            string.Join("; ", errors.GetRange(0, Math.Min(errors.Count, 12)));
        return false;
    }

    private static bool CellEquals(GridCell left, GridCell right) =>
        left.MaterialIndex == right.MaterialIndex &&
        SameBits(left.Mass, right.Mass) &&
        SameBits(left.VelocityX, right.VelocityX) &&
        SameBits(left.VelocityY, right.VelocityY) &&
        SameBits(left.Pressure, right.Pressure) &&
        left.IsActive == right.IsActive &&
        left.BodyId == right.BodyId &&
        left.RestFrames == right.RestFrames &&
        SameBits(left.Temperature, right.Temperature) &&
        SameBits(left.Lifetime, right.Lifetime);

    private static bool SameBits(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static string Describe(GridCell cell) =>
        $"{cell.MaterialIndex}/{cell.Mass:R}/{cell.VelocityX:R}/{cell.VelocityY:R}/" +
        $"{cell.Pressure:R}/{cell.IsActive}/{cell.BodyId}/{cell.RestFrames}/" +
        $"{cell.Temperature:R}/{cell.Lifetime:R}";

    private static void Require(bool condition, string error, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(error);
        }
    }
}
