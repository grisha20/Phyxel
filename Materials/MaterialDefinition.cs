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
    Eraser = 5
}

public enum MaterialSimulationKind : uint
{
    None = 0,
    CellularAutomata = 1,
    Lattice = 2,
    Tool = 3
}

public sealed record MaterialDefinition(
    MaterialId Id,
    string Name,
    Color Color,
    MaterialProperties Properties);
