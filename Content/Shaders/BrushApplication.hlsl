#include "PhysicsShared.hlsli"

StructuredBuffer<BrushDrawCommand> Commands : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<GridCell> Grid : register(u0);

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint commandIndex = dispatchThreadId.z;
    if (commandIndex >= CommandCount || dispatchThreadId.x >= MaximumBrushDiameter ||
        dispatchThreadId.y >= MaximumBrushDiameter)
    {
        return;
    }

    BrushDrawCommand command = Commands[commandIndex];
    int halfDiameter = int(MaximumBrushDiameter / 2);
    int2 offset = int2(dispatchThreadId.xy) - int2(halfDiameter, halfDiameter);
    if (dot(float2(offset), float2(offset)) > command.Radius * command.Radius)
    {
        return;
    }

    int2 position = int2(command.X, command.Y) + offset;
    if (position.x < 0 || position.y < 0 || position.x >= int(Width) || position.y >= int(Height))
    {
        return;
    }

    uint index = FlattenCoordinate(uint2(position));
    if (command.Mode == 0 && HashUnitFloat(index ^ command.Seed ^ FrameIndex) > command.Density)
    {
        return;
    }

    GridCell existing = Grid[index];
    if (command.Mode == 2)
    {
        if (existing.IsActive != 0 && IsCellularMaterial(Materials[existing.MaterialId].SimulationKind))
        {
            Grid[index] = CreateEmptyCell();
        }
        return;
    }

    if (command.Mode != 0)
    {
        Grid[index] = CreateEmptyCell();
        return;
    }

    MaterialProperties material = Materials[command.MaterialId];
    if (material.SimulationKind == SimulationKindTool)
    {
        return;
    }
    if (IsCellularMaterial(material.SimulationKind) && existing.IsActive != 0 &&
        Materials[existing.MaterialId].SimulationKind == 2)
    {
        return;
    }

    GridCell cell = CreateEmptyCell();
    cell.MaterialId = command.MaterialId;
    cell.Mass = material.SimulationKind == 2 ? material.Density : 1;
    cell.IsActive = 1;
    cell.BodyId = IsMovableSolidMaterial(material) ? command.Reserved : 0;
    Grid[index] = cell;
}
