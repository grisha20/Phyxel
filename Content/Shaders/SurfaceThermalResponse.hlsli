// Normalized external coupling for a resolved granular surface. Internal
// conduction still uses ThermalConductivity. A positive k is not an optical
// absorptivity or a gas-side heat-transfer coefficient; k=0 retains the
// existing explicit game insulator contract. No SI coefficient is implied.
float ExternalSurfaceThermalResponse(MaterialProperties material)
{
    return material.SimulationKind == SimulationKindGranular
        ? (material.ThermalConductivity > 0 ? 1.0 : 0.0)
        : saturate(material.ThermalConductivity);
}
