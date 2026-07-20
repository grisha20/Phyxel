#include "PhysicsShared.hlsli"

StructuredBuffer<BrushDrawCommand> Commands : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
RWStructuredBuffer<GridCell> Grid : register(u0);

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint commandIndex = dispatchThreadId.z;
    if (commandIndex >= CommandCount || dispatchThreadId.x >= DispatchExtentX ||
        dispatchThreadId.y >= DispatchExtentY)
    {
        return;
    }

    BrushDrawCommand command = Commands[commandIndex];
    int radius = int(ceil(command.Radius));
    int2 start = int2(command.X, command.Y);
    int2 end = command.Shape == BrushCommandShapeSegment
        ? int2(command.EndX, command.EndY)
        : start;
    int2 boundsMinimum = min(start, end) - int2(radius, radius);
    int2 position = boundsMinimum + int2(dispatchThreadId.xy);
    float2 segment = float2(end - start);
    float segmentLengthSquared = dot(segment, segment);
    float interpolation = segmentLengthSquared > 0
        ? saturate(dot(float2(position - start), segment) / segmentLengthSquared)
        : 0;
    float2 nearest = float2(start) + segment * interpolation;
    float2 distanceFromStroke = float2(position) - nearest;
    if (dot(distanceFromStroke, distanceFromStroke) > command.Radius * command.Radius)
    {
        return;
    }

    if (position.x < 0 || position.y < 0 || position.x >= int(Width) || position.y >= int(Height))
    {
        return;
    }

    uint index = FlattenCoordinate(uint2(position));
    if (command.Mode == BrushCommandModeMaterial &&
        HashUnitFloat(index ^ command.Seed ^ FrameIndex) > command.Density)
    {
        return;
    }

    GridCell existing = Grid[index];
    if (command.Mode == BrushCommandModeSetTemperature)
    {
        if (existing.IsActive != 0)
        {
            existing.Temperature = clamp(command.TargetTemperature, -273.15, 5000.0);
            Grid[index] = existing;
        }
        return;
    }

    if (command.Mode == BrushCommandModeErase)
    {
        Grid[index] = CreateEmptyCell();
        return;
    }
    if (command.Mode != BrushCommandModeMaterial)
    {
        return;
    }

    MaterialProperties material = Materials[command.MaterialIndex];
    if (material.SimulationKind == SimulationKindTool)
    {
        return;
    }
    if (existing.IsActive != 0)
    {
        if ((material.Flags & MaterialFlagFlame) != 0)
        {
            MaterialProperties existingMaterial = Materials[existing.MaterialIndex];
            if (existingMaterial.SimulationKind == SimulationKindSolid &&
                existingMaterial.BurnedIntoMaterialIndex != 0xffffffffu &&
                existingMaterial.FlameSpreadRate > 0)
            {
                // Flame tools ignite combustible solids in place. Every other
                // material command is strictly empty-only.
                existing.Temperature = max(
                    existing.Temperature,
                    existingMaterial.IgnitionTemperature + 1.0);
                Grid[index] = existing;
            }
        }
        return;
    }

    GridCell cell = CreateEmptyCell();
    cell.MaterialIndex = command.MaterialIndex;
    cell.Mass = material.SimulationKind == 2 ? material.Density : 1;
    cell.IsActive = 1;
    cell.BodyId = IsMovableSolidMaterial(material) ? command.Reserved : 0;
    cell.Temperature = material.InitialTemperature;
    cell.Lifetime = InitialMaterialLifetime(material, index ^ command.Seed ^ FrameIndex);
    Grid[index] = cell;
}
