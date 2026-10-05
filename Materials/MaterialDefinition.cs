using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Phyxel.Physics;

namespace Phyxel.Materials;

public enum MaterialSimulationKind : uint
{
    None = 0,
    Granular = 1,
    Solid = 2,
    Tool = 3,
    Liquid = 4,
    Gas = 5
}

[Flags]
public enum MaterialFlags : uint
{
    None = 0,
    MovableSolid = 1u << 0,
    Flame = 1u << 1,

    /// <summary>
    /// Материал содержит собственный окислитель и горит без доступа воздуха.
    /// Эталонный случай — порох: он работает в закрытой гильзе, поэтому
    /// зерно в середине кучи обязано сдетонировать вместе с остальными.
    /// Обычное топливо без этого флага горит только с открытых сторон.
    /// </summary>
    SelfOxidizing = 1u << 2,

    /// <summary>
    /// Дополнительная непроницаемость для поля воздуха. Обычные solid/liquid
    /// перекрывают связи потока по геометрии мелкой сетки; этот флаг также
    /// позволяет блокировать воздух материалам иных типов.
    /// </summary>
    BlocksAir = 1u << 3,

    /// <summary>
    /// Газ визуализируется через FIRE_BLEND-поле, а не собственной дискретной
    /// текстурой частиц. Это соответствует SMKE в The Powder Toy.
    /// </summary>
    Smoke = 1u << 4,

    /// <summary>Reversible latent-heat liquid/vapour pair, without lifecycle.</summary>
    PhaseEnthalpy = 1u << 5,
    ThermalHeater = 1u << 6,
    ThermalCooler = 1u << 7,
    // Runtime tag for the CO2/ambient-air density ratio; no layout expansion.
    ThermalCarbonDioxide = 1u << 8,
    // Coal retains ignition like TPT's burning life counter.
    PersistentCoalIgnition = 1u << 9,
    // Reversible solid/liquid enthalpy; liquid may also have liquid/vapour enthalpy.
    FusionEnthalpy = 1u << 10,
    LiquidConvection = 1u << 11,
    // Compact rigid pieces use material/fluid density, without hull thinning.
    DensityBody = 1u << 12,
    UniversalPores = 1u << 13,
    // Molten metals and other excluded liquids cannot enter or diffuse through pores.
    NonAbsorbableLiquid = 1u << 14
}

public static class CoreMaterialIds
{
    public const string Empty = "core:empty";
    public const string Sand = "core:sand";
    public const string Water = "core:water";
    public const string Oil = "core:oil";
    public const string FrozenOil = "core:frozen_oil";
    public const string OilVapour = "core:oil_vapour";
    public const string Ice = "core:ice";
    public const string Steam = "core:steam";
    public const string Metal = "core:metal";
    public const string Stone = "core:stone";
    public const string Eraser = "core:eraser";
    public const string Fixture = "core:fixture";
    public const string Wood = "core:wood";
    public const string Coal = "core:coal";
    public const string WetCharcoal = "core:wet_charcoal";
    public const string StoneCoal = "core:stone_coal";
    public const string Smoke = "core:smoke";
    public const string Co2 = "core:co2";
    public const string Fire = "core:fire";
    public const string Heater = "core:heater";
    public const string Cooler = "core:cooler";
    public const string Gunpowder = "core:gunpowder";
    public static IReadOnlyList<string> Required { get; } =
    [
        Empty,
        Sand,
        Water,
        Ice,
        Steam,
        Metal,
        Stone,
        Eraser,
        Fixture,
        Wood,
        Coal,
        WetCharcoal,
        StoneCoal,
        Smoke,
        Co2,
        Fire,
        Heater,
        Cooler
    ];
}

public sealed record MaterialTransitionRule(float Temperature, string IntoId, float LatentHeat = 0);

// Mobility is a dimensionless gameplay proxy, not viscosity in Pa.s.
public sealed record MaterialLiquidFlowDefinition(float ReferenceTemperature,
    float TemperatureSensitivity, float MinimumMobility, float MaximumMobility);

public sealed record MaterialTransitionDefinitions(
    MaterialTransitionRule? Below,
    MaterialTransitionRule? Above);

public sealed record MaterialCombustionDefinition(
    float IgnitionTemperature,
    float BurnRate,
    float HeatPerMass,
    string BurnedIntoId,
    float FlameSpreadRate,
    float MaximumTemperature,
    float PressurePerMass = 0,
    float FlameLifetimeMultiplier = 1,
    float OxidizerPerMass = 20,
    float ContactIgnitionTemperature = -273.15f);

public sealed record MaterialEmissionDefinition(
    string SmokeIntoId,
    float SmokeRate,
    string GasIntoId,
    float GasRate,
    string? FlameIntoId,
    float FlameRate);

public sealed record MaterialLifecycleDefinition(
    float MinimumLifetime,
    float MaximumLifetime,
    string DecayIntoId,
    float? ExtinctionTemperature = null);

public sealed record MaterialLiquidContactTransitionDefinition(
    string IntoId,
    float RatePerSecond,
    string? WithId = null);

public sealed record MaterialGasDefinition(
    float Diffusion,
    float Buoyancy,
    float HotAir,
    float HazeStrength,
    float OxidizerDisplacement = 1);

public sealed record MaterialMoistureDefinition(
    string LiquidId, string DryId, string WetId,
    float Capacity, float AbsorptionRate, float DryingRate, float CapillaryRate = 0);

public sealed record MaterialMotionDefinition(
    float Advection,
    float AirDrag,
    float AirLoss,
    float Loss,
    float Collision);

public sealed record MaterialFuelAbsorptionDefinition(
    string LiquidId, float Capacity, float AbsorptionRate, float SaturatedDensity, bool AllLiquids = false);

public sealed record MaterialThermalRegulatorDefinition(bool Heating, float TargetTemperature, float MaximumPower);

public sealed record MaterialDefinition(
    string Id,
    ushort RuntimeIndex,
    string Name,
    Color Color,
    MaterialProperties Properties,
    int UiOrder = 0,
    bool Hidden = false,
    string? Category = null)
{
    public MaterialTransitionDefinitions? PhaseTransitions { get; init; }
    public MaterialCombustionDefinition? Combustion { get; init; }
    public MaterialEmissionDefinition? Emissions { get; init; }
    public MaterialLifecycleDefinition? Lifecycle { get; init; }
    public MaterialLiquidContactTransitionDefinition? LiquidContactTransition { get; init; }
    public MaterialMoistureDefinition? Moisture { get; init; }
    public MaterialFuelAbsorptionDefinition? FuelAbsorption { get; init; }
    public MaterialGasDefinition? Gas { get; init; }
    public MaterialThermalRegulatorDefinition? ThermalRegulator { get; init; }
    public MaterialLiquidFlowDefinition? LiquidFlow { get; init; }
    public MaterialMotionDefinition Motion { get; init; } = new(
        MaterialRegistry.DefaultMotionAdvection,
        MaterialRegistry.DefaultMotionAirDrag,
        MaterialRegistry.DefaultMotionAirLoss,
        MaterialRegistry.DefaultMotionLoss,
        MaterialRegistry.DefaultMotionCollision);
    internal string SourcePath { get; init; } = string.Empty;
    internal bool IsBundled { get; init; }
}
