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
        SimulationWorldSnapshot? phase = PhaseAcceptanceScenario.Create(mode, width, height, materials);
        if (phase is not null)
        {
            return phase;
        }
        if (mode is not (
            AcceptanceScenarioMode.ThermalUniform or
            AcceptanceScenarioMode.ThermalContact or
            AcceptanceScenarioMode.ThermalCapacity or
            AcceptanceScenarioMode.ThermalConductivityCompare or
            AcceptanceScenarioMode.ThermalFast or
            AcceptanceScenarioMode.ThermalSlow or
            AcceptanceScenarioMode.ThermalInsulator or
            AcceptanceScenarioMode.ThermalVacuum or
            AcceptanceScenarioMode.ThermalGas or
            AcceptanceScenarioMode.TemperatureTool or
            AcceptanceScenarioMode.TemperatureProbeGpu or
            AcceptanceScenarioMode.SteamSelfCooling or
            AcceptanceScenarioMode.PhaseDispatchSmoke))
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
                Set(cells, width, 239, 135,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_low_capacity"), 400, 2);
                Set(cells, width, 240, 135,
                    materials.GetRequiredRuntimeIndex("acceptance:thermal_high_capacity"), 0, 2);
                break;
            case AcceptanceScenarioMode.ThermalConductivityCompare:
                FillConductivityComparison(cells, width, materials);
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
            case AcceptanceScenarioMode.TemperatureTool:
                CreateTemperatureToolFixture(cells, width, height, materials);
                break;
            case AcceptanceScenarioMode.TemperatureProbeGpu:
                CreateTemperatureProbeFixture(cells, width, height, materials);
                break;
            case AcceptanceScenarioMode.SteamSelfCooling:
                Fill(cells, width, 236, 180, 243, 187,
                    materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
                    materials[CoreMaterialIds.Steam].Properties.InitialTemperature, 1);
                break;
            case AcceptanceScenarioMode.PhaseDispatchSmoke:
                CreatePhaseDispatchFixture(cells, width, height, materials);
                break;
        }
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    private static void CreatePhaseDispatchFixture(
        Span<GridCell> cells,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (width < 260 || height < 180)
        {
            throw new InvalidOperationException("Phase dispatch acceptance requires at least 260x180 cells.");
        }
        uint source = materials.GetRequiredRuntimeIndex("acceptance:phase_source");
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        for (int y = 119; y <= 136; y++)
        {
            for (int x = 219; x <= 236; x++)
            {
                if (x is 219 or 236 || y is 119 or 136)
                {
                    cells[y * width + x] = new GridCell
                    {
                        MaterialIndex = fixture,
                        Mass = 2,
                        IsActive = 1,
                        RestFrames = 2,
                        Temperature = 20
                    };
                }
            }
        }
        for (int y = 120; y < 136; y++)
        {
            for (int x = 220; x < 236; x++)
            {
                cells[y * width + x] = new GridCell
                {
                    MaterialIndex = source,
                    Mass = 2,
                    VelocityX = 7,
                    VelocityY = -3,
                    Pressure = 9,
                    IsActive = 1,
                    BodyId = 123,
                    RestFrames = 17,
                    Temperature = 150
                };
            }
        }
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
        uint otherGas = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2);
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

    private static void FillConductivityComparison(
        Span<GridCell> cells,
        int width,
        MaterialRegistry materials)
    {
        uint fast = materials.GetRequiredRuntimeIndex("acceptance:thermal_fast");
        uint slow = materials.GetRequiredRuntimeIndex("acceptance:thermal_slow");
        Fill(cells, width, 140, 60, 171, 91, fast, 400, 2);
        Fill(cells, width, 172, 60, 203, 91, fast, 0, 2);
        Fill(cells, width, 140, 160, 171, 191, slow, 400, 2);
        Fill(cells, width, 172, 160, 203, 191, slow, 0, 2);
    }

    private static void CreateTemperatureProbeFixture(
        Span<GridCell> cells,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (width < 400 || height < 260)
        {
            throw new InvalidOperationException("Temperature probe acceptance requires at least 400x260 cells.");
        }

        uint sand = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint probe = materials.GetRequiredRuntimeIndex("acceptance:temperature_probe");
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        uint fast = materials.GetRequiredRuntimeIndex("acceptance:thermal_fast");
        Fill(cells, width, 40, 220, 79, 249, sand, 20, 2);
        Fill(cells, width, 100, 220, 139, 249, probe, 123.5f, 2);
        Fill(cells, width, 35, 250, 144, 253, fixture, 20, 2);
        Fill(cells, width, 200, 100, 239, 159, fast, 400, 2);
        Fill(cells, width, 240, 100, 279, 159, fast, 0, 2);
    }

    private static void CreateTemperatureToolFixture(
        Span<GridCell> cells,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (width < 400 || height < 220)
        {
            throw new InvalidOperationException("Temperature tool acceptance requires at least 400x220 cells.");
        }
        uint sand = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        Fill(cells, width, 160, 100, 319, 169, sand, 20, 1);
        Fill(cells, width, 155, 170, 324, 174, fixture, 20, 1);
        ref GridCell preservationProbe = ref cells[135 * width + 245];
        preservationProbe.Mass = 0.75f;
        preservationProbe.VelocityX = 3.25f;
        preservationProbe.VelocityY = -1.5f;
        preservationProbe.Pressure = 2.5f;
        preservationProbe.BodyId = 4242;
        preservationProbe.RestFrames = 17;
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
