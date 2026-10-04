// Stored ambient oxidizer is an AMOUNT in fresh-air-equivalent units.
// It may exceed one when gas/liquid displaces a volume; never clamp inventory.
// Only the portion fitting the current free volume is available to combustion.
// This field belongs to space; moving/removing a gas never creates fresh air.
static const float OxidizerExtinctionThreshold = 0.20;
static const float OxidizerPerFuelMass = 20.0;

// Space that can contain gas, regardless of its composition. A solid face
// blocks supply, but is not a zero-oxygen volume diluting the other faces.
// Inert gas still counts as space, so CO2/steam really lower concentration.
float OxidizerSpace(GridCell cell, MaterialProperties material)
{
    return cell.IsActive == 0 || material.SimulationKind == SimulationKindGas ? 1 : 0;
}

float OxidizerCapacity(GridCell cell, MaterialProperties material)
{
    if (cell.IsActive == 0) return 1;
    if (material.SimulationKind != SimulationKindGas) return 0;
    if ((material.Flags & MaterialFlagFlame) != 0) return 1;
    // Gas metadata determines displacement: pure CO2/steam use 1, smoke
    // aerosol uses 0.05. Fractional emissions displace their occupied share.
    return 1 - saturate(material.ThermalDeviceTargetTemperature) *
        saturate(cell.Mass / max(material.Density, 0.0001));
}

float OxidizerAccessible(float amount, GridCell cell, MaterialProperties material)
{
    return min(max(0, amount), OxidizerCapacity(cell, material));
}

// Tagged only on flame cells produced by fuel carrying its own oxidizer.
static const uint SelfOxidizingFlameMarker = 0x80000000u;
// Heat tracer from fuel whose reaction has already paid its oxygen demand.
static const uint ReactedFuelFlameMarker = 0x40000000u;
