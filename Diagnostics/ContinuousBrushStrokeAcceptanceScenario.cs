using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class ContinuousBrushStrokeAcceptanceScenario
{
    internal const int StartX = 30;
    internal const int EndX = 450;
    internal const int Co2Y = 40;
    internal const int SteamY = 80;
    internal const int SandY = 120;
    internal const int TemperatureY = 170;
    internal const int EraserY = 220;
    internal const float TargetTemperature = 500;
    private const float Radius = 4;

    public static SimulationWorldSnapshot? CreateInitialWorld(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.ContinuousBrushStroke)
        {
            return null;
        }
        if (width < 480 || height < 250)
        {
            throw new InvalidOperationException(
                "continuous_brush_stroke requires at least 480x250 cells.");
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        FillLane(cells, width, TemperatureY, materials[CoreMaterialIds.Water]);
        FillLane(cells, width, EraserY, materials[CoreMaterialIds.Sand]);
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        uint frame,
        AcceptanceMaterialIndices materials)
    {
        if (frame != 0)
        {
            return [];
        }

        return
        [
            CreateSegment(Co2Y, materials.Gas, BrushCommandMode.Material, 1001, 0),
            CreateSegment(SteamY, materials.Resolve(CoreMaterialIds.Steam),
                BrushCommandMode.Material, 1002, 0),
            CreateSegment(SandY, materials.Sand, BrushCommandMode.Material, 1003, 0),
            CreateSegment(TemperatureY, materials.Water,
                BrushCommandMode.SetTemperature, 1004, TargetTemperature),
            CreateSegment(EraserY, materials.Eraser, BrushCommandMode.Erase, 1005, 0)
        ];
    }

    private static void FillLane(
        Span<GridCell> cells,
        int width,
        int centerY,
        MaterialDefinition material)
    {
        for (int y = centerY - 6; y <= centerY + 6; y++)
        {
            for (int x = StartX - 6; x <= EndX + 6; x++)
            {
                cells[y * width + x] = new GridCell
                {
                    MaterialIndex = material.RuntimeIndex,
                    Mass = 1,
                    IsActive = 1,
                    Temperature = material.Properties.InitialTemperature
                };
            }
        }
    }

    private static BrushDrawCommand CreateSegment(
        int y,
        uint material,
        BrushCommandMode mode,
        uint seed,
        float targetTemperature) =>
        new()
        {
            X = StartX,
            Y = y,
            EndX = EndX,
            EndY = y,
            Shape = BrushCommandShape.Segment,
            Radius = Radius,
            MaterialIndex = material,
            Density = mode == BrushCommandMode.Material ? 0.25f : 1,
            Mode = mode,
            Seed = seed,
            TargetTemperature = targetTemperature
        };
}
