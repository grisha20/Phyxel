using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class PhaseAcceptanceScenario
{
    public static bool IsPhaseMode(AcceptanceScenarioMode mode) => mode is
        AcceptanceScenarioMode.PhaseThresholds or
        AcceptanceScenarioMode.PhaseHysteresis or
        AcceptanceScenarioMode.PhaseSingleTransition or
        AcceptanceScenarioMode.PhaseNormalizationMatrix or
        AcceptanceScenarioMode.PhaseSummaryLiquidGas or
        AcceptanceScenarioMode.PhaseSummarySolidLiquid or
        AcceptanceScenarioMode.PhaseSummaryGasMovable or
        AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
        AcceptanceScenarioMode.PhasePauseContinue or
        AcceptanceScenarioMode.PhaseWakeGas or
        AcceptanceScenarioMode.PhaseWakeLiquid or
        AcceptanceScenarioMode.PhaseReadbackFallback or
        AcceptanceScenarioMode.PhaseExternalReorder or
        AcceptanceScenarioMode.PhaseDisabledRegistry or
        AcceptanceScenarioMode.PhaseEnergyContract or
        AcceptanceScenarioMode.PhaseV5RoundTrip or
        AcceptanceScenarioMode.PhasePerformanceSteady or
        AcceptanceScenarioMode.PhasePerformanceBurst or
        AcceptanceScenarioMode.WaterIceSteam or
        AcceptanceScenarioMode.WaterIceSteamMotion or
        AcceptanceScenarioMode.WaterIceSteamPause or
        AcceptanceScenarioMode.WaterIceSteamV5RoundTrip;

    public static SimulationWorldSnapshot? Create(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (!IsPhaseMode(mode))
        {
            return null;
        }
        if (width < 480 || height < 270)
        {
            throw new InvalidOperationException("Phase acceptance requires at least 480x270 cells.");
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        if (CorePhaseAcceptanceScenario.IsCorePhaseMode(mode))
        {
            CorePhaseAcceptanceScenario.Populate(mode, cells, width, materials);
            return new SimulationWorldSnapshot(width, height, bytes);
        }
        switch (mode)
        {
            case AcceptanceScenarioMode.PhaseThresholds:
                CreateThresholds(cells, width, materials);
                break;
            case AcceptanceScenarioMode.PhaseHysteresis:
                CreateHysteresis(cells, width, materials);
                break;
            case AcceptanceScenarioMode.PhaseSingleTransition:
                Set(cells, width, 240, 135, materials, "acceptance:chain_a", 100, 1.25f,
                    3, -4, 5, 1, 71, 19);
                break;
            case AcceptanceScenarioMode.PhaseNormalizationMatrix:
            case AcceptanceScenarioMode.PhaseV5RoundTrip:
                CreateNormalizationMatrix(cells, width, materials);
                break;
            case AcceptanceScenarioMode.PhaseSummaryLiquidGas:
                SetNormalizationCell(cells, width, 240, 135, materials, "acceptance:norm_liquid_to_gas", 0);
                break;
            case AcceptanceScenarioMode.PhaseSummarySolidLiquid:
                SetNormalizationCell(cells, width, 240, 135, materials, "acceptance:norm_fixed_to_liquid", 6);
                break;
            case AcceptanceScenarioMode.PhaseSummaryGasMovable:
                SetNormalizationCell(cells, width, 240, 135, materials, "acceptance:norm_gas_to_movable", 7);
                break;
            case AcceptanceScenarioMode.PhaseSummaryLiquidFixed:
                SetNormalizationCell(cells, width, 240, 135, materials, "acceptance:norm_liquid_to_fixed", 5);
                break;
            case AcceptanceScenarioMode.PhasePauseContinue:
                Set(cells, width, 240, 135, materials, "acceptance:pause_source", 20, 1.5f,
                    2.5f, -1.25f, 7, 1, 4242, 23);
                break;
            case AcceptanceScenarioMode.PhaseWakeGas:
                Fill(cells, width, 232, 150, 247, 157, materials, "acceptance:wake_gas_source", 110, 1);
                break;
            case AcceptanceScenarioMode.PhaseWakeLiquid:
                Fill(cells, width, 232, 70, 247, 77, materials, "acceptance:wake_liquid_source", 110, 1);
                Fill(cells, width, 180, 210, 299, 213, materials, CoreMaterialIds.Fixture, 20, 2);
                break;
            case AcceptanceScenarioMode.PhaseReadbackFallback:
                Fill(cells, width, 232, 120, 247, 135, materials, "acceptance:phase_source", 150, 2);
                break;
            case AcceptanceScenarioMode.PhaseExternalReorder:
                Fill(cells, width, 232, 120, 247, 135, materials, "acceptance:external_source", 110, 2);
                break;
            case AcceptanceScenarioMode.PhaseDisabledRegistry:
                Set(cells, width, 239, 135, materials, CoreMaterialIds.Metal, 400, 2);
                Set(cells, width, 240, 135, materials, CoreMaterialIds.Metal, 0, 2);
                break;
            case AcceptanceScenarioMode.PhaseEnergyContract:
                Set(cells, width, 240, 135, materials, "acceptance:energy_source", 110, 2.5f,
                    0, 0, 0, 1, 0, 2);
                break;
            case AcceptanceScenarioMode.PhasePerformanceSteady:
                Fill(cells, width, 0, 0, width - 1, height - 1, materials,
                    "acceptance:perf_source", 20, 1);
                break;
            case AcceptanceScenarioMode.PhasePerformanceBurst:
                Fill(cells, width, 0, 0, width - 1, height - 1, materials,
                    "acceptance:perf_source", 110, 1);
                break;
        }
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        AcceptanceScenarioMode mode,
        uint frame,
        AcceptanceMaterialIndices materials)
    {
        if (CorePhaseAcceptanceScenario.IsCorePhaseMode(mode))
        {
            return CorePhaseAcceptanceScenario.CreateCommands(mode, frame, materials);
        }
        if (frame == 0 && mode is
            AcceptanceScenarioMode.PhaseHysteresis or
            AcceptanceScenarioMode.PhaseNormalizationMatrix or
            AcceptanceScenarioMode.PhaseSummaryLiquidGas or
            AcceptanceScenarioMode.PhaseSummarySolidLiquid or
            AcceptanceScenarioMode.PhaseSummaryGasMovable or
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
            AcceptanceScenarioMode.PhaseReadbackFallback or
            AcceptanceScenarioMode.PhaseV5RoundTrip)
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
        if (mode != AcceptanceScenarioMode.PhasePauseContinue || frame != 1)
        {
            return [];
        }
        return
        [
            new BrushDrawCommand
            {
                X = 240,
                Y = 135,
                Radius = 1,
                MaterialIndex = materials.Resolve("acceptance:pause_source"),
                Mode = BrushCommandMode.SetTemperature,
                TargetTemperature = 110
            }
        ];
    }

    private static void CreateThresholds(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        float[] temperatures = [-10, 0, 20, 100, 110];
        for (int index = 0; index < temperatures.Length; index++)
        {
            Set(cells, width, 220 + index * 10, 130, materials, "acceptance:threshold_source",
                temperatures[index], 1 + index * 0.125f, index + 0.25f, -index - 0.5f,
                index + 0.75f, 1, (uint)(100 + index), (uint)(10 + index));
        }
        // Thermal diffusion canonicalizes inactive cells to zero before the phase pass.
        // A zero inactive probe therefore isolates the phase shader's no-write contract.
        cells[130 * width + 280] = default;
    }

    private static void CreateHysteresis(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        (string Id, float Temperature)[] cases =
        [
            ("acceptance:cold_liquid", -10),
            ("acceptance:cold_solid", -10),
            ("acceptance:cold_solid", 10),
            ("acceptance:cold_liquid", 10),
            ("acceptance:cold_liquid", 1),
            ("acceptance:cold_solid", 1),
            ("acceptance:hot_liquid", 110),
            ("acceptance:hot_gas", 110),
            ("acceptance:hot_gas", 80),
            ("acceptance:hot_liquid", 80),
            ("acceptance:hot_liquid", 97),
            ("acceptance:hot_gas", 97)
        ];
        for (int index = 0; index < cases.Length; index++)
        {
            int x = 180 + index * 10;
            Set(cells, width, x, 130, materials, cases[index].Id,
                cases[index].Temperature, 1 + index * 0.0625f, 0, 0, 0, 1, 0, 0);
            Set(cells, width, x - 1, 130, materials, CoreMaterialIds.Fixture, 20, 2, restFrames: 2);
            Set(cells, width, x + 1, 130, materials, CoreMaterialIds.Fixture, 20, 2, restFrames: 2);
            Set(cells, width, x, 129, materials, CoreMaterialIds.Fixture, 20, 2, restFrames: 2);
            Set(cells, width, x, 131, materials, CoreMaterialIds.Fixture, 20, 2, restFrames: 2);
        }
    }

    private static void CreateNormalizationMatrix(Span<GridCell> cells, int width, MaterialRegistry materials)
    {
        string[] sources =
        [
            "acceptance:norm_liquid_to_gas",
            "acceptance:norm_gas_to_liquid",
            "acceptance:norm_liquid_to_liquid",
            "acceptance:norm_granular_to_liquid",
            "acceptance:norm_liquid_to_granular",
            "acceptance:norm_liquid_to_fixed",
            "acceptance:norm_fixed_to_liquid",
            "acceptance:norm_gas_to_movable"
        ];
        for (int index = 0; index < sources.Length; index++)
        {
            SetNormalizationCell(cells, width, 205 + index * 10, 135, materials, sources[index], index);
        }
    }

    private static void SetNormalizationCell(
        Span<GridCell> cells,
        int width,
        int x,
        int y,
        MaterialRegistry materials,
        string source,
        int index)
    {
        Set(cells, width, x, y, materials, source, 100 + index * 0.5f,
            1 + index * 0.125f, 10 + index * 0.25f, -20 - index * 0.5f,
            30 + index * 0.75f, 1, (uint)(1000 + index), (uint)(40 + index));
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
        float mass)
    {
        uint material = materials.GetRequiredRuntimeIndex(id);
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                cells[y * width + x] = new GridCell
                {
                    MaterialIndex = material,
                    Mass = mass,
                    Temperature = temperature,
                    IsActive = 1,
                    RestFrames = 2
                };
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
    }
}
