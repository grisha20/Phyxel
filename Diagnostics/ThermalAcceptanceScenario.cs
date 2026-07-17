using System;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class ThermalAcceptanceScenario
{
    public const float MinimumThermalMass = 0.0001f;

    public static SimulationWorldSnapshot? Create(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (mode is not (
            AcceptanceScenarioMode.ThermalUniform or
            AcceptanceScenarioMode.ThermalContact or
            AcceptanceScenarioMode.ThermalCapacity or
            AcceptanceScenarioMode.ThermalFast or
            AcceptanceScenarioMode.ThermalSlow or
            AcceptanceScenarioMode.ThermalInsulator or
            AcceptanceScenarioMode.ThermalVacuum or
            AcceptanceScenarioMode.ThermalGas))
        {
            return null;
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        switch (mode)
        {
            case AcceptanceScenarioMode.ThermalUniform:
                Fill(cells, width, 160, 90, 319, 179,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_hot"), 400, 2);
                break;
            case AcceptanceScenarioMode.ThermalContact:
                FillContact(cells, width,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_fast"),
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_fast"));
                break;
            case AcceptanceScenarioMode.ThermalCapacity:
                FillContact(cells, width,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_low_capacity"),
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_high_capacity"));
                break;
            case AcceptanceScenarioMode.ThermalFast:
                FillContact(cells, width,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_fast"),
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_fast"));
                break;
            case AcceptanceScenarioMode.ThermalSlow:
                FillContact(cells, width,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_slow"),
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_slow"));
                break;
            case AcceptanceScenarioMode.ThermalInsulator:
                Fill(cells, width, 140, 100, 199, 169,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_hot"), 400, 2);
                Fill(cells, width, 200, 100, 219, 169,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_insulator"), 20, 2);
                Fill(cells, width, 220, 100, 279, 169,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_cold"), 0, 2);
                break;
            case AcceptanceScenarioMode.ThermalVacuum:
                Fill(cells, width, 140, 100, 199, 169,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_hot"), 400, 2);
                Fill(cells, width, 220, 100, 279, 169,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_cold"), 0, 2);
                break;
            case AcceptanceScenarioMode.ThermalGas:
                CreateGas(cells, width, materials);
                break;
        }
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    private static void FillContact(
        Span<GridCell> cells,
        int width,
        uint leftMaterial,
        uint rightMaterial)
    {
        Fill(cells, width, 160, 100, 239, 169, leftMaterial, 400, 2);
        Fill(cells, width, 240, 100, 319, 169, rightMaterial, 0, 2);
    }

    private static void CreateGas(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        uint wall = materials.GetRequiredRuntimeIndex("acceptance:thermal_insulator");
        uint gas = materials.GetRequiredRuntimeIndex("acceptance:thermal_gas");
        uint otherGas = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Gas);
        Fill(cells, width, 149, 79, 331, 80, wall, 20, 2);
        Fill(cells, width, 149, 190, 331, 191, wall, 20, 2);
        Fill(cells, width, 149, 81, 150, 189, wall, 20, 2);
        Fill(cells, width, 239, 81, 241, 189, wall, 20, 2);
        Fill(cells, width, 330, 81, 331, 189, wall, 20, 2);
        for (int y = 82; y <= 188; y++)
        {
            for (int x = 152; x <= 237; x++)
            {
                float mass = ((x + y) & 1) == 0 ? 0.8f : 1f;
                Set(cells, width, x, y, gas, x < 195 ? 400 : 0, mass);
            }
            for (int x = 243; x <= 328; x++)
            {
                float mass = ((x + y) & 1) == 0 ? 0.8f : 1f;
                Set(cells, width, x, y, otherGas, x < 286 ? 300 : 100, mass);
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
        uint material,
        float temperature,
        float mass)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                Set(cells, width, x, y, material, temperature, mass);
            }
        }
    }

    private static void Set(
        Span<GridCell> cells,
        int width,
        int x,
        int y,
        uint material,
        float temperature,
        float mass)
    {
        cells[y * width + x] = new GridCell
        {
            MaterialIndex = material,
            Mass = mass,
            IsActive = 1,
            Temperature = temperature
        };
    }
}
