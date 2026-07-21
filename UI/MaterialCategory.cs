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
        if (TryResolveExplicitCategory(material.Category, out MaterialCategoryType explicitCategory))
        {
            return explicitCategory;
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

    public static bool TryResolveExplicitCategory(string? value, out MaterialCategoryType category)
    {
        category = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim())
        {
            case "powders": category = MaterialCategoryType.Powders; return true;
            case "liquids": category = MaterialCategoryType.Liquids; return true;
            case "gases": category = MaterialCategoryType.Gases; return true;
            case "solids": category = MaterialCategoryType.Solids; return true;
            case "combustion": category = MaterialCategoryType.Combustion; return true;
            case "tools": category = MaterialCategoryType.Tools; return true;
            default: return false;
        }
    }
}
