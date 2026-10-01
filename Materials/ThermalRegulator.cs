using System;
using Phyxel.Physics;

namespace Phyxel.Materials;

public static class ThermalRegulator
{
    public const float MaximumPower = 3600;
    public static bool Enabled(MaterialProperties material) =>
        (material.Flags & (uint)(MaterialFlags.ThermalHeater | MaterialFlags.ThermalCooler)) != 0;

    public static bool ValidCellSettings(GridCell cell) =>
        float.IsFinite(cell.DeviceTargetTemperature) &&
        cell.DeviceTargetTemperature >= MaterialRegistry.MinimumInitialTemperature &&
        cell.DeviceTargetTemperature <= MaterialRegistry.MaximumInitialTemperature &&
        float.IsFinite(cell.DeviceMaximumPower) && cell.DeviceMaximumPower >= 0 &&
        cell.DeviceMaximumPower <= MaximumPower;
}
