using System;
using Phyxel.Physics;

namespace Phyxel.Materials;

/// <summary>
/// Opt-in liquid/vapour enthalpy. Infinite-lived phase materials use the
/// cell's tagged auxiliary slot (Lifetime) for latent progress per unit mass.
/// This preserves the 40-byte world layout and existing mass-weighted transport.
/// </summary>
public static class PhaseEnthalpy
{
    public static bool Enabled(MaterialProperties material) =>
        (material.Flags & (uint)MaterialFlags.PhaseEnthalpy) != 0;

    public static MaterialProperties Liquid(MaterialProperties material,
        ReadOnlySpan<MaterialProperties> materials) =>
        material.SimulationKind == (uint)MaterialSimulationKind.Liquid
            ? material : materials[(int)material.TransitionBelowMaterialIndex];

    public static float VapourReference(MaterialProperties liquid, MaterialProperties vapour) =>
        liquid.TransitionAboveLatentHeat +
        (liquid.HeatCapacity - vapour.HeatCapacity) * liquid.TransitionAboveTemperature;

    public static float SpecificEnergy(GridCell cell, ReadOnlySpan<MaterialProperties> materials)
    {
        MaterialProperties material = materials[(int)cell.MaterialIndex];
        if (!Enabled(material)) return material.HeatCapacity * cell.Temperature;
        if (material.SimulationKind == (uint)MaterialSimulationKind.Liquid)
            return material.HeatCapacity * cell.Temperature + cell.PhaseProgress;
        return material.HeatCapacity * cell.Temperature +
            VapourReference(Liquid(material, materials), material) - cell.PhaseProgress;
    }

    public static void SetSpecificEnergy(ref GridCell cell, float energy,
        ReadOnlySpan<MaterialProperties> materials)
    {
        MaterialProperties material = materials[(int)cell.MaterialIndex];
        if (!Enabled(material))
        {
            cell.Temperature = energy / material.HeatCapacity;
            return;
        }
        MaterialProperties liquid = Liquid(material, materials);
        if (material.SimulationKind == (uint)MaterialSimulationKind.Liquid)
        {
            float sensible = liquid.HeatCapacity * liquid.TransitionAboveTemperature;
            cell.PhaseProgress = Math.Clamp(energy - sensible, 0, liquid.TransitionAboveLatentHeat);
            cell.Temperature = cell.PhaseProgress > 0 && cell.PhaseProgress < liquid.TransitionAboveLatentHeat
                ? liquid.TransitionAboveTemperature : (energy - cell.PhaseProgress) / liquid.HeatCapacity;
        }
        else
        {
            float reference = VapourReference(liquid, material);
            float sensible = material.HeatCapacity * material.TransitionBelowTemperature + reference;
            float latentRange = sensible - liquid.HeatCapacity * material.TransitionBelowTemperature;
            cell.PhaseProgress = Math.Clamp(sensible - energy, 0, latentRange);
            cell.Temperature = cell.PhaseProgress > 0 && cell.PhaseProgress < latentRange
                ? material.TransitionBelowTemperature : (energy - reference + cell.PhaseProgress) / material.HeatCapacity;
        }
    }

    public static uint SelectTarget(ref GridCell cell, ReadOnlySpan<MaterialProperties> materials)
    {
        MaterialProperties material = materials[(int)cell.MaterialIndex];
        if (!Enabled(material)) return PhaseTransitionRuntime.SelectTarget(cell.Temperature, material);
        float energy = SpecificEnergy(cell, materials);
        SetSpecificEnergy(ref cell, energy, materials);
        if (material.SimulationKind == (uint)MaterialSimulationKind.Liquid)
        {
            if (energy >= material.HeatCapacity * material.TransitionAboveTemperature +
                material.TransitionAboveLatentHeat)
                return material.TransitionAboveMaterialIndex;
            // Other transitions (e.g. freezing) keep their existing contract.
            return cell.Temperature < material.TransitionBelowTemperature
                ? material.TransitionBelowMaterialIndex : uint.MaxValue;
        }
        MaterialProperties liquid = Liquid(material, materials);
        return energy <= liquid.HeatCapacity * material.TransitionBelowTemperature
            ? material.TransitionBelowMaterialIndex : uint.MaxValue;
    }

    public static float TransitionTemperature(GridCell sourceCell, MaterialProperties source,
        MaterialProperties target)
    {
        bool boiling = source.SimulationKind == (uint)MaterialSimulationKind.Liquid;
        MaterialProperties liquid = boiling ? source : target;
        MaterialProperties vapour = boiling ? target : source;
        float reference = VapourReference(liquid, vapour);
        float energy = source.HeatCapacity * sourceCell.Temperature +
            (boiling ? sourceCell.PhaseProgress : reference - sourceCell.PhaseProgress);
        return (energy - (boiling ? reference : 0)) / target.HeatCapacity;
    }
}
