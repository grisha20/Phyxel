// Only combustion and its display need these values. Keep this out of the
// general physics include to avoid recompiling unrelated expensive solvers.
static const uint FiniteHeatEmissionMarker = 0x20000000u;
// Finite energy controls the heat carried by the packet; it must not also
// shorten its residence independently of the fuel's configured flame life.
static const float CoalFlameResidenceScale = 1;

// Display age is independent of the finite heat tracer's physical residence.
// Called only for FIRE; this metadata bit has a separate meaning on liquids.
float FlameDisplayLifetime(GridCell cell, MaterialProperties material)
{
    if ((cell.BodyId & FiniteHeatEmissionMarker)==0) return cell.Lifetime;
    // A paid hot tracer is still in its flame phase. Age alone used to turn
    // its colour almost black well before it left the furnace channel.
    // This is a bounded artistic palette, not extra heat or gas chemistry.
    // Keep actual cooling and the final lifetime fade visible; never colour
    // SMKE or extend the physical packet's life here.
    float age = cell.Lifetime / CoalFlameResidenceScale;
    float hot = saturate((cell.Temperature-material.FlameExtinctionTemperature) /
        max(1.0, material.InitialTemperature-material.FlameExtinctionTemperature));
    float endFade = saturate(age / max(.1, .05*material.MaximumLifetime));
    return max(age, .75*material.MaximumLifetime*hot) * endFade * hot;
}
