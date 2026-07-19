using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class BrushEmptyOnlyAcceptanceScenario
{
    internal const int BlockHalfSize = 6;
    internal const int LargeSandLeft = 70;
    internal const int LargeSandTop = 135;
    internal const int LargeSandRight = 180;
    internal const int LargeSandBottom = 175;
    internal const int EraserX = 260;
    internal const int EraserY = 220;
    internal const int TemperatureX = 300;
    internal const int TemperatureY = 220;
    internal const int OverlapX = 350;
    internal const int OverlapY = 220;
    internal const int EmptyDrawX = 420;
    internal const int EmptyDrawY = 220;

    internal static readonly (string Id, int X, int Y)[] PreservationBlocks =
    [
        (CoreMaterialIds.Sand, 45, 70),
        (CoreMaterialIds.Water, 110, 70),
        (CoreMaterialIds.Gas, 175, 70),
        (CoreMaterialIds.Steam, 240, 70),
        (CoreMaterialIds.Fixture, 305, 70),
        (CoreMaterialIds.Wood, 370, 70),
        (CoreMaterialIds.Coal, 435, 70)
    ];

    public static SimulationWorldSnapshot? CreateInitialWorld(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.BrushEmptyOnly)
        {
            return null;
        }
        if (width < 460 || height < 240)
        {
            throw new InvalidOperationException("brush_empty_only requires at least 460x240 cells.");
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        for (int blockIndex = 0; blockIndex < PreservationBlocks.Length; blockIndex++)
        {
            (string id, int centerX, int centerY) = PreservationBlocks[blockIndex];
            FillBlock(cells, width, centerX, centerY,
                materials.GetRequiredRuntimeIndex(id), blockIndex, id == CoreMaterialIds.Wood ? 20 : 30 + blockIndex);
        }

        uint sand = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        for (int y = LargeSandTop; y <= LargeSandBottom; y++)
        {
            for (int x = LargeSandLeft; x <= LargeSandRight; x++)
            {
                cells[y * width + x] = CreateProbeCell(sand, x, y, 17, 42);
            }
        }

        cells[EraserY * width + EraserX] = CreateProbeCell(sand, EraserX, EraserY, 31, 60);
        cells[TemperatureY * width + TemperatureX] = CreateProbeCell(
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water), TemperatureX, TemperatureY, 32, 61);
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        uint frame,
        AcceptanceMaterialIndices materials)
    {
        if (frame == 1)
        {
            uint[] brushMaterials =
            [
                materials.Sand,
                materials.Water,
                materials.Gas,
                materials.Resolve(CoreMaterialIds.Steam),
                materials.Fire,
                materials.Wood,
                materials.Coal,
                materials.Resolve(CoreMaterialIds.StoneCoal),
                materials.Stone
            ];
            List<BrushDrawCommand> commands = [];
            uint seed = 1000;
            foreach ((_, int x, int y) in PreservationBlocks)
            {
                foreach (uint material in brushMaterials)
                {
                    commands.Add(CreateMaterial(x, y, 10, material, seed++));
                }
            }
            return commands;
        }
        if (frame is 2 or 3 or 4)
        {
            uint material = frame switch
            {
                2 => materials.Gas,
                3 => materials.Resolve(CoreMaterialIds.Steam),
                _ => materials.Fire
            };
            List<BrushDrawCommand> commands = [];
            for (int x = 80; x <= 170; x += 18)
            {
                commands.Add(CreateMaterial(x, 155, 22, material, frame * 1000u + (uint)x));
            }
            return commands;
        }
        if (frame == 5)
        {
            return
            [
                CreateMaterial(EmptyDrawX, EmptyDrawY, 3, materials.Water, 5001),
                CreateMaterial(OverlapX, OverlapY, 3, materials.Gas, 5002),
                CreateMaterial(OverlapX, OverlapY, 3,
                    materials.Resolve(CoreMaterialIds.Steam), 5003),
                Create(EraserX, EraserY, 2, materials.Eraser, BrushCommandMode.Erase, 5004, 0),
                Create(TemperatureX, TemperatureY, 2, materials.Water,
                    BrushCommandMode.SetTemperature, 5005, 500)
            ];
        }
        return [];
    }

    private static void FillBlock(
        Span<GridCell> cells,
        int width,
        int centerX,
        int centerY,
        uint material,
        int ordinal,
        float temperature)
    {
        for (int y = centerY - BlockHalfSize; y <= centerY + BlockHalfSize; y++)
        {
            for (int x = centerX - BlockHalfSize; x <= centerX + BlockHalfSize; x++)
            {
                cells[y * width + x] = CreateProbeCell(material, x, y, ordinal, temperature);
            }
        }
    }

    private static GridCell CreateProbeCell(uint material, int x, int y, int ordinal, float temperature) =>
        new()
        {
            MaterialIndex = material,
            Mass = 0.55f + ((x + y + ordinal) & 3) * 0.1f,
            VelocityX = ordinal + 0.25f,
            VelocityY = -ordinal - 0.5f,
            Pressure = ordinal + 1.75f,
            IsActive = 1,
            BodyId = (uint)(7000 + ordinal),
            RestFrames = (uint)(11 + ordinal),
            Temperature = temperature,
            Lifetime = ordinal + 0.875f
        };

    private static BrushDrawCommand CreateMaterial(
        int x,
        int y,
        float radius,
        uint material,
        uint seed) =>
        Create(x, y, radius, material, BrushCommandMode.Material, seed, 0);

    private static BrushDrawCommand Create(
        int x,
        int y,
        float radius,
        uint material,
        BrushCommandMode mode,
        uint seed,
        float targetTemperature) =>
        new()
        {
            X = x,
            Y = y,
            Radius = radius,
            MaterialIndex = material,
            Density = 1,
            Mode = mode,
            Seed = seed,
            TargetTemperature = targetTemperature
        };
}
