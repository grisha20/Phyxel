using System;
using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public static class SpecificationRegressionScenario
{
    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        SpecificationScenarioMode mode,
        uint frame,
        int width,
        int height)
    {
        return mode switch
        {
            SpecificationScenarioMode.WaterRest => CreateWaterRest(frame, height),
            SpecificationScenarioMode.Funnel => CreateFunnels(frame, height),
            SpecificationScenarioMode.MetalElastic => CreateBeam(frame, MaterialId.Metal, 50, true, height),
            SpecificationScenarioMode.MetalPlastic => CreateBeam(frame, MaterialId.Metal, 100, true, height),
            SpecificationScenarioMode.ConcreteCrack => CreateBeam(frame, MaterialId.Concrete, 50, false, height),
            SpecificationScenarioMode.ConcreteBreak => CreateBeam(frame, MaterialId.Concrete, 80, false, height),
            SpecificationScenarioMode.RestSand => CreateRestingSand(frame, height),
            SpecificationScenarioMode.RestWater => CreateWaterRest(frame, height),
            SpecificationScenarioMode.WaterSlope => CreateWaterSlope(frame),
            SpecificationScenarioMode.SandSlope => CreateSandSlope(frame, height),
            _ => []
        };
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSandSlope(uint frame, int height)
    {
        return frame <= 8
            ? CreateRestingSand(frame, height)
            : frame is >= 9 and <= 16
                ? CreateRestingSand(frame - 8, height)
                : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterSlope(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> ramp = [];
            AddLine(ramp, 100, 300, 800, 700, 6, 3, MaterialId.Fixture, 7501);
            AddLine(ramp, 100, 120, 100, 300, 6, 3, MaterialId.Fixture, 7501);
            return ramp;
        }

        if (frame == 1)
        {
            List<BrushDrawCommand> basin = [];
            AddLine(basin, 800, 700, 1200, 700, 6, 3, MaterialId.Fixture, 7501);
            AddLine(basin, 1200, 450, 1200, 700, 6, 3, MaterialId.Fixture, 7501);
            return basin;
        }

        return frame == 2 ? AddFill(145, 175, 315, 265, 11, 6, MaterialId.Water) : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateRestingSand(uint frame, int height)
    {
        int floor = Math.Min(height - 100, 900);
        if (frame == 0)
        {
            List<BrushDrawCommand> support = [];
            AddLine(support, 350, floor, 1570, floor, 7, 4, MaterialId.Fixture, 7401);
            return support;
        }

        if (frame is < 1 or > 8)
        {
            return [];
        }

        List<BrushDrawCommand> sand = [];
        int row = 0;
        for (int y = floor - 430; y <= floor - 10; y += 12, row++)
        {
            if (row % 8 != frame - 1)
            {
                continue;
            }

            int halfWidth = (int)MathF.Round((y - (floor - 430)) * 1.43f);
            for (int x = 960 - halfWidth; x <= 960 + halfWidth; x += 12)
            {
                sand.Add(Create(x, y, 8, MaterialId.Sand, 0, 0));
            }
        }

        return sand;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterRest(uint frame, int height)
    {
        int bottom = Math.Min(height - 80, 520);
        if (frame == 0)
        {
            List<BrushDrawCommand> shell = [];
            AddLine(shell, 98, bottom - 160, 98, bottom, 5, 3, MaterialId.Concrete, 7001);
            AddLine(shell, 603, bottom - 160, 603, bottom, 5, 3, MaterialId.Concrete, 7001);
            AddLine(shell, 98, bottom, 603, bottom, 5, 3, MaterialId.Concrete, 7001);
            return shell;
        }

        return frame == 1 ? AddFill(104, bottom - 28, 597, bottom - 9, 9, 5, MaterialId.Water) : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateFunnels(uint frame, int height)
    {
        int bottom = Math.Min(height - 100, 620);
        if (frame == 0)
        {
            List<BrushDrawCommand> walls = [];
            AddLine(walls, 160, bottom - 260, 290, bottom - 82, 5, 4, MaterialId.Fixture, 7101);
            AddLine(walls, 440, bottom - 260, 310, bottom - 82, 5, 4, MaterialId.Fixture, 7101);
            AddLine(walls, 720, bottom - 260, 850, bottom - 82, 5, 4, MaterialId.Fixture, 7102);
            AddLine(walls, 1000, bottom - 260, 870, bottom - 82, 5, 4, MaterialId.Fixture, 7102);
            return walls;
        }

        if (frame == 1)
        {
            List<BrushDrawCommand> supports = [];
            AddLine(supports, 290, bottom - 82, 290, height - 2, 7, 4, MaterialId.Fixture, 7101);
            AddLine(supports, 310, bottom - 82, 310, height - 2, 7, 4, MaterialId.Fixture, 7101);
            return supports;
        }

        if (frame == 2)
        {
            List<BrushDrawCommand> supports = [];
            AddLine(supports, 850, bottom - 82, 850, height - 2, 7, 4, MaterialId.Fixture, 7102);
            AddLine(supports, 870, bottom - 82, 870, height - 2, 7, 4, MaterialId.Fixture, 7102);
            return supports;
        }

        if (frame == 3)
        {
            return AddFill(205, bottom - 250, 395, bottom - 150, 10, 6, MaterialId.Water);
        }

        return frame == 4 ? AddFill(765, bottom - 250, 955, bottom - 220, 10, 6, MaterialId.Sand) : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateBeam(
        uint frame,
        MaterialId beamMaterial,
        int pileHeight,
        bool removeLoad,
        int height)
    {
        int beamY = Math.Min(150, height - 80);
        if (frame == 0)
        {
            List<BrushDrawCommand> supports = [];
            AddLine(supports, 116, beamY, 116, height - 8, 5, 4, MaterialId.Concrete, 7201);
            AddLine(supports, 324, beamY, 324, height - 8, 5, 4, MaterialId.Concrete, 7202);
            return supports;
        }

        if (frame == 1)
        {
            List<BrushDrawCommand> beam = [];
            AddLine(beam, 120, beamY - 1, 319, beamY - 1, 1, 0.49f, beamMaterial, 7301);
            return beam;
        }

        if (frame == 2)
        {
            List<BrushDrawCommand> beam = [];
            AddLine(beam, 120, beamY, 319, beamY, 1, 0.49f, beamMaterial, 7301);
            return beam;
        }

        if (frame == 10)
        {
            return AddFill(174, beamY - pileHeight - 2, 266, beamY - 5, 7, 3.5f, MaterialId.Sand);
        }

        return removeLoad && frame == 240
            ? AddFill(140, beamY - pileHeight - 20, 300, height - 1, 14, 10, MaterialId.Eraser, 2)
            : [];
    }

    private static List<BrushDrawCommand> AddFill(
        int left,
        int top,
        int right,
        int bottom,
        int spacing,
        float radius,
        MaterialId material,
        uint mode = 0)
    {
        List<BrushDrawCommand> commands = [];
        for (int y = top; y <= bottom; y += spacing)
        {
            for (int x = left; x <= right; x += spacing)
            {
                commands.Add(Create(x, y, radius, material, mode, 0));
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
        MaterialId material,
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
        MaterialId material,
        uint mode,
        uint bodyId)
    {
        return new BrushDrawCommand
        {
            X = x,
            Y = y,
            MaterialId = (uint)material,
            Radius = radius,
            Density = 1,
            Mode = mode,
            Seed = (uint)(y * 2048 + x),
            Reserved = bodyId
        };
    }
}
