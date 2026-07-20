#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CellMaterials : register(u1);

bool IsOrdinaryGas(GridCell cell)
{
    if (cell.IsActive == 0)
    {
        return false;
    }
    MaterialProperties material = Materials[cell.MaterialIndex];
    return material.SimulationKind == SimulationKindGas &&
        (material.Flags & MaterialFlagFlame) == 0;
}

bool IsEmpty(GridCell cell)
{
    return cell.IsActive == 0;
}

void StorePair(uint firstIndex, GridCell first, uint secondIndex, GridCell second)
{
    Grid[firstIndex] = first;
    Grid[secondIndex] = second;
    CellMaterials[firstIndex] = first.IsActive != 0 ? first.MaterialIndex : 0;
    CellMaterials[secondIndex] = second.IsActive != 0 ? second.MaterialIndex : 0;
}

void MovePacket(
    uint gasIndex,
    GridCell gas,
    uint emptyIndex,
    bool gasMovesToFirst,
    bool vertical)
{
    gas.BodyId = 0;
    gas.Pressure = 0;
    gas.RestFrames = 0;
    if (vertical)
    {
        gas.VelocityX = 0;
        gas.VelocityY = gasMovesToFirst ? -4 : 4;
    }
    else
    {
        gas.VelocityX = gasMovesToFirst ? -6 : 6;
        gas.VelocityY = 0;
    }

    GridCell empty = CreateEmptyCell();
    if (gasMovesToFirst)
    {
        StorePair(emptyIndex, gas, gasIndex, empty);
    }
    else
    {
        StorePair(gasIndex, empty, emptyIndex, gas);
    }
}

float PacketMoveChance(
    GridCell gas,
    bool gasIsFirst,
    bool vertical,
    bool diagonal)
{
    float flow = saturate(Materials[gas.MaterialIndex].FlowRate);
    if (vertical || diagonal)
    {
        // Y grows downwards. Gas diffusion is deliberately stronger than its
        // buoyant drift: an open cloud should spread locally instead of being
        // pulled into a narrow ceiling layer.
        return gasIsFirst
            ? (diagonal ? 0.34 : 0.22) * flow
            : (diagonal ? 0.48 : 0.36) * flow;
    }
    return 0.62 * flow;
}

void ResolvePacketPair(
    uint firstIndex,
    uint secondIndex,
    bool vertical,
    bool diagonal,
    uint seed)
{
    GridCell first = Grid[firstIndex];
    GridCell second = Grid[secondIndex];
    bool firstGas = IsOrdinaryGas(first);
    bool secondGas = IsOrdinaryGas(second);
    bool firstEmpty = IsEmpty(first);
    bool secondEmpty = IsEmpty(second);
    if ((!firstGas && !firstEmpty) || (!secondGas && !secondEmpty) ||
        (!firstGas && !secondGas))
    {
        return;
    }

    if (firstGas && secondGas)
    {
        if (first.MaterialIndex == second.MaterialIndex)
        {
            return;
        }

        if (vertical || diagonal)
        {
            float firstDensity = Materials[first.MaterialIndex].Density;
            float secondDensity = Materials[second.MaterialIndex].Density;
            if (firstDensity > secondDensity + 0.000001 &&
                HashUnitFloat(seed) < (diagonal ? 0.24 : 0.75))
            {
                first.RestFrames = 0;
                second.RestFrames = 0;
                StorePair(firstIndex, second, secondIndex, first);
            }
        }
        else if (HashUnitFloat(seed) < 0.12)
        {
            first.RestFrames = 0;
            second.RestFrames = 0;
            StorePair(firstIndex, second, secondIndex, first);
        }
        return;
    }

    GridCell gas = first;
    bool gasIsFirst = firstGas;
    if (!firstGas)
    {
        gas = second;
    }
    if (HashUnitFloat(seed) >= PacketMoveChance(gas, gasIsFirst, vertical, diagonal))
    {
        return;
    }

    if (gasIsFirst)
    {
        MovePacket(firstIndex, first, secondIndex, false, vertical || diagonal);
    }
    else
    {
        MovePacket(secondIndex, second, firstIndex, true, vertical || diagonal);
    }
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (SimulationPhase <= 1)
    {
        uint2 upper = uint2(dispatchThreadId.x, dispatchThreadId.y * 2 + SimulationPhase);
        if (upper.x >= Width || upper.y + 1 >= Height)
        {
            return;
        }
        uint firstIndex = FlattenCoordinate(upper);
        ResolvePacketPair(
            firstIndex,
            firstIndex + Width,
            true,
            false,
            firstIndex ^ (FrameIndex * 0x9e3779b9u));
        return;
    }

    if (SimulationPhase <= 3)
    {
        uint2 left = uint2(dispatchThreadId.x * 2 + SimulationPhase - 2, dispatchThreadId.y);
        if (left.x + 1 >= Width || left.y >= Height)
        {
            return;
        }
        uint firstIndex = FlattenCoordinate(left);
        ResolvePacketPair(
            firstIndex,
            firstIndex + 1,
            false,
            false,
            firstIndex ^ (FrameIndex * 0x85ebca6bu));
        return;
    }

    // One disjoint diagonal per 2x2 block adds local isotropic diffusion
    // without write races or the long-range mass teleportation used before.
    uint2 block = dispatchThreadId.xy * 2;
    if (block.x + 1 >= Width || block.y + 1 >= Height)
    {
        return;
    }
    bool otherDiagonal = HashUnitFloat(
        FlattenCoordinate(block) ^ (FrameIndex * 0xc2b2ae35u)) < 0.5;
    uint2 upper = block + uint2(otherDiagonal ? 1u : 0u, 0u);
    uint2 lower = block + uint2(otherDiagonal ? 0u : 1u, 1u);
    uint firstIndex = FlattenCoordinate(upper);
    uint secondIndex = FlattenCoordinate(lower);
    ResolvePacketPair(
        firstIndex,
        secondIndex,
        false,
        true,
        firstIndex ^ secondIndex ^ (FrameIndex * 0x27d4eb2du));
}
