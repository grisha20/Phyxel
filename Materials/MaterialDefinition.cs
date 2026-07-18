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
    Flame = 1u << 1
}

public static class CoreMaterialIds
{
    public const string Empty = "core:empty";
    public const string Sand = "core:sand";
    public const string Water = "core:water";
    public const string Ice = "core:ice";
    public const string Steam = "core:steam";
    public const string Metal = "core:metal";
    public const string Stone = "core:stone";
    public const string Eraser = "core:eraser";
    public const string Gas = "core:gas";
    public const string Fixture = "core:fixture";
    public const string Wood = "core:wood";
    public const string Coal = "core:coal";
    public const string Smoke = "core:smoke";
    public const string Co2 = "core:co2";
    public const string Fire = "core:fire";
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
        Gas,
        Fixture,
        Wood,
        Coal,
        Smoke,
        Co2,
        Fire
    ];
}

public sealed record MaterialTransitionRule(float Temperature, string IntoId);

public sealed record MaterialTransitionDefinitions(
    MaterialTransitionRule? Below,
    MaterialTransitionRule? Above);

public sealed record MaterialCombustionDefinition(
    float IgnitionTemperature,
    float BurnRate,
    float HeatPerMass,
    string BurnedIntoId,
    float FlameSpreadRate);

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
    string DecayIntoId);

public sealed record MaterialDefinition(
    string Id,
    ushort RuntimeIndex,
    string Name,
    Color Color,
    MaterialProperties Properties,
    int UiOrder = 0,
    bool Hidden = false)
{
    public MaterialTransitionDefinitions? PhaseTransitions { get; init; }
    public MaterialCombustionDefinition? Combustion { get; init; }
    public MaterialEmissionDefinition? Emissions { get; init; }
    public MaterialLifecycleDefinition? Lifecycle { get; init; }
    internal string SourcePath { get; init; } = string.Empty;
    internal bool IsBundled { get; init; }
}
