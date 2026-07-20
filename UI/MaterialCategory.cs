using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

public enum MaterialCategoryType
{
    Powders,
    Liquids,
    Gases,
    Solids,
    Combustion,
    Tools
}

public sealed record MaterialCategoryDefinition(
    MaterialCategoryType Type,
    string DisplayName,
    Color AccentColor,
    string IconSymbol);

public static class MaterialCategoryResolver
{
    public static readonly IReadOnlyList<MaterialCategoryDefinition> AllCategories =
    [
        new(MaterialCategoryType.Powders, "Порошки", UiTheme.CategoryPowders, "::"),
        new(MaterialCategoryType.Liquids, "Жидкости", UiTheme.CategoryLiquids, "~~"),
        new(MaterialCategoryType.Gases, "Газы", UiTheme.CategoryGases, "oo"),
        new(MaterialCategoryType.Solids, "Твёрдые", UiTheme.CategorySolids, "[]"),
        new(MaterialCategoryType.Combustion, "Горение", UiTheme.CategoryCombustion, "^^"),
        new(MaterialCategoryType.Tools, "Инструменты", UiTheme.CategoryTools, "++")
    ];

    public static MaterialCategoryType Resolve(MaterialDefinition material)
    {
        // 1. Explicit Category if specified in JSON
        if (!string.IsNullOrWhiteSpace(material.Category))
        {
            string cat = material.Category.Trim().ToLowerInvariant();
            switch (cat)
            {
                case "powders":
                case "powder":
                case "granular":
                case "порошки":
                case "порошок":
                    return MaterialCategoryType.Powders;
                case "liquids":
                case "liquid":
                case "жидкости":
                case "жидкость":
                    return MaterialCategoryType.Liquids;
                case "gases":
                case "gas":
                case "газы":
                case "газ":
                    return MaterialCategoryType.Gases;
                case "solids":
                case "solid":
                case "твёрдые":
                case "твердые":
                case "твёрдое":
                case "твердое":
                    return MaterialCategoryType.Solids;
                case "combustion":
                case "fire":
                case "горение":
                case "огонь":
                    return MaterialCategoryType.Combustion;
                case "tools":
                case "tool":
                case "инструменты":
                case "инструмент":
                    return MaterialCategoryType.Tools;
            }
        }

        // 2. Fallback to properties (kind/flags/id)
        MaterialSimulationKind kind = (MaterialSimulationKind)material.Properties.SimulationKind;
        MaterialFlags flags = (MaterialFlags)material.Properties.Flags;
        string id = material.Id ?? string.Empty;

        if (kind == MaterialSimulationKind.Tool || id.Equals(CoreMaterialIds.Eraser, StringComparison.OrdinalIgnoreCase))
        {
            return MaterialCategoryType.Tools;
        }

        if (id.Equals(CoreMaterialIds.Fire, StringComparison.OrdinalIgnoreCase) ||
            (flags & MaterialFlags.Flame) != 0)
        {
            return MaterialCategoryType.Combustion;
        }

        switch (kind)
        {
            case MaterialSimulationKind.Gas:
                return MaterialCategoryType.Gases;
            case MaterialSimulationKind.Liquid:
                return MaterialCategoryType.Liquids;
            case MaterialSimulationKind.Granular:
                return MaterialCategoryType.Powders;
            case MaterialSimulationKind.Solid:
                return MaterialCategoryType.Solids;
            default:
                return MaterialCategoryType.Solids;
        }
    }
}
