using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Phyxel.Physics;

namespace Phyxel.Materials;

public sealed class MaterialRegistry
{
    public const int MaximumMaterials = 256;
    public const float MaximumDensity = 100f;
    public const float DefaultInitialTemperature = 20f;
    public const float DefaultThermalConductivity = 0.15f;
    public const float DefaultHeatCapacity = 1f;
    public const float MinimumInitialTemperature = -273.15f;
    public const float MaximumInitialTemperature = 5000f;
    public const float MinimumThermalConductivity = 0f;
    public const float MaximumThermalConductivity = 1f;
    public const float MinimumHeatCapacity = 0.01f;
    public const float MaximumHeatCapacity = 100f;
    public const float MaximumCombustionBurnRate = 100f;
    public const float MaximumCombustionHeatPerMass = 1_000_000f;
    public const float MaximumFlameSpreadRate = 100f;
    public const float MaximumLifetime = 3600f;
    public const float MaximumAmbientCoolingRate = 100f;
    public const float MaximumContactTransitionRate = 100f;
    public const float DefaultGasDiffusion = 0.25f;
    public const float MinimumGasDiffusion = 0f;
    public const float MaximumGasDiffusion = 1f;
    public const float MinimumGasBuoyancy = -0.25f;
    public const float MaximumGasBuoyancy = 0.25f;

    private readonly ReadOnlyCollection<MaterialDefinition> materials;
    private readonly ReadOnlyCollection<MaterialDefinition> selectableMaterials;
    private readonly Dictionary<string, MaterialDefinition> byId;

    public MaterialRegistry(string? externalDirectory = null)
        : this(
            ResolveCoreDirectory(),
            externalDirectory ?? ResolveExternalDirectory())
    {
    }

    internal MaterialRegistry(string coreDirectory, string externalDirectory)
    {
        List<MaterialDefinition> coreDefinitions = MaterialFileLoader
            .LoadCore(coreDirectory, MaximumMaterials)
            .ToList();
        ValidateRequiredCoreMaterials(coreDefinitions);
        ValidateCorePhaseTransitions(coreDefinitions);
        ValidateCoreCombustion(coreDefinitions);
        ValidateCoreEmissions(coreDefinitions);
        ValidateCoreLifecycles(coreDefinitions);
        ValidateCoreContactTransitions(coreDefinitions);

        HashSet<string> reservedIds = coreDefinitions
            .Select(material => material.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<MaterialDefinition> externalDefinitions = MaterialFileLoader.LoadExternal(
                externalDirectory,
                reservedIds,
                MaximumMaterials - coreDefinitions.Count,
                coreDirectory)
            .ToList();
        List<MaterialDefinition> definitions = coreDefinitions
            .Concat(FilterExternalPhaseTransitions(coreDefinitions, externalDefinitions))
            .OrderBy(material => material.Id == CoreMaterialIds.Empty ? 0 : 1)
            .ThenBy(material => material.Id, StringComparer.Ordinal)
            .ToList();

        for (int index = 0; index < definitions.Count; index++)
        {
            MaterialDefinition definition = definitions[index];
            definitions[index] = definition with
            {
                RuntimeIndex = checked((ushort)index)
            };
        }

        Dictionary<string, MaterialDefinition> indexedById = definitions
            .ToDictionary(material => material.Id, StringComparer.Ordinal);
        for (int index = 0; index < definitions.Count; index++)
        {
            MaterialDefinition definition = definitions[index];
            definitions[index] = definition with
            {
                Properties = ResolveProperties(definition, indexedById)
            };
        }

        materials = definitions.AsReadOnly();
        selectableMaterials = definitions
            .Where(material => !material.Hidden && material.Id != CoreMaterialIds.Empty)
            .OrderBy(material => material.UiOrder)
            .ThenBy(material => material.Id, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
        byId = definitions.ToDictionary(material => material.Id, StringComparer.Ordinal);
        RegistryHasPhaseTransitions = definitions.Any(material => material.PhaseTransitions is not null);
        RegistryHasCombustibleMaterials = definitions.Any(material => material.Combustion is not null);
        RegistryHasTransientMaterials = definitions.Any(material => material.Lifecycle is not null);
        RegistryHasContactTransitions = definitions.Any(material => material.LiquidContactTransition is not null);
        PhaseTransitionGraphFlags = definitions
            .Where(material => material.PhaseTransitions is not null)
            .Aggregate(
                PhaseTransitionSummaryFlags.None,
                (flags, source) => flags | EnumerateResolvedTargets(source, byId)
                    .Aggregate(
                        PhaseTransitionSummaryFlags.None,
                        (targetFlags, target) => targetFlags |
                            PhaseTransitionRuntime.GetSummaryFlags(source.Properties, target.Properties)));
    }

    public IReadOnlyList<MaterialDefinition> Materials => materials;
    public IReadOnlyList<MaterialDefinition> SelectableMaterials => selectableMaterials;
    public int Count => materials.Count;
    public bool RegistryHasPhaseTransitions { get; }
    public bool RegistryHasCombustibleMaterials { get; }
    public bool RegistryHasTransientMaterials { get; }
    public bool RegistryHasContactTransitions { get; }
    public PhaseTransitionSummaryFlags PhaseTransitionGraphFlags { get; }

    public MaterialDefinition this[ushort runtimeIndex] => materials[runtimeIndex];
    public MaterialDefinition this[uint runtimeIndex] => materials[checked((int)runtimeIndex)];
    public MaterialDefinition this[string id] => byId[NormalizeId(id)];

    public bool TryGet(string id, out MaterialDefinition definition) =>
        byId.TryGetValue(NormalizeId(id), out definition!);

    public bool TryGet(ushort runtimeIndex, out MaterialDefinition definition)
    {
        if (runtimeIndex < materials.Count)
        {
            definition = materials[runtimeIndex];
            return true;
        }

        definition = null!;
        return false;
    }

    public ushort GetRequiredRuntimeIndex(string id) => this[id].RuntimeIndex;

    public MaterialProperties[] CreateGpuTable()
    {
        MaterialProperties[] table = new MaterialProperties[materials.Count];
        foreach (MaterialDefinition material in materials)
        {
            table[material.RuntimeIndex] = material.Properties;
        }

        return table;
    }

    public MaterialEmissionProperties[] CreateEmissionGpuTable()
    {
        MaterialEmissionProperties[] table = new MaterialEmissionProperties[materials.Count];
        foreach (MaterialDefinition material in materials)
        {
            table[material.RuntimeIndex] = ResolveEmissionProperties(material, byId);
        }
        return table;
    }

    public static string ResolveExternalDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("PHYXEL_MATERIALS_PATH");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "Materials")
            : Path.GetFullPath(configured.Trim());
    }

    public static string ResolveCoreDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("PHYXEL_CORE_MATERIALS_PATH");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "Materials", "core")
            : Path.GetFullPath(configured.Trim());
    }

    public static string NormalizeId(string id) => id.Trim().ToLowerInvariant();

    private static void ValidateRequiredCoreMaterials(IReadOnlyCollection<MaterialDefinition> definitions)
    {
        HashSet<string> ids = definitions
            .Select(material => material.Id)
            .ToHashSet(StringComparer.Ordinal);
        string[] missing = CoreMaterialIds.Required
            .Where(id => !ids.Contains(id))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Core material set is incomplete. Missing: {string.Join(", ", missing)}.");
        }

        MaterialDefinition empty = definitions.Single(material => material.Id == CoreMaterialIds.Empty);
        if ((MaterialSimulationKind)empty.Properties.SimulationKind != MaterialSimulationKind.None || !empty.Hidden)
        {
            throw new InvalidDataException("core:empty must have kind 'none' and ui.hidden=true.");
        }
    }

    private static void ValidateCorePhaseTransitions(IReadOnlyCollection<MaterialDefinition> coreDefinitions)
    {
        Dictionary<string, MaterialDefinition> coreById = coreDefinitions
            .ToDictionary(material => material.Id, StringComparer.Ordinal);
        foreach (MaterialDefinition source in coreDefinitions.OrderBy(material => material.Id, StringComparer.Ordinal))
        {
            string? error = FindPhaseTransitionReferenceError(source, coreById, requireBundledTarget: true);
            if (error is not null)
            {
                throw new InvalidDataException(
                    $"Invalid core phase transition in '{source.Id}' from '{source.SourcePath}': {error}");
            }
        }

        IReadOnlySet<string> cycles =
            PhaseTransitionCycleValidator.FindInstantaneousCycleMaterials(coreDefinitions);
        if (cycles.Count > 0)
        {
            throw new InvalidDataException(
                $"Core phase transitions contain an instantaneous cycle: {string.Join(", ", cycles.OrderBy(id => id, StringComparer.Ordinal))}.");
        }
    }

    private static void ValidateCoreCombustion(IReadOnlyCollection<MaterialDefinition> coreDefinitions)
    {
        Dictionary<string, MaterialDefinition> coreById = coreDefinitions
            .ToDictionary(material => material.Id, StringComparer.Ordinal);
        foreach (MaterialDefinition source in coreDefinitions.OrderBy(material => material.Id, StringComparer.Ordinal))
        {
            string? error = FindCombustionReferenceError(source, coreById, requireBundledTarget: true);
            if (error is not null)
            {
                throw new InvalidDataException(
                    $"Invalid core combustion in '{source.Id}' from '{source.SourcePath}': {error}");
            }
        }
    }

    private static void ValidateCoreEmissions(IReadOnlyCollection<MaterialDefinition> coreDefinitions)
    {
        Dictionary<string, MaterialDefinition> coreById = coreDefinitions
            .ToDictionary(material => material.Id, StringComparer.Ordinal);
        foreach (MaterialDefinition source in coreDefinitions.OrderBy(material => material.Id, StringComparer.Ordinal))
        {
            string? error = FindEmissionReferenceError(source, coreById, requireBundledTarget: true);
            if (error is not null)
            {
                throw new InvalidDataException(
                    $"Invalid core emissions in '{source.Id}' from '{source.SourcePath}': {error}");
            }
        }
    }

    private static void ValidateCoreLifecycles(IReadOnlyCollection<MaterialDefinition> coreDefinitions)
    {
        Dictionary<string, MaterialDefinition> coreById = coreDefinitions
            .ToDictionary(material => material.Id, StringComparer.Ordinal);
        foreach (MaterialDefinition source in coreDefinitions.OrderBy(material => material.Id, StringComparer.Ordinal))
        {
            string? error = FindLifecycleReferenceError(source, coreById, requireBundledTarget: true);
            if (error is not null)
            {
                throw new InvalidDataException(
                    $"Invalid core lifecycle in '{source.Id}' from '{source.SourcePath}': {error}");
            }
        }
    }

    private static void ValidateCoreContactTransitions(
        IReadOnlyCollection<MaterialDefinition> coreDefinitions)
    {
        Dictionary<string, MaterialDefinition> coreById = coreDefinitions
            .ToDictionary(material => material.Id, StringComparer.Ordinal);
        foreach (MaterialDefinition source in coreDefinitions.OrderBy(material => material.Id, StringComparer.Ordinal))
        {
            string? error = FindContactTransitionReferenceError(source, coreById, requireBundledTarget: true);
            if (error is not null)
            {
                throw new InvalidDataException(
                    $"Invalid core contact transition in '{source.Id}' from '{source.SourcePath}': {error}");
            }
        }
    }

    private static IReadOnlyList<MaterialDefinition> FilterExternalPhaseTransitions(
        IReadOnlyCollection<MaterialDefinition> coreDefinitions,
        IReadOnlyCollection<MaterialDefinition> externalDefinitions)
    {
        List<MaterialDefinition> valid = externalDefinitions
            .OrderBy(material => material.Id, StringComparer.Ordinal)
            .ToList();
        while (true)
        {
            Dictionary<string, MaterialDefinition> available = coreDefinitions
                .Concat(valid)
                .ToDictionary(material => material.Id, StringComparer.Ordinal);
            List<(MaterialDefinition Source, string Error)> invalidReferences = valid
                .Select(source => (
                    Source: source,
                    Error: FindPhaseTransitionReferenceError(source, available, requireBundledTarget: false) ??
                        FindCombustionReferenceError(source, available, requireBundledTarget: false) ??
                        FindEmissionReferenceError(source, available, requireBundledTarget: false) ??
                        FindLifecycleReferenceError(source, available, requireBundledTarget: false) ??
                        FindContactTransitionReferenceError(source, available, requireBundledTarget: false)))
                .Where(result => result.Error is not null)
                .Select(result => (result.Source, result.Error!))
                .ToList();
            if (invalidReferences.Count > 0)
            {
                foreach ((MaterialDefinition source, string error) in invalidReferences)
                {
                    MaterialFileLoader.LogError(
                        source.SourcePath,
                        $"Material '{source.Id}' has an invalid material rule: {error}");
                }
                HashSet<string> removedIds = invalidReferences
                    .Select(result => result.Source.Id)
                    .ToHashSet(StringComparer.Ordinal);
                valid.RemoveAll(material => removedIds.Contains(material.Id));
                continue;
            }

            MaterialDefinition[] allDefinitions = coreDefinitions.Concat(valid).ToArray();
            IReadOnlySet<string> cycleIds =
                PhaseTransitionCycleValidator.FindInstantaneousCycleMaterials(allDefinitions);
            if (cycleIds.Count == 0)
            {
                return valid;
            }

            HashSet<string> externalCycleIds = valid
                .Where(material => cycleIds.Contains(material.Id))
                .Select(material => material.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (externalCycleIds.Count == 0)
            {
                throw new InvalidDataException(
                    $"Core phase transitions contain an instantaneous cycle: {string.Join(", ", cycleIds.OrderBy(id => id, StringComparer.Ordinal))}.");
            }

            foreach (MaterialDefinition source in valid
                .Where(material => externalCycleIds.Contains(material.Id))
                .OrderBy(material => material.Id, StringComparer.Ordinal))
            {
                MaterialFileLoader.LogError(
                    source.SourcePath,
                    $"Material '{source.Id}' participates in an instantaneous phase-transition cycle.");
            }
            valid.RemoveAll(material => externalCycleIds.Contains(material.Id));
        }
    }

    private static string? FindPhaseTransitionReferenceError(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> available,
        bool requireBundledTarget)
    {
        foreach ((string direction, MaterialTransitionRule rule) in EnumerateTransitionRules(source))
        {
            if (!available.TryGetValue(rule.IntoId, out MaterialDefinition? target))
            {
                return $"{direction} target '{rule.IntoId}' does not exist in the valid material set.";
            }
            if (requireBundledTarget && !target.IsBundled)
            {
                return $"{direction} target '{rule.IntoId}' is not a bundled core material.";
            }
            MaterialSimulationKind targetKind = (MaterialSimulationKind)target.Properties.SimulationKind;
            if (target.Id == CoreMaterialIds.Empty)
            {
                return $"{direction} target '{rule.IntoId}' cannot be core:empty.";
            }
            if (targetKind is MaterialSimulationKind.None or MaterialSimulationKind.Tool)
            {
                return $"{direction} target '{rule.IntoId}' has forbidden kind '{targetKind.ToString().ToLowerInvariant()}'.";
            }
            if (target.Id == source.Id)
            {
                return $"{direction} target cannot be the source material itself ('{source.Id}').";
            }
        }
        return null;
    }

    private static string? FindCombustionReferenceError(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> available,
        bool requireBundledTarget)
    {
        if (source.Combustion is not { } combustion)
        {
            return null;
        }

        if (!available.TryGetValue(combustion.BurnedIntoId, out MaterialDefinition? target))
        {
            return $"burnedInto target '{combustion.BurnedIntoId}' does not exist in the valid material set.";
        }
        if (requireBundledTarget && !target.IsBundled)
        {
            return $"burnedInto target '{combustion.BurnedIntoId}' is not a bundled core material.";
        }
        if (target.Id == source.Id)
        {
            return $"burnedInto target cannot be the source material itself ('{source.Id}').";
        }
        if (target.Id == CoreMaterialIds.Empty)
        {
            return null;
        }

        MaterialSimulationKind targetKind = (MaterialSimulationKind)target.Properties.SimulationKind;
        if (targetKind is not (MaterialSimulationKind.Solid or MaterialSimulationKind.Granular))
        {
            return $"burnedInto target '{combustion.BurnedIntoId}' must be solid or granular in combustion v1.";
        }
        if ((target.Properties.Flags & (uint)MaterialFlags.MovableSolid) != 0)
        {
            return $"burnedInto target '{combustion.BurnedIntoId}' cannot be movable solid in combustion v1.";
        }
        if (target.PhaseTransitions is not null)
        {
            return $"burnedInto target '{combustion.BurnedIntoId}' cannot also have thermal.transitions in combustion v1.";
        }
        if (target.Properties.Density <= 0 || target.Properties.Density >= source.Properties.Density)
        {
            return $"burnedInto target density {target.Properties.Density} must be greater than 0 and less than source density {source.Properties.Density}.";
        }
        return null;
    }

    private static string? FindEmissionReferenceError(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> available,
        bool requireBundledTarget)
    {
        if (source.Emissions is not { } emissions)
        {
            return null;
        }
        List<(string Field, string TargetId)> targets =
        [
            ("smokeInto", emissions.SmokeIntoId),
            ("gasInto", emissions.GasIntoId)
        ];
        if (emissions.FlameIntoId is { } flameTarget)
        {
            targets.Add(("flameInto", flameTarget));
        }
        foreach ((string field, string targetId) in targets)
        {
            if (!available.TryGetValue(targetId, out MaterialDefinition? target))
            {
                return $"emissions.{field} target '{targetId}' does not exist in the valid material set.";
            }
            if (requireBundledTarget && !target.IsBundled)
            {
                return $"emissions.{field} target '{targetId}' is not a bundled core material.";
            }
            MaterialSimulationKind targetKind = (MaterialSimulationKind)target.Properties.SimulationKind;
            if (targetKind != MaterialSimulationKind.Gas)
            {
                return $"emissions.{field} target '{targetId}' must be gas.";
            }
            if (target.Properties.Density <= 0)
            {
                return $"emissions.{field} target '{targetId}' must have density greater than 0.";
            }
            if (field == "flameInto" &&
                (target.Properties.Flags & (uint)MaterialFlags.Flame) == 0)
            {
                return $"emissions.flameInto target '{targetId}' must have flag 'flame'.";
            }
        }
        return null;
    }

    private static string? FindLifecycleReferenceError(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> available,
        bool requireBundledTarget)
    {
        if (source.Lifecycle is not { } lifecycle)
        {
            return null;
        }
        if (!available.TryGetValue(lifecycle.DecayIntoId, out MaterialDefinition? target))
        {
            return $"lifecycle.decayInto target '{lifecycle.DecayIntoId}' does not exist in the valid material set.";
        }
        if (requireBundledTarget && !target.IsBundled)
        {
            return $"lifecycle.decayInto target '{lifecycle.DecayIntoId}' is not a bundled core material.";
        }
        MaterialSimulationKind targetKind = (MaterialSimulationKind)target.Properties.SimulationKind;
        if (target.Id != CoreMaterialIds.Empty && targetKind != MaterialSimulationKind.Gas)
        {
            return $"lifecycle.decayInto target '{lifecycle.DecayIntoId}' must be gas or core:empty.";
        }
        return null;
    }

    private static string? FindContactTransitionReferenceError(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> available,
        bool requireBundledTarget)
    {
        if (source.LiquidContactTransition is not { } transition)
        {
            return null;
        }
        if (!available.TryGetValue(transition.IntoId, out MaterialDefinition? target))
        {
            return $"liquid target '{transition.IntoId}' does not exist in the valid material set.";
        }
        if (requireBundledTarget && !target.IsBundled)
        {
            return $"liquid target '{transition.IntoId}' is not a bundled core material.";
        }
        if (target.Id == source.Id)
        {
            return $"liquid target cannot be the source material itself ('{source.Id}').";
        }
        if ((MaterialSimulationKind)source.Properties.SimulationKind != MaterialSimulationKind.Granular ||
            (MaterialSimulationKind)target.Properties.SimulationKind != MaterialSimulationKind.Granular)
        {
            return "liquid contact source and target must both have kind 'granular'.";
        }
        return null;
    }

    private static IEnumerable<(string Direction, MaterialTransitionRule Rule)> EnumerateTransitionRules(
        MaterialDefinition source)
    {
        if (source.PhaseTransitions?.Below is { } below)
        {
            yield return ("below", below);
        }
        if (source.PhaseTransitions?.Above is { } above)
        {
            yield return ("above", above);
        }
    }

    private static IEnumerable<MaterialDefinition> EnumerateResolvedTargets(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> definitions)
    {
        foreach ((_, MaterialTransitionRule rule) in EnumerateTransitionRules(source))
        {
            yield return definitions[rule.IntoId];
        }
    }

    private static MaterialProperties ResolveProperties(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> indexedById)
    {
        MaterialProperties properties = source.Properties;
        properties.TransitionBelowTemperature = source.PhaseTransitions?.Below?.Temperature ?? 0;
        properties.TransitionBelowMaterialIndex = source.PhaseTransitions?.Below is { } below
            ? indexedById[below.IntoId].RuntimeIndex
            : uint.MaxValue;
        properties.TransitionAboveTemperature = source.PhaseTransitions?.Above?.Temperature ?? 0;
        properties.TransitionAboveMaterialIndex = source.PhaseTransitions?.Above is { } above
            ? indexedById[above.IntoId].RuntimeIndex
            : uint.MaxValue;
        properties.IgnitionTemperature = source.Combustion?.IgnitionTemperature ?? 0;
        properties.BurnRate = source.Combustion?.BurnRate ?? 0;
        properties.HeatPerMass = source.Combustion?.HeatPerMass ?? 0;
        properties.BurnedIntoMaterialIndex = source.Combustion is { } combustion
            ? indexedById[combustion.BurnedIntoId].RuntimeIndex
            : uint.MaxValue;
        properties.FlameSpreadRate = source.Combustion?.FlameSpreadRate ?? 0;
        properties.MinimumLifetime = source.Lifecycle?.MinimumLifetime ?? 0;
        properties.MaximumLifetime = source.Lifecycle?.MaximumLifetime ?? 0;
        properties.DecayIntoMaterialIndex = source.Lifecycle is { } lifecycle
            ? indexedById[lifecycle.DecayIntoId].RuntimeIndex
            : uint.MaxValue;
        properties.MaximumCombustionTemperature = source.Combustion?.MaximumTemperature ?? 0;
        properties.TransitionAboveLatentHeat = source.PhaseTransitions?.Above?.LatentHeat ?? 0;
        properties.ContactLiquidIntoMaterialIndex = source.LiquidContactTransition is { } contact
            ? indexedById[contact.IntoId].RuntimeIndex
            : uint.MaxValue;
        properties.ContactLiquidRatePerSecond = source.LiquidContactTransition?.RatePerSecond ?? 0;
        return properties;
    }

    private static MaterialEmissionProperties ResolveEmissionProperties(
        MaterialDefinition source,
        IReadOnlyDictionary<string, MaterialDefinition> indexedById)
    {
        if (source.Emissions is not { } emissions)
        {
            return new MaterialEmissionProperties
            {
                SmokeIntoMaterialIndex = uint.MaxValue,
                GasIntoMaterialIndex = uint.MaxValue,
                FlameIntoMaterialIndex = uint.MaxValue
            };
        }
        return new MaterialEmissionProperties
        {
            SmokeIntoMaterialIndex = indexedById[emissions.SmokeIntoId].RuntimeIndex,
            SmokeRate = emissions.SmokeRate,
            GasIntoMaterialIndex = indexedById[emissions.GasIntoId].RuntimeIndex,
            GasRate = emissions.GasRate,
            FlameIntoMaterialIndex = emissions.FlameIntoId is { } flameId
                ? indexedById[flameId].RuntimeIndex
                : uint.MaxValue,
            FlameRate = emissions.FlameRate
        };
    }

    internal static MaterialProperties CreateProperties(
        MaterialSimulationKind kind,
        MaterialFlags flags,
        float density,
        float friction,
        float flowRate,
        float initialTemperature,
        float thermalConductivity,
        float heatCapacity,
        float ambientTemperature,
        float ambientCoolingRate,
        float gasDiffusion,
        float gasBuoyancy,
        Color color)
    {
        return new MaterialProperties
        {
            Flags = (uint)flags,
            SimulationKind = (uint)kind,
            Density = density,
            Friction = friction,
            FlowRate = flowRate,
            ColorR = color.R / 255f,
            ColorG = color.G / 255f,
            ColorB = color.B / 255f,
            ColorA = color.A / 255f,
            InitialTemperature = initialTemperature,
            ThermalConductivity = thermalConductivity,
            HeatCapacity = heatCapacity,
            AmbientTemperature = ambientTemperature,
            AmbientCoolingRate = ambientCoolingRate,
            GasDiffusion = gasDiffusion,
            GasBuoyancy = gasBuoyancy,
            TransitionBelowMaterialIndex = uint.MaxValue,
            TransitionAboveMaterialIndex = uint.MaxValue,
            BurnedIntoMaterialIndex = uint.MaxValue,
            DecayIntoMaterialIndex = uint.MaxValue,
            ContactLiquidIntoMaterialIndex = uint.MaxValue
        };
    }
}
