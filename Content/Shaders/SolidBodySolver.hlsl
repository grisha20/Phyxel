#include "PhysicsShared.hlsli"

StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<uint> SourceBodyFlags : register(t1);
StructuredBuffer<uint> SourceBodyBuoyancy : register(t2);
StructuredBuffer<uint> SourceDisplacementReservations : register(t3);
StructuredBuffer<uint> SourceBodyGeometry : register(t4);
RWStructuredBuffer<uint> BodyFlags : register(u0);
RWStructuredBuffer<GridCell> DestinationGrid : register(u1);
RWStructuredBuffer<uint> BodyBuoyancy : register(u2);
RWStructuredBuffer<uint> DisplacementReservations : register(u3);
RWStructuredBuffer<uint> BodyGeometry : register(u4);

static const uint BodyBlocked = 1;
static const uint BodyActive = 4;
static const uint BodyTouchesWater = 16;
static const uint GeometryContainsMetal = 1;
static const uint GeometryHasHull = 2;
static const uint GeometrySpanShift = 2;
static const uint GeometrySpanMask = 0x3ff;
static const uint GeometryCellCountShift = 12;
static const uint GeometryCellCountUnit = 1u << GeometryCellCountShift;
static const uint HullSearchDistance = 512;
static const uint HullMinimumGap = 8;
static const uint DisplacementStackHeight = 4;
static const uint DisplacementFallbackAttempts = 64;
static const uint DisplacementSourceMarker = 0x80000000u;
static const uint DisplacementBlockedMarker = 0x40000000u;
static const uint DisplacementHullFloorProbe = 64;

bool IsMovableSolid(GridCell cell)
{
    return cell.IsActive != 0 && IsFallingSolid(cell.MaterialId) && cell.BodyId != 0;
}

bool IsSolidMaterial(uint materialId)
{
    return materialId == 3 || materialId == 4 || materialId == 7;
}

bool BodyMoves(GridCell cell)
{
    if (!IsMovableSolid(cell))
    {
        return false;
    }
    uint flags = SourceBodyFlags[cell.BodyId - 1];
    uint geometry = SourceBodyGeometry[cell.BodyId - 1];
    if ((flags & (BodyBlocked | BodyActive)) != BodyActive)
    {
        return false;
    }
    if ((geometry & GeometryHasHull) != 0 && (flags & BodyTouchesWater) != 0)
    {
        uint buoyancy = SourceBodyBuoyancy[cell.BodyId - 1];
        uint hullSpan = max((geometry >> GeometrySpanShift) & GeometrySpanMask, 1);
        uint waterDepth = buoyancy & 0xffff;
        uint cargoQuarterMass = buoyancy >> 16;
        uint solidCells = geometry >> GeometryCellCountShift;

        // Rasterized walls are much thicker than real sheet metal. Treat each
        // metal pixel as 0.75 water pixels (concrete as 1.0), then apply
        // Archimedes' principle in 2D: draft = effective mass / hull width.
        uint weightNumerator = (geometry & GeometryContainsMetal) != 0 ? 3 : 4;
        uint effectiveQuarterMass = solidCells * weightNumerator + cargoQuarterMass;
        uint requiredDraft = (effectiveQuarterMass + hullSpan * 4 - 1) /
            (hullSpan * 4);
        requiredDraft = max(requiredDraft, 8);
        if (waterDepth >= requiredDraft)
        {
            return false;
        }
    }
    return true;
}

uint SeparatedBodyWallDistance(uint2 coordinate, int2 direction, uint bodyId)
{
    int2 firstCandidate = int2(coordinate) + direction;
    if (firstCandidate.x < 0 || firstCandidate.y < 0 ||
        firstCandidate.x >= int(Width) || firstCandidate.y >= int(Height))
    {
        return 0;
    }
    GridCell firstSample = SourceGrid[FlattenCoordinate(uint2(firstCandidate))];
    if ((IsMovableSolid(firstSample) && firstSample.BodyId == bodyId) ||
        (firstSample.IsActive != 0 && IsSolidMaterial(firstSample.MaterialId)))
    {
        return 0;
    }
    for (uint distance = 2; distance <= HullSearchDistance; distance++)
    {
        int2 candidate = int2(coordinate) + direction * int(distance);
        if (candidate.x < 0 || candidate.y < 0 ||
            candidate.x >= int(Width) || candidate.y >= int(Height))
        {
            return 0;
        }
        GridCell sample = SourceGrid[FlattenCoordinate(uint2(candidate))];
        if (IsMovableSolid(sample) && sample.BodyId == bodyId)
        {
            return distance > HullMinimumGap ? distance : 0;
        }
        if (sample.IsActive != 0 && IsSolidMaterial(sample.MaterialId))
        {
            return 0;
        }
    }
    return 0;
}

uint BodyHorizontalHullSpan(uint2 coordinate, uint bodyId)
{
    return max(
        SeparatedBodyWallDistance(coordinate, int2(-1, 0), bodyId),
        SeparatedBodyWallDistance(coordinate, int2(1, 0), bodyId));
}

bool BodyHasHullSpace(uint2 coordinate, uint bodyId, uint horizontalSpan)
{
    return horizontalSpan > 0 ||
        SeparatedBodyWallDistance(coordinate, int2(0, -1), bodyId) > 0 ||
        SeparatedBodyWallDistance(coordinate, int2(0, 1), bodyId) > 0;
}

uint WaterDepthAt(int2 coordinate)
{
    if (coordinate.x < 0 || coordinate.y < 0 ||
        coordinate.x >= int(Width) || coordinate.y >= int(Height))
    {
        return 0;
    }
    uint depth = 0;
    for (int y = coordinate.y; y >= 0 && depth < 0xffff; y--)
    {
        GridCell sample = SourceGrid[uint(y) * Width + uint(coordinate.x)];
        if (sample.IsActive == 0 || sample.MaterialId != 2)
        {
            break;
        }
        depth++;
    }
    return depth;
}

void AtomicMaxGeometrySpan(uint bodyIndex, uint hullSpan)
{
    hullSpan = min(hullSpan, GeometrySpanMask);
    uint expected = BodyGeometry[bodyIndex];
    for (uint attempt = 0; attempt < 32; attempt++)
    {
        uint oldSpan = (expected >> GeometrySpanShift) & GeometrySpanMask;
        uint updated = (expected & ~(GeometrySpanMask << GeometrySpanShift)) |
            (max(oldSpan, hullSpan) << GeometrySpanShift);
        if (updated == expected)
        {
            return;
        }
        uint observed;
        InterlockedCompareExchange(BodyGeometry[bodyIndex], expected, updated, observed);
        if (observed == expected)
        {
            return;
        }
        expected = observed;
    }
}

void AtomicMaxWaterDepth(uint bodyIndex, uint waterDepth)
{
    waterDepth = min(waterDepth, 0xffff);
    uint expected = BodyBuoyancy[bodyIndex];
    for (uint attempt = 0; attempt < 32; attempt++)
    {
        uint updated = (expected & 0xffff0000u) |
            max(expected & 0xffff, waterDepth);
        if (updated == expected)
        {
            return;
        }
        uint observed;
        InterlockedCompareExchange(BodyBuoyancy[bodyIndex], expected, updated, observed);
        if (observed == expected)
        {
            return;
        }
        expected = observed;
    }
}

void AtomicAddCargoMass(uint bodyIndex, uint quarterMass)
{
    uint expected = BodyBuoyancy[bodyIndex];
    for (uint attempt = 0; attempt < 32; attempt++)
    {
        uint oldCargo = expected >> 16;
        uint newCargo = min(0xffff, oldCargo + quarterMass);
        uint updated = (expected & 0xffff) | (newCargo << 16);
        if (updated == expected)
        {
            return;
        }
        uint observed;
        InterlockedCompareExchange(BodyBuoyancy[bodyIndex], expected, updated, observed);
        if (observed == expected)
        {
            return;
        }
        expected = observed;
    }
}

[numthreads(16, 16, 1)]
void AnalyzeSolidGeometry(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    GridCell cell = SourceGrid[FlattenCoordinate(coordinate)];
    if (!IsMovableSolid(cell))
    {
        return;
    }
    uint horizontalSpan = BodyHorizontalHullSpan(coordinate, cell.BodyId);
    uint geometryFlags = cell.MaterialId == 3 ? GeometryContainsMetal : 0;
    if (BodyHasHullSpace(coordinate, cell.BodyId, horizontalSpan))
    {
        geometryFlags |= GeometryHasHull;
    }
    uint bodyIndex = cell.BodyId - 1;
    uint ignored;
    InterlockedAdd(BodyGeometry[bodyIndex], GeometryCellCountUnit, ignored);
    InterlockedOr(BodyGeometry[bodyIndex], geometryFlags, ignored);
    AtomicMaxGeometrySpan(bodyIndex, horizontalSpan);
}

bool IsBetweenBodyWalls(uint2 coordinate, uint bodyId);
uint CargoColumnQuarterMass(uint2 floorCoordinate, uint bodyId);

[numthreads(16, 16, 1)]
void AnalyzeSolidBodies(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = SourceGrid[index];
    if (!IsMovableSolid(cell))
    {
        return;
    }
    if (SolidPass == 0 && cell.RestFrames >= 2)
    {
        return;
    }

    uint flags = BodyActive;
    uint waterDepth = max(
        max(WaterDepthAt(int2(coordinate) + int2(-1, 0)),
            WaterDepthAt(int2(coordinate) + int2(1, 0))),
        max(WaterDepthAt(int2(coordinate) + int2(0, -1)),
            WaterDepthAt(int2(coordinate) + int2(0, 1))));
    if (waterDepth > 0)
    {
        flags |= BodyTouchesWater;
    }
    if (coordinate.y + 1 >= Height)
    {
        flags |= BodyBlocked;
    }
    else
    {
        GridCell below = SourceGrid[index + Width];
        bool solidObstacle = below.IsActive != 0 && IsSolidMaterial(below.MaterialId);
        if (solidObstacle && below.BodyId != cell.BodyId)
        {
            flags |= BodyBlocked;
        }
    }
    uint bodyIndex = cell.BodyId - 1;
    uint ignored;
    InterlockedOr(BodyFlags[bodyIndex], flags, ignored);
    AtomicMaxWaterDepth(bodyIndex, waterDepth);
    uint geometry = SourceBodyGeometry[bodyIndex];
    if ((geometry & GeometryHasHull) != 0 && coordinate.y > 0)
    {
        GridCell above = SourceGrid[index - Width];
        if ((!IsMovableSolid(above) || above.BodyId != cell.BodyId) &&
            IsBetweenBodyWalls(uint2(coordinate.x, coordinate.y - 1), cell.BodyId))
        {
            uint cargoMass = CargoColumnQuarterMass(coordinate, cell.BodyId);
            if (cargoMass > 0)
            {
                AtomicAddCargoMass(bodyIndex, cargoMass);
            }
        }
    }
}

bool IsBetweenBodyWalls(uint2 coordinate, uint bodyId)
{
    bool leftWall = false;
    bool rightWall = false;
    for (uint distance = 1; distance <= HullSearchDistance; distance++)
    {
        if (coordinate.x >= distance)
        {
            GridCell left = SourceGrid[FlattenCoordinate(
                uint2(coordinate.x - distance, coordinate.y))];
            leftWall = leftWall || (IsMovableSolid(left) && left.BodyId == bodyId);
        }
        if (coordinate.x + distance < Width)
        {
            GridCell right = SourceGrid[FlattenCoordinate(
                uint2(coordinate.x + distance, coordinate.y))];
            rightWall = rightWall || (IsMovableSolid(right) && right.BodyId == bodyId);
        }
        if (leftWall && rightWall)
        {
            return true;
        }
        if (coordinate.x < distance && coordinate.x + distance >= Width)
        {
            break;
        }
    }
    return false;
}

uint CargoColumnQuarterMass(uint2 floorCoordinate, uint bodyId)
{
    uint cargoMass = 0;
    for (uint distance = 1;
        distance <= HullSearchDistance && floorCoordinate.y >= distance;
        distance++)
    {
        GridCell sample = SourceGrid[FlattenCoordinate(
            uint2(floorCoordinate.x, floorCoordinate.y - distance))];
        if (IsMovableSolid(sample) && sample.BodyId == bodyId)
        {
            break;
        }
        if (sample.IsActive == 0)
        {
            break;
        }
        if (sample.MaterialId == 1)
        {
            cargoMass = min(0xffff, cargoMass + 6);
        }
        else if (sample.MaterialId == 2)
        {
            cargoMass = min(0xffff, cargoMass + 4);
        }
        else
        {
            break;
        }
    }
    return cargoMass;
}

void FindBodyRowExtent(uint2 coordinate, uint bodyId, out int left, out int right)
{
    left = int(coordinate.x);
    right = int(coordinate.x);
    uint y = coordinate.y - 1;
    uint start = coordinate.x > HullSearchDistance
        ? coordinate.x - HullSearchDistance
        : 0;
    uint end = min(Width - 1, coordinate.x + HullSearchDistance);
    for (uint x = start; x <= end; x++)
    {
        GridCell sample = SourceGrid[y * Width + x];
        if (IsMovableSolid(sample) && sample.BodyId == bodyId)
        {
            left = min(left, int(x));
            right = max(right, int(x));
        }
    }
}

bool IsAboveAnyHullFloor(uint2 coordinate)
{
    for (uint distance = 1;
        distance <= DisplacementHullFloorProbe && coordinate.y + distance < Height;
        distance++)
    {
        GridCell sample = SourceGrid[FlattenCoordinate(
            uint2(coordinate.x, coordinate.y + distance))];
        if (sample.IsActive == 0 || sample.MaterialId == 1 ||
            sample.MaterialId == 2 || sample.MaterialId == 6)
        {
            continue;
        }
        if (!IsMovableSolid(sample))
        {
            return false;
        }
        return (SourceBodyGeometry[sample.BodyId - 1] & GeometryHasHull) != 0;
    }
    return false;
}

bool SurfaceTarget(int x, uint sourceY, uint stack, uint bodyId, out uint targetIndex)
{
    targetIndex = 0;
    if (x < 0 || x >= int(Width))
    {
        return false;
    }
    int surfaceY = int(sourceY);
    while (surfaceY >= 0)
    {
        GridCell sample = SourceGrid[uint(surfaceY) * Width + uint(x)];
        if (sample.IsActive == 0 || sample.MaterialId != 2)
        {
            break;
        }
        surfaceY--;
    }
    int targetY = surfaceY - int(stack);
    if (targetY < 0)
    {
        return false;
    }
    uint2 target = uint2(uint(x), uint(targetY));
    GridCell targetCell = SourceGrid[FlattenCoordinate(target)];
    if (targetCell.IsActive != 0 || IsBetweenBodyWalls(target, bodyId) ||
        IsAboveAnyHullFloor(target))
    {
        return false;
    }
    if (target.y > 0)
    {
        GridCell above = SourceGrid[FlattenCoordinate(uint2(target.x, target.y - 1))];
        if (BodyMoves(above))
        {
            return false;
        }
    }
    targetIndex = FlattenCoordinate(target);
    return true;
}

bool TryReserveDisplacementTarget(
    int x,
    uint2 sourceCoordinate,
    uint bodyId,
    uint sourceIndex,
    out uint reservedTarget)
{
    reservedTarget = 0;
    for (uint stack = 0; stack < DisplacementStackHeight; stack++)
    {
        uint targetIndex;
        if (!SurfaceTarget(x, sourceCoordinate.y, stack, bodyId, targetIndex))
        {
            continue;
        }
        uint previous;
        InterlockedCompareExchange(
            DisplacementReservations[targetIndex],
            0,
            sourceIndex + 1,
            previous);
        if (previous == 0 || previous == sourceIndex + 1)
        {
            DisplacementReservations[sourceIndex] =
                DisplacementSourceMarker | (targetIndex + 1);
            reservedTarget = targetIndex;
            return true;
        }
    }
    return false;
}

bool ReserveDisplacement(
    uint2 sourceCoordinate,
    uint bodyId,
    uint sourceIndex,
    out uint reservedTarget)
{
    reservedTarget = 0;
    int left;
    int right;
    FindBodyRowExtent(sourceCoordinate, bodyId, left, right);
    int center = (left + right) / 2;
    int ordinal = sourceCoordinate.x <= uint(center)
        ? int(sourceCoordinate.x) - left
        : right - int(sourceCoordinate.x);
    int width = max(right - left + 1, 1);
    uint geometry = SourceBodyGeometry[bodyId - 1];
    int hullSpan = int((geometry >> GeometrySpanShift) & GeometrySpanMask);
    int globalReach = max((hullSpan + 1) / 2, width);
    int globalLeft = int(sourceCoordinate.x) - globalReach - 2;
    int globalRight = int(sourceCoordinate.x) + globalReach + 2;
    bool preferLeft = sourceCoordinate.x <= uint(center);
    int candidates[4] =
    {
        preferLeft ? left - 2 - ordinal : right + 2 + ordinal,
        preferLeft ? right + 2 + width + ordinal : left - 2 - width - ordinal,
        left - 2 - ordinal,
        right + 2 + ordinal
    };
    for (uint candidate = 0; candidate < 4; candidate++)
    {
        if (TryReserveDisplacementTarget(
            candidates[candidate],
            sourceCoordinate,
            bodyId,
            sourceIndex,
            reservedTarget))
        {
            return true;
        }
    }
    uint firstFallback = HashValue(sourceIndex ^ bodyId) % DisplacementFallbackAttempts;
    for (uint attempt = 0; attempt < DisplacementFallbackAttempts; attempt++)
    {
        uint lane = (firstFallback + attempt) % DisplacementFallbackAttempts;
        bool chooseLeft = ((sourceIndex + attempt) & 1) == 0;
        int x = chooseLeft ? left - 2 - int(lane) : right + 2 + int(lane);
        if (TryReserveDisplacementTarget(
            x,
            sourceCoordinate,
            bodyId,
            sourceIndex,
            reservedTarget))
        {
            return true;
        }
    }
    int globalCandidates[2] =
    {
        preferLeft ? globalLeft : globalRight,
        preferLeft ? globalRight : globalLeft
    };
    for (uint candidate = 0; candidate < 2; candidate++)
    {
        if (TryReserveDisplacementTarget(
            globalCandidates[candidate],
            sourceCoordinate,
            bodyId,
            sourceIndex,
            reservedTarget))
        {
            return true;
        }
    }
    for (uint attempt = 0; attempt < DisplacementFallbackAttempts; attempt++)
    {
        uint lane = (firstFallback + attempt) % DisplacementFallbackAttempts;
        bool chooseLeft = ((sourceIndex + attempt) & 1) == 0;
        int x = chooseLeft
            ? globalLeft - int(lane)
            : globalRight + int(lane);
        if (TryReserveDisplacementTarget(
            x,
            sourceCoordinate,
            bodyId,
            sourceIndex,
            reservedTarget))
        {
            return true;
        }
    }
    return false;
}

[numthreads(16, 16, 1)]
void PlanHullWaterDisplacement(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y == 0 || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    GridCell water = SourceGrid[index];
    GridCell above = SourceGrid[index - Width];
    if (water.IsActive == 0 || water.MaterialId != 2 || !BodyMoves(above))
    {
        return;
    }
    uint geometry = SourceBodyGeometry[above.BodyId - 1];
    if ((geometry & GeometryHasHull) == 0)
    {
        return;
    }
    uint ignoredTarget;
    if (!ReserveDisplacement(coordinate, above.BodyId, index, ignoredTarget))
    {
        uint ignored;
        InterlockedOr(
            DisplacementReservations[above.BodyId - 1],
            DisplacementBlockedMarker,
            ignored);
    }
}

bool BodyMovesWithDisplacement(GridCell cell)
{
    if (!BodyMoves(cell))
    {
        return false;
    }
    uint geometry = SourceBodyGeometry[cell.BodyId - 1];
    return (geometry & GeometryHasHull) == 0 ||
        (SourceDisplacementReservations[cell.BodyId - 1] &
            DisplacementBlockedMarker) == 0;
}

[numthreads(16, 16, 1)]
void MoveSolidBodies(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    GridCell current = SourceGrid[index];
    bool aboveMoves = coordinate.y > 0 &&
        BodyMovesWithDisplacement(SourceGrid[index - Width]);
    if (aboveMoves)
    {
        GridCell moved = SourceGrid[index - Width];
        moved.RestFrames = 0;
        DestinationGrid[index] = moved;
        return;
    }
    if (!BodyMovesWithDisplacement(current))
    {
        if (IsMovableSolid(current))
        {
            current.RestFrames = min(current.RestFrames + 1, 2);
        }
        DestinationGrid[index] = current;
        return;
    }

    GridCell replacement = CreateEmptyCell();
    for (uint y = coordinate.y + 1; y < Height; y++)
    {
        GridCell sample = SourceGrid[y * Width + coordinate.x];
        if (!IsMovableSolid(sample) || sample.BodyId != current.BodyId)
        {
            replacement = sample;
            replacement.RestFrames = 0;
            replacement.VelocityX = 0;
            replacement.VelocityY = 0;
            uint geometry = SourceBodyGeometry[current.BodyId - 1];
            if ((geometry & GeometryHasHull) != 0 && sample.IsActive != 0 &&
                sample.MaterialId == 2)
            {
                // Water below a descending hull is either transferred by the
                // reservation pass or stays in its own interior cell. Pulling
                // it into the vacated top cell would duplicate/teleport it.
                replacement = CreateEmptyCell();
            }
            break;
        }
    }
    DestinationGrid[index] = replacement;
}

[numthreads(16, 16, 1)]
void ApplyHullWaterDisplacement(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    uint sourcePlusOne = SourceDisplacementReservations[index];
    if (sourcePlusOne == 0 ||
        (sourcePlusOne & (DisplacementSourceMarker | DisplacementBlockedMarker)) != 0)
    {
        return;
    }
    uint sourceIndex = sourcePlusOne - 1;
    GridCell displaced = SourceGrid[sourceIndex];
    GridCell bodyAbove = CreateEmptyCell();
    if (sourceIndex >= Width)
    {
        bodyAbove = SourceGrid[sourceIndex - Width];
    }
    if (displaced.IsActive == 0 || displaced.MaterialId != 2 ||
        !BodyMovesWithDisplacement(bodyAbove))
    {
        return;
    }
    displaced.RestFrames = 0;
    displaced.VelocityX = 0;
    displaced.VelocityY = 0;
    displaced.Pressure = 0;
    DestinationGrid[index] = displaced;
}
