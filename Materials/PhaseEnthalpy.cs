using System;
using Phyxel.Physics;
namespace Phyxel.Materials;

/// <summary>Tagged specific latent progress: melting/boiling positive,
/// liquid freezing negative. Liquid energy reference remains c*T.</summary>
public static class PhaseEnthalpy
{
    public static float EffectiveCapacity(GridCell c, ReadOnlySpan<MaterialProperties> materials)
    {
        var m=materials[(int)c.MaterialIndex];
        return m.HeatCapacity*Math.Max(c.Mass,.0001f) + (c.MoistureMass>0 && m.MoistureCapacity>0
            ? c.MoistureMass*materials[(int)m.MoistureLiquidMaterialIndex].HeatCapacity : 0)
            + (c.FuelMass>0 && m.FuelCapacity>0 ? c.FuelMass*materials[(int)m.FuelLiquidMaterialIndex].HeatCapacity : 0);
    }
    public static bool Enabled(MaterialProperties m) =>
        (m.Flags & (uint)(MaterialFlags.PhaseEnthalpy | MaterialFlags.FusionEnthalpy)) != 0;
    public static bool FusionEnabled(MaterialProperties m) => (m.Flags & (uint)MaterialFlags.FusionEnthalpy) != 0;
    public static bool VapourEnabled(MaterialProperties m) => (m.Flags & (uint)MaterialFlags.PhaseEnthalpy) != 0;
    public static MaterialProperties Liquid(MaterialProperties m, ReadOnlySpan<MaterialProperties> materials) =>
        m.SimulationKind == (uint)MaterialSimulationKind.Liquid ? m :
        materials[(int)(m.SimulationKind == (uint)MaterialSimulationKind.Solid
            ? m.TransitionAboveMaterialIndex : m.TransitionBelowMaterialIndex)];
    public static float SolidReference(MaterialProperties solid, MaterialProperties liquid) =>
        (liquid.HeatCapacity-solid.HeatCapacity)*solid.TransitionAboveTemperature-solid.TransitionAboveLatentHeat;
    public static float VapourReference(MaterialProperties liquid, MaterialProperties vapour) =>
        liquid.TransitionAboveLatentHeat+(liquid.HeatCapacity-vapour.HeatCapacity)*liquid.TransitionAboveTemperature;
    public static float SpecificEnergy(GridCell c, ReadOnlySpan<MaterialProperties> materials)
    {
        MaterialProperties m=materials[(int)c.MaterialIndex];
        if((c.MoistureMass>0 && m.MoistureCapacity>0) || (c.FuelMass>0 && m.FuelCapacity>0))
            return (EffectiveCapacity(c,materials)*c.Temperature+c.MoistureEnergy)/c.Mass;
        if(!Enabled(m)) return m.HeatCapacity*c.Temperature;
        if(m.SimulationKind==(uint)MaterialSimulationKind.Solid)
            return m.HeatCapacity*c.Temperature+SolidReference(m,Liquid(m,materials))+c.PhaseProgress;
        if(m.SimulationKind==(uint)MaterialSimulationKind.Liquid) return m.HeatCapacity*c.Temperature+c.PhaseProgress;
        return m.HeatCapacity*c.Temperature+VapourReference(Liquid(m,materials),m)-c.PhaseProgress;
    }
    public static void SetSpecificEnergy(ref GridCell c, float energy, ReadOnlySpan<MaterialProperties> materials)
    {
        MaterialProperties m=materials[(int)c.MaterialIndex];
        if(c.MoistureMass>0 && m.MoistureCapacity>0)
        {
            MaterialProperties water=materials[(int)m.MoistureLiquidMaterialIndex];
            float capacity=EffectiveCapacity(c,materials), total=energy*c.Mass;
            c.MoistureEnergy=Math.Max(total-capacity*water.TransitionAboveTemperature,0);
            c.Temperature=Math.Min(total/capacity,water.TransitionAboveTemperature);
            return;
        }
        if(c.FuelMass>0 && m.FuelCapacity>0) { c.Temperature=energy*c.Mass/EffectiveCapacity(c,materials); return; }
        if(!Enabled(m)) { c.Temperature=energy/m.HeatCapacity; return; }
        MaterialProperties liquid=Liquid(m,materials);
        if(m.SimulationKind==(uint)MaterialSimulationKind.Solid)
        {
            float reference=SolidReference(m,liquid), start=m.HeatCapacity*m.TransitionAboveTemperature+reference;
            c.PhaseProgress=Math.Clamp(energy-start,0,m.TransitionAboveLatentHeat);
            c.Temperature=c.PhaseProgress>0 && c.PhaseProgress<m.TransitionAboveLatentHeat
                ? m.TransitionAboveTemperature : (energy-reference-c.PhaseProgress)/m.HeatCapacity;
        }
        else if(m.SimulationKind==(uint)MaterialSimulationKind.Liquid)
        {
            if(FusionEnabled(m) && energy<m.HeatCapacity*m.TransitionBelowTemperature)
            {
                MaterialProperties solid=materials[(int)m.TransitionBelowMaterialIndex];
                float start=m.HeatCapacity*m.TransitionBelowTemperature;
                float range=start-(solid.HeatCapacity*m.TransitionBelowTemperature+SolidReference(solid,m));
                c.PhaseProgress=Math.Clamp(energy-start,-range,0);
                c.Temperature=c.PhaseProgress<0 && c.PhaseProgress>-range
                    ? m.TransitionBelowTemperature : (energy-c.PhaseProgress)/m.HeatCapacity;
            }
            else
            {
                c.PhaseProgress=VapourEnabled(m)
                    ? Math.Clamp(energy-m.HeatCapacity*m.TransitionAboveTemperature,0,m.TransitionAboveLatentHeat) : 0;
                c.Temperature=c.PhaseProgress>0 && c.PhaseProgress<m.TransitionAboveLatentHeat
                    ? m.TransitionAboveTemperature : (energy-c.PhaseProgress)/m.HeatCapacity;
            }
        }
        else
        {
            float reference=VapourReference(liquid,m), start=m.HeatCapacity*m.TransitionBelowTemperature+reference;
            float range=start-liquid.HeatCapacity*m.TransitionBelowTemperature;
            c.PhaseProgress=Math.Clamp(start-energy,0,range);
            c.Temperature=c.PhaseProgress>0 && c.PhaseProgress<range
                ? m.TransitionBelowTemperature : (energy-reference+c.PhaseProgress)/m.HeatCapacity;
        }
    }
    public static uint SelectTarget(ref GridCell c, ReadOnlySpan<MaterialProperties> materials)
    {
        MaterialProperties m=materials[(int)c.MaterialIndex];
        if(!Enabled(m)) return PhaseTransitionRuntime.SelectTarget(c.Temperature,m);
        float energy=SpecificEnergy(c,materials);
        SetSpecificEnergy(ref c,energy,materials);
        MaterialProperties liquid=Liquid(m,materials);
        if(m.SimulationKind==(uint)MaterialSimulationKind.Solid)
            return energy>=liquid.HeatCapacity*m.TransitionAboveTemperature ? m.TransitionAboveMaterialIndex : uint.MaxValue;
        if(m.SimulationKind==(uint)MaterialSimulationKind.Liquid)
        {
            if(FusionEnabled(m))
            {
                MaterialProperties solid=materials[(int)m.TransitionBelowMaterialIndex];
                if(energy<=solid.HeatCapacity*m.TransitionBelowTemperature+SolidReference(solid,m))
                    return m.TransitionBelowMaterialIndex;
            }
            else if(c.Temperature<m.TransitionBelowTemperature) return m.TransitionBelowMaterialIndex;
            return VapourEnabled(m) && energy>=m.HeatCapacity*m.TransitionAboveTemperature+m.TransitionAboveLatentHeat
                ? m.TransitionAboveMaterialIndex : uint.MaxValue;
        }
        return energy<=liquid.HeatCapacity*m.TransitionBelowTemperature ? m.TransitionBelowMaterialIndex : uint.MaxValue;
    }
    // Pair-only normalization API. TryApply canonicalizes the target with
    // the full table too, so very cold condensate can carry freezing progress.
    public static float TransitionTemperature(GridCell c, MaterialProperties source, MaterialProperties target)
    {
        if(source.SimulationKind==(uint)MaterialSimulationKind.Solid)
            return (source.HeatCapacity*c.Temperature+SolidReference(source,target)+c.PhaseProgress)/target.HeatCapacity;
        if(target.SimulationKind==(uint)MaterialSimulationKind.Solid)
            return (source.HeatCapacity*c.Temperature+c.PhaseProgress-SolidReference(target,source))/target.HeatCapacity;
        bool boiling=source.SimulationKind==(uint)MaterialSimulationKind.Liquid;
        float reference=VapourReference(boiling?source:target,boiling?target:source);
        return (source.HeatCapacity*c.Temperature+(boiling?c.PhaseProgress-reference:reference-c.PhaseProgress))/target.HeatCapacity;
    }
}
