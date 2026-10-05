using System;
using System.IO;
using Phyxel.Materials;

namespace Phyxel.Core;

public enum FilterSelection
{
    Steam,
    Water,
    Oil,
    Gases,
    Liquids,
    Powders,
    SelectedMaterial,
    Wall,
    AirOnly,
    NoAir
}

// A uint per fine cell; zero means no overlay. Low 9 bits are index+1,
// remapped through the scene palette on save/load. Other bits select channels.
public static class FilterRules
{
    public const uint IdMask = 511;
    public const uint Gas = 1u << 16;
    public const uint Liquid = 1u << 17;
    public const uint AmbientAir = 1u << 18;
    public const uint Closed = 1u << 19;
    public const uint Powder = 1u << 20;
    public const uint AllParticles = 1u << 21;
    public const uint KnownMask = IdMask | Gas | Liquid | AmbientAir | Closed | Powder | AllParticles;

    public static uint Select(FilterSelection selection, MaterialRegistry registry, ushort selected)
    {
        if (selection == FilterSelection.Wall) return Closed;
        if (selection == FilterSelection.AirOnly) return AmbientAir;
        if (selection == FilterSelection.NoAir) return AllParticles;
        if (selection == FilterSelection.Gases) return Gas | AmbientAir;
        if (selection == FilterSelection.Liquids) return Liquid;
        if (selection == FilterSelection.Powders) return Powder;
        ushort target = selection switch
        {
            FilterSelection.Steam => registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
            FilterSelection.Water => registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            FilterSelection.Oil => registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),
            _ => selected
        };
        var kind = (MaterialSimulationKind)registry[target].Properties.SimulationKind;
        return kind is MaterialSimulationKind.Gas or MaterialSimulationKind.Liquid or MaterialSimulationKind.Granular
            ? (uint)target + 1 : Closed;
    }

    public static bool Allows(uint rule, uint material, MaterialSimulationKind kind) => rule == 0 ||
        ((rule & Closed) == 0 &&
         ((rule & AllParticles) != 0 ||
          ((rule & IdMask) != 0 && (rule & IdMask) == material + 1) ||
          (kind == MaterialSimulationKind.Gas && (rule & Gas) != 0) ||
          (kind == MaterialSimulationKind.Liquid && (rule & Liquid) != 0) ||
          (kind == MaterialSimulationKind.Granular && (rule & Powder) != 0)));

    public static bool AirAllows(uint rule) => rule == 0 ||
        ((rule & Closed) == 0 && (rule & AmbientAir) != 0);

    public static void Validate(uint rule, int materialCount)
    {
        if ((rule & ~KnownMask) != 0 || (rule & IdMask) > materialCount ||
            (rule != 0 && (rule & (IdMask | Gas | Liquid | Powder | Closed | AmbientAir | AllParticles)) == 0))
            throw new InvalidDataException("Invalid filter rule.");
    }

    public static string Label(FilterSelection selection, string materialName) => selection switch
    {
        FilterSelection.Steam => "Только пар",
        FilterSelection.Water => "Только вода",
        FilterSelection.Oil => "Только масло",
        FilterSelection.Gases => "Все газы",
        FilterSelection.Liquids => "Все жидкости",
        FilterSelection.Powders => "Все порошки",
        FilterSelection.Wall => "Сплошная стенка",
        FilterSelection.AirOnly => "Только воздух",
        FilterSelection.NoAir => "Без воздуха",
        _ => "Только " + materialName
    };
}
