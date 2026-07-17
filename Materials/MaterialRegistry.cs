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

    private readonly ReadOnlyCollection<MaterialDefinition> materials;
    private readonly ReadOnlyCollection<MaterialDefinition> selectableMaterials;
    private readonly Dictionary<string, MaterialDefinition> byId;

    public MaterialRegistry(string? externalDirectory = null)
    {
        string coreDirectory = ResolveCoreDirectory();
        List<MaterialDefinition> coreDefinitions = MaterialFileLoader
            .LoadCore(coreDirectory, MaximumMaterials)
            .ToList();
        ValidateRequiredCoreMaterials(coreDefinitions);

        HashSet<string> reservedIds = coreDefinitions
            .Select(material => material.Id)
            .ToHashSet(StringComparer.Ordinal);
        string directory = externalDirectory ?? ResolveExternalDirectory();
        List<MaterialDefinition> definitions = coreDefinitions
            .Concat(MaterialFileLoader.LoadExternal(
                directory,
                reservedIds,
                MaximumMaterials - coreDefinitions.Count,
                coreDirectory))
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

        materials = definitions.AsReadOnly();
        selectableMaterials = definitions
            .Where(material => !material.Hidden && material.Id != CoreMaterialIds.Empty)
            .OrderBy(material => material.UiOrder)
            .ThenBy(material => material.Id, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
        byId = definitions.ToDictionary(material => material.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<MaterialDefinition> Materials => materials;
    public IReadOnlyList<MaterialDefinition> SelectableMaterials => selectableMaterials;
    public int Count => materials.Count;

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

    internal static MaterialProperties CreateProperties(
        MaterialSimulationKind kind,
        MaterialFlags flags,
        float density,
        float friction,
        float flowRate,
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
            ColorA = color.A / 255f
        };
    }
}
