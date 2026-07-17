using System;
using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public enum AcceptanceScenarioMode
{
    None,
    Bowl,
    SolidGravity,
    Sand,
    Hydro,
    Slope,
    Gas,
    WaterStress,
    FlatSurface,
    WaterDrain,
    CommunicatingVessels,
    PressureTube,
    SavedPressure,
    SavedIsolation,
    SavedGravity,
    Buoyancy,
    SavedSandWater,
    ExternalGranular,
    ExternalLiquid,
    ExternalGas,
    ExternalSolids,
    UnderwaterGranularPile,
    GranularWaterDisplacement,
    GranularBarrier,
    GranularBarrierHydraulic,
    TemperatureBrush,
    TemperatureTool,
    ThermalUniform,
    ThermalContact,
    ThermalCapacity,
    ThermalConductivityCompare,
    ThermalFast,
    ThermalSlow,
    ThermalInsulator,
    ThermalVacuum,
    ThermalGas,
    TemperatureProbeGpu
}

public static class AcceptanceRegressionScenario
{
    private static AcceptanceMaterialIndices materials = null!;

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        AcceptanceScenarioMode mode,
        uint frame,
        MaterialRegistry? materialRegistry = null)
    {
        if (materialRegistry is null)
        {
            throw new InvalidOperationException("Acceptance-сценарий требует реестр материалов.");
        }
        materials = new AcceptanceMaterialIndices(materialRegistry);
        return mode switch
        {
            AcceptanceScenarioMode.Bowl => CreateBowl(frame),
            AcceptanceScenarioMode.SolidGravity => CreateSolidGravity(frame),
            AcceptanceScenarioMode.Sand => CreateSand(frame),
            AcceptanceScenarioMode.Hydro => CreateHydro(frame),
            AcceptanceScenarioMode.Slope => CreateSlope(frame),
            AcceptanceScenarioMode.Gas => CreateGas(frame),
            AcceptanceScenarioMode.WaterStress => CreateWaterStress(frame),
            AcceptanceScenarioMode.FlatSurface => CreateFlatSurface(frame),
            AcceptanceScenarioMode.WaterDrain => CreateWaterDrain(frame),
            AcceptanceScenarioMode.CommunicatingVessels => CreateCommunicatingVessels(frame),
            AcceptanceScenarioMode.PressureTube => CreatePressureTube(frame),
            AcceptanceScenarioMode.SavedPressure => [],
            AcceptanceScenarioMode.SavedIsolation => CreateSavedIsolation(frame),
            AcceptanceScenarioMode.SavedGravity => [],
            AcceptanceScenarioMode.Buoyancy => CreateBuoyancy(frame),
            AcceptanceScenarioMode.SavedSandWater => [],
            AcceptanceScenarioMode.ExternalGranular => CreateExternalGranular(frame),
            AcceptanceScenarioMode.ExternalLiquid => CreateExternalLiquid(frame),
            AcceptanceScenarioMode.ExternalGas => CreateExternalGas(frame),
            AcceptanceScenarioMode.ExternalSolids => CreateExternalSolids(frame),
            AcceptanceScenarioMode.UnderwaterGranularPile => CreateUnderwaterGranular(frame),
            AcceptanceScenarioMode.GranularWaterDisplacement => CreateUnderwaterGranular(frame),
            AcceptanceScenarioMode.GranularBarrier => CreateGranularBarrier(frame),
            AcceptanceScenarioMode.GranularBarrierHydraulic => CreateGranularBarrier(frame),
            AcceptanceScenarioMode.TemperatureBrush => CreateTemperatureBrush(frame),
            AcceptanceScenarioMode.TemperatureTool => CreateTemperatureTool(frame),
            AcceptanceScenarioMode.ThermalUniform or
            AcceptanceScenarioMode.ThermalContact or
            AcceptanceScenarioMode.ThermalCapacity or
            AcceptanceScenarioMode.ThermalConductivityCompare or
            AcceptanceScenarioMode.ThermalFast or
            AcceptanceScenarioMode.ThermalSlow or
            AcceptanceScenarioMode.ThermalInsulator or
            AcceptanceScenarioMode.ThermalVacuum or
            AcceptanceScenarioMode.ThermalGas or
            AcceptanceScenarioMode.TemperatureProbeGpu => [],
            _ => []
        };
    }

    private static IReadOnlyList<BrushDrawCommand> CreateTemperatureBrush(uint frame)
    {
        if (frame == 0)
        {
            return
            [
                Create(100, 60, 10, materials.Sand, 0, 0),
                Create(200, 60, 10, materials.Resolve("acceptance:temperature_probe"), 0, 0),
                Create(300, 60, 10, materials.Sand, 0, 0)
            ];
        }
        return frame == 1
            ? [Create(300, 60, 14, materials.Eraser, 1, 0)]
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateTemperatureTool(uint frame)
    {
        if (frame == 1)
        {
            return
            [
                CreateTemperature(240, 135, 15, materials.Sand, 500),
                CreateTemperature(80, 60, 10, materials.Sand, 500)
            ];
        }
        if (frame == 2)
        {
            return [Create(240, 135, 4, materials.Eraser, (uint)BrushCommandMode.Erase, 0)];
        }
        return frame == 131
            ? [CreateTemperature(80, 60, 10, materials.Sand, 500)]
            : [];
    }

    private static BrushDrawCommand CreateTemperature(
        int x,
        int y,
        float radius,
        uint material,
        float temperature)
    {
        BrushDrawCommand command = Create(
            x,
            y,
            radius,
            material,
            (uint)BrushCommandMode.SetTemperature,
            0);
        command.TargetTemperature = temperature;
        return command;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateBowl(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 114, 115, 114, 225, 8, 5, materials.Metal, 1001);
            AddLine(commands, 325, 115, 325, 225, 8, 5, materials.Metal, 1001);
            AddLine(commands, 114, 225, 325, 225, 8, 5, materials.Metal, 1001);
            return commands;
        }
        if (frame == 2)
        {
            return AddFill(121, 170, 318, 218, 8, 5, materials.Water, 0);
        }
        return frame == 130
            ? AddFill(121, 130, 318, 158, 8, 5, materials.Sand, 0)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSolidGravity(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 20, 250, 460, 250, 7, 4, materials.Fixture, 2001);
            AddLine(commands, 215, 180, 215, 250, 7, 4, materials.Fixture, 2002);
            AddLine(commands, 415, 180, 415, 250, 7, 4, materials.Fixture, 2003);
            AddLine(commands, 140, 150, 140, 250, 7, 4, materials.Fixture, 2004);
            AddLine(commands, 35, 100, 35, 250, 7, 4, materials.Fixture, 2005);
            AddLine(commands, 130, 100, 130, 250, 7, 4, materials.Fixture, 2005);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(60, 25, 109, 74, 6, 4, materials.Metal, 2101);
        }
        if (frame == 2)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 220, 60, 410, 60, 7, 5, materials.Stone, 2201);
            return commands;
        }
        if (frame == 3)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 130, 100, 200, 100, 7, 5, materials.Metal, 2301);
            return commands;
        }
        if (frame == 4)
        {
            return AddFill(43, 205, 122, 240, 7, 4, materials.Water, 0);
        }
        if (frame == 5)
        {
            return AddFill(43, 229, 122, 240, 7, 4, materials.Sand, 0);
        }
        if (frame == 30)
        {
            return [Create(165, 100, 6, materials.Eraser, 1, 0)];
        }
        if (frame == 200)
        {
            return [Create(315, 172, 8, materials.Eraser, 1, 0)];
        }
        if (frame == 205)
        {
            return
            [
                Create(415, 183, 17, materials.Eraser, 1, 0),
                Create(415, 210, 11, materials.Eraser, 1, 0),
                Create(415, 232, 11, materials.Eraser, 1, 0),
                Create(415, 247, 8, materials.Eraser, 1, 0)
            ];
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSand(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 80, 245, 400, 245, 7, 4, materials.Fixture, 3001);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand sand = Create(240, 75, 25, materials.Sand, 0, 0);
        sand.Density = 0.51f;
        sand.Seed = 3002;
        return [sand];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateHydro(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 15, 95, 15, 250, 7, 4, materials.Metal, 4001);
            AddLine(commands, 260, 95, 260, 250, 7, 4, materials.Metal, 4001);
            AddLine(commands, 15, 250, 260, 250, 7, 4, materials.Metal, 4001);
            AddLine(commands, 138, 95, 138, 218, 7, 4, materials.Metal, 4001);
            return commands;
        }
        if (frame == 1)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 290, 150, 290, 250, 7, 4, materials.Metal, 4002);
            AddLine(commands, 465, 150, 465, 250, 7, 4, materials.Metal, 4002);
            AddLine(commands, 290, 250, 465, 250, 7, 4, materials.Metal, 4002);
            return commands;
        }
        if (frame == 2)
        {
            return AddFill(25, 155, 128, 240, 7, 5, materials.Water, 0);
        }
        if (frame is >= 3 and <= 20)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 378, 20, 378, 50, 6, 4, materials.Water, 0);
            return commands;
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSlope(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 5, 250, 475, 250, 7, 5, materials.Metal, 5001);
            AddLine(commands, 300, 225, 390, 70, 7, 5, materials.Metal, 5001);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand sand = Create(382, 42, 24, materials.Sand, 0, 0);
        sand.Density = 0.62f;
        sand.Seed = 5002;
        return [sand];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateGas(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 50, 30, 430, 30, 7, 5, materials.Metal, 6001);
            AddLine(commands, 50, 30, 50, 250, 7, 5, materials.Metal, 6001);
            AddLine(commands, 430, 30, 430, 250, 7, 5, materials.Metal, 6001);
            AddLine(commands, 50, 250, 430, 250, 7, 5, materials.Metal, 6001);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand gas = Create(240, 215, 25, materials.Gas, 0, 0);
        gas.Density = 0.72f;
        gas.Seed = 6002;
        return [gas];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterStress(uint frame)
    {
        List<BrushDrawCommand> commands = [];
        if (frame == 0)
        {
            AddLine(commands, 24, 1000, 1896, 1000, 16, 8, materials.Metal, 7001);
            AddLine(commands, 24, 96, 24, 1000, 16, 8, materials.Metal, 7001);
            AddLine(commands, 1896, 96, 1896, 1000, 16, 8, materials.Metal, 7001);
            return commands;
        }
        if (frame == 1)
        {
            AddLine(commands, 320, 900, 820, 520, 16, 8, materials.Metal, 7002);
            AddLine(commands, 1100, 520, 1600, 900, 16, 8, materials.Metal, 7002);
            return commands;
        }
        if (frame is >= 2 and <= 18)
        {
            int y = 160 + ((int)frame - 2) * 42;
            AddLine(commands, 100, y, 1820, y, 52, 34, materials.Water, 0);
            return commands;
        }
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalGranular(uint frame)
    {
        uint fixture = materials.Resolve("acceptance:fixture");
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 80, 245, 400, 245, 7, 4, fixture, 0);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand granular = Create(
            240, 75, 25, materials.Resolve("test:granular"), 0, 0);
        granular.Density = 0.51f;
        granular.Seed = 12001;
        return [granular];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalLiquid(uint frame)
    {
        uint fixture = materials.Resolve("acceptance:fixture");
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 40, 120, 40, 245, 7, 5, fixture, 0);
            AddLine(commands, 440, 120, 440, 245, 7, 5, fixture, 0);
            AddLine(commands, 40, 245, 440, 245, 7, 5, fixture, 0);
            return commands;
        }
        return frame == 1
            ? AddFill(70, 155, 210, 230, 8, 5, materials.Resolve("acceptance:liquid"), 0)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalGas(uint frame)
    {
        uint fixture = materials.Resolve("acceptance:fixture");
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 50, 30, 430, 30, 7, 5, fixture, 0);
            AddLine(commands, 50, 30, 50, 250, 7, 5, fixture, 0);
            AddLine(commands, 430, 30, 430, 250, 7, 5, fixture, 0);
            AddLine(commands, 50, 250, 430, 250, 7, 5, fixture, 0);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand gas = Create(240, 215, 25, materials.Resolve("acceptance:gas"), 0, 0);
        gas.Density = 0.72f;
        gas.Seed = 12002;
        return [gas];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalSolids(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(
                commands,
                20, 245, 460, 245, 7, 5, materials.Resolve("acceptance:fixture"), 0);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(
                80, 55, 145, 105, 6, 4, materials.Resolve("acceptance:solid_light"), 13001);
        }
        return frame == 2
            ? AddFill(
                300, 55, 365, 105, 6, 4, materials.Resolve("acceptance:solid_heavy"), 13002)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateUnderwaterGranular(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 30, 70, 30, 250, 7, 4, materials.Fixture, 14001);
            AddLine(commands, 450, 70, 450, 250, 7, 4, materials.Fixture, 14001);
            AddLine(commands, 30, 250, 450, 250, 7, 4, materials.Fixture, 14001);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(50, 125, 430, 230, 15, 11, materials.Water, 0);
        }
        if (frame != 2)
        {
            return [];
        }
        return [Create(240, 80, 20, materials.Resolve("test:granular"), 0, 0)];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateGranularBarrier(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 30, 70, 30, 250, 7, 4, materials.Fixture, 14101);
            AddLine(commands, 450, 70, 450, 250, 7, 4, materials.Fixture, 14101);
            AddLine(commands, 30, 250, 450, 250, 7, 4, materials.Fixture, 14101);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(210, 90, 270, 240, 7, 5, materials.Resolve("test:granular"), 0);
        }
        return frame == 2
            ? AddFill(50, 175, 175, 238, 7, 5, materials.Water, 0)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateFlatSurface(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 10, 255, 470, 255, 7, 5, materials.Metal, 7101);
            AddLine(commands, 10, 90, 10, 255, 7, 5, materials.Metal, 7101);
            AddLine(commands, 470, 90, 470, 255, 7, 5, materials.Metal, 7101);
            return commands;
        }
        if (frame == 1)
        {
            BrushDrawCommand sand = Create(355, 178, 30, materials.Sand, 0, 0);
            sand.Density = 0.72f;
            sand.Seed = 7102;
            return [sand];
        }
        if (frame == 2)
        {
            return AddFill(275, 165, 455, 245, 8, 5, materials.Water, 0);
        }
        if (frame is >= 3 and <= 300)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 240, 25, 240, 55, 6, 4, materials.Water, 0);
            return commands;
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterDrain(uint frame)
    {
        List<BrushDrawCommand> commands = [];
        if (frame == 0)
        {
            AddLine(commands, 5, 255, 475, 255, 7, 5, materials.Metal, 8001);
            AddLine(commands, 5, 20, 5, 255, 7, 5, materials.Metal, 8001);
            AddLine(commands, 475, 20, 475, 255, 7, 5, materials.Metal, 8001);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(20, 205, 230, 245, 8, 5, materials.Water, 0);
        }
        if (frame == 2)
        {
            return AddFill(238, 205, 460, 245, 8, 5, materials.Water, 0);
        }
        if (frame is >= 30 and <= 75)
        {
            int offset = ((int)frame % 5 - 2) * 4;
            BrushDrawCommand sand = Create(240 + offset, 65, 18, materials.Sand, 0, 0);
            sand.Density = 0.62f;
            sand.Seed = 8002 + frame;
            return [sand];
        }
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateCommunicatingVessels(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 25, 35, 25, 252, 7, 5, materials.Stone, 9001);
            AddLine(commands, 455, 25, 455, 252, 7, 5, materials.Stone, 9001);
            AddLine(commands, 25, 252, 455, 252, 7, 5, materials.Stone, 9001);
            AddLine(commands, 105, 35, 105, 218, 7, 5, materials.Stone, 9001);
            AddLine(commands, 275, 85, 275, 232, 7, 5, materials.Stone, 9001);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(295, 40, 440, 190, 10, 6, materials.Water, 0);
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreatePressureTube(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 300, 40, 300, 192, 6, 4, materials.Stone, 9101);
            AddLine(commands, 300, 238, 300, 255, 6, 4, materials.Stone, 9101);
            AddLine(commands, 470, 40, 470, 255, 6, 4, materials.Stone, 9101);
            AddLine(commands, 300, 255, 470, 255, 6, 4, materials.Stone, 9101);
            AddLine(commands, 230, 70, 230, 210, 6, 4, materials.Stone, 9102);
            AddLine(commands, 260, 70, 260, 180, 6, 4, materials.Stone, 9102);
            AddLine(commands, 260, 180, 300, 200, 6, 4, materials.Stone, 9102);
            AddLine(commands, 230, 210, 300, 230, 6, 4, materials.Stone, 9102);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(315, 145, 455, 245, 8, 5, materials.Water, 0);
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSavedIsolation(uint frame)
    {
        // Saved-scene pressure regressions must preserve the captured mass.
        // Injecting water here can imitate a rising spiral even when pressure
        // routing is stalled, because this coordinate drains into the vessel.
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateBuoyancy(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 15, 255, 465, 255, 7, 5, materials.Fixture, 10001);
            AddLine(commands, 55, 75, 185, 75, 7, 5, materials.Metal, 10101);
            AddLine(commands, 55, 75, 55, 140, 7, 5, materials.Metal, 10101);
            AddLine(commands, 185, 75, 185, 140, 7, 5, materials.Metal, 10101);
            AddLine(commands, 55, 140, 185, 140, 7, 5, materials.Metal, 10101);
            AddLine(commands, 215, 75, 215, 140, 7, 5, materials.Metal, 10201);
            AddLine(commands, 330, 75, 330, 140, 7, 5, materials.Metal, 10201);
            AddLine(commands, 215, 140, 330, 140, 7, 5, materials.Metal, 10201);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(375, 90, 425, 140, 6, 4, materials.Metal, 10301);
        }
        if (frame == 2)
        {
            return AddFill(20, 175, 240, 247, 8, 5, materials.Water, 0);
        }
        if (frame == 3)
        {
            return AddFill(240, 175, 460, 247, 8, 5, materials.Water, 0);
        }
        if (frame == 4)
        {
            return AddFill(235, 92, 310, 128, 6, 4, materials.Sand, 0);
        }
        return [];
    }

    private static List<BrushDrawCommand> AddFill(
        int left,
        int top,
        int right,
        int bottom,
        int spacing,
        float radius,
        uint material,
        uint bodyId)
    {
        List<BrushDrawCommand> commands = [];
        for (int y = top; y <= bottom; y += spacing)
        {
            for (int x = left; x <= right; x += spacing)
            {
                commands.Add(Create(x, y, radius, material, 0, bodyId));
            }
        }
        return commands;
    }

    private static void AddLine(
        List<BrushDrawCommand> commands,
        int startX,
        int startY,
        int endX,
        int endY,
        int spacing,
        float radius,
        uint material,
        uint bodyId)
    {
        int length = Math.Max(Math.Abs(endX - startX), Math.Abs(endY - startY));
        int samples = Math.Max(1, length / spacing);
        for (int sample = 0; sample <= samples; sample++)
        {
            float amount = sample / (float)samples;
            commands.Add(Create(
                (int)MathF.Round(startX + (endX - startX) * amount),
                (int)MathF.Round(startY + (endY - startY) * amount),
                radius,
                material,
                0,
                bodyId));
        }
    }

    private static BrushDrawCommand Create(
        int x,
        int y,
        float radius,
        uint material,
        uint mode,
        uint bodyId)
    {
        return new BrushDrawCommand
        {
            X = x,
            Y = y,
            MaterialIndex = material,
            Radius = radius,
            Density = 1,
            Mode = (BrushCommandMode)mode,
            Seed = (uint)(x + y * 2048),
            Reserved = bodyId
        };
    }
}
