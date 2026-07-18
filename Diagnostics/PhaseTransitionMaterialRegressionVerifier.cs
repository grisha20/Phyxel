using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class PhaseTransitionMaterialRegressionVerifier
{
    private sealed record ExpectedCoreMaterial(
        string Id,
        MaterialSimulationKind Kind,
        MaterialFlags Flags,
        float Density,
        float Friction,
        float FlowRate,
        string Color,
        float InitialTemperature,
        float Conductivity,
        float HeatCapacity);

    private static readonly ExpectedCoreMaterial[] ExpectedCoreMaterials =
    [
        new(CoreMaterialIds.Empty, MaterialSimulationKind.None, MaterialFlags.None,
            0, 0, 0, "#00000000", 0, 0, 1),
        new(CoreMaterialIds.Sand, MaterialSimulationKind.Granular, MaterialFlags.None,
            1.5f, 0.75f, 0.18f, "#DAB85C", 20, 0.15f, 0.83f),
        new(CoreMaterialIds.Water, MaterialSimulationKind.Liquid, MaterialFlags.None,
            1, 0.025f, 0.92f, "#2B84CF", 20, 0.60f, 4.18f),
        new(CoreMaterialIds.Ice, MaterialSimulationKind.Solid, MaterialFlags.None,
            0.92f, 0.10f, 0, "#A9DDF2", -5, 0.80f, 2.10f),
        new(CoreMaterialIds.Steam, MaterialSimulationKind.Gas, MaterialFlags.None,
            0.03f, 0.005f, 1.20f, "#DDEBF0A0", 105, 0.04f, 2.08f),
        new(CoreMaterialIds.Metal, MaterialSimulationKind.Solid, MaterialFlags.MovableSolid,
            7.8f, 0.35f, 0, "#8E9CA6", 20, 1, 0.50f),
        new(CoreMaterialIds.Stone, MaterialSimulationKind.Solid, MaterialFlags.MovableSolid,
            9.2f, 0.75f, 0, "#5C6065", 20, 0.25f, 0.84f),
        new(CoreMaterialIds.Gas, MaterialSimulationKind.Gas, MaterialFlags.None,
            0.08f, 0.005f, 1.2f, "#9BC4D29B", 20, 0.03f, 1),
        new(CoreMaterialIds.Fixture, MaterialSimulationKind.Solid, MaterialFlags.None,
            100, 0.9f, 0, "#525B63", 20, 0.25f, 0.84f),
        new(CoreMaterialIds.Eraser, MaterialSimulationKind.Tool, MaterialFlags.None,
            0, 0, 0, "#DE5858", 20, 0, 1)
    ];

    public static int Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("PHYXEL_PHASE_MATERIALS_SUCCESS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYXEL_PHASE_MATERIALS_FAILED {exception}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        VerifyLayouts();
        string root = Path.Combine(Path.GetTempPath(), $"phyxel-phase-materials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string coreDirectory = MaterialRegistry.ResolveCoreDirectory();
            await VerifyDefaultsAndCoreRegressionAsync(root, coreDirectory);
            await VerifyValidRulesAsync(root, coreDirectory);
            await VerifyRuntimeResolutionAsync(root, coreDirectory);
            await VerifyInvalidDocumentsAsync(root, coreDirectory);
            await VerifyInvalidRegistryReferencesAsync(root, coreDirectory);
            await VerifyExternalDependencyCascadeAsync(root, coreDirectory);
            await VerifyCoreFailuresAsync(root, coreDirectory);
            await VerifyCycleValidationAsync(root, coreDirectory);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifyLayouts()
    {
        Require(Marshal.SizeOf<MaterialProperties>() == 64,
            "MaterialProperties must be 64 bytes.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.TransitionBelowTemperature)).ToInt32() == 48,
            "TransitionBelowTemperature offset must be 48.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.TransitionBelowMaterialIndex)).ToInt32() == 52,
            "TransitionBelowMaterialIndex offset must be 52.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.TransitionAboveTemperature)).ToInt32() == 56,
            "TransitionAboveTemperature offset must be 56.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.TransitionAboveMaterialIndex)).ToInt32() == 60,
            "TransitionAboveMaterialIndex offset must be 60.");
        Require(Marshal.SizeOf<GridCell>() == 36, "GridCell must remain 36 bytes.");

        string shaderPath = Path.Combine(
            AppContext.BaseDirectory,
            "Content",
            "Shaders",
            "PhysicsShared.hlsli");
        string shader = File.ReadAllText(shaderPath);
        int structureStart = shader.IndexOf("struct MaterialProperties", StringComparison.Ordinal);
        int structureEnd = shader.IndexOf("};", structureStart, StringComparison.Ordinal);
        Require(structureStart >= 0 && structureEnd > structureStart,
            "HLSL MaterialProperties structure was not found.");
        string layout = shader[structureStart..structureEnd];
        string[] fields =
        [
            "uint Flags", "uint SimulationKind", "float Density", "float Friction",
            "float FlowRate", "float ColorR", "float ColorG", "float ColorB", "float ColorA",
            "float InitialTemperature", "float ThermalConductivity", "float HeatCapacity",
            "float TransitionBelowTemperature", "uint TransitionBelowMaterialIndex",
            "float TransitionAboveTemperature", "uint TransitionAboveMaterialIndex"
        ];
        int previous = -1;
        foreach (string field in fields)
        {
            int position = layout.IndexOf(field, StringComparison.Ordinal);
            Require(position > previous, $"HLSL MaterialProperties field '{field}' is missing or out of order.");
            previous = position;
        }
    }

    private static async Task VerifyDefaultsAndCoreRegressionAsync(string root, string coreDirectory)
    {
        string externalDirectory = CreateDirectory(root, "defaults");
        await WriteMaterialAsync(
            externalDirectory,
            "granular.json",
            CreateMaterialJson("test:granular", "granular", null));
        MaterialRegistry registry = new(coreDirectory, externalDirectory);

        Require(registry.RegistryHasPhaseTransitions,
            "Bundled water/ice/steam transitions did not enable registry metadata.");
        Require(registry[CoreMaterialIds.Empty].RuntimeIndex == 0,
            "core:empty no longer has runtime index 0.");
        foreach (ExpectedCoreMaterial expected in ExpectedCoreMaterials)
        {
            MaterialDefinition material = registry[expected.Id];
            MaterialProperties properties = material.Properties;
            Require((MaterialSimulationKind)properties.SimulationKind == expected.Kind,
                $"{expected.Id} kind changed.");
            Require((MaterialFlags)properties.Flags == expected.Flags, $"{expected.Id} flags changed.");
            Require(Same(properties.Density, expected.Density), $"{expected.Id} density changed.");
            Require(Same(properties.Friction, expected.Friction), $"{expected.Id} friction changed.");
            Require(Same(properties.FlowRate, expected.FlowRate), $"{expected.Id} flowRate changed.");
            Require(material.Color == ParseColor(expected.Color), $"{expected.Id} color changed.");
            Require(Same(properties.InitialTemperature, expected.InitialTemperature),
                $"{expected.Id} initialTemperature changed.");
            Require(Same(properties.ThermalConductivity, expected.Conductivity),
                $"{expected.Id} conductivity changed.");
            Require(Same(properties.HeatCapacity, expected.HeatCapacity),
                $"{expected.Id} heatCapacity changed.");
            VerifyExpectedCoreTransitions(registry, material);
        }

        MaterialProperties legacy = registry["test:granular"].Properties;
        Require(Same(legacy.InitialTemperature, MaterialRegistry.DefaultInitialTemperature),
            "Legacy JSON initialTemperature default changed.");
        Require(Same(legacy.ThermalConductivity, MaterialRegistry.DefaultThermalConductivity),
            "Legacy JSON conductivity default changed.");
        Require(Same(legacy.HeatCapacity, MaterialRegistry.DefaultHeatCapacity),
            "Legacy JSON heatCapacity default changed.");
        VerifyNoTransitions(legacy, "test:granular");

        MaterialProperties[] table = registry.CreateGpuTable();
        foreach (MaterialDefinition material in registry.Materials)
        {
            Require(SameProperties(table[material.RuntimeIndex], material.Properties),
                $"GPU table changed values for {material.Id}.");
        }
    }

    private static async Task VerifyValidRulesAsync(string root, string coreDirectory)
    {
        string directory = CreateDirectory(root, "valid");
        await WriteMaterialAsync(directory, "target-cold.json", CreateMaterialJson("test:target_cold", "solid", null));
        await WriteMaterialAsync(directory, "target-hot.json", CreateMaterialJson("test:target_hot", "gas", null));
        await WriteMaterialAsync(directory, "below.json", CreateMaterialJson(
            "test:below_only", "granular", ThermalWithTransitions(
                "{ \"below\": { \"temperature\": -10.0, \"into\": \" TEST:TARGET_COLD \" } }")));
        await WriteMaterialAsync(directory, "above.json", CreateMaterialJson(
            "test:above_only", "liquid", ThermalWithTransitions(
                "{ \"above\": { \"temperature\": 80.0, \"into\": \"test:target_hot\" } }")));
        await WriteMaterialAsync(directory, "both.json", CreateMaterialJson(
            "test:both", "liquid", ThermalWithTransitions(
                "{ \"below\": { \"temperature\": 0.0, \"into\": \"test:target_cold\" }, " +
                "\"above\": { \"temperature\": 100.0, \"into\": \"test:target_hot\" } }")));
        await WriteMaterialAsync(directory, "core-target.json", CreateMaterialJson(
            "test:core_target", "granular", ThermalWithTransitions(
                "{ \"above\": { \"temperature\": 50.0, \"into\": \"core:sand\" } }")));
        await WriteMaterialAsync(directory, "minimum.json", CreateMaterialJson(
            "test:minimum_threshold", "granular", ThermalWithTransitions(
                "{ \"below\": { \"temperature\": -273.15, \"into\": \"test:target_cold\" } }")));
        await WriteMaterialAsync(directory, "maximum.json", CreateMaterialJson(
            "test:maximum_threshold", "granular", ThermalWithTransitions(
                "{ \"above\": { \"temperature\": 5000.0, \"into\": \"test:target_hot\" } }")));

        MaterialRegistry registry = new(coreDirectory, directory);
        Require(registry.RegistryHasPhaseTransitions, "Valid transitions did not set registry metadata.");
        VerifyRule(registry, "test:below_only", true, -10, "test:target_cold", false, 0, null);
        VerifyRule(registry, "test:above_only", false, 0, null, true, 80, "test:target_hot");
        VerifyRule(registry, "test:both", true, 0, "test:target_cold", true, 100, "test:target_hot");
        VerifyRule(registry, "test:core_target", false, 0, null, true, 50, CoreMaterialIds.Sand);
        VerifyRule(registry, "test:minimum_threshold", true, -273.15f, "test:target_cold", false, 0, null);
        VerifyRule(registry, "test:maximum_threshold", false, 0, null, true, 5000, "test:target_hot");
    }

    private static async Task VerifyRuntimeResolutionAsync(string root, string coreDirectory)
    {
        string first = CreateDirectory(root, "runtime-order-first");
        string second = CreateDirectory(root, "runtime-order-second");
        string shifted = CreateDirectory(root, "runtime-order-shifted");
        string sourceJson = CreateMaterialJson(
            "test:zz_source", "granular", ThermalWithTransitions(
                "{ \"above\": { \"temperature\": 10.0, \"into\": \"test:zz_target\" } }"));
        string targetJson = CreateMaterialJson("test:zz_target", "gas", null);
        await WriteMaterialAsync(first, "a-source.json", sourceJson);
        await WriteMaterialAsync(first, "z-target.json", targetJson);
        await WriteMaterialAsync(second, "z-source.json", sourceJson);
        await WriteMaterialAsync(second, "a-target.json", targetJson);
        await WriteMaterialAsync(shifted, "source.json", sourceJson);
        await WriteMaterialAsync(shifted, "target.json", targetJson);
        await WriteMaterialAsync(shifted, "padding.json", CreateMaterialJson("test:aa_padding", "solid", null));

        MaterialRegistry firstRegistry = new(coreDirectory, first);
        MaterialRegistry secondRegistry = new(coreDirectory, second);
        MaterialRegistry shiftedRegistry = new(coreDirectory, shifted);
        uint firstTargetIndex = firstRegistry["test:zz_source"].Properties.TransitionAboveMaterialIndex;
        uint secondTargetIndex = secondRegistry["test:zz_source"].Properties.TransitionAboveMaterialIndex;
        uint shiftedTargetIndex = shiftedRegistry["test:zz_source"].Properties.TransitionAboveMaterialIndex;
        Require(firstTargetIndex == firstRegistry["test:zz_target"].RuntimeIndex,
            "First registry target resolution is incorrect.");
        Require(secondTargetIndex == secondRegistry["test:zz_target"].RuntimeIndex,
            "File order changed target resolution.");
        Require(firstTargetIndex == secondTargetIndex,
            "File names changed deterministic runtime ordering.");
        Require(shiftedTargetIndex == shiftedRegistry["test:zz_target"].RuntimeIndex,
            "Shifted registry retained a stale target index.");
        Require(shiftedTargetIndex != firstTargetIndex,
            "Runtime-order test did not shift the target index.");
        Require(shiftedRegistry["test:zz_source"].PhaseTransitions!.Above!.IntoId == "test:zz_target",
            "Raw target ID changed during runtime resolution.");
        Require(shiftedRegistry.CreateGpuTable()[shiftedRegistry["test:zz_source"].RuntimeIndex]
                .TransitionAboveMaterialIndex == shiftedTargetIndex,
            "GPU table did not use the current registry target index.");
    }

    private static async Task VerifyInvalidDocumentsAsync(string root, string coreDirectory)
    {
        string directory = CreateDirectory(root, "invalid-documents");
        await WriteMaterialAsync(directory, "valid-target.json", CreateMaterialJson("test:valid_target", "solid", null));
        Dictionary<string, string> invalid = new()
        {
            ["unknown-transitions.json"] = CreateMaterialJson("test:unknown_transitions", "granular",
                ThermalWithTransitions("{ \"sideways\": { \"temperature\": 0, \"into\": \"test:valid_target\" } }")),
            ["empty-transitions.json"] = CreateMaterialJson("test:empty_transitions", "granular", ThermalWithTransitions("{}")),
            ["null-transitions.json"] = CreateMaterialJson("test:null_transitions", "granular", StandardThermal() + ", \"transitions\": null"),
            ["array-transitions.json"] = CreateMaterialJson("test:array_transitions", "granular", StandardThermal() + ", \"transitions\": []"),
            ["null-below.json"] = CreateMaterialJson("test:null_below", "granular", ThermalWithTransitions("{ \"below\": null }")),
            ["array-below.json"] = CreateMaterialJson("test:array_below", "granular", ThermalWithTransitions("{ \"below\": [] }")),
            ["empty-below.json"] = CreateMaterialJson("test:empty_below", "granular", ThermalWithTransitions("{ \"below\": {} }")),
            ["missing-temperature.json"] = CreateMaterialJson("test:missing_temperature", "granular",
                ThermalWithTransitions("{ \"below\": { \"into\": \"test:valid_target\" } }")),
            ["missing-into.json"] = CreateMaterialJson("test:missing_into", "granular",
                ThermalWithTransitions("{ \"below\": { \"temperature\": 0 } }")),
            ["nan.json"] = CreateMaterialJson("test:nan_threshold", "granular",
                ThermalWithTransitions("{ \"below\": { \"temperature\": NaN, \"into\": \"test:valid_target\" } }")),
            ["infinity.json"] = CreateMaterialJson("test:infinity_threshold", "granular",
                ThermalWithTransitions("{ \"above\": { \"temperature\": Infinity, \"into\": \"test:valid_target\" } }")),
            ["threshold-low.json"] = CreateMaterialJson("test:threshold_low", "granular",
                ThermalWithTransitions("{ \"below\": { \"temperature\": -273.16, \"into\": \"test:valid_target\" } }")),
            ["threshold-high.json"] = CreateMaterialJson("test:threshold_high", "granular",
                ThermalWithTransitions("{ \"above\": { \"temperature\": 5000.01, \"into\": \"test:valid_target\" } }")),
            ["invalid-id.json"] = CreateMaterialJson("test:invalid_target_id", "granular",
                ThermalWithTransitions("{ \"below\": { \"temperature\": 0, \"into\": \"not an id\" } }")),
            ["overlap.json"] = CreateMaterialJson("test:overlap", "granular",
                ThermalWithTransitions("{ \"below\": { \"temperature\": 10, \"into\": \"test:valid_target\" }, " +
                    "\"above\": { \"temperature\": 10, \"into\": \"core:sand\" } }")),
            ["none-source.json"] = CreateMaterialJson("test:none_source", "none",
                ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:valid_target\" } }")),
            ["tool-source.json"] = CreateMaterialJson("test:tool_source", "tool",
                ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:valid_target\" } }")),
            ["self.json"] = CreateMaterialJson("test:self", "granular",
                ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:self\" } }")),
            ["unknown-direction.json"] = CreateMaterialJson("test:unknown_direction", "granular",
                ThermalWithTransitions("{ \"below\": { \"temperature\": 0, \"into\": \"test:valid_target\", \"typo\": 1 } }")),
            ["unknown-thermal.json"] = CreateMaterialJson("test:unknown_thermal_phase", "granular",
                StandardThermal() + ", \"transitions\": { \"above\": { \"temperature\": 0, \"into\": \"test:valid_target\" } }, \"typo\": 1")
        };
        foreach ((string fileName, string json) in invalid)
        {
            await WriteMaterialAsync(directory, fileName, json);
        }

        (MaterialRegistry registry, string errors) = LoadWithCapturedErrors(coreDirectory, directory);
        foreach (string fileName in invalid.Keys)
        {
            Require(errors.Contains(fileName, StringComparison.Ordinal),
                $"Invalid document '{fileName}' did not produce a path-specific error.");
        }
        Require(CountOccurrences(errors, "PHYXEL_MATERIAL_ERROR") == invalid.Count,
            "Invalid documents did not produce exactly one error each.");
        Require(registry.Count == ExpectedCoreMaterials.Length + 1,
            "An invalid transition document entered the registry.");
        Require(registry.RegistryHasPhaseTransitions,
            "Invalid external documents disabled bundled core transitions.");
        Require(errors.Contains("thermal.transitions", StringComparison.Ordinal),
            "Nested validation errors do not identify thermal.transitions.");
    }

    private static async Task VerifyInvalidRegistryReferencesAsync(string root, string coreDirectory)
    {
        string directory = CreateDirectory(root, "invalid-references");
        await WriteMaterialAsync(directory, "none-target.json", CreateMaterialJson("test:none_target", "none", null));
        await WriteMaterialAsync(directory, "tool-target.json", CreateMaterialJson("test:tool_target", "tool", null));
        await WriteMaterialAsync(directory, "to-none.json", CreateMaterialJson("test:to_none", "granular",
            ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:none_target\" } }")));
        await WriteMaterialAsync(directory, "to-tool.json", CreateMaterialJson("test:to_tool", "granular",
            ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:tool_target\" } }")));
        await WriteMaterialAsync(directory, "to-empty.json", CreateMaterialJson("test:to_empty", "granular",
            ThermalWithTransitions("{ \"below\": { \"temperature\": 0, \"into\": \"core:empty\" } }")));
        await WriteMaterialAsync(directory, "to-missing.json", CreateMaterialJson("test:to_missing", "granular",
            ThermalWithTransitions("{ \"below\": { \"temperature\": 0, \"into\": \"test:missing\" } }")));

        (MaterialRegistry registry, string errors) = LoadWithCapturedErrors(coreDirectory, directory);
        foreach (string id in new[] { "test:to_none", "test:to_tool", "test:to_empty", "test:to_missing" })
        {
            Require(!registry.TryGet(id, out _), $"Invalid registry source '{id}' was retained.");
            Require(errors.Contains(id, StringComparison.Ordinal), $"Registry error does not identify '{id}'.");
        }
        Require(registry.TryGet("test:none_target", out _), "Unreferenced none target was unexpectedly removed.");
        Require(registry.TryGet("test:tool_target", out _), "Unreferenced tool target was unexpectedly removed.");
    }

    private static async Task VerifyExternalDependencyCascadeAsync(string root, string coreDirectory)
    {
        string directory = CreateDirectory(root, "dependency-cascade");
        await WriteMaterialAsync(directory, "a.json", CreateMaterialJson("test:cascade_a", "granular",
            ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:missing\" } }")));
        await WriteMaterialAsync(directory, "b.json", CreateMaterialJson("test:cascade_b", "liquid",
            ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:cascade_a\" } }")));
        await WriteMaterialAsync(directory, "c.json", CreateMaterialJson("test:cascade_c", "gas",
            ThermalWithTransitions("{ \"above\": { \"temperature\": 0, \"into\": \"test:cascade_b\" } }")));

        (MaterialRegistry registry, string errors) = LoadWithCapturedErrors(coreDirectory, directory);
        foreach (string id in new[] { "test:cascade_a", "test:cascade_b", "test:cascade_c" })
        {
            Require(!registry.TryGet(id, out _), $"Dependency cascade retained '{id}'.");
            Require(errors.Contains(id, StringComparison.Ordinal),
                $"Dependency cascade did not report '{id}'.");
        }
        Require(CountOccurrences(errors, "PHYXEL_MATERIAL_ERROR") == 3,
            "Dependency cascade did not report each removed source exactly once.");
        Require(registry.RegistryHasPhaseTransitions,
            "Removed dependency cascade disabled bundled core transitions.");
    }

    private static async Task VerifyCoreFailuresAsync(string root, string sourceCoreDirectory)
    {
        string missingCore = await CopyCoreDirectoryAsync(root, "core-missing-target", sourceCoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(missingCore, "water.json"), CreateMaterialJson(
            CoreMaterialIds.Water, "liquid", ThermalWithTransitions(
                "{ \"above\": { \"temperature\": 100, \"into\": \"core:missing\" } }")));
        ExpectRegistryFailure(missingCore, CreateDirectory(root, "core-missing-external"),
            CoreMaterialIds.Water, "core:missing");

        string externalCore = await CopyCoreDirectoryAsync(root, "core-external-target", sourceCoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(externalCore, "water.json"), CreateMaterialJson(
            CoreMaterialIds.Water, "liquid", ThermalWithTransitions(
                "{ \"above\": { \"temperature\": 100, \"into\": \"test:external_target\" } }")));
        string externalDirectory = CreateDirectory(root, "core-external-materials");
        await WriteMaterialAsync(externalDirectory, "target.json", CreateMaterialJson("test:external_target", "gas", null));
        ExpectRegistryFailure(externalCore, externalDirectory, CoreMaterialIds.Water, "test:external_target");

        string toolCore = await CopyCoreDirectoryAsync(root, "core-tool-target", sourceCoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(toolCore, "water.json"), CreateMaterialJson(
            CoreMaterialIds.Water, "liquid", ThermalWithTransitions(
                "{ \"above\": { \"temperature\": 100, \"into\": \"core:eraser\" } }")));
        ExpectRegistryFailure(toolCore, CreateDirectory(root, "core-tool-external"),
            CoreMaterialIds.Water, CoreMaterialIds.Eraser);
    }

    private static async Task VerifyCycleValidationAsync(string root, string coreDirectory)
    {
        string safe = CreateDirectory(root, "cycles-safe");
        await WritePairAsync(safe,
            "test:water_like", "below", 0, "test:ice_like",
            "test:ice_like", "above", 2, "test:water_like");
        await WritePairAsync(safe,
            "test:water_hot", "above", 100, "test:steam_like",
            "test:steam_like", "below", 95, "test:water_hot");
        await WriteMaterialAsync(safe, "safe-a.json", CreateDirectionalMaterial(
            "test:safe_a", "above", 10, "test:safe_b"));
        await WriteMaterialAsync(safe, "safe-b.json", CreateDirectionalMaterial(
            "test:safe_b", "below", 20, "test:safe_c"));
        await WriteMaterialAsync(safe, "safe-c.json", CreateDirectionalMaterial(
            "test:safe_c", "above", 30, "test:safe_a"));
        MaterialRegistry safeRegistry = new(coreDirectory, safe);
        foreach (string id in new[]
        {
            "test:water_like", "test:ice_like", "test:water_hot", "test:steam_like",
            "test:safe_a", "test:safe_b", "test:safe_c"
        })
        {
            Require(safeRegistry.TryGet(id, out _), $"Safe hysteresis graph rejected '{id}'.");
        }

        string twoNode = CreateDirectory(root, "cycles-two-node");
        await WritePairAsync(twoNode,
            "test:cycle_a", "above", 10, "test:cycle_b",
            "test:cycle_b", "below", 20, "test:cycle_a");
        (MaterialRegistry twoRegistry, string twoErrors) = LoadWithCapturedErrors(coreDirectory, twoNode);
        Require(!twoRegistry.TryGet("test:cycle_a", out _) && !twoRegistry.TryGet("test:cycle_b", out _),
            "Two-node instantaneous cycle was accepted.");
        Require(twoErrors.Contains("instantaneous", StringComparison.OrdinalIgnoreCase),
            "Two-node cycle error is unclear.");

        string threeNode = CreateDirectory(root, "cycles-three-node");
        await WriteMaterialAsync(threeNode, "a.json", CreateDirectionalMaterial(
            "test:cycle3_a", "above", 10, "test:cycle3_b"));
        await WriteMaterialAsync(threeNode, "b.json", CreateDirectionalMaterial(
            "test:cycle3_b", "below", 20, "test:cycle3_c"));
        await WriteMaterialAsync(threeNode, "c.json", CreateDirectionalMaterial(
            "test:cycle3_c", "above", 5, "test:cycle3_a"));
        (MaterialRegistry threeRegistry, string threeErrors) = LoadWithCapturedErrors(coreDirectory, threeNode);
        foreach (string id in new[] { "test:cycle3_a", "test:cycle3_b", "test:cycle3_c" })
        {
            Require(!threeRegistry.TryGet(id, out _), $"Three-node instantaneous cycle retained '{id}'.");
            Require(threeErrors.Contains(id, StringComparison.Ordinal),
                $"Three-node cycle did not report '{id}'.");
        }

        string coreCycle = await CopyCoreDirectoryAsync(root, "core-cycle", coreDirectory);
        await File.WriteAllTextAsync(Path.Combine(coreCycle, "water.json"), CreateDirectionalMaterial(
            CoreMaterialIds.Water, "above", 10, CoreMaterialIds.Sand, "liquid"));
        await File.WriteAllTextAsync(Path.Combine(coreCycle, "sand.json"), CreateDirectionalMaterial(
            CoreMaterialIds.Sand, "below", 20, CoreMaterialIds.Water, "granular"));
        ExpectRegistryFailure(coreCycle, CreateDirectory(root, "core-cycle-external"),
            "instantaneous", CoreMaterialIds.Water);
    }

    private static void VerifyExpectedCoreTransitions(
        MaterialRegistry registry,
        MaterialDefinition material)
    {
        switch (material.Id)
        {
            case CoreMaterialIds.Water:
                VerifyRule(registry, material.Id, true, 0, CoreMaterialIds.Ice,
                    true, 100, CoreMaterialIds.Steam);
                break;
            case CoreMaterialIds.Ice:
                VerifyRule(registry, material.Id, false, 0, null,
                    true, 2, CoreMaterialIds.Water);
                break;
            case CoreMaterialIds.Steam:
                VerifyRule(registry, material.Id, true, 95, CoreMaterialIds.Water,
                    false, 0, null);
                break;
            default:
                Require(material.PhaseTransitions is null,
                    $"{material.Id} unexpectedly has raw transitions.");
                VerifyNoTransitions(material.Properties, material.Id);
                break;
        }
    }

    private static void VerifyRule(
        MaterialRegistry registry,
        string sourceId,
        bool hasBelow,
        float belowTemperature,
        string? belowTarget,
        bool hasAbove,
        float aboveTemperature,
        string? aboveTarget)
    {
        MaterialDefinition source = registry[sourceId];
        MaterialProperties properties = source.Properties;
        MaterialProperties gpu = registry.CreateGpuTable()[source.RuntimeIndex];
        Require((source.PhaseTransitions?.Below is not null) == hasBelow,
            $"{sourceId} raw below rule is incorrect.");
        Require((source.PhaseTransitions?.Above is not null) == hasAbove,
            $"{sourceId} raw above rule is incorrect.");
        if (hasBelow)
        {
            Require(Same(properties.TransitionBelowTemperature, belowTemperature),
                $"{sourceId} below threshold is incorrect.");
            Require(source.PhaseTransitions!.Below!.IntoId == belowTarget,
                $"{sourceId} raw below target is incorrect.");
            Require(properties.TransitionBelowMaterialIndex == registry[belowTarget!].RuntimeIndex,
                $"{sourceId} below runtime target is incorrect.");
        }
        else
        {
            Require(properties.TransitionBelowMaterialIndex == uint.MaxValue,
                $"{sourceId} missing below rule lacks sentinel.");
        }
        if (hasAbove)
        {
            Require(Same(properties.TransitionAboveTemperature, aboveTemperature),
                $"{sourceId} above threshold is incorrect.");
            Require(source.PhaseTransitions!.Above!.IntoId == aboveTarget,
                $"{sourceId} raw above target is incorrect.");
            Require(properties.TransitionAboveMaterialIndex == registry[aboveTarget!].RuntimeIndex,
                $"{sourceId} above runtime target is incorrect.");
        }
        else
        {
            Require(properties.TransitionAboveMaterialIndex == uint.MaxValue,
                $"{sourceId} missing above rule lacks sentinel.");
        }
        Require(SameProperties(gpu, properties), $"{sourceId} GPU table entry is incorrect.");
    }

    private static void VerifyNoTransitions(MaterialProperties properties, string id)
    {
        Require(Same(properties.TransitionBelowTemperature, 0), $"{id} missing below threshold is not zero.");
        Require(properties.TransitionBelowMaterialIndex == uint.MaxValue, $"{id} missing below sentinel is wrong.");
        Require(Same(properties.TransitionAboveTemperature, 0), $"{id} missing above threshold is not zero.");
        Require(properties.TransitionAboveMaterialIndex == uint.MaxValue, $"{id} missing above sentinel is wrong.");
    }

    private static (MaterialRegistry Registry, string Errors) LoadWithCapturedErrors(
        string coreDirectory,
        string externalDirectory)
    {
        TextWriter originalError = Console.Error;
        using StringWriter captured = new();
        try
        {
            Console.SetError(captured);
            MaterialRegistry registry = new(coreDirectory, externalDirectory);
            return (registry, captured.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static void ExpectRegistryFailure(
        string coreDirectory,
        string externalDirectory,
        params string[] expectedMessages)
    {
        try
        {
            _ = new MaterialRegistry(coreDirectory, externalDirectory);
        }
        catch (InvalidDataException exception)
        {
            foreach (string expected in expectedMessages)
            {
                Require(exception.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase),
                    $"Core failure does not identify '{expected}'.");
            }
            return;
        }
        throw new InvalidOperationException("Invalid core phase transition did not stop registry startup.");
    }

    private static async Task WritePairAsync(
        string directory,
        string firstId,
        string firstDirection,
        float firstTemperature,
        string firstTarget,
        string secondId,
        string secondDirection,
        float secondTemperature,
        string secondTarget)
    {
        await WriteMaterialAsync(directory, firstId[(firstId.IndexOf(':') + 1)..] + ".json",
            CreateDirectionalMaterial(firstId, firstDirection, firstTemperature, firstTarget));
        await WriteMaterialAsync(directory, secondId[(secondId.IndexOf(':') + 1)..] + ".json",
            CreateDirectionalMaterial(secondId, secondDirection, secondTemperature, secondTarget));
    }

    private static string CreateDirectionalMaterial(
        string id,
        string direction,
        float temperature,
        string target,
        string kind = "granular") =>
        CreateMaterialJson(
            id,
            kind,
            ThermalWithTransitions(
                $"{{ \"{direction}\": {{ \"temperature\": {temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"into\": \"{target}\" }} }}"));

    private static string StandardThermal() =>
        "\"initialTemperature\": 20.0, \"conductivity\": 0.15, \"heatCapacity\": 1.0";

    private static string ThermalWithTransitions(string transitions) =>
        StandardThermal() + $", \"transitions\": {transitions}";

    private static string CreateMaterialJson(string id, string kind, string? thermalBody)
    {
        const string template = """
            {
              "schema": 1,
              "id": "__ID__",
              "name": "Test",
              "kind": "__KIND__",
              "color": "#12345678",
              "physics": { "density": 1.0, "friction": 0.2, "flowRate": 0.3 }
              __THERMAL__
            }
            """;
        string thermal = thermalBody is null ? string.Empty : $",\n  \"thermal\": {{ {thermalBody} }}";
        return template
            .Replace("__ID__", id, StringComparison.Ordinal)
            .Replace("__KIND__", kind, StringComparison.Ordinal)
            .Replace("__THERMAL__", thermal, StringComparison.Ordinal);
    }

    private static async Task WriteMaterialAsync(string directory, string fileName, string json) =>
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), json);

    private static string CreateDirectory(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> CopyCoreDirectoryAsync(
        string root,
        string name,
        string sourceDirectory)
    {
        string destination = CreateDirectory(root, name);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*.json", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using FileStream source = File.OpenRead(sourcePath);
            await using FileStream target = File.Create(destinationPath);
            await source.CopyToAsync(target);
        }
        return destination;
    }

    private static bool SameProperties(MaterialProperties left, MaterialProperties right) =>
        left.Flags == right.Flags &&
        left.SimulationKind == right.SimulationKind &&
        Same(left.Density, right.Density) &&
        Same(left.Friction, right.Friction) &&
        Same(left.FlowRate, right.FlowRate) &&
        Same(left.ColorR, right.ColorR) &&
        Same(left.ColorG, right.ColorG) &&
        Same(left.ColorB, right.ColorB) &&
        Same(left.ColorA, right.ColorA) &&
        Same(left.InitialTemperature, right.InitialTemperature) &&
        Same(left.ThermalConductivity, right.ThermalConductivity) &&
        Same(left.HeatCapacity, right.HeatCapacity) &&
        Same(left.TransitionBelowTemperature, right.TransitionBelowTemperature) &&
        left.TransitionBelowMaterialIndex == right.TransitionBelowMaterialIndex &&
        Same(left.TransitionAboveTemperature, right.TransitionAboveTemperature) &&
        left.TransitionAboveMaterialIndex == right.TransitionAboveMaterialIndex;

    private static Color ParseColor(string value)
    {
        string hex = value[1..];
        return new Color(
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16),
            hex.Length == 8 ? Convert.ToByte(hex[6..8], 16) : byte.MaxValue);
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }
        return count;
    }

    private static bool Same(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
