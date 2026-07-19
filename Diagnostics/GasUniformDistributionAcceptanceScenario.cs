using System;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class GasUniformDistributionAcceptanceScenario
{
    internal const int SingleLeft = 8;
    internal const int SingleTop = 8;
    internal const int SingleRight = 156;
    internal const int SingleBottom = 130;
    internal const int MultiLeft = 160;
    internal const int MultiTop = 8;
    internal const int MultiRight = 471;
    internal const int MultiBottom = 130;
    internal const int ObstacleLeft = 8;
    internal const int ObstacleTop = 136;
    internal const int ObstacleRight = 471;
    internal const int ObstacleBottom = 263;
    internal const int SingleMass = 400;
    internal const int MultiMass = 300;
    internal const int ObstacleMass = 300;

    public static SimulationWorldSnapshot? CreateInitialWorld(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.GasUniformDistribution)
        {
            return null;
        }
        if (width < 480 || height < 270)
        {
            throw new InvalidOperationException(
                "gas_uniform_distribution requires at least 480x270 cells.");
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);

        BuildChamber(cells, width, SingleLeft, SingleTop, SingleRight, SingleBottom, fixture);
        BuildChamber(cells, width, MultiLeft, MultiTop, MultiRight, MultiBottom, fixture);
        BuildChamber(cells, width, ObstacleLeft, ObstacleTop, ObstacleRight, ObstacleBottom, fixture);

        Fill(cells, width, 70, 100, 89, 119,
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Gas), 20, 0);

        (string Id, int Left)[] gases =
        [
            (CoreMaterialIds.Steam, 190),
            (CoreMaterialIds.Smoke, 250),
            (CoreMaterialIds.Gas, 310),
            (CoreMaterialIds.Co2, 370)
        ];
        foreach ((string id, int left) in gases)
        {
            MaterialDefinition material = materials[id];
            Fill(cells, width, left, 80, left + 14, 99,
                material.RuntimeIndex,
                id == CoreMaterialIds.Steam ? 200 : material.Properties.InitialTemperature,
                material.Properties.MaximumLifetime);
        }

        // A divider with a bottom opening and two ceiling pockets exercises
        // connected-space traversal without allowing gases through fixtures.
        Fill(cells, width, 238, 170, 241, 229, fixture, 20, 0);
        Fill(cells, width, 80, 170, 145, 173, fixture, 20, 0);
        Fill(cells, width, 330, 170, 395, 173, fixture, 20, 0);
        uint externalGas = materials.GetRequiredRuntimeIndex("acceptance:gas");
        Fill(cells, width, 210, 145, 229, 159, externalGas, 20, 0);

        // Ordinary gas must leave occupied non-gas cells untouched.
        Fill(cells, width, 190, 245, 209, 254,
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand), 20, 0);
        Fill(cells, width, 270, 245, 289, 254,
            materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water), 20, 0);

        return new SimulationWorldSnapshot(width, height, bytes);
    }

    private static void BuildChamber(
        Span<GridCell> cells,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        uint fixture)
    {
        Fill(cells, width, left, top, right, top + 3, fixture, 20, 0);
        Fill(cells, width, left, bottom - 3, right, bottom, fixture, 20, 0);
        Fill(cells, width, left, top, left + 3, bottom, fixture, 20, 0);
        Fill(cells, width, right - 3, top, right, bottom, fixture, 20, 0);
    }

    private static void Fill(
        Span<GridCell> cells,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        uint material,
        float temperature,
        float lifetime)
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
                    Temperature = temperature,
                    Lifetime = lifetime
                };
            }
        }
    }
}
