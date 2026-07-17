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
    MovableSolid = 1u << 0
}

public static class CoreMaterialIds
{
    public const string Empty = "core:empty";
    public const string Sand = "core:sand";
    public const string Water = "core:water";
    public const string Metal = "core:metal";
    public const string Stone = "core:stone";
    public const string Eraser = "core:eraser";
    public const string Gas = "core:gas";
    public const string Fixture = "core:fixture";
    public static IReadOnlyList<string> Required { get; } =
    [
        Empty,
        Sand,
        Water,
        Metal,
        Stone,
        Eraser,
        Gas,
        Fixture
    ];
}

public sealed record MaterialDefinition(
    string Id,
    ushort RuntimeIndex,
    string Name,
    Color Color,
    MaterialProperties Properties,
    int UiOrder = 0,
    bool Hidden = false);
