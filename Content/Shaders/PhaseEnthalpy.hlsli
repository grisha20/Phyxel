// Include after Materials. Lifetime is tagged specific latent progress.
// Liquid freezing is negative; solid melting and liquid boiling positive.
bool HasFusionEnthalpy(MaterialProperties m) { return (m.Flags & MaterialFlagFusionEnthalpy) != 0; }
bool HasVapourEnthalpy(MaterialProperties m) { return (m.Flags & MaterialFlagPhaseEnthalpy) != 0; }
bool HasPhaseEnthalpy(MaterialProperties m) { return HasFusionEnthalpy(m) || HasVapourEnthalpy(m); }
MaterialProperties PhaseLiquid(MaterialProperties m)
{
    if(m.SimulationKind==SimulationKindLiquid) return m;
    return Materials[m.SimulationKind==SimulationKindSolid ? m.TransitionAboveMaterialIndex : m.TransitionBelowMaterialIndex];
}
float SolidEnthalpyReference(MaterialProperties solid, MaterialProperties liquid)
{
    return (liquid.HeatCapacity-solid.HeatCapacity)*solid.TransitionAboveTemperature-solid.TransitionAboveLatentHeat;
}
float VapourEnthalpyReference(MaterialProperties liquid, MaterialProperties vapour)
{
    return liquid.TransitionAboveLatentHeat+(liquid.HeatCapacity-vapour.HeatCapacity)*liquid.TransitionAboveTemperature;
}
float CellEffectiveCapacity(GridCell c)
{
    MaterialProperties m=Materials[c.MaterialIndex];
    // Capacity and specific enthalpy must describe the same actual parcel.
    // A mass floor here, followed by heat/actual-mass in air exchange, made
    // minute combustion products overshoot by orders of magnitude and reach
    // infinity/NaN. Positive sub-resolution stock is retained, not erased.
    return m.HeatCapacity*max(c.Mass,0) + (c.MoistureMass>0 && m.MoistureCapacity>0
        ? c.MoistureMass*Materials[m.MoistureLiquidMaterialIndex].HeatCapacity : 0)
        + (c.FuelMass>0 && m.FuelCapacity>0 ? c.FuelMass*Materials[RetainedLiquidIndex(c,m)].HeatCapacity : 0);
}
float CellSpecificEnthalpy(GridCell c)
{
    MaterialProperties m=Materials[c.MaterialIndex];
    if((c.MoistureMass>0 && m.MoistureCapacity>0) || (c.FuelMass>0 && m.FuelCapacity>0))
        return (CellEffectiveCapacity(c)*c.Temperature+c.MoistureEnergy)/c.Mass;
    if(!HasPhaseEnthalpy(m)) return m.HeatCapacity*c.Temperature;
    if(m.SimulationKind==SimulationKindSolid)
        return m.HeatCapacity*c.Temperature+SolidEnthalpyReference(m,PhaseLiquid(m))+c.Lifetime;
    if(m.SimulationKind==SimulationKindLiquid) return m.HeatCapacity*c.Temperature+c.Lifetime;
    return m.HeatCapacity*c.Temperature+VapourEnthalpyReference(PhaseLiquid(m),m)-c.Lifetime;
}
GridCell SetCellSpecificEnthalpy(GridCell c, float energy)
{
    MaterialProperties m=Materials[c.MaterialIndex];
    if(c.MoistureMass>0 && m.MoistureCapacity>0)
    {
        MaterialProperties water=Materials[m.MoistureLiquidMaterialIndex];
        float capacity=CellEffectiveCapacity(c), total=energy*c.Mass;
        // Keep the wet surface at boiling while vapour awaits an outlet.
        // The excess stays in the energy ledger, not a discarded T clamp.
        c.MoistureEnergy=max(total-capacity*water.TransitionAboveTemperature,0);
        c.Temperature=min(total/capacity,water.TransitionAboveTemperature);
        return c;
    }
    if(c.FuelMass>0 && m.FuelCapacity>0) { c.Temperature=energy*c.Mass/CellEffectiveCapacity(c); return c; }
    if(!HasPhaseEnthalpy(m)) { c.Temperature=energy/m.HeatCapacity; return c; }
    MaterialProperties liquid=PhaseLiquid(m);
    if(m.SimulationKind==SimulationKindSolid)
    {
        float reference=SolidEnthalpyReference(m,liquid), start=m.HeatCapacity*m.TransitionAboveTemperature+reference;
        c.Lifetime=clamp(energy-start,0,m.TransitionAboveLatentHeat);
        c.Temperature=c.Lifetime>0 && c.Lifetime<m.TransitionAboveLatentHeat
            ? m.TransitionAboveTemperature : (energy-reference-c.Lifetime)/m.HeatCapacity;
    }
    else if(m.SimulationKind==SimulationKindLiquid)
    {
        if(HasFusionEnthalpy(m) && energy<m.HeatCapacity*m.TransitionBelowTemperature)
        {
            MaterialProperties solid=Materials[m.TransitionBelowMaterialIndex];
            float start=m.HeatCapacity*m.TransitionBelowTemperature;
            float range=start-(solid.HeatCapacity*m.TransitionBelowTemperature+SolidEnthalpyReference(solid,m));
            c.Lifetime=clamp(energy-start,-range,0);
            c.Temperature=c.Lifetime<0 && c.Lifetime>-range
                ? m.TransitionBelowTemperature : (energy-c.Lifetime)/m.HeatCapacity;
        }
        else
        {
            c.Lifetime=HasVapourEnthalpy(m)
                ? clamp(energy-m.HeatCapacity*m.TransitionAboveTemperature,0,m.TransitionAboveLatentHeat) : 0;
            c.Temperature=c.Lifetime>0 && c.Lifetime<m.TransitionAboveLatentHeat
                ? m.TransitionAboveTemperature : (energy-c.Lifetime)/m.HeatCapacity;
        }
    }
    else
    {
        float reference=VapourEnthalpyReference(liquid,m), start=m.HeatCapacity*m.TransitionBelowTemperature+reference;
        float range=start-liquid.HeatCapacity*m.TransitionBelowTemperature;
        c.Lifetime=clamp(start-energy,0,range);
        c.Temperature=c.Lifetime>0 && c.Lifetime<range
            ? m.TransitionBelowTemperature : (energy-reference+c.Lifetime)/m.HeatCapacity;
    }
    return c;
}
uint SelectEnthalpyPhaseTarget(GridCell c)
{
    MaterialProperties m=Materials[c.MaterialIndex]; float energy=CellSpecificEnthalpy(c);
    MaterialProperties liquid=PhaseLiquid(m);
    if(m.SimulationKind==SimulationKindSolid)
        return energy>=liquid.HeatCapacity*m.TransitionAboveTemperature ? m.TransitionAboveMaterialIndex : 0xffffffffu;
    if(m.SimulationKind==SimulationKindLiquid)
    {
        if(HasFusionEnthalpy(m))
        {
            MaterialProperties solid=Materials[m.TransitionBelowMaterialIndex];
            if(energy<=solid.HeatCapacity*m.TransitionBelowTemperature+SolidEnthalpyReference(solid,m)) return m.TransitionBelowMaterialIndex;
        }
        else if(c.Temperature<m.TransitionBelowTemperature) return m.TransitionBelowMaterialIndex;
        if(HasVapourEnthalpy(m) && energy>=m.HeatCapacity*m.TransitionAboveTemperature+m.TransitionAboveLatentHeat)
            return m.TransitionAboveMaterialIndex;
    }
    else if(energy<=liquid.HeatCapacity*m.TransitionBelowTemperature) return m.TransitionBelowMaterialIndex;
    return 0xffffffffu;
}
