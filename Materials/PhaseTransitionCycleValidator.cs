using System;
using System.Collections.Generic;
using System.Linq;

namespace Phyxel.Materials;

internal static class PhaseTransitionCycleValidator
{
    public static IReadOnlySet<string> FindInstantaneousCycleMaterials(
        IEnumerable<MaterialDefinition> definitions)
    {
        MaterialDefinition[] materials = definitions
            .OrderBy(material => material.Id, StringComparer.Ordinal)
            .ToArray();
        double[] boundaries = materials
            .SelectMany(GetThresholds)
            .Select(value => (double)value)
            .Append(MaterialRegistry.MinimumInitialTemperature)
            .Append(MaterialRegistry.MaximumInitialTemperature)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        HashSet<string> cycleMaterials = new(StringComparer.Ordinal);
        for (int index = 0; index + 1 < boundaries.Length; index++)
        {
            double lower = boundaries[index];
            double upper = boundaries[index + 1];
            if (lower >= upper)
            {
                continue;
            }

            double sampleTemperature = lower + (upper - lower) / 2;
            Dictionary<string, string> edges = BuildApplicableEdges(materials, sampleTemperature);
            CollectCycleMaterials(materials, edges, cycleMaterials);
        }

        return cycleMaterials;
    }

    private static IEnumerable<float> GetThresholds(MaterialDefinition material)
    {
        if (material.PhaseTransitions?.Below is { } below)
        {
            yield return below.Temperature;
        }
        if (material.PhaseTransitions?.Above is { } above)
        {
            yield return above.Temperature;
        }
    }

    private static Dictionary<string, string> BuildApplicableEdges(
        IEnumerable<MaterialDefinition> materials,
        double temperature)
    {
        Dictionary<string, string> edges = new(StringComparer.Ordinal);
        foreach (MaterialDefinition material in materials)
        {
            MaterialTransitionDefinitions? transitions = material.PhaseTransitions;
            if (transitions?.Below is { } below && temperature < below.Temperature)
            {
                edges.Add(material.Id, below.IntoId);
            }
            else if (transitions?.Above is { } above && temperature > above.Temperature)
            {
                edges.Add(material.Id, above.IntoId);
            }
        }
        return edges;
    }

    private static void CollectCycleMaterials(
        IReadOnlyList<MaterialDefinition> materials,
        IReadOnlyDictionary<string, string> edges,
        HashSet<string> cycleMaterials)
    {
        Dictionary<string, byte> colors = materials.ToDictionary(
            material => material.Id,
            _ => (byte)0,
            StringComparer.Ordinal);
        List<string> path = [];
        Dictionary<string, int> pathIndices = new(StringComparer.Ordinal);

        foreach (MaterialDefinition material in materials)
        {
            if (colors[material.Id] == 0)
            {
                Visit(material.Id);
            }
        }

        void Visit(string id)
        {
            colors[id] = 1;
            pathIndices[id] = path.Count;
            path.Add(id);

            if (edges.TryGetValue(id, out string? targetId) && colors.ContainsKey(targetId))
            {
                byte targetColor = colors[targetId];
                if (targetColor == 0)
                {
                    Visit(targetId);
                }
                else if (targetColor == 1)
                {
                    int cycleStart = pathIndices[targetId];
                    for (int index = cycleStart; index < path.Count; index++)
                    {
                        cycleMaterials.Add(path[index]);
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            pathIndices.Remove(id);
            colors[id] = 2;
        }
    }
}
