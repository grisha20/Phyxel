using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Xna.Framework;
using Phyxel.Physics;

namespace Phyxel.Materials;

public sealed class MaterialRegistry
{
    private readonly ReadOnlyCollection<MaterialDefinition> materials;
    private readonly Dictionary<MaterialId, MaterialDefinition> byId;

    public MaterialRegistry(IEnumerable<MaterialDefinition>? extensions = null)
    {
        List<MaterialDefinition> definitions = CreateBuiltIns();
        if (extensions is not null)
        {
            definitions.AddRange(extensions);
        }

        materials = definitions.AsReadOnly();
        byId = definitions.ToDictionary(material => material.Id);
    }

    public IReadOnlyList<MaterialDefinition> Materials => materials;

    public IReadOnlyList<MaterialDefinition> SelectableMaterials =>
        materials.Where(material => material.Id != MaterialId.Empty).ToArray();

    public MaterialDefinition this[MaterialId id] => byId[id];

    public MaterialProperties[] CreateGpuTable()
    {
        int capacity = Enum.GetValues<MaterialId>().Max(id => (int)id) + 1;
        MaterialProperties[] table = new MaterialProperties[capacity];
        foreach (MaterialDefinition material in materials)
        {
            table[(int)material.Id] = material.Properties;
        }

        return table;
    }

    private static List<MaterialDefinition> CreateBuiltIns()
    {
        return
        [
            Create(MaterialId.Empty, "Пустота", new Color(0, 0, 0, 0), MaterialSimulationKind.None, 0f, 0f, 0f),
            Create(MaterialId.Sand, "Песок", new Color(218, 184, 92), MaterialSimulationKind.Granular, 1.5f, 0.75f, 0.18f),
            Create(MaterialId.Water, "Вода", new Color(43, 132, 207), MaterialSimulationKind.Liquid, 1f, 0.025f, 0.92f),
            Create(MaterialId.Metal, "Металл", new Color(142, 156, 166), MaterialSimulationKind.Solid, 7.8f, 0.35f, 0f),
            Create(MaterialId.Concrete, "Бетон", new Color(92, 96, 101), MaterialSimulationKind.Solid, 9.2f, 0.75f, 0f),
            Create(MaterialId.Eraser, "Ластик", new Color(222, 88, 88), MaterialSimulationKind.Tool, 0f, 0f, 0f),
            Create(MaterialId.Gas, "Газ", new Color(155, 196, 210, 155), MaterialSimulationKind.Gas, 0.08f, 0.005f, 1.2f),
            Create(MaterialId.Fixture, "Опора", new Color(82, 91, 99), MaterialSimulationKind.Solid, 100f, 0.9f, 0f)
        ];
    }

    private static MaterialDefinition Create(
        MaterialId id,
        string name,
        Color color,
        MaterialSimulationKind kind,
        float density,
        float friction,
        float flowRate)
    {
        return new MaterialDefinition(id, name, color, new MaterialProperties
        {
            MaterialId = (uint)id,
            SimulationKind = (uint)kind,
            Density = density,
            Friction = friction,
            FlowRate = flowRate
        });
    }
}
