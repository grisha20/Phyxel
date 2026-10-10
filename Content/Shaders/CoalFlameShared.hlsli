// Only combustion and its display need these values. Keep this out of the
// general physics include to avoid recompiling unrelated expensive solvers.
static const uint FiniteHeatEmissionMarker = 0x20000000u;
// Finite energy controls the heat carried by the packet; it must not also
// shorten its residence independently of the fuel's configured flame life.
static const float CoalFlameResidenceScale = 1;

// Display age is independent of the finite heat tracer's physical residence.
// Called only for FIRE; this metadata bit has a separate meaning on liquids.
float FlameDisplayLifetime(GridCell cell)
{
    return cell.Lifetime / ((cell.BodyId & FiniteHeatEmissionMarker)!=0 ? CoalFlameResidenceScale : 1);
}
