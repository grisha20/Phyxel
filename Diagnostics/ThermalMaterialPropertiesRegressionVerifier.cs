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

internal static class ThermalMaterialPropertiesRegressionVerifier
{
    private sealed record ExpectedMaterial(
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

    private static readonly ExpectedMaterial[] ExpectedCoreMaterials =
    [
        new(CoreMaterialIds.Empty, MaterialSimulationKind.None, MaterialFlags.None,
            0f, 0f, 0f, "#00000000", 0f, 0f, 1f),
        new(CoreMaterialIds.Sand, MaterialSimulationKind.Granular, MaterialFlags.None,
            1.5f, 0.75f, 0.18f, "#DAB85C", 20f, 0.15f, 0.83f),
        new(CoreMaterialIds.Water, MaterialSimulationKind.Liquid, MaterialFlags.None,
            1f, 0.025f, 0.92f, "#2B84CF", 20f, 0.60f, 4.18f),
        new(CoreMaterialIds.Ice, MaterialSimulationKind.Solid, MaterialFlags.None,
            0.92f, 0.10f, 0f, "#A9DDF2", -5f, 0.80f, 2.10f),
        new(CoreMaterialIds.Steam, MaterialSimulationKind.Gas, MaterialFlags.None,
            0.03f, 0.005f, 1.20f, "#DDEBF0A0", 105f, 0.04f, 2.08f),
        new(CoreMaterialIds.Metal, MaterialSimulationKind.Solid, MaterialFlags.MovableSolid,
            7.8f, 0.35f, 0f, "#8E9CA6", 20f, 1f, 0.50f),
        new(CoreMaterialIds.Stone, MaterialSimulationKind.Solid, MaterialFlags.MovableSolid,
            9.2f, 0.75f, 0f, "#5C6065", 20f, 0.25f, 0.84f),
        new(CoreMaterialIds.Gas, MaterialSimulationKind.Gas, MaterialFlags.None,
            0.08f, 0.005f, 1.2f, "#9BC4D29B", 20f, 0.03f, 1f),
        new(CoreMaterialIds.Fixture, MaterialSimulationKind.Solid, MaterialFlags.None,
            100f, 0.9f, 0f, "#525B63", 20f, 0.25f, 0.84f),
        new(CoreMaterialIds.Wood, MaterialSimulationKind.Solid, MaterialFlags.None,
            0.8f, 0.65f, 0f, "#8B5A2B", 20f, 0.65f, 1.0f),
        new(CoreMaterialIds.Coal, MaterialSimulationKind.Granular, MaterialFlags.None,
            0.2f, 0.6f, 0.35f, "#292929", 20f, 0.78f, 1.0f),
        new(CoreMaterialIds.Smoke, MaterialSimulationKind.Gas, MaterialFlags.None,
            0.04f, 0f, 1f, "#777777B0", 120f, 0.08f, 1f),
        new(CoreMaterialIds.Co2, MaterialSimulationKind.Gas, MaterialFlags.None,
            0.06f, 0f, 0.8f, "#B5B5B580", 120f, 0.06f, 0.85f),
        new(CoreMaterialIds.Fire, MaterialSimulationKind.Gas, MaterialFlags.Flame,
            1.0f, 0f, 1.4f, "#FF3A08E8", 650f, 0.35f, 1.0f),
        new(CoreMaterialIds.Eraser, MaterialSimulationKind.Tool, MaterialFlags.None,
            0f, 0f, 0f, "#DE5858", 20f, 0f, 1f)
    ];

    public static int Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("PHYXEL_THERMAL_MATERIALS_SUCCESS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYXEL_THERMAL_MATERIALS_FAILED {exception}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        Require(Marshal.SizeOf<MaterialProperties>() == 112, "MaterialProperties must be 112 bytes.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.AmbientTemperature)).ToInt32() == 104,
            "AmbientTemperature offset must be 104.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.AmbientCoolingRate)).ToInt32() == 108,
            "AmbientCoolingRate offset must be 108.");
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"phyxel-thermal-materials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await WriteExternalMaterialsAsync(directory);
            MaterialRegistry registry;
            string materialErrors;
            TextWriter originalError = Console.Error;
            using StringWriter capturedError = new();
            try
            {
                Console.SetError(capturedError);
                registry = new MaterialRegistry(directory);
            }
            finally
            {
                Console.SetError(originalError);
            }
            materialErrors = capturedError.ToString();

            VerifyCoreMaterials(registry);
            VerifyExternalMaterials(registry, materialErrors);
            VerifyGpuTable(registry);
            await VerifyInvalidCoreStopsLoadingAsync(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void VerifyCoreMaterials(MaterialRegistry registry)
    {
        Require(ExpectedCoreMaterials.Length == 15, "Expected bundled core material count changed.");
        foreach (ExpectedMaterial expected in ExpectedCoreMaterials)
        {
            MaterialDefinition actual = registry[expected.Id];
            MaterialProperties properties = actual.Properties;
            Require((MaterialSimulationKind)properties.SimulationKind == expected.Kind,
                $"{expected.Id} SimulationKind changed.");
            Require((MaterialFlags)properties.Flags == expected.Flags,
                $"{expected.Id} flags changed.");
            Require(Same(properties.Density, expected.Density), $"{expected.Id} density changed.");
            Require(Same(properties.Friction, expected.Friction), $"{expected.Id} friction changed.");
            Require(Same(properties.FlowRate, expected.FlowRate), $"{expected.Id} flowRate changed.");
            Color expectedColor = ParseColor(expected.Color);
            Require(actual.Color == expectedColor, $"{expected.Id} color changed.");
            Require(Same(properties.ColorR, expectedColor.R / 255f), $"{expected.Id} GPU red changed.");
            Require(Same(properties.ColorG, expectedColor.G / 255f), $"{expected.Id} GPU green changed.");
            Require(Same(properties.ColorB, expectedColor.B / 255f), $"{expected.Id} GPU blue changed.");
            Require(Same(properties.ColorA, expectedColor.A / 255f), $"{expected.Id} GPU alpha changed.");
            Require(Same(properties.InitialTemperature, expected.InitialTemperature),
                $"{expected.Id} initialTemperature is incorrect.");
            Require(Same(properties.ThermalConductivity, expected.Conductivity),
                $"{expected.Id} conductivity is incorrect.");
            Require(Same(properties.HeatCapacity, expected.HeatCapacity),
                $"{expected.Id} heatCapacity is incorrect.");
            float expectedAmbientTemperature = expected.Id == CoreMaterialIds.Steam ? 20f : 0f;
            float expectedAmbientRate = expected.Id == CoreMaterialIds.Steam ? 0.04f : 0f;
            Require(Same(properties.AmbientTemperature, expectedAmbientTemperature) &&
                Same(properties.AmbientCoolingRate, expectedAmbientRate),
                $"{expected.Id} ambient cooling is incorrect.");
            VerifyCoreTransitions(registry, expected.Id, properties);
        }
        MaterialDefinition water = registry[CoreMaterialIds.Water];
        MaterialDefinition ice = registry[CoreMaterialIds.Ice];
        MaterialDefinition steam = registry[CoreMaterialIds.Steam];
        Require(water.UiOrder == 20 && ice.UiOrder == 21 && steam.UiOrder == 22,
            "Water/Ice/Steam UI order is not 20/21/22.");
        Require(!water.Hidden && !ice.Hidden && !steam.Hidden,
            "Water/Ice/Steam must be visible in the material menu.");
        Require(water.Name == "Вода" && ice.Name == "Лёд" && steam.Name == "Пар",
            "Water/Ice/Steam Russian names changed.");
        string[] visibleIds = registry.SelectableMaterials.Select(material => material.Id).ToArray();
        int waterPosition = Array.IndexOf(visibleIds, CoreMaterialIds.Water);
        Require(waterPosition >= 0 && waterPosition + 2 < visibleIds.Length &&
            visibleIds[waterPosition + 1] == CoreMaterialIds.Ice &&
            visibleIds[waterPosition + 2] == CoreMaterialIds.Steam,
            "Material menu does not place Water, Ice and Steam consecutively.");
    }

    private static void VerifyCoreTransitions(
        MaterialRegistry registry,
        string id,
        MaterialProperties properties)
    {
        switch (id)
        {
            case CoreMaterialIds.Water:
                Require(Same(properties.TransitionBelowTemperature, 0f),
                    "core:water freeze threshold changed.");
                Require(properties.TransitionBelowMaterialIndex == registry[CoreMaterialIds.Ice].RuntimeIndex,
                    "core:water freeze target is not core:ice.");
                Require(Same(properties.TransitionAboveTemperature, 100f),
                    "core:water boiling threshold changed.");
                Require(properties.TransitionAboveMaterialIndex == registry[CoreMaterialIds.Steam].RuntimeIndex,
                    "core:water boiling target is not core:steam.");
                break;
            case CoreMaterialIds.Ice:
                Require(properties.TransitionBelowMaterialIndex == uint.MaxValue,
                    "core:ice unexpectedly has a below transition.");
                Require(Same(properties.TransitionAboveTemperature, 2f),
                    "core:ice melting threshold changed.");
                Require(properties.TransitionAboveMaterialIndex == registry[CoreMaterialIds.Water].RuntimeIndex,
                    "core:ice melting target is not core:water.");
                break;
            case CoreMaterialIds.Steam:
                Require(Same(properties.TransitionBelowTemperature, 95f),
                    "core:steam condensation threshold changed.");
                Require(properties.TransitionBelowMaterialIndex == registry[CoreMaterialIds.Water].RuntimeIndex,
                    "core:steam condensation target is not core:water.");
                Require(properties.TransitionAboveMaterialIndex == uint.MaxValue,
                    "core:steam unexpectedly has an above transition.");
                break;
            default:
                Require(properties.TransitionBelowMaterialIndex == uint.MaxValue,
                    $"{id} unexpectedly has a below phase transition.");
                Require(properties.TransitionAboveMaterialIndex == uint.MaxValue,
                    $"{id} unexpectedly has an above phase transition.");
                break;
        }
    }

    private static void VerifyExternalMaterials(MaterialRegistry registry, string errors)
    {
        MaterialProperties legacy = registry["test:granular"].Properties;
        Require(Same(legacy.InitialTemperature, MaterialRegistry.DefaultInitialTemperature),
            "External material without thermal did not receive default initialTemperature.");
        Require(Same(legacy.ThermalConductivity, MaterialRegistry.DefaultThermalConductivity),
            "External material without thermal did not receive default conductivity.");
        Require(Same(legacy.HeatCapacity, MaterialRegistry.DefaultHeatCapacity),
            "External material without thermal did not receive default heatCapacity.");
        Require(Same(legacy.AmbientTemperature, 0) && Same(legacy.AmbientCoolingRate, 0),
            "External material without ambientCooling changed thermal behavior.");

        MaterialProperties custom = registry["test:custom_thermal"].Properties;
        Require(Same(custom.InitialTemperature, -125.5f), "Custom initialTemperature was not loaded.");
        Require(Same(custom.ThermalConductivity, 0.72f), "Custom conductivity was not loaded.");
        Require(Same(custom.HeatCapacity, 8.25f), "Custom heatCapacity was not loaded.");
        Require(Same(custom.AmbientTemperature, -10f) && Same(custom.AmbientCoolingRate, 2.5f),
            "Custom ambientCooling was not loaded.");

        MaterialProperties minimums = registry["test:thermal_minimums"].Properties;
        Require(Same(minimums.InitialTemperature, MaterialRegistry.MinimumInitialTemperature),
            "Minimum initialTemperature boundary was rejected.");
        Require(Same(minimums.ThermalConductivity, MaterialRegistry.MinimumThermalConductivity),
            "Minimum conductivity boundary was rejected.");
        Require(Same(minimums.HeatCapacity, MaterialRegistry.MinimumHeatCapacity),
            "Minimum heatCapacity boundary was rejected.");
        MaterialProperties maximums = registry["test:thermal_maximums"].Properties;
        Require(Same(maximums.InitialTemperature, MaterialRegistry.MaximumInitialTemperature),
            "Maximum initialTemperature boundary was rejected.");
        Require(Same(maximums.ThermalConductivity, MaterialRegistry.MaximumThermalConductivity),
            "Maximum conductivity boundary was rejected.");
        Require(Same(maximums.HeatCapacity, MaterialRegistry.MaximumHeatCapacity),
            "Maximum heatCapacity boundary was rejected.");

        string[] invalidIds =
        [
            "test:nan", "test:infinity", "test:temperature_low", "test:temperature_high",
            "test:conductivity_low", "test:conductivity_high", "test:heat_capacity_zero",
            "test:heat_capacity_low", "test:heat_capacity_high", "test:unknown_thermal",
            "test:ambient_not_object", "test:ambient_missing_temperature",
            "test:ambient_missing_rate", "test:ambient_rate_zero", "test:ambient_rate_high",
            "test:ambient_temperature_low", "test:ambient_unknown"
        ];
        foreach (string id in invalidIds)
        {
            Require(!registry.TryGet(id, out _), $"Invalid external material '{id}' was loaded.");
        }
        Require(CountOccurrences(errors, "PHYXEL_MATERIAL_ERROR") == invalidIds.Length,
            "Invalid external materials did not produce one PHYXEL_MATERIAL_ERROR each.");
    }

    private static void VerifyGpuTable(MaterialRegistry registry)
    {
        MaterialProperties[] table = registry.CreateGpuTable();
        Require(table.Length == registry.Count, "GPU material table length is incorrect.");
        foreach (MaterialDefinition material in registry.Materials)
        {
            MaterialProperties expected = material.Properties;
            MaterialProperties actual = table[material.RuntimeIndex];
            Require(SameProperties(actual, expected),
                $"GPU material table entry {material.RuntimeIndex} does not match {material.Id}.");
        }
    }

    private static async Task VerifyInvalidCoreStopsLoadingAsync(string parentDirectory)
    {
        string coreDirectory = Path.Combine(parentDirectory, "invalid-core");
        Directory.CreateDirectory(coreDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(coreDirectory, "invalid.json"),
            CreateMaterialJson(
                "core:invalid",
                "{ \"initialTemperature\": 20.0, \"conductivity\": 0.15, \"heatCapacity\": 1.0, \"typo\": 2.0 }"));
        try
        {
            MaterialFileLoader.LoadCore(coreDirectory, MaterialRegistry.MaximumMaterials);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid core thermal data did not stop loading.");
    }

    private static async Task WriteExternalMaterialsAsync(string directory)
    {
        Dictionary<string, string?> materials = new()
        {
            ["granular.json"] = CreateMaterialJson("test:granular", null),
            ["custom.json"] = CreateMaterialJson(
                "test:custom_thermal",
                "{ \"initialTemperature\": -125.5, \"conductivity\": 0.72, \"heatCapacity\": 8.25, " +
                "\"ambientCooling\": { \"temperature\": -10.0, \"rate\": 2.5 } }"),
            ["minimums.json"] = CreateMaterialJson(
                "test:thermal_minimums",
                "{ \"initialTemperature\": -273.15, \"conductivity\": 0.0, \"heatCapacity\": 0.01 }"),
            ["maximums.json"] = CreateMaterialJson(
                "test:thermal_maximums",
                "{ \"initialTemperature\": 5000.0, \"conductivity\": 1.0, \"heatCapacity\": 100.0 }"),
            ["nan.json"] = CreateMaterialJson(
                "test:nan",
                "{ \"initialTemperature\": NaN, \"conductivity\": 0.15, \"heatCapacity\": 1.0 }"),
            ["infinity.json"] = CreateMaterialJson(
                "test:infinity",
                "{ \"initialTemperature\": Infinity, \"conductivity\": 0.15, \"heatCapacity\": 1.0 }"),
            ["temperature-low.json"] = CreateMaterialJson(
                "test:temperature_low",
                "{ \"initialTemperature\": -273.16, \"conductivity\": 0.15, \"heatCapacity\": 1.0 }"),
            ["temperature-high.json"] = CreateMaterialJson(
                "test:temperature_high",
                "{ \"initialTemperature\": 5000.01, \"conductivity\": 0.15, \"heatCapacity\": 1.0 }"),
            ["conductivity-low.json"] = CreateMaterialJson(
                "test:conductivity_low",
                "{ \"initialTemperature\": 20.0, \"conductivity\": -0.01, \"heatCapacity\": 1.0 }"),
            ["conductivity-high.json"] = CreateMaterialJson(
                "test:conductivity_high",
                "{ \"initialTemperature\": 20.0, \"conductivity\": 1.01, \"heatCapacity\": 1.0 }"),
            ["heat-capacity-zero.json"] = CreateMaterialJson(
                "test:heat_capacity_zero",
                "{ \"initialTemperature\": 20.0, \"conductivity\": 0.15, \"heatCapacity\": 0.0 }"),
            ["heat-capacity-low.json"] = CreateMaterialJson(
                "test:heat_capacity_low",
                "{ \"initialTemperature\": 20.0, \"conductivity\": 0.15, \"heatCapacity\": 0.009 }"),
            ["heat-capacity-high.json"] = CreateMaterialJson(
                "test:heat_capacity_high",
                "{ \"initialTemperature\": 20.0, \"conductivity\": 0.15, \"heatCapacity\": 100.01 }"),
            ["unknown.json"] = CreateMaterialJson(
                "test:unknown_thermal",
                "{ \"initialTemperature\": 20.0, \"conductivity\": 0.15, \"heatCapacity\": 1.0, \"conductivty\": 0.2 }"),
            ["ambient-not-object.json"] = CreateMaterialJson(
                "test:ambient_not_object", ThermalWithAmbient("[]")),
            ["ambient-missing-temperature.json"] = CreateMaterialJson(
                "test:ambient_missing_temperature", ThermalWithAmbient("{ \"rate\": 1.0 }")),
            ["ambient-missing-rate.json"] = CreateMaterialJson(
                "test:ambient_missing_rate", ThermalWithAmbient("{ \"temperature\": 20.0 }")),
            ["ambient-rate-zero.json"] = CreateMaterialJson(
                "test:ambient_rate_zero", ThermalWithAmbient("{ \"temperature\": 20.0, \"rate\": 0.0 }")),
            ["ambient-rate-high.json"] = CreateMaterialJson(
                "test:ambient_rate_high", ThermalWithAmbient("{ \"temperature\": 20.0, \"rate\": 100.01 }")),
            ["ambient-temperature-low.json"] = CreateMaterialJson(
                "test:ambient_temperature_low", ThermalWithAmbient("{ \"temperature\": -273.16, \"rate\": 1.0 }")),
            ["ambient-unknown.json"] = CreateMaterialJson(
                "test:ambient_unknown", ThermalWithAmbient(
                    "{ \"temperature\": 20.0, \"rate\": 1.0, \"typo\": 1 }"))
        };
        foreach ((string fileName, string? contents) in materials)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, fileName), contents);
        }
    }

    private static string ThermalWithAmbient(string ambient) =>
        $"{{ \"initialTemperature\": 20.0, \"conductivity\": 0.15, " +
        $"\"heatCapacity\": 1.0, \"ambientCooling\": {ambient} }}";

    private static string CreateMaterialJson(string id, string? thermal)
    {
        const string template = """
            {
              "schema": 1,
              "id": "__ID__",
              "name": "Test",
              "kind": "granular",
              "color": "#12345678",
              "physics": { "density": 2.5, "friction": 0.4, "flowRate": 0.2 }
              __THERMAL__
            }
            """;
        string thermalProperty = thermal is null ? string.Empty : $",\n  \"thermal\": {thermal}";
        return template
            .Replace("__ID__", id, StringComparison.Ordinal)
            .Replace("__THERMAL__", thermalProperty, StringComparison.Ordinal);
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
        left.TransitionAboveMaterialIndex == right.TransitionAboveMaterialIndex &&
        Same(left.IgnitionTemperature, right.IgnitionTemperature) &&
        Same(left.BurnRate, right.BurnRate) &&
        Same(left.HeatPerMass, right.HeatPerMass) &&
        left.BurnedIntoMaterialIndex == right.BurnedIntoMaterialIndex &&
        Same(left.FlameSpreadRate, right.FlameSpreadRate) &&
        Same(left.MinimumLifetime, right.MinimumLifetime) &&
        Same(left.MaximumLifetime, right.MaximumLifetime) &&
        left.DecayIntoMaterialIndex == right.DecayIntoMaterialIndex &&
        Same(left.MaximumCombustionTemperature, right.MaximumCombustionTemperature) &&
        Same(left.TransitionAboveLatentHeat, right.TransitionAboveLatentHeat) &&
        Same(left.AmbientTemperature, right.AmbientTemperature) &&
        Same(left.AmbientCoolingRate, right.AmbientCoolingRate);

    private static Color ParseColor(string value)
    {
        string hex = value[1..];
        byte red = Convert.ToByte(hex[0..2], 16);
        byte green = Convert.ToByte(hex[2..4], 16);
        byte blue = Convert.ToByte(hex[4..6], 16);
        byte alpha = hex.Length == 8 ? Convert.ToByte(hex[6..8], 16) : byte.MaxValue;
        return new Color(red, green, blue, alpha);
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
