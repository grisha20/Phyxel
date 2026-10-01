// Include after the Materials structured buffer declaration.
// Lifetime is a tagged auxiliary slot: phase enthalpy and finite lifetime
// are mutually exclusive, enforced by the material loader/registry.
bool HasPhaseEnthalpy(MaterialProperties material)
{
    return (material.Flags & MaterialFlagPhaseEnthalpy) != 0;
}

MaterialProperties PhaseLiquid(MaterialProperties material)
{
    if (material.SimulationKind == SimulationKindLiquid) return material;
    return Materials[material.TransitionBelowMaterialIndex];
}

float VapourEnthalpyReference(MaterialProperties liquid, MaterialProperties vapour)
{
    return liquid.TransitionAboveLatentHeat +
        (liquid.HeatCapacity - vapour.HeatCapacity) * liquid.TransitionAboveTemperature;
}

float CellSpecificEnthalpy(GridCell cell)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (!HasPhaseEnthalpy(material)) return material.HeatCapacity * cell.Temperature;
    if (material.SimulationKind == SimulationKindLiquid)
        return material.HeatCapacity * cell.Temperature + cell.Lifetime;
    return material.HeatCapacity * cell.Temperature +
        VapourEnthalpyReference(PhaseLiquid(material), material) - cell.Lifetime;
}

GridCell SetCellSpecificEnthalpy(GridCell cell, float energy)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (!HasPhaseEnthalpy(material))
    {
        cell.Temperature = energy / material.HeatCapacity;
        return cell;
    }
    MaterialProperties liquid = PhaseLiquid(material);
    if (material.SimulationKind == SimulationKindLiquid)
    {
        float sensible = liquid.HeatCapacity * liquid.TransitionAboveTemperature;
        cell.Lifetime = clamp(energy - sensible, 0, liquid.TransitionAboveLatentHeat);
        cell.Temperature = cell.Lifetime > 0 && cell.Lifetime < liquid.TransitionAboveLatentHeat
            ? liquid.TransitionAboveTemperature : (energy - cell.Lifetime) / liquid.HeatCapacity;
    }
    else
    {
        float reference = VapourEnthalpyReference(liquid, material);
        float sensible = material.HeatCapacity * material.TransitionBelowTemperature + reference;
        float latentRange = sensible - liquid.HeatCapacity * material.TransitionBelowTemperature;
        cell.Lifetime = clamp(sensible - energy, 0, latentRange);
        cell.Temperature = cell.Lifetime > 0 && cell.Lifetime < latentRange
            ? material.TransitionBelowTemperature : (energy - reference + cell.Lifetime) / material.HeatCapacity;
    }
    return cell;
}

uint SelectEnthalpyPhaseTarget(GridCell cell)
{
    MaterialProperties material = Materials[cell.MaterialIndex];
    float energy = CellSpecificEnthalpy(cell);
    if (material.SimulationKind == SimulationKindLiquid)
    {
        if (energy >= material.HeatCapacity * material.TransitionAboveTemperature +
            material.TransitionAboveLatentHeat)
            return material.TransitionAboveMaterialIndex;
        if (cell.Temperature < material.TransitionBelowTemperature)
            return material.TransitionBelowMaterialIndex;
    }
    else if (energy <= PhaseLiquid(material).HeatCapacity * material.TransitionBelowTemperature)
        return material.TransitionBelowMaterialIndex;
    return 0xffffffffu;
}

float EnthalpyTransitionTemperature(GridCell cell, MaterialProperties source, MaterialProperties target)
{
    float energy = CellSpecificEnthalpy(cell);
    if (source.SimulationKind == SimulationKindLiquid)
        energy -= VapourEnthalpyReference(source, target);
    return energy / target.HeatCapacity;
}
