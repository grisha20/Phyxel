using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class ContinuousBrushStrokeAcceptanceVerifier
{
    public static bool Validate(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        out string report)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        List<string> errors = [];
        VerifyMaterialLane(cells, snapshot.Width, ContinuousBrushStrokeAcceptanceScenario.Co2Y,
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2), "CO2", errors);
        VerifyMaterialLane(cells, snapshot.Width, ContinuousBrushStrokeAcceptanceScenario.SteamY,
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam), "steam", errors);
        VerifyMaterialLane(cells, snapshot.Width, ContinuousBrushStrokeAcceptanceScenario.SandY,
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand), "sand", errors);

        int temperatureCells = 0;
        int erasedCells = 0;
        for (int x = ContinuousBrushStrokeAcceptanceScenario.StartX;
            x <= ContinuousBrushStrokeAcceptanceScenario.EndX;
            x++)
        {
            GridCell heated = cells[
                ContinuousBrushStrokeAcceptanceScenario.TemperatureY * snapshot.Width + x];
            if (heated.IsActive != 0 &&
                heated.MaterialIndex == materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water) &&
                Math.Abs(heated.Temperature -
                    ContinuousBrushStrokeAcceptanceScenario.TargetTemperature) <= 0.001f)
            {
                temperatureCells++;
            }
            else
            {
                AddError($"temperature gap at x={x}: {Describe(heated)}", errors);
            }

            GridCell erased = cells[
                ContinuousBrushStrokeAcceptanceScenario.EraserY * snapshot.Width + x];
            if (erased.IsActive == 0 && erased.MaterialIndex == 0)
            {
                erasedCells++;
            }
            else
            {
                AddError($"eraser gap at x={x}: {Describe(erased)}", errors);
            }
        }

        int expected = ContinuousBrushStrokeAcceptanceScenario.EndX -
            ContinuousBrushStrokeAcceptanceScenario.StartX + 1;
        report = $"PHYXEL_CONTINUOUS_BRUSH expected={expected} " +
            $"temperature={temperatureCells} eraser={erasedCells}";
        if (errors.Count == 0)
        {
            return true;
        }

        report += Environment.NewLine + "PHYXEL_CONTINUOUS_BRUSH_FAILURE " +
            string.Join("; ", errors);
        return false;
    }

    private static void VerifyMaterialLane(
        ReadOnlySpan<GridCell> cells,
        int width,
        int y,
        uint expectedMaterial,
        string label,
        List<string> errors)
    {
        for (int x = ContinuousBrushStrokeAcceptanceScenario.StartX;
            x <= ContinuousBrushStrokeAcceptanceScenario.EndX;
            x++)
        {
            GridCell cell = cells[y * width + x];
            if (cell.IsActive == 0 || cell.MaterialIndex != expectedMaterial)
            {
                AddError($"{label} gap at x={x}: {Describe(cell)}", errors);
            }
        }
    }

    private static void AddError(string error, List<string> errors)
    {
        if (errors.Count < 12)
        {
            errors.Add(error);
        }
    }

    private static string Describe(GridCell cell) =>
        $"{cell.MaterialIndex}/{cell.Mass:R}/{cell.IsActive}/{cell.Temperature:R}";
}
