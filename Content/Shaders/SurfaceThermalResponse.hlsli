// Normalized external coupling for a resolved granular or opt-in solid surface. Internal
// conduction still uses ThermalConductivity. A positive k is not an optical
// absorptivity or a gas-side heat-transfer coefficient; k=0 retains the
// existing explicit game insulator contract. No SI coefficient is implied.
static const uint MaterialFlagExternalSurfaceHeat = 1u << 18;
float ExternalSurfaceThermalResponse(MaterialProperties material)
{
    return material.SimulationKind == SimulationKindGranular ||
        (material.Flags & MaterialFlagExternalSurfaceHeat) != 0
        ? (material.ThermalConductivity > 0 ? 1.0 : 0.0)
        : saturate(material.ThermalConductivity);
}
