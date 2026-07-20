using System;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class SteamDistributionAndCoolingAcceptanceScenario
{
    internal const int Left = 20;
    internal const int Top = 20;
    internal const int Right = 459;
    internal const int Bottom = 255;
    internal const int InitialMass = 256;

    public static SimulationWorldSnapshot? CreateInitialWorld(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.SteamDistributionAndCooling)
        {
            return null;
        }
        if (width < 480 || height < 270)
        {
            throw new InvalidOperationException(
                "steam_distribution_and_cooling requires at least 480x270 cells.");
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        Fill(cells, width, Left, Top, Right, Top + 3, fixture, 20);
        Fill(cells, width, Left, Bottom - 3, Right, Bottom, fixture, 20);
        Fill(cells, width, Left, Top, Left + 3, Bottom, fixture, 20);
        Fill(cells, width, Right - 3, Top, Right, Bottom, fixture, 20);
        Fill(cells, width, 232, 190, 247, 205, steam,
            materials[CoreMaterialIds.Steam].Properties.InitialTemperature);
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
