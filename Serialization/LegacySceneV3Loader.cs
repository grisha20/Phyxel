using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Serialization;

internal static class LegacySceneV3Loader
{
    private static readonly string[] LegacyPalette =
    [
        CoreMaterialIds.Empty,
        CoreMaterialIds.Sand,
        CoreMaterialIds.Water,
        CoreMaterialIds.Metal,
        CoreMaterialIds.Stone,
        CoreMaterialIds.Eraser,
        CoreMaterialIds.Gas,
        CoreMaterialIds.Fixture
    ];

    private sealed class SceneFileV3
    {
        public int Version { get; set; }
        public float Scale { get; set; }
        public float Gravity { get; set; }
        public int BrushRadius { get; set; }
        public float SpawnDensity { get; set; }
        public bool SolidGravity { get; set; }
        public uint SelectedMaterial { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public bool HydraulicPressure { get; set; }
    }

    public static LoadedSimulationScene Load(
        byte[] sceneJson,
        SimulationWorldSnapshot? world,
        MaterialRegistry materialRegistry,
        List<string> warnings,
        JsonSerializerOptions options)
    {
        SceneFileV3 state = JsonSerializer.Deserialize<SceneFileV3>(sceneJson, options) ??
            throw new InvalidDataException("Сцена v3 не содержит состояния.");
        ushort selectedMaterial = ResolveLegacyIndex(state.SelectedMaterial, materialRegistry);
        if (world is not null)
        {
            RemapSnapshotToRuntime(world, materialRegistry, warnings);
        }

        return new LoadedSimulationScene(
            new SimulationSceneState(
                state.Version,
                state.Scale,
                state.Gravity,
                state.BrushRadius,
                state.SpawnDensity,
                state.SolidGravity,
                selectedMaterial,
                state.SavedAt,
                state.HydraulicPressure),
            world,
            warnings);
    }

    private static void RemapSnapshotToRuntime(
        SimulationWorldSnapshot world,
        MaterialRegistry materialRegistry,
        List<string> warnings)
    {
        SimulationStateSerializer.ValidateSnapshotSize(world);
        uint[] legacyToRuntime = new uint[LegacyPalette.Length];
        for (int index = 0; index < LegacyPalette.Length; index++)
        {
            legacyToRuntime[index] = materialRegistry.GetRequiredRuntimeIndex(LegacyPalette[index]);
        }

        bool warned = false;
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        for (int index = 0; index < cells.Length; index++)
        {
            if (cells[index].IsActive == 0)
            {
                cells[index] = default;
                continue;
            }
            uint legacyIndex = cells[index].MaterialIndex;
            if (legacyIndex >= legacyToRuntime.Length)
            {
                cells[index] = default;
                if (!warned)
                {
                    warned = true;
                    AddWarning(warnings, $"Сцена v3 содержит неизвестный legacy-индекс {legacyIndex}; клетки удалены.");
                }
                continue;
            }
            uint runtimeIndex = legacyToRuntime[legacyIndex];
            cells[index].MaterialIndex = runtimeIndex;
            cells[index].Temperature = materialRegistry[runtimeIndex].Properties.InitialTemperature;
        }
    }

    private static ushort ResolveLegacyIndex(uint legacyIndex, MaterialRegistry materialRegistry) =>
        legacyIndex < LegacyPalette.Length
            ? materialRegistry.GetRequiredRuntimeIndex(LegacyPalette[legacyIndex])
            : materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);

    private static void AddWarning(List<string> warnings, string message)
    {
        warnings.Add(message);
        Console.Error.WriteLine($"PHYXEL_SCENE_WARNING {message}");
    }
}
