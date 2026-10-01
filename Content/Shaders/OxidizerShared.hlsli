// Normalized ambient oxidizer: 1 is fresh air, 0 is exhausted/displaced air.
// This field belongs to space; moving/removing a gas never creates fresh air.
static const float OxidizerExtinctionThreshold = 0.20;
static const float OxidizerPerFuelMass = 20.0;

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

// Tagged only on flame cells produced by fuel carrying its own oxidizer.
static const uint SelfOxidizingFlameMarker = 0x80000000u;
