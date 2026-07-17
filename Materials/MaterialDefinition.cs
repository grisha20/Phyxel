using System;
using Microsoft.Xna.Framework;
using Phyxel.Physics;

namespace Phyxel.Materials;

public enum MaterialId : uint
{
    Empty = 0,
    Sand = 1,
    Water = 2,
    Metal = 3,
    Concrete = 4,
    Eraser = 5,
    Gas = 6,
    Fixture = 7
}

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
    public const string Concrete = "core:concrete";
    public const string Eraser = "core:eraser";
    public const string Gas = "core:gas";
    public const string Fixture = "core:fixture";
    public const string GoldSand = "core:gold_sand";

}

public sealed record MaterialDefinition(
    string Id,
    ushort RuntimeIndex,
    string Name,
    Color Color,
    MaterialProperties Properties,
    int UiOrder = 0,
    bool Hidden = false);
