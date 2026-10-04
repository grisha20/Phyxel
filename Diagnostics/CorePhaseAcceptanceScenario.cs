using System;
using System.Collections.Generic;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class CorePhaseAcceptanceScenario
{
    public static bool IsCorePhaseMode(AcceptanceScenarioMode mode) => mode is
        AcceptanceScenarioMode.WaterIceSteam or
        AcceptanceScenarioMode.WaterIceSteamMotion or
        AcceptanceScenarioMode.WaterIceSteamPause or
        AcceptanceScenarioMode.WaterIceSteamV5RoundTrip;

    public static void Populate(
        AcceptanceScenarioMode mode,
        Span<GridCell> cells,
        int width,
        MaterialRegistry materials)
    {
        switch (mode)
        {
            case AcceptanceScenarioMode.WaterIceSteam:
                PopulateChain(cells, width, materials);
                break;
            case AcceptanceScenarioMode.WaterIceSteamMotion:
                PopulateMotion(cells, width, materials);
                break;
            case AcceptanceScenarioMode.WaterIceSteamPause:
                Set(cells, width, 220, 135, materials, CoreMaterialIds.Water, 20, 1.25f,
                    2.5f, -1.5f, 7, 1, 9001, 17);
                Cage(cells, width, 220, 135, materials, -100);
                Set(cells, width, 260, 135, materials, CoreMaterialIds.Water, 20, 1.75f,
                    -3.5f, 4.5f, 9, 1, 9002, 23);
                Cage(cells, width, 260, 135, materials, 650);
                break;
            case AcceptanceScenarioMode.WaterIceSteamV5RoundTrip:
                PopulateRoundTrip(cells, width, materials);
                break;
        }
    }

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        AcceptanceScenarioMode mode,
        uint frame,
        AcceptanceMaterialIndices materials)
    {
        if (frame == 0)
        {
            return
            [
                new BrushDrawCommand
                {
                    X = 20,
                    Y = 20,
                    Radius = 1,
                    MaterialIndex = materials.Fixture,
                    Mode = BrushCommandMode.Material
                }
            ];
        }
        if (mode != AcceptanceScenarioMode.WaterIceSteamPause || frame != 1)
        {
            return [];
        }
        return
        [
            new BrushDrawCommand
            {
                X = 220,
                Y = 135,
                Radius = 1,
                MaterialIndex = materials.Water,
                Mode = BrushCommandMode.SetTemperature,
                TargetTemperature = -100
            },
            new BrushDrawCommand
            {
                X = 260,
                Y = 135,
                Radius = 1,
                MaterialIndex = materials.Water,
                Mode = BrushCommandMode.SetTemperature,
                TargetTemperature = 650
            }
        ];
    }

    private static void PopulateChain(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        // Immediate-boil fixtures supply >= cw*100 + L (water at 650 C).
        // Water at 110 C must accumulate latent heat instead; steam_energy
        // checks that contract without forcing a transition.
        Set(cells, width, 180, 130, materials, CoreMaterialIds.Water, 20, 1);
        Set(cells, width, 200, 130, materials, CoreMaterialIds.Water, 0, 1.125f);
        Set(cells, width, 220, 130, materials, CoreMaterialIds.Ice, 0, 1.25f, restFrames: 2);
        // Steam at the exact condensation threshold now cools below it before
        // the phase pass. Keep this cell above the threshold so the chain still
        // covers a stable steam state; generic phase acceptance owns exact
        // threshold and hysteresis coverage with non-ambient materials.
        Set(cells, width, 240, 130, materials, CoreMaterialIds.Steam, 122, 1.375f);
        Set(cells, width, 260, 130, materials, CoreMaterialIds.Water, 100, 1.5f);

        Set(cells, width, 180, 160, materials, CoreMaterialIds.Water, -10, 1.625f,
            3, -4, 5, 1, 71, 19);
        Set(cells, width, 200, 160, materials, CoreMaterialIds.Ice, 10, 1.75f,
            6, -7, 8, 1, 72, 2);
        Set(cells, width, 220, 160, materials, CoreMaterialIds.Water, 650, 1.875f,
            9, -10, 11, 1, 73, 21);
        Set(cells, width, 240, 160, materials, CoreMaterialIds.Steam, 80, 2,
            12, -13, 14, 1, 74, 22);
        Set(cells, width, 260, 160, materials, CoreMaterialIds.Ice, 1500, 2.125f,
            15, -16, 17, 1, 75, 2);

        foreach ((int X, int Y, float Temperature) in new[]
        {
            (180, 130, 20f), (200, 130, 0f), (220, 130, 0f), (240, 130, 122f),
            (260, 130, 100f), (180, 160, -10f), (200, 160, 10f),
            (220, 160, 650f), (240, 160, 80f), (260, 160, 1500f)
        })
        {
            Cage(cells, width, X, Y, materials, Temperature);
        }
    }

    private static void PopulateMotion(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        Fill(cells, width, 70, 160, 85, 163, materials, CoreMaterialIds.Ice, -5, 1, 2);
        Fill(cells, width, 150, 80, 157, 87, materials, CoreMaterialIds.Ice, 10, 1, 2);
        // A small paid excess avoids an exact-threshold test accidentally
        // becoming a partial boil when neighbouring gas gives heat to air.
        Fill(cells, width, 230, 180, 237, 187, materials, CoreMaterialIds.Water, 100.1f, 1, 0);
        for (int y = 180; y <= 187; y++) for (int x = 230; x <= 237; x++)
            cells[y * width + x].PhaseProgress = materials[CoreMaterialIds.Water].Properties.TransitionAboveLatentHeat;
        Fill(cells, width, 238, 180, 245, 187, materials, CoreMaterialIds.Co2, 100.1f, 1, 0);
        Fill(cells, width, 310, 80, 317, 87, materials, CoreMaterialIds.Steam, 80, 1, 0);
    }

    private static void PopulateRoundTrip(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        Set(cells, width, 200, 135, materials, CoreMaterialIds.Water, -10, 1.25f,
            1, -2, 3, 1, 101, 11);
        Set(cells, width, 220, 135, materials, CoreMaterialIds.Ice, 10, 1.5f,
            4, -5, 6, 1, 102, 2);
        Set(cells, width, 240, 135, materials, CoreMaterialIds.Water, 650, 1.75f,
            7, -8, 9, 1, 103, 13);
        Set(cells, width, 260, 135, materials, CoreMaterialIds.Steam, 80, 2,
            10, -11, 12, 1, 104, 14);
        Set(cells, width, 280, 135, materials, CoreMaterialIds.Ice, 1500, 2.25f,
            13, -14, 15, 1, 105, 2);
        foreach ((int X, float Temperature) in new[]
        {
            (200, -10f), (220, 10f), (240, 650f), (260, 80f), (280, 1500f)
        })
        {
            Cage(cells, width, X, 135, materials, Temperature);
        }
    }

    private static void Cage(
        Span<GridCell> cells,
        int width,
        int centerX,
        int centerY,
        MaterialRegistry materials,
        float temperature)
    {
        for (int y = centerY - 1; y <= centerY + 1; y++)
        {
            for (int x = centerX - 1; x <= centerX + 1; x++)
            {
                if (x == centerX && y == centerY)
                {
                    continue;
                }
                Set(cells, width, x, y, materials, CoreMaterialIds.Fixture,
                    temperature, 2, restFrames: 2);
            }
        }
    }

    private static void Fill(
        Span<GridCell> cells,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        MaterialRegistry materials,
        string id,
        float temperature,
        float mass,
        uint restFrames)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                Set(cells, width, x, y, materials, id, temperature, mass, restFrames: restFrames);
            }
        }
    }

    private static void Set(
        Span<GridCell> cells,
        int width,
        int x,
        int y,
        MaterialRegistry materials,
        string id,
        float temperature,
        float mass,
        float velocityX = 0,
        float velocityY = 0,
        float pressure = 0,
        uint active = 1,
        uint bodyId = 0,
        uint restFrames = 0)
    {
        cells[y * width + x] = new GridCell
        {
            MaterialIndex = materials.GetRequiredRuntimeIndex(id),
            Mass = mass,
            VelocityX = velocityX,
            VelocityY = velocityY,
            Pressure = pressure,
            IsActive = active,
            BodyId = bodyId,
            RestFrames = restFrames,
            Temperature = temperature
        };
        // Normalization/motion fixtures prepay fusion energy. Partial fusion
        // is measured independently by IceFusionRegressionVerifier; these
        // cases still test full conversion and subsequent movement.
        if (id == CoreMaterialIds.Ice && temperature > 0)
            cells[y * width + x].PhaseProgress = materials[CoreMaterialIds.Ice].Properties.TransitionAboveLatentHeat;
        if (id == CoreMaterialIds.Water && temperature < 0)
            cells[y * width + x].PhaseProgress = -materials[CoreMaterialIds.Ice].Properties.TransitionAboveLatentHeat;
        // These normalization/motion fixtures start with fully withdrawn
        // condensation heat. Partial cooling is covered by steam_energy.
        if (id == CoreMaterialIds.Steam && temperature < 98)
        {
            var liquid = materials[CoreMaterialIds.Water].Properties;
            var vapour = materials[CoreMaterialIds.Steam].Properties;
            cells[y * width + x].PhaseProgress = liquid.TransitionAboveLatentHeat +
                (liquid.HeatCapacity - vapour.HeatCapacity) *
                (liquid.TransitionAboveTemperature - vapour.TransitionBelowTemperature);
        }
    }
}
