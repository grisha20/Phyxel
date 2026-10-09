using System;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

// An opt-in workload, not a new material rule: an already ignited shallow bed.
internal static class CoalStripPerformanceScenario
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("PHYXEL_FIRE_COAL_STRIP") == "1";

    internal static SimulationWorldSnapshot? Create(AcceptanceScenarioMode mode, int width, int height, MaterialRegistry registry)
    {
        if (mode != AcceptanceScenarioMode.FireOpen || !Enabled) return null;
        var cells = new GridCell[width * height];
        uint coal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        int depth = Math.Max(2, height / 80);
        for (int y = height - depth - 2; y < height - 2; y++)
        for (int x = width / 8; x < width * 7 / 8; x++)
            cells[y * width + x] = new() { IsActive = 1, MaterialIndex = coal, Mass = 1, Temperature = 600 };
        Console.WriteLine($"PHYXEL_COAL_STRIP width={width} height={height} depth={depth} temperature=600");
        return new(width, height, MemoryMarshal.AsBytes(cells.AsSpan()).ToArray());
    }
}
