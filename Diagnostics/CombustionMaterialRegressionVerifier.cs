using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class CombustionMaterialRegressionVerifier
{
    public static int Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("PHYXEL_COMBUSTION_MATERIALS_SUCCESS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYXEL_COMBUSTION_MATERIALS_FAILED {exception}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        VerifyLayout();
        VerifyRuntimeContract();
        string root = Path.Combine(Path.GetTempPath(), $"phyxel-combustion-materials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string coreDirectory = MaterialRegistry.ResolveCoreDirectory();
            await VerifyValidDefinitionsAsync(root, coreDirectory);
            await VerifyRuntimeReorderAsync(root, coreDirectory);
            await VerifyInvalidDefinitionsAsync(root, coreDirectory);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifyRuntimeContract()
    {
        MaterialProperties source = new()
        {
            SimulationKind = (uint)MaterialSimulationKind.Solid,
            Density = 0.8f,
            HeatCapacity = 1.7f,
            IgnitionTemperature = 300f,
            BurnRate = 0.08f,
            HeatPerMass = 1800f,
            MaximumCombustionTemperature = 900f,
            FlameSpreadRate = 3f,
            BurnedIntoMaterialIndex = 2
        };
        MaterialProperties coal = new()
        {
            SimulationKind = (uint)MaterialSimulationKind.Solid,
            Density = 0.2f,
            HeatCapacity = 1.1f,
            BurnedIntoMaterialIndex = uint.MaxValue
        };
        MaterialProperties metal = new()
        {
            SimulationKind = (uint)MaterialSimulationKind.Solid,
            Density = 7.8f,
            HeatCapacity = 0.5f,
            BurnedIntoMaterialIndex = uint.MaxValue
        };
        MaterialProperties[] materials = [new MaterialProperties(), source, coal, metal];

        GridCell exact = new() { MaterialIndex = 0, Mass = 0.8f, IsActive = 1, Temperature = 300f };
        Require(!CombustionRuntime.IsBurning(exact, materials), "Exact ignition threshold must not burn.");

        GridCell burning = new() { MaterialIndex = 1, Mass = 0.8f, IsActive = 1, Temperature = 301f };
        float originalTemperature = burning.Temperature;
        Require(CombustionRuntime.TryApply(ref burning, materials, 0.05f, out CombustionSummaryFlags summary, out float burned),
            "Hot wood did not burn.");
        Require(Same(burned, 0.004f), "Burn rate is not mass-per-second over fixed dt.");
        Require(Same(burning.Mass, 0.796f), "Burning mass decrement is incorrect.");
        Require(burning.Temperature > originalTemperature, "Combustion did not add heat.");
        Require((summary & CombustionSummaryFlags.CombustionOccurred) != 0,
            "Combustion summary did not report fuel consumption.");

        GridCell capped = new() { MaterialIndex = 1, Mass = 0.8f, IsActive = 1, Temperature = 899.9f };
        CombustionRuntime.TryApply(ref capped, materials, 0.05f, out _, out _);
        Require(capped.Temperature <= source.MaximumCombustionTemperature,
            "Combustion-generated heat exceeded the material temperature ceiling.");
        GridCell externallyHot = new() { MaterialIndex = 1, Mass = 0.8f, IsActive = 1, Temperature = 1200f };
        CombustionRuntime.TryApply(ref externallyHot, materials, 0.05f, out _, out _);
        Require(Same(externallyHot.Temperature, 1200f),
            "Combustion ceiling incorrectly cooled an externally hotter cell.");

        GridCell fourTicks = new() { MaterialIndex = 1, Mass = 0.8f, IsActive = 1, Temperature = 301f };
        for (int index = 0; index < 4; index++)
        {
            CombustionRuntime.TryApply(ref fourTicks, materials, 0.05f, out _, out _);
        }
        Require(Same(fourTicks.Mass, 0.784f), "Four thermal ticks did not consume summed burn dt.");
        Require(CombustionDispatchPolicy.GetDispatchCount(true, true, true, false, 4) == 1,
            "Catch-up ticks must produce one combustion dispatch.");
        Require(CombustionDispatchPolicy.GetDispatchCount(true, true, true, true, 4) == 0,
            "Pause must suppress combustion dispatch.");
        Require(CombustionDispatchPolicy.GetDispatchCount(false, true, true, false, 4) == 0,
            "Registry without combustion must skip dispatch.");

        GridCell cooled = new() { MaterialIndex = 1, Mass = 0.8f, IsActive = 1, Temperature = 299.9f };
        Require(!CombustionRuntime.TryApply(ref cooled, materials, 0.05f, out _, out _),
            "Cooling below ignition did not stop combustion.");
        GridCell hotMetal = new() { MaterialIndex = 3, Mass = 7.8f, IsActive = 1, Temperature = 900f };
        Require(!CombustionRuntime.IsBurning(hotMetal, materials),
            "Hot non-combustible metal was treated as burning.");

        GridCell burnout = new() { MaterialIndex = 1, Mass = 0.204f, IsActive = 1, Temperature = 500f };
        Require(CombustionRuntime.TryApply(ref burnout, materials, 0.05f, out CombustionSummaryFlags burnoutSummary, out _),
            "Final fuel interval did not burn.");
        Require((burnoutSummary & CombustionSummaryFlags.BurnoutOccurred) != 0 &&
            burnout.MaterialIndex == 2 && Same(burnout.Mass, 0.2f),
            "Burnout did not normalize to the residue material exactly once.");

        MaterialProperties flame = new()
        {
            Flags = (uint)MaterialFlags.Flame,
            SimulationKind = (uint)MaterialSimulationKind.Gas,
            Density = 0.12f,
            InitialTemperature = 650f,
            FlameSpreadRate = 3f,
            MinimumLifetime = 2f,
            MaximumLifetime = 2.8f,
            DecayIntoMaterialIndex = 2
        };
        MaterialProperties smoke = new()
        {
            SimulationKind = (uint)MaterialSimulationKind.Gas,
            Density = 0.04f,
            MinimumLifetime = 4f,
            MaximumLifetime = 5f,
            DecayIntoMaterialIndex = 0
        };
        MaterialProperties[] transientMaterials = [new MaterialProperties(), flame, smoke];
        GridCell liveFlame = new()
        {
            MaterialIndex = 1,
            Mass = 0.12f,
            IsActive = 1,
            Temperature = 650f,
            Lifetime = 2f
        };
        Require(TransientMaterialRuntime.IsFlame(liveFlame, transientMaterials),
            "A live flame cell was not recognized by the shared predicate.");
        Require(TransientMaterialRuntime.TryAdvance(ref liveFlame, transientMaterials, 0.5f) &&
            Same(liveFlame.Lifetime, 1.5f) && Same(liveFlame.Temperature, 650f) &&
            Same(liveFlame.VelocityY, -8f),
            "Flame lifetime or hot/upward state did not advance deterministically.");
        Require(TransientMaterialRuntime.ShouldIgnite(source, 0.05f, 0.10f) &&
            !TransientMaterialRuntime.ShouldIgnite(source, 0.05f, 0.90f),
            "Data-driven flame spread probability is incorrect.");

        GridCell cooledFlame = liveFlame;
        cooledFlame.Temperature = 50f;
        Require(TransientMaterialRuntime.TryAdvance(ref cooledFlame, transientMaterials, 0.05f) &&
            cooledFlame.MaterialIndex == 2 && cooledFlame.IsActive != 0 &&
            Same(cooledFlame.Lifetime, 4f) && Same(cooledFlame.Mass, smoke.Density),
            "Cooling did not extinguish a flame into its lifecycle target.");
        GridCell expiredSmoke = new()
        {
            MaterialIndex = 2,
            Mass = 0.04f,
            IsActive = 1,
            Lifetime = 0.01f
        };
        Require(TransientMaterialRuntime.TryAdvance(ref expiredSmoke, transientMaterials, 0.05f) &&
            expiredSmoke.IsActive == 0,
            "Expired smoke did not decay into empty.");
    }

    private static void VerifyLayout()
    {
        Require(Marshal.SizeOf<MaterialProperties>() == 104,
            "MaterialProperties must be 104 bytes.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.IgnitionTemperature)).ToInt32() == 64,
            "IgnitionTemperature offset must be 64.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.BurnRate)).ToInt32() == 68,
            "BurnRate offset must be 68.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.HeatPerMass)).ToInt32() == 72,
            "HeatPerMass offset must be 72.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.BurnedIntoMaterialIndex)).ToInt32() == 76,
            "BurnedIntoMaterialIndex offset must be 76.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.FlameSpreadRate)).ToInt32() == 80,
            "FlameSpreadRate offset must be 80.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.MinimumLifetime)).ToInt32() == 84,
            "MinimumLifetime offset must be 84.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.MaximumLifetime)).ToInt32() == 88,
            "MaximumLifetime offset must be 88.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.DecayIntoMaterialIndex)).ToInt32() == 92,
            "DecayIntoMaterialIndex offset must be 92.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.MaximumCombustionTemperature)).ToInt32() == 96,
            "MaximumCombustionTemperature offset must be 96.");
        Require(Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.TransitionAboveLatentHeat)).ToInt32() == 100,
            "TransitionAboveLatentHeat offset must be 100.");
        Require(Marshal.SizeOf<GridCell>() == 40, "GridCell must be 40 bytes.");

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
            "float IgnitionTemperature", "float BurnRate", "float HeatPerMass",
            "uint BurnedIntoMaterialIndex", "float FlameSpreadRate",
            "float MinimumLifetime", "float MaximumLifetime", "uint DecayIntoMaterialIndex",
            "float MaximumCombustionTemperature", "float TransitionAboveLatentHeat"
        ];
        int previous = -1;
        foreach (string field in fields)
        {
            int position = layout.IndexOf(field, StringComparison.Ordinal);
            Require(position > previous, $"HLSL combustion field '{field}' is missing or out of order.");
            previous = position;
        }
    }

    private static async Task VerifyValidDefinitionsAsync(string root, string coreDirectory)
    {
        string directory = CreateDirectory(root, "valid");
        await WriteMaterialAsync(directory, "coal.json", MaterialJson(
            "test:coal", "solid", 0.20f));
        await WriteMaterialAsync(directory, "smoke.json", MaterialJson("test:smoke", "gas", 0.04f));
        await WriteMaterialAsync(directory, "co2.json", MaterialJson("test:co2", "gas", 0.06f));
        await WriteMaterialAsync(directory, "wood.json", MaterialJson(
            "test:wood",
            "solid",
            0.80f,
            CombustionJson(300f, 0.08f, 1800f, "test:coal"),
            emissionsJson: EmissionsJson(0.15f, "test:smoke", 0.10f, "test:co2")));
        await WriteMaterialAsync(directory, "ash.json", MaterialJson(
            "test:ash",
            "solid",
            0.30f,
            CombustionJson(250f, 0.10f, 900f, CoreMaterialIds.Empty)));
        MaterialRegistry registry = new(coreDirectory, directory);
        Require(registry.RegistryHasCombustibleMaterials,
            "Valid combustion definitions did not set registry metadata.");

        MaterialDefinition wood = registry["test:wood"];
        Require(wood.Combustion is not null, "Raw combustion definition was not retained.");
        Require(wood.Combustion!.BurnedIntoId == "test:coal",
            "Raw burnedInto string target was not normalized.");
        Require(Same(wood.Properties.IgnitionTemperature, 300f) &&
            Same(wood.Properties.BurnRate, 0.08f) &&
            Same(wood.Properties.HeatPerMass, 1800f) &&
            Same(wood.Properties.MaximumCombustionTemperature, 900f),
            "Combustion numeric properties were not copied to GPU properties.");
        Require(registry[CoreMaterialIds.Coal].Combustion is null &&
            registry[CoreMaterialIds.Coal].Properties.BurnedIntoMaterialIndex == uint.MaxValue,
            "Bundled coal must be an inert hot residue in combustion v1.");
        Require(wood.Properties.BurnedIntoMaterialIndex == registry["test:coal"].RuntimeIndex,
            "Combustion target was not resolved after runtime ordering.");
        Require(registry["test:coal"].Properties.BurnedIntoMaterialIndex == uint.MaxValue,
            "Non-combustible target lacks the combustion sentinel.");
        MaterialEmissionProperties emissions = registry.CreateEmissionGpuTable()[wood.RuntimeIndex];
        Require(emissions.SmokeIntoMaterialIndex == registry["test:smoke"].RuntimeIndex &&
            emissions.GasIntoMaterialIndex == registry["test:co2"].RuntimeIndex &&
            Same(emissions.SmokeRate, 0.15f) && Same(emissions.GasRate, 0.10f),
            "Emission targets or rates were not resolved after runtime ordering.");

        MaterialDefinition ash = registry["test:ash"];
        Require(ash.Properties.BurnedIntoMaterialIndex == registry[CoreMaterialIds.Empty].RuntimeIndex,
            "core:empty is not accepted as an explicit combustion target.");
    }

    private static async Task VerifyRuntimeReorderAsync(string root, string coreDirectory)
    {
        string first = CreateDirectory(root, "reorder-first");
        string second = CreateDirectory(root, "reorder-second");
        string source = MaterialJson(
            "test:reorder_wood",
            "solid",
            0.80f,
            CombustionJson(300f, 0.08f, 1800f, "test:reorder_coal"));
        string target = MaterialJson("test:reorder_coal", "solid", 0.20f);
        await WriteMaterialAsync(first, "a-source.json", source);
        await WriteMaterialAsync(first, "z-target.json", target);
        await WriteMaterialAsync(second, "z-source.json", source);
        await WriteMaterialAsync(second, "a-target.json", target);
        await WriteMaterialAsync(second, "padding.json", MaterialJson("test:reorder_aaa", "solid", 0.10f));

        MaterialRegistry firstRegistry = new(coreDirectory, first);
        MaterialRegistry secondRegistry = new(coreDirectory, second);
        uint firstTarget = firstRegistry["test:reorder_wood"].Properties.BurnedIntoMaterialIndex;
        uint secondTarget = secondRegistry["test:reorder_wood"].Properties.BurnedIntoMaterialIndex;
        Require(firstTarget == firstRegistry["test:reorder_coal"].RuntimeIndex,
            "First registry combustion target resolution is incorrect.");
        Require(secondTarget == secondRegistry["test:reorder_coal"].RuntimeIndex,
            "Reordered registry combustion target resolution is incorrect.");
        Require(firstTarget != secondTarget,
            "Runtime reorder did not move the combustion target index in the diagnostic fixture.");
    }

    private static async Task VerifyInvalidDefinitionsAsync(string root, string coreDirectory)
    {
        await ExpectRejectedAsync(root, coreDirectory, "unknown-field", "test:unknown_field",
            MaterialJson("test:unknown_field", "solid", 0.8f,
                "{ \"ignitionTemperature\": 300, \"burnRate\": 0.1, \"heatPerMass\": 1000, \"burnedInto\": \"core:empty\", \"typo\": 1 }") );
        await ExpectRejectedAsync(root, coreDirectory, "missing-field", "test:missing_field",
            MaterialJson("test:missing_field", "solid", 0.8f,
                "{ \"ignitionTemperature\": 300, \"burnRate\": 0.1, \"burnedInto\": \"core:empty\" }") );
        await ExpectRejectedAsync(root, coreDirectory, "zero-rate", "test:zero_rate",
            MaterialJson("test:zero_rate", "solid", 0.8f,
                CombustionJson(300f, 0f, 1000f, "core:empty")));
        await ExpectRejectedAsync(root, coreDirectory, "liquid-source", "test:liquid_source",
            MaterialJson("test:liquid_source", "liquid", 0.8f,
                CombustionJson(300f, 0.1f, 1000f, "core:empty")));
        await ExpectRejectedAsync(root, coreDirectory, "movable-source", "test:movable_source",
            MaterialJson("test:movable_source", "solid", 0.8f,
                CombustionJson(300f, 0.1f, 1000f, "core:empty"), "movable-solid"));
        await ExpectRejectedAsync(root, coreDirectory, "phase-source", "test:phase_source",
            MaterialJson("test:phase_source", "solid", 0.8f,
                CombustionJson(300f, 0.1f, 1000f, "core:empty"),
                thermalExtra: ", \"transitions\": { \"above\": { \"temperature\": 400, \"into\": \"core:stone\" } }"));
        await ExpectRejectedAsync(root, coreDirectory, "emissions", "test:emissions",
            MaterialJson("test:emissions", "solid", 0.8f,
                CombustionJson(300f, 0.1f, 1000f, "core:empty"),
                thermalExtra: ", \"emissions\": { \"smokeInto\": \"core:gas\" }"));

        string targetDirectory = CreateDirectory(root, "invalid-targets");
        await WriteMaterialAsync(targetDirectory, "granular-target.json", MaterialJson("test:granular_target", "granular", 0.2f));
        await WriteMaterialAsync(targetDirectory, "movable-target.json", MaterialJson("test:movable_target", "solid", 0.2f, flags: "movable-solid"));
        await WriteMaterialAsync(targetDirectory, "large-target.json", MaterialJson("test:large_target", "solid", 0.9f));
        await WriteMaterialAsync(targetDirectory, "phase-target.json", MaterialJson(
            "test:phase_target", "solid", 0.2f,
            thermalExtra: ", \"transitions\": { \"above\": { \"temperature\": 400, \"into\": \"core:stone\" } }"));
        await WriteMaterialAsync(targetDirectory, "granular-source.json", MaterialJson("test:granular_source", "solid", 0.8f, CombustionJson(300, 0.1f, 1000, "test:granular_target")));
        await WriteMaterialAsync(targetDirectory, "movable-source.json", MaterialJson("test:movable_source2", "solid", 0.8f, CombustionJson(300, 0.1f, 1000, "test:movable_target")));
        await WriteMaterialAsync(targetDirectory, "large-source.json", MaterialJson("test:large_source", "solid", 0.8f, CombustionJson(300, 0.1f, 1000, "test:large_target")));
        await WriteMaterialAsync(targetDirectory, "phase-source.json", MaterialJson("test:phase_source2", "solid", 0.8f, CombustionJson(300, 0.1f, 1000, "test:phase_target")));
        TextWriter originalError = Console.Error;
        using StringWriter captured = new();
        MaterialRegistry registry;
        try
        {
            Console.SetError(captured);
            registry = new MaterialRegistry(coreDirectory, targetDirectory);
        }
        finally
        {
            Console.SetError(originalError);
        }
        Require(registry.TryGet("test:granular_source", out _),
            "Granular combustion residue should be accepted for BCOL-like targets.");
        foreach (string id in new[] { "test:movable_source2", "test:large_source", "test:phase_source2" })
        {
            Require(!registry.TryGet(id, out _), $"Invalid combustion target retained source '{id}'.");
        }
        Require(captured.ToString().Contains("PHYXEL_MATERIAL_ERROR", StringComparison.Ordinal),
            "Invalid combustion target did not report PHYXEL_MATERIAL_ERROR.");
    }

    private static async Task ExpectRejectedAsync(
        string root,
        string coreDirectory,
        string name,
        string id,
        string json)
    {
        string directory = CreateDirectory(root, $"invalid-{name}");
        await WriteMaterialAsync(directory, "material.json", json);
        TextWriter originalError = Console.Error;
        using StringWriter captured = new();
        MaterialRegistry registry;
        try
        {
            Console.SetError(captured);
            registry = new MaterialRegistry(coreDirectory, directory);
        }
        finally
        {
            Console.SetError(originalError);
        }
        Require(!registry.TryGet(id, out _), $"Invalid combustion material '{id}' was loaded.");
        Require(captured.ToString().Contains("PHYXEL_MATERIAL_ERROR", StringComparison.Ordinal),
            $"Invalid combustion material '{id}' did not report PHYXEL_MATERIAL_ERROR.");
    }

    private static string MaterialJson(
        string id,
        string kind,
        float density,
        string? combustionJson = null,
        string? flags = null,
        string thermalExtra = "",
        string? emissionsJson = null)
    {
        string combustion = combustionJson is null
            ? string.Empty
            : $",\n  \"combustion\": {combustionJson}";
        string emissions = emissionsJson is null
            ? string.Empty
            : $",\n  \"emissions\": {emissionsJson}";
        string flagsJson = flags is null ? "[]" : $"[\"{flags}\"]";
        return $$"""
            {
              "schema": 1,
              "id": "{{id}}",
              "name": "Test",
              "kind": "{{kind}}",
              "flags": {{flagsJson}},
              "color": "#123456",
              "physics": { "density": {{density.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "friction": 0.2, "flowRate": 0.0 },
              "thermal": { "initialTemperature": 20.0, "conductivity": 0.15, "heatCapacity": 1.0{{thermalExtra}} }{{combustion}}{{emissions}}
            }
            """;
    }

    private static string CombustionJson(float ignition, float burnRate, float heatPerMass, string target) =>
        $$"""{ "ignitionTemperature": {{ignition.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "burnRate": {{burnRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "heatPerMass": {{heatPerMass.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "burnedInto": "{{target}}", "maximumTemperature": 900.0 }""";

    private static string EmissionsJson(float smokeRate, string smokeTarget, float gasRate, string gasTarget) =>
        $$"""{ "smokeInto": "{{smokeTarget}}", "smokeRate": {{smokeRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "gasInto": "{{gasTarget}}", "gasRate": {{gasRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""";

    private static string CreateDirectory(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Task WriteMaterialAsync(string directory, string fileName, string json) =>
        File.WriteAllTextAsync(Path.Combine(directory, fileName), json);

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
