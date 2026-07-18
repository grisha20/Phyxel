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
                Properties = ResolvePhaseTransitionProperties(definition, indexedById)
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
                    Error: FindPhaseTransitionReferenceError(source, available, requireBundledTarget: false)))
                .Where(result => result.Error is not null)
                .Select(result => (result.Source, result.Error!))
                .ToList();
            if (invalidReferences.Count > 0)
            {
                foreach ((MaterialDefinition source, string error) in invalidReferences)
                {
                    MaterialFileLoader.LogError(
                        source.SourcePath,
                        $"Material '{source.Id}' has an invalid phase transition: {error}");
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

    private static MaterialProperties ResolvePhaseTransitionProperties(
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
        return properties;
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
            TransitionBelowMaterialIndex = uint.MaxValue,
            TransitionAboveMaterialIndex = uint.MaxValue
        };
    }
}
