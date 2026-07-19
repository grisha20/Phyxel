using System;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class CoalTypesAcceptanceScenario
{
    internal const string WetCharcoalId = "core:wet_charcoal";
    internal const string StoneCoalId = "core:stone_coal";
    internal const string ExternalLightId = "acceptance:granular_light";
    internal const string ExternalHeavyId = "acceptance:granular_heavy";
    internal const int WaterTop = 90;
    internal const int WaterBottom = 225;
    internal const int GranularTop = 160;
    internal const int GranularBottom = 171;
    internal const int InitialGranularMass = 132;
    internal static readonly (int Left, int Right, string MaterialId)[] Chambers =
    [
        (10, 95, CoreMaterialIds.Coal),
        (100, 185, WetCharcoalId),
        (190, 275, StoneCoalId),
        (280, 365, ExternalLightId),
        (370, 455, ExternalHeavyId)
    ];

    public static SimulationWorldSnapshot? CreateInitialWorld(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.CoalTypes)
        {
            return null;
        }
        if (width < 470 || height < 240)
        {
            throw new InvalidOperationException("coal_types requires at least 470x240 cells.");
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        foreach ((int left, int right, string materialId) in Chambers)
        {
            Fill(cells, width, left, 48, right, 51, fixture, 20);
            Fill(cells, width, left, 48, left + 3, 229, fixture, 20);
            Fill(cells, width, right - 3, 48, right, 229, fixture, 20);
            Fill(cells, width, left, 226, right, 229, fixture, 20);
            Fill(cells, width, left + 4, WaterTop, right - 4, WaterBottom, water, 20);
            if (!materials.TryGet(materialId, out MaterialDefinition material))
            {
                continue;
            }
            int center = (left + right) / 2;
            Fill(cells, width, center - 5, GranularTop, center + 5, GranularBottom,
                material.RuntimeIndex, 20);
        }
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    private static void Fill(
        Span<GridCell> cells,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        uint material,
        float temperature)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                cells[y * width + x] = new GridCell
                {
                    MaterialIndex = material,
                    Mass = 1,
                    IsActive = 1,
                    Temperature = temperature
                };
            }
        }
    }
}
