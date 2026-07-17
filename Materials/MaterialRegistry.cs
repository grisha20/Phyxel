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
        List<MaterialDefinition> definitions = CreateBuiltIns();

        HashSet<string> reservedIds = definitions
            .Select(material => material.Id)
            .ToHashSet(StringComparer.Ordinal);
        string directory = externalDirectory ?? ResolveExternalDirectory();
        definitions.AddRange(MaterialFileLoader.Load(
            directory,
            reservedIds,
            MaximumMaterials - definitions.Count));

        for (int index = 0; index < definitions.Count; index++)
        {
            MaterialDefinition definition = definitions[index];
            MaterialProperties properties = definition.Properties;
            definitions[index] = definition with
            {
                RuntimeIndex = checked((ushort)index),
                Properties = properties
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

    public static string NormalizeId(string id) => id.Trim().ToLowerInvariant();

    private static List<MaterialDefinition> CreateBuiltIns()
    {
        return
        [
            Create(CoreMaterialIds.Empty, MaterialId.Empty, "Пустота", new Color(0, 0, 0, 0), MaterialSimulationKind.None, 0f, 0f, 0f, -1000, true),
            Create(CoreMaterialIds.Sand, MaterialId.Sand, "Песок", new Color(218, 184, 92), MaterialSimulationKind.Granular, 1.5f, 0.75f, 0.18f, 10),
            Create(CoreMaterialIds.Water, MaterialId.Water, "Вода", new Color(43, 132, 207), MaterialSimulationKind.Liquid, 1f, 0.025f, 0.92f, 20),
            Create(CoreMaterialIds.Metal, MaterialId.Metal, "Металл", new Color(142, 156, 166), MaterialSimulationKind.Solid, 7.8f, 0.35f, 0f, 30, flags: MaterialFlags.MovableSolid),
            Create(CoreMaterialIds.Concrete, MaterialId.Concrete, "Бетон", new Color(92, 96, 101), MaterialSimulationKind.Solid, 9.2f, 0.75f, 0f, 40, flags: MaterialFlags.MovableSolid),
            Create(CoreMaterialIds.Eraser, MaterialId.Eraser, "Ластик", new Color(222, 88, 88), MaterialSimulationKind.Tool, 0f, 0f, 0f, 50),
            Create(CoreMaterialIds.Gas, MaterialId.Gas, "Газ", new Color(155, 196, 210, 155), MaterialSimulationKind.Gas, 0.08f, 0.005f, 1.2f, 60),
            Create(CoreMaterialIds.Fixture, MaterialId.Fixture, "Опора", new Color(82, 91, 99), MaterialSimulationKind.Solid, 100f, 0.9f, 0f, 70)
        ];
    }

    private static MaterialDefinition Create(
        string id,
        MaterialId legacyId,
        string name,
        Color color,
        MaterialSimulationKind kind,
        float density,
        float friction,
        float flowRate,
        int uiOrder,
        bool hidden = false,
        MaterialFlags flags = MaterialFlags.None)
    {
        return new MaterialDefinition(
            id,
            checked((ushort)legacyId),
            name,
            color,
            CreateProperties(kind, flags, density, friction, flowRate, color),
            uiOrder,
            hidden);
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
