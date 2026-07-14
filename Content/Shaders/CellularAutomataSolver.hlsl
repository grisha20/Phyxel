#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
// Shared with the solid-body flags. During the hydraulic phases the first nine
// rows store packed column bounds, transfer slots, and the pressure activity count.
RWStructuredBuffer<uint> WaterColumnState : register(u1);
RWStructuredBuffer<uint> PathBlockerMasks : register(u2);
RWStructuredBuffer<uint> CellMaterials : register(u3);
RWStructuredBuffer<uint> WaterPressureRoutes : register(u4);

static const uint SandRestThreshold = 30;
static const uint FluidRestThreshold = 60;
static const float GasMinimumMass = 0.01;
static const float GasTransferThreshold = 0.03;
static const uint PathBlockerTileWidth = 32;
// Keeps the connectivity check local even for tall maps. Horizontal reach is
// provided by the power-of-two column spans dispatched by the coordinator.
static const uint HydraulicConnectionSearchDepth = 128;
static const uint HydraulicHeadRouteTolerance = 0;
static const uint HydraulicSurfaceTolerance = 1;
static const uint HydraulicTransfersPerColumn = 8;
static const uint HydraulicActivityRow = 9;

uint CellKind(GridCell cell)
{
    return cell.IsActive == 0 ? 0 : Materials[cell.MaterialId].SimulationKind;
}

uint CellKindFromMaterial(uint materialId)
{
    return materialId == 0 ? 0 : Materials[materialId].SimulationKind;
}

uint CellKindAtIndex(uint index)
{
    return CellKindFromMaterial(CellMaterials[index]);
}

uint CellKindAt(uint2 coordinate)
{
    return CellKindAtIndex(FlattenCoordinate(coordinate));
}

float CellRankFromMaterial(uint materialId)
{
    uint kind = CellKindFromMaterial(materialId);
    if (kind == 0)
    {
        return 0;
    }
    if (kind == 5)
    {
        return -1;
    }
    return Materials[materialId].Density;
}

float CellRank(GridCell cell)
{
    uint kind = CellKind(cell);
    if (kind == 0)
    {
        return 0;
    }
    if (kind == 5)
    {
        return -1;
    }
    return Materials[cell.MaterialId].Density;
}

bool IsSolid(GridCell cell)
{
    return CellKind(cell) == 2;
}

bool IsGasOrEmpty(GridCell cell)
{
    uint kind = CellKind(cell);
    return kind == 0 || kind == 5;
}

GridCell GasWithMass(GridCell templateCell, float mass)
{
    if (mass < GasMinimumMass)
    {
        return CreateEmptyCell();
    }
    templateCell.MaterialId = 6;
    templateCell.Mass = min(mass, 1);
    templateCell.IsActive = 1;
    templateCell.BodyId = 0;
    templateCell.Pressure = 0;
    return templateCell;
}

GridCell GasTemplate(GridCell first, GridCell second)
{
    if (CellKind(first) == 5)
    {
        return first;
    }
    if (CellKind(second) == 5)
    {
        return second;
    }
    GridCell cell = CreateEmptyCell();
    cell.MaterialId = 6;
    cell.Mass = 1;
    cell.IsActive = 1;
    return cell;
}


void MarkMovement(inout GridCell first, inout GridCell second, float horizontal, float vertical)
{
    first.RestFrames = 0;
    second.RestFrames = 0;
    first.VelocityX = horizontal;
    first.VelocityY = vertical;
    second.VelocityX = -horizontal;
    second.VelocityY = -vertical;
}

void SwapCells(uint firstIndex, uint secondIndex, float horizontal, float vertical)
{
    GridCell first = Grid[firstIndex];
    GridCell second = Grid[secondIndex];
    MarkMovement(first, second, horizontal, vertical);
    Grid[firstIndex] = second;
    Grid[secondIndex] = first;
    uint firstMaterial = CellMaterials[firstIndex];
    CellMaterials[firstIndex] = CellMaterials[secondIndex];
    CellMaterials[secondIndex] = firstMaterial;
}

void RelaxGasPair(
    uint firstIndex,
    uint secondIndex,
    float firstShare,
    float horizontal,
    float vertical)
{
    GridCell first = Grid[firstIndex];
    GridCell second = Grid[secondIndex];
    float firstMass = CellKind(first) == 5 ? first.Mass : 0;
    float secondMass = CellKind(second) == 5 ? second.Mass : 0;
    float totalMass = firstMass + secondMass;
    if (totalMass < GasMinimumMass)
    {
        Grid[firstIndex] = CreateEmptyCell();
        Grid[secondIndex] = CreateEmptyCell();
        CellMaterials[firstIndex] = 0;
        CellMaterials[secondIndex] = 0;
        return;
    }
    float targetFirst = min(1, totalMass * firstShare);
    float targetSecond = totalMass - targetFirst;
    if (abs(targetFirst - firstMass) <= GasTransferThreshold)
    {
        return;
    }
    GridCell templateCell = GasTemplate(first, second);
    GridCell resolvedFirst = GasWithMass(templateCell, targetFirst);
    GridCell resolvedSecond = GasWithMass(templateCell, targetSecond);
    MarkMovement(resolvedFirst, resolvedSecond, horizontal, vertical);
    Grid[firstIndex] = resolvedFirst;
    Grid[secondIndex] = resolvedSecond;
    CellMaterials[firstIndex] = resolvedFirst.IsActive != 0 ? resolvedFirst.MaterialId : 0;
    CellMaterials[secondIndex] = resolvedSecond.IsActive != 0 ? resolvedSecond.MaterialId : 0;
}

bool SandSupported(uint2 coordinate)
{
    if (coordinate.y + 1 >= Height)
    {
        return true;
    }
    uint kind = CellKindAt(coordinate + uint2(0, 1));
    return kind == 1 || kind == 2;
}
#define MaxSolidDistance 8

uint SolidDistanceBelow(uint2 coordinate)
{
    for (uint distance = 1; distance <= MaxSolidDistance && coordinate.y + distance < Height; distance++)
    {
        if (CellKindAt(coordinate + uint2(0, distance)) == 2)
        {
            return distance;
        }
    }
    return MaxSolidDistance + 1;
}



bool SandCanRoll(uint2 source, uint2 destination, uint sandMaterial, uint targetMaterial)
{
    if (CellRankFromMaterial(sandMaterial) <= CellRankFromMaterial(targetMaterial))
    {
        return false;
    }
    uint sourceDistance = SolidDistanceBelow(source);
    if (sourceDistance > MaxSolidDistance)
    {
        return false;
    }
    uint destinationDistance = SolidDistanceBelow(destination);
    return destinationDistance > sourceDistance;
}

bool WaterSupported(uint2 coordinate)
{
    if (coordinate.y + 1 >= Height)
    {
        return true;
    }
    uint kind = CellKindAt(coordinate + uint2(0, 1));
    return kind != 0 && kind != 5;
}

bool WaterCanEnter(uint waterMaterial, uint destinationMaterial)
{
    uint destinationKind = CellKindFromMaterial(destinationMaterial);
    if (destinationKind == 2)
    {
        return false;
    }
    return CellRankFromMaterial(waterMaterial) > CellRankFromMaterial(destinationMaterial);
}

bool IsWaterAt(int x, int y)
{
    if (x < 0 || y < 0 || x >= int(Width) || y >= int(Height))
    {
        return false;
    }
    return CellKindAt(uint2(x, y)) == 4;
}

bool WaterCanFlowSide(
    uint2 source,
    uint2 destination,
    uint waterMaterial,
    uint targetMaterial)
{
    uint stride = abs(int(destination.x) - int(source.x));
    uint requiredDepth = stride >= 128 ? 7 : stride >= 32 ? 3 : stride >= 2 ? 1 : 0;
    if (requiredDepth > 0)
    {
        if (source.y < requiredDepth ||
            !IsWaterAt(int(source.x), int(source.y) - int(requiredDepth)))
        {
            return false;
        }
    }
    if (!WaterCanEnter(waterMaterial, targetMaterial) || !WaterSupported(destination))
    {
        return false;
    }
    int direction = destination.x > source.x ? 1 : -1;
    if (IsWaterAt(int(source.x), int(source.y) - 1))
    {
        return true;
    }
    int sourceShoulder = int(source.x) - direction;
    int destinationShoulder = int(destination.x) + direction;
    return IsWaterAt(sourceShoulder, int(source.y)) &&
        !IsWaterAt(destinationShoulder, int(destination.y));
}

bool WaterCanFlowSideOpt(
    uint2 source,
    uint2 destination,
    uint waterMaterial,
    uint targetMaterial,
    bool hasWaterAbove,
    bool hasWaterLeft,
    bool hasWaterRight)
{
    uint stride = abs(int(destination.x) - int(source.x));
    uint requiredDepth = stride >= 128 ? 7 : stride >= 32 ? 3 : stride >= 2 ? 1 : 0;
    if (requiredDepth > 0)
    {
        if (source.y < requiredDepth ||
            !IsWaterAt(int(source.x), int(source.y) - int(requiredDepth)))
        {
            return false;
        }
    }
    if (!WaterCanEnter(waterMaterial, targetMaterial) || !WaterSupported(destination))
    {
        return false;
    }
    if (hasWaterAbove)
    {
        return true;
    }
    int direction = destination.x > source.x ? 1 : -1;
    bool hasWaterBehind = direction == 1 ? hasWaterLeft : hasWaterRight;
    int destinationShoulder = int(destination.x) + direction;
    return hasWaterBehind && !IsWaterAt(destinationShoulder, int(destination.y));
}

bool WaterPathClear(uint2 first, uint2 second)
{
    uint start = min(first.x, second.x) + 1;
    uint end = max(first.x, second.x);
    if (start >= end)
    {
        return true;
    }
    uint tilesPerRow = (Width + PathBlockerTileWidth - 1) / PathBlockerTileWidth;
    uint firstTile = start / PathBlockerTileWidth;
    uint lastTile = (end - 1) / PathBlockerTileWidth;
    uint row = first.y * tilesPerRow;
    for (uint tile = firstTile; tile <= lastTile; tile++)
    {
        uint relevantBits = 0xffffffffu;
        if (tile == firstTile)
        {
            relevantBits &= 0xffffffffu << (start & 31);
        }
        if (tile == lastTile)
        {
            uint endBit = end - tile * PathBlockerTileWidth;
            if (endBit < PathBlockerTileWidth)
            {
                relevantBits &= (1u << endBit) - 1u;
            }
        }
        if ((PathBlockerMasks[row + tile] & relevantBits) != 0)
        {
            return false;
        }
    }
    return true;
}

void BuildPathBlockerMask(uint2 tileCoordinate)
{
    uint tilesPerRow = (Width + PathBlockerTileWidth - 1) / PathBlockerTileWidth;
    if (tileCoordinate.x >= tilesPerRow || tileCoordinate.y >= Height)
    {
        return;
    }
    uint firstX = tileCoordinate.x * PathBlockerTileWidth;
    uint row = tileCoordinate.y * Width;
    uint mask = 0;
    for (uint bit = 0; bit < PathBlockerTileWidth && firstX + bit < Width; bit++)
    {
        uint kind = CellKindAtIndex(row + firstX + bit);
        if (kind != 0 && kind != 4 && kind != 5)
        {
            mask |= 1u << bit;
        }
    }
    PathBlockerMasks[tileCoordinate.y * tilesPerRow + tileCoordinate.x] = mask;
}

bool GasPathClear(uint2 first, uint2 second)
{
    uint start = min(first.x, second.x) + 1;
    uint end = max(first.x, second.x);
    uint row = first.y * Width;
    for (uint x = start; x < end; x++)
    {
        uint kind = CellKindAtIndex(row + x);
        if (kind != 0 && kind != 5)
        {
            return false;
        }
    }
    return true;
}

bool GasNearCeiling(uint2 coordinate)
{
    for (uint distance = 1; distance <= 12 && coordinate.y >= distance; distance++)
    {
        if (CellKindAt(coordinate - uint2(0, distance)) == 2)
        {
            return true;
        }
    }
    return false;
}

int WaterBaseY(uint x)
{
    for (int y = int(Height) - 1; y >= 0; y--)
    {
        if (CellKindAt(uint2(x, y)) == 4)
        {
            return y;
        }
    }
    return -1;
}

uint WaterSurfaceY(uint2 coordinate)
{
    uint top = coordinate.y;
    while (top > 0)
    {
        if (CellKindAt(uint2(coordinate.x, top - 1)) != 4)
        {
            break;
        }
        top--;
    }
    return top;
}

uint PackWaterColumn(uint top, uint base)
{
    return ((top + 1) & 0xffffu) | ((base + 1) << 16);
}

bool UnpackWaterColumn(uint packed, out uint top, out uint base)
{
    if (packed == 0)
    {
        top = 0;
        base = 0;
        return false;
    }
    top = (packed & 0xffffu) - 1;
    base = (packed >> 16) - 1;
    return true;
}

void BuildWaterColumnInfo(uint x)
{
    if (x >= Width)
    {
        return;
    }
    int base = WaterBaseY(x);
    WaterColumnState[x] = base < 0
        ? 0
        : PackWaterColumn(WaterSurfaceY(uint2(x, uint(base))), uint(base));
    for (uint lane = 0; lane < HydraulicTransfersPerColumn; lane++)
    {
        WaterColumnState[Width * (lane + 1) + x] = 0;
    }
    if (x == 0)
    {
        WaterColumnState[Width * HydraulicActivityRow] = 0;
    }
}

bool WaterPathFilled(uint firstX, uint secondX, uint y)
{
    uint start = min(firstX, secondX);
    uint end = max(firstX, secondX);
    uint row = y * Width;
    for (uint x = start; x <= end; x++)
    {
        if (CellKindAtIndex(row + x) != 4)
        {
            return false;
        }
    }
    return true;
}

bool WaterPathFilledBetween(uint firstX, uint secondX, uint y)
{
    uint start = min(firstX, secondX) + 1;
    uint end = max(firstX, secondX);
    uint row = y * Width;
    for (uint x = start; x < end; x++)
    {
        if (CellKindAtIndex(row + x) != 4)
        {
            return false;
        }
    }
    return true;
}

bool HasFilledWaterConnection(
    uint firstX,
    uint firstTop,
    uint firstBase,
    uint secondX,
    uint secondTop,
    uint secondBase)
{
    uint overlapTop = max(firstTop, secondTop);
    uint overlapBase = min(firstBase, secondBase);
    if (overlapTop > overlapBase)
    {
        return false;
    }
    uint searchTop = overlapBase > HydraulicConnectionSearchDepth
        ? max(overlapTop, overlapBase - HydraulicConnectionSearchDepth)
        : overlapTop;
    for (int y = int(overlapBase); y >= int(searchTop); y--)
    {
        if (WaterPathFilled(firstX, secondX, uint(y)))
        {
            return true;
        }
    }
    return false;
}

void PlanWaterColumnMove(
    uint firstX,
    uint firstTop,
    uint secondX,
    uint secondTop)
{
    uint sourceX;
    uint sourceTop;
    uint destinationX;
    uint destinationTop;
    if (firstTop + 1 < secondTop)
    {
        sourceX = firstX;
        sourceTop = firstTop;
        destinationX = secondX;
        destinationTop = secondTop;
    }
    else if (secondTop + 1 < firstTop)
    {
        sourceX = secondX;
        sourceTop = secondTop;
        destinationX = firstX;
        destinationTop = firstTop;
    }
    else
    {
        return;
    }
    if (destinationTop == 0)
    {
        return;
    }
    uint sourceIndex = FlattenCoordinate(uint2(sourceX, sourceTop));
    uint destinationIndex = FlattenCoordinate(uint2(destinationX, destinationTop - 1));
    if (CellKindAtIndex(sourceIndex) != 4 || CellKindAtIndex(destinationIndex) != 0)
    {
        return;
    }
    WaterColumnState[Width + sourceX] = sourceIndex + 1;
    WaterColumnState[Width * 2 + sourceX] = destinationIndex + 1;
}

void ResolveVerticalPair(uint2 upperCoordinate)
{
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = upperIndex + Width;
    uint upperMaterial = CellMaterials[upperIndex];
    uint lowerMaterial = CellMaterials[lowerIndex];
    uint upperKind = CellKindFromMaterial(upperMaterial);
    uint lowerKind = CellKindFromMaterial(lowerMaterial);
    if (upperKind == 2 || lowerKind == 2)
    {
        return;
    }
    if ((upperKind == 0 || upperKind == 5) &&
        (lowerKind == 0 || lowerKind == 5) &&
        (upperKind == 5 || lowerKind == 5))
    {
        RelaxGasPair(upperIndex, lowerIndex, 0.72, 0, -36);
        return;
    }
    if (upperKind != 0 && CellRankFromMaterial(upperMaterial) > CellRankFromMaterial(lowerMaterial))
    {
        SwapCells(upperIndex, lowerIndex, 0, 60);
    }
}

void ResolveDiagonalPair(uint2 upperCoordinate, uint2 lowerCoordinate)
{
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = FlattenCoordinate(lowerCoordinate);
    uint upperMaterial = CellMaterials[upperIndex];
    uint lowerMaterial = CellMaterials[lowerIndex];
    uint upperKind = CellKindFromMaterial(upperMaterial);
    uint lowerKind = CellKindFromMaterial(lowerMaterial);
    if (upperKind == 2 || lowerKind == 2)
    {
        return;
    }
    uint2 firstCorner = uint2(upperCoordinate.x, lowerCoordinate.y);
    uint2 secondCorner = uint2(lowerCoordinate.x, upperCoordinate.y);
    if (CellKindAt(firstCorner) == 2 && CellKindAt(secondCorner) == 2)
    {
        return;
    }
    if ((upperKind == 0 || upperKind == 5) &&
        (lowerKind == 0 || lowerKind == 5) &&
        (upperKind == 5 || lowerKind == 5))
    {
        float direction = lowerCoordinate.x > upperCoordinate.x ? 24 : -24;
        RelaxGasPair(upperIndex, lowerIndex, 0.62, direction, -28);
        return;
    }
    bool supported = upperKind == 1
        ? SandSupported(upperCoordinate)
        : upperKind == 4 && WaterSupported(upperCoordinate);
    if (supported && upperKind != 0 &&
        CellRankFromMaterial(upperMaterial) > CellRankFromMaterial(lowerMaterial))
    {
        float direction = lowerCoordinate.x > upperCoordinate.x ? 32 : -32;
        SwapCells(upperIndex, lowerIndex, direction, 48);
    }
}

void ResolveHorizontalPair(uint2 leftCoordinate)
{
    uint leftIndex = FlattenCoordinate(leftCoordinate);
    uint rightIndex = leftIndex + 1;
    uint leftMaterial = CellMaterials[leftIndex];
    uint rightMaterial = CellMaterials[rightIndex];
    uint leftKind = CellKindFromMaterial(leftMaterial);
    uint rightKind = CellKindFromMaterial(rightMaterial);
    if (leftKind == 2 || rightKind == 2)
    {
        return;
    }
    if (leftKind == 1 && SandCanRoll(
        leftCoordinate,
        leftCoordinate + uint2(1, 0),
        leftMaterial,
        rightMaterial))
    {
        SwapCells(leftIndex, rightIndex, 30, 0);
        return;
    }
    if (rightKind == 1 && SandCanRoll(
        leftCoordinate + uint2(1, 0),
        leftCoordinate,
        rightMaterial,
        leftMaterial))
    {
        SwapCells(leftIndex, rightIndex, -30, 0);
        return;
    }
    if ((leftKind == 0 || leftKind == 5) &&
        (rightKind == 0 || rightKind == 5) &&
        (leftKind == 5 || rightKind == 5))
    {
        RelaxGasPair(leftIndex, rightIndex, 0.5, 28, 0);
        return;
    }
    if (leftKind == 4 && WaterCanFlowSide(
        leftCoordinate,
        leftCoordinate + uint2(1, 0),
        leftMaterial,
        rightMaterial))
    {
        SwapCells(leftIndex, rightIndex, 42, 0);
        return;
    }
    if (rightKind == 4 && WaterCanFlowSide(
        leftCoordinate + uint2(1, 0),
        leftCoordinate,
        rightMaterial,
        leftMaterial))
    {
        SwapCells(leftIndex, rightIndex, -42, 0);
    }
}

void ResolveWaterSpan(uint2 leftCoordinate, uint stride)
{
    uint rightX = leftCoordinate.x + stride;
    if (rightX >= Width)
    {
        return;
    }
    uint leftIndex = FlattenCoordinate(leftCoordinate);
    uint rightIndex = FlattenCoordinate(uint2(rightX, leftCoordinate.y));
    uint leftMaterial = CellMaterials[leftIndex];
    uint rightMaterial = CellMaterials[rightIndex];
    uint leftKind = CellKindFromMaterial(leftMaterial);
    uint rightKind = CellKindFromMaterial(rightMaterial);
    uint2 rightCoordinate = uint2(rightX, leftCoordinate.y);
    if (leftKind == 4 && !WaterSupported(leftCoordinate))
    {
        return;
    }
    if (rightKind == 4 && !WaterSupported(rightCoordinate))
    {
        return;
    }
    if ((leftKind == 5 || rightKind == 5) &&
        (leftKind == 0 || leftKind == 5) &&
        (rightKind == 0 || rightKind == 5) &&
        GasNearCeiling(leftCoordinate) && GasNearCeiling(rightCoordinate) &&
        GasPathClear(leftCoordinate, rightCoordinate))
    {
        RelaxGasPair(leftIndex, rightIndex, 0.5, 44, 0);
        return;
    }
    if ((leftKind != 4 && rightKind != 4) ||
        (leftKind != 0 && leftKind != 4) ||
        (rightKind != 0 && rightKind != 4))
    {
        return;
    }
    if (leftKind == 4 && rightKind == 4)
    {
        return;
    }
    if (leftKind == 4)
    {
        if (WaterCanFlowSide(leftCoordinate, rightCoordinate, leftMaterial, rightMaterial) &&
            WaterPathFilledBetween(leftCoordinate.x, rightCoordinate.x, leftCoordinate.y))
        {
            SwapCells(leftIndex, rightIndex, 52, 0);
        }
        return;
    }
    if (rightKind == 4)
    {
        if (WaterCanFlowSide(rightCoordinate, leftCoordinate, rightMaterial, leftMaterial) &&
            WaterPathFilledBetween(leftCoordinate.x, rightCoordinate.x, leftCoordinate.y))
        {
            SwapCells(leftIndex, rightIndex, -52, 0);
        }
    }
}

void ResolveWaterColumnSpan(uint2 leftCoordinate, uint stride)
{
    if (leftCoordinate.y != 0)
    {
        return;
    }
    uint rightX = leftCoordinate.x + stride;
    if (rightX >= Width)
    {
        return;
    }
    WaterColumnState[Width + leftCoordinate.x] = 0;
    WaterColumnState[Width * 2 + leftCoordinate.x] = 0;
    WaterColumnState[Width + rightX] = 0;
    WaterColumnState[Width * 2 + rightX] = 0;
    uint leftTop;
    uint leftBase;
    uint rightTop;
    uint rightBase;
    if (!UnpackWaterColumn(WaterColumnState[leftCoordinate.x], leftTop, leftBase) ||
        !UnpackWaterColumn(WaterColumnState[rightX], rightTop, rightBase) ||
        abs(int(leftTop) - int(rightTop)) <= 1)
    {
        return;
    }
    if (!HasFilledWaterConnection(
        leftCoordinate.x,
        leftTop,
        leftBase,
        rightX,
        rightTop,
        rightBase))
    {
        return;
    }
    PlanWaterColumnMove(
        leftCoordinate.x,
        leftTop,
        rightX,
        rightTop);
}

void ApplyWaterColumnMove(uint x)
{
    uint encodedSource = WaterColumnState[Width + x];
    uint encodedDestination = WaterColumnState[Width * 2 + x];
    WaterColumnState[Width + x] = 0;
    WaterColumnState[Width * 2 + x] = 0;
    if (encodedSource == 0 || encodedDestination == 0)
    {
        return;
    }
    uint sourceIndex = encodedSource - 1;
    uint destinationIndex = encodedDestination - 1;
    if (CellKindAtIndex(sourceIndex) != 4 || CellKindAtIndex(destinationIndex) != 0)
    {
        return;
    }
    uint sourceX = sourceIndex % Width;
    uint destinationX = destinationIndex % Width;
    uint sourceTop;
    uint sourceBase;
    uint destinationTop;
    uint destinationBase;
    if (!UnpackWaterColumn(WaterColumnState[sourceX], sourceTop, sourceBase) ||
        !UnpackWaterColumn(WaterColumnState[destinationX], destinationTop, destinationBase))
    {
        return;
    }
    GridCell water = Grid[sourceIndex];
    GridCell empty = CreateEmptyCell();
    float horizontal = destinationX > sourceX ? 54 : -54;
    MarkMovement(water, empty, horizontal, 36);
    Grid[sourceIndex] = empty;
    Grid[destinationIndex] = water;
    CellMaterials[sourceIndex] = 0;
    CellMaterials[destinationIndex] = water.MaterialId;
    WaterColumnState[sourceX] = sourceTop == sourceBase
        ? 0
        : PackWaterColumn(sourceTop + 1, sourceBase);
    WaterColumnState[destinationX] = PackWaterColumn(destinationTop - 1, destinationBase);
}

uint PressureSourceHead(uint route)
{
    if (route == 0 || (route & 0x80000000u) != 0)
    {
        return Height;
    }
    uint sourceX = route - 1;
    if (sourceX >= Width)
    {
        return Height;
    }
    uint sourceTop;
    uint sourceBase;
    if (!UnpackWaterColumn(WaterColumnState[sourceX], sourceTop, sourceBase) ||
        sourceBase < sourceTop + 3 || !WaterSupported(uint2(sourceX, sourceBase)))
    {
        return Height;
    }
    return sourceTop;
}

uint PressureOwnSourceHead(uint2 coordinate)
{
    uint sourceTop;
    uint sourceBase;
    if (!UnpackWaterColumn(WaterColumnState[coordinate.x], sourceTop, sourceBase) ||
        coordinate.y < sourceTop || coordinate.y > sourceBase ||
        sourceBase < sourceTop + 3 || !WaterSupported(uint2(coordinate.x, sourceBase)))
    {
        return Height;
    }
    return sourceTop;
}

void RelaxWaterPressureRoute(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    if (CellKindAtIndex(index) != 4)
    {
        WaterPressureRoutes[index] = 0;
        return;
    }
    uint bestRoute = WaterPressureRoutes[index];
    uint bestHead = PressureSourceHead(bestRoute);
    uint ownRoute = coordinate.x + 1;
    // A column can contain several disconnected vertical water segments. The
    // compact cache describes only the bottom segment, so seed its route only
    // from cells that actually belong to that continuous segment.
    uint ownHead = PressureOwnSourceHead(coordinate);
    if (ownHead + HydraulicHeadRouteTolerance < bestHead)
    {
        bestRoute = ownRoute;
        bestHead = ownHead;
    }
    int2 offsets[4] =
    {
        int2(-1, 0),
        int2(1, 0),
        int2(0, -1),
        int2(0, 1)
    };
    for (uint neighbor = 0; neighbor < 4; neighbor++)
    {
        int2 candidate = int2(coordinate) + offsets[neighbor];
        if (candidate.x < 0 || candidate.y < 0 ||
            candidate.x >= int(Width) || candidate.y >= int(Height))
        {
            continue;
        }
        uint candidateIndex = FlattenCoordinate(uint2(candidate));
        if (CellKindAtIndex(candidateIndex) != 4)
        {
            continue;
        }
        uint candidateRoute = WaterPressureRoutes[candidateIndex];
        uint candidateHead = PressureSourceHead(candidateRoute);
        if (candidateHead + HydraulicHeadRouteTolerance < bestHead)
        {
            bestRoute = candidateRoute;
            bestHead = candidateHead;
        }
    }
    WaterPressureRoutes[index] = bestHead < Height ? bestRoute : 0;
    GridCell routedCell = Grid[index];
    routedCell.Pressure = bestHead < Height ? float(bestHead + 1) : 0;
    Grid[index] = routedCell;
}

void PlanPressurizedWaterMove(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    if (CellKindAtIndex(index) != 4)
    {
        return;
    }
    uint route = WaterPressureRoutes[index];
    uint sourceHead = PressureSourceHead(route);
    if (sourceHead >= Height)
    {
        return;
    }
    uint sourceX = route - 1;
    int sideDirection = ((FrameIndex + coordinate.x) & 1) == 0 ? -1 : 1;
    int2 offsets[8] =
    {
        int2(0, -1),
        int2(sideDirection, -1),
        int2(-sideDirection, -1),
        int2(sideDirection, 0),
        int2(-sideDirection, 0),
        int2(sideDirection, 1),
        int2(-sideDirection, 1),
        int2(0, 1)
    };
    for (uint attempt = 0; attempt < 8; attempt++)
    {
        int2 destinationCoordinate = int2(coordinate) + offsets[attempt];
        if (destinationCoordinate.x < 0 ||
            destinationCoordinate.y < int(sourceHead + HydraulicSurfaceTolerance) ||
            destinationCoordinate.x >= int(Width) || destinationCoordinate.y >= int(Height))
        {
            continue;
        }
        uint2 destination = uint2(destinationCoordinate);
        uint destinationIndex = FlattenCoordinate(destination);
        if (CellKindAtIndex(destinationIndex) != 0)
        {
            continue;
        }
        if (offsets[attempt].x != 0 && offsets[attempt].y != 0 &&
            CellKindAt(uint2(destinationCoordinate.x, coordinate.y)) == 2 &&
            CellKindAt(uint2(coordinate.x, destinationCoordinate.y)) == 2)
        {
            continue;
        }
        uint reservation = 0x80000000u | route;
        uint previousReservation;
        InterlockedCompareExchange(
            WaterPressureRoutes[destinationIndex],
            0,
            reservation,
            previousReservation);
        if (previousReservation != 0)
        {
            continue;
        }
        uint firstLane = (destinationIndex + FrameIndex) % HydraulicTransfersPerColumn;
        for (uint laneAttempt = 0; laneAttempt < HydraulicTransfersPerColumn; laneAttempt++)
        {
            uint lane = (firstLane + laneAttempt) % HydraulicTransfersPerColumn;
            uint previousDestination;
            InterlockedCompareExchange(
                WaterColumnState[Width * (lane + 1) + sourceX],
                0,
                destinationIndex + 1,
                previousDestination);
            if (previousDestination == 0)
            {
                return;
            }
        }
        uint ignored;
        InterlockedCompareExchange(
            WaterPressureRoutes[destinationIndex],
            reservation,
            0,
            ignored);
    }
}

void PlanPressurizedWaterReturn(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    if (coordinate.y == 0 || CellKindAtIndex(index) != 4 ||
        CellKindAtIndex(index - Width) != 0)
    {
        return;
    }
    uint route = WaterPressureRoutes[index];
    uint sourceHead = PressureSourceHead(route);
    if (sourceHead >= Height || coordinate.y + HydraulicSurfaceTolerance >= sourceHead)
    {
        return;
    }
    uint sourceX = route - 1;
    uint encodedSource = 0x80000000u | (index + 1);
    uint firstLane = (index + FrameIndex) % HydraulicTransfersPerColumn;
    for (uint laneAttempt = 0; laneAttempt < HydraulicTransfersPerColumn; laneAttempt++)
    {
        uint lane = (firstLane + laneAttempt) % HydraulicTransfersPerColumn;
        uint previousSource;
        InterlockedCompareExchange(
            WaterColumnState[Width * (lane + 1) + sourceX],
            0,
            encodedSource,
            previousSource);
        if (previousSource == 0)
        {
            return;
        }
    }
}

void ApplyPressurizedWaterReturnSlot(uint sourceX, uint lane)
{
    uint slot = Width * (lane + 1) + sourceX;
    uint encodedSource = WaterColumnState[slot];
    WaterColumnState[slot] = 0;
    if ((encodedSource & 0x80000000u) == 0)
    {
        return;
    }
    uint sourceWaterIndex = (encodedSource & 0x7fffffffu) - 1;
    uint route = sourceX + 1;
    uint sourceTop;
    uint sourceBase;
    if (WaterPressureRoutes[sourceWaterIndex] != route ||
        !UnpackWaterColumn(WaterColumnState[sourceX], sourceTop, sourceBase) ||
        sourceTop == 0)
    {
        return;
    }
    uint sourceWaterY = sourceWaterIndex / Width;
    if (sourceWaterY + HydraulicSurfaceTolerance >= sourceTop)
    {
        return;
    }
    uint destinationIndex = FlattenCoordinate(uint2(sourceX, sourceTop - 1));
    if (CellKindAtIndex(sourceWaterIndex) != 4 || CellKindAtIndex(destinationIndex) != 0)
    {
        return;
    }
    GridCell water = Grid[sourceWaterIndex];
    GridCell empty = CreateEmptyCell();
    uint sourceWaterX = sourceWaterIndex % Width;
    float horizontal = sourceX == sourceWaterX ? 0 : sourceX > sourceWaterX ? 54 : -54;
    MarkMovement(water, empty, horizontal, 38);
    Grid[sourceWaterIndex] = empty;
    Grid[destinationIndex] = water;
    CellMaterials[sourceWaterIndex] = 0;
    CellMaterials[destinationIndex] = water.MaterialId;
    WaterPressureRoutes[sourceWaterIndex] = 0;
    WaterPressureRoutes[destinationIndex] = route;
    uint ignored;
    InterlockedAdd(WaterColumnState[Width * HydraulicActivityRow], 1, ignored);
    WaterColumnState[sourceX] = PackWaterColumn(sourceTop - 1, sourceBase);
}

void ApplyPressurizedWaterReturn(uint sourceX)
{
    for (uint lane = 0; lane < HydraulicTransfersPerColumn; lane++)
    {
        ApplyPressurizedWaterReturnSlot(sourceX, lane);
    }
}

void ApplyPressurizedWaterMoveSlot(uint sourceX, uint lane)
{
    uint slot = Width * (lane + 1) + sourceX;
    uint encodedDestination = WaterColumnState[slot];
    WaterColumnState[slot] = 0;
    if (encodedDestination == 0)
    {
        return;
    }
    uint destinationIndex = encodedDestination - 1;
    uint route = sourceX + 1;
    if (WaterPressureRoutes[destinationIndex] != (0x80000000u | route))
    {
        return;
    }
    uint sourceTop;
    uint sourceBase;
    if (!UnpackWaterColumn(WaterColumnState[sourceX], sourceTop, sourceBase))
    {
        WaterPressureRoutes[destinationIndex] = 0;
        return;
    }
    uint sourceIndex = FlattenCoordinate(uint2(sourceX, sourceTop));
    if (CellKindAtIndex(sourceIndex) != 4 || CellKindAtIndex(destinationIndex) != 0)
    {
        WaterPressureRoutes[destinationIndex] = 0;
        return;
    }
    GridCell water = Grid[sourceIndex];
    GridCell empty = CreateEmptyCell();
    uint destinationX = destinationIndex % Width;
    float horizontal = destinationX == sourceX ? 0 : destinationX > sourceX ? 54 : -54;
    MarkMovement(water, empty, horizontal, 36);
    Grid[sourceIndex] = empty;
    Grid[destinationIndex] = water;
    CellMaterials[sourceIndex] = 0;
    CellMaterials[destinationIndex] = water.MaterialId;
    WaterPressureRoutes[sourceIndex] = 0;
    WaterPressureRoutes[destinationIndex] = route;
    uint ignored;
    InterlockedAdd(
        WaterColumnState[Width * HydraulicActivityRow],
        1,
        ignored);
    WaterColumnState[sourceX] = sourceTop == sourceBase
        ? 0
        : PackWaterColumn(sourceTop + 1, sourceBase);
}

void ApplyPressurizedWaterMove(uint sourceX)
{
    for (uint lane = 0; lane < HydraulicTransfersPerColumn; lane++)
    {
        ApplyPressurizedWaterMoveSlot(sourceX, lane);
    }
}

bool CanMoveSand(uint2 coordinate, GridCell cell)
{
    if (coordinate.y + 1 >= Height)
    {
        return false;
    }
    uint belowMaterial = CellMaterials[FlattenCoordinate(coordinate + uint2(0, 1))];
    uint belowKind = CellKindFromMaterial(belowMaterial);
    if (belowKind != 2 && CellRankFromMaterial(cell.MaterialId) > CellRankFromMaterial(belowMaterial))
    {
        return true;
    }
    if (!SandSupported(coordinate))
    {
        return false;
    }
    for (int direction = -1; direction <= 1; direction += 2)
    {
        int x = int(coordinate.x) + direction;
        if (x < 0 || x >= int(Width))
        {
            continue;
        }
        uint diagonalMaterial = CellMaterials[FlattenCoordinate(uint2(x, coordinate.y + 1))];
        if (CellKindFromMaterial(diagonalMaterial) != 2 &&
            CellRankFromMaterial(cell.MaterialId) > CellRankFromMaterial(diagonalMaterial))
        {
            return true;
        }
    }
    if (belowKind != 1)
    {
        // Optimization: Calculate source solid distance once.
        // If it's > MaxSolidDistance, sand cannot roll, so we can skip rolling checks entirely.
        uint sourceDistance = SolidDistanceBelow(coordinate);
        if (sourceDistance <= MaxSolidDistance)
        {
            for (int direction = -1; direction <= 1; direction += 2)
            {
                int x = int(coordinate.x) + direction;
                if (x < 0 || x >= int(Width))
                {
                    continue;
                }
                uint2 side = uint2(x, coordinate.y);
                uint targetMaterial = CellMaterials[FlattenCoordinate(side)];
                if (CellRankFromMaterial(cell.MaterialId) > CellRankFromMaterial(targetMaterial))
                {
                    uint destinationDistance = SolidDistanceBelow(side);
                    if (destinationDistance > sourceDistance)
                    {
                        return true;
                    }
                }
            }
        }
    }
    return false;
}

bool CanMoveWater(uint2 coordinate, GridCell cell)
{
    if (coordinate.y + 1 < Height)
    {
        uint belowMaterial = CellMaterials[FlattenCoordinate(coordinate + uint2(0, 1))];
        if (WaterCanEnter(cell.MaterialId, belowMaterial))
        {
            return true;
        }
        if (WaterSupported(coordinate))
        {
            for (int direction = -1; direction <= 1; direction += 2)
            {
                int x = int(coordinate.x) + direction;
                if (x < 0 || x >= int(Width))
                {
                    continue;
                }
                uint2 diagonalCoordinate = uint2(x, coordinate.y + 1);
                if (WaterCanEnter(
                    cell.MaterialId,
                    CellMaterials[FlattenCoordinate(diagonalCoordinate)]))
                {
                    return true;
                }
            }
        }
    }

    // Only check horizontal flow if the water is supported!
    if (WaterSupported(coordinate))
    {
        // Cache neighbor water states to reduce redundant buffer reads inside loop
        bool hasWaterAbove = IsWaterAt(int(coordinate.x), int(coordinate.y) - 1);
        bool hasWaterLeft = IsWaterAt(int(coordinate.x) - 1, int(coordinate.y));
        bool hasWaterRight = IsWaterAt(int(coordinate.x) + 1, int(coordinate.y));

        for (int direction = -1; direction <= 1; direction += 2)
        {
            int x = int(coordinate.x) + direction;
            if (x < 0 || x >= int(Width))
            {
                continue;
            }
            uint2 sideCoordinate = uint2(x, coordinate.y);
            if (WaterCanFlowSideOpt(
                coordinate,
                sideCoordinate,
                cell.MaterialId,
                CellMaterials[FlattenCoordinate(sideCoordinate)],
                hasWaterAbove,
                hasWaterLeft,
                hasWaterRight))
            {
                return true;
            }
        }
        static const uint spans[8] = { 2, 4, 8, 16, 32, 64, 128, 256 };
        for (uint spanIndex = 0; spanIndex < 8; spanIndex++)
        {
            int stride = int(spans[spanIndex]);
            for (int direction = -1; direction <= 1; direction += 2)
            {
                int x = int(coordinate.x) + direction * stride;
                if (x < 0 || x >= int(Width))
                {
                    continue;
                }
                uint2 destination = uint2(x, coordinate.y);
                if (WaterCanFlowSideOpt(
                    coordinate,
                    destination,
                    cell.MaterialId,
                    CellMaterials[FlattenCoordinate(destination)],
                    hasWaterAbove,
                    hasWaterLeft,
                    hasWaterRight) &&
                    WaterPathFilledBetween(coordinate.x, destination.x, coordinate.y))
                {
                    return true;
                }
            }
        }
    }
    return false;
}

bool CanMoveGas(uint2 coordinate, GridCell cell)
{
    for (int yOffset = -1; yOffset <= 0; yOffset++)
    {
        int y = int(coordinate.y) + yOffset;
        if (y < 0 || y >= int(Height))
        {
            continue;
        }
        for (int xOffset = -1; xOffset <= 1; xOffset++)
        {
            if (xOffset == 0 && yOffset == 0)
            {
                continue;
            }
            int x = int(coordinate.x) + xOffset;
            if (x < 0 || x >= int(Width))
            {
                continue;
            }
            uint neighborIndex = FlattenCoordinate(uint2(x, y));
            uint neighborKind = CellKindAtIndex(neighborIndex);
            if (neighborKind == 0 && cell.Mass > GasTransferThreshold * 2)
            {
                return true;
            }
            if (neighborKind == 5)
            {
                GridCell neighbor = Grid[neighborIndex];
                if (abs(neighbor.Mass - cell.Mass) > GasTransferThreshold * 2)
                {
                    return true;
                }
            }
        }
    }
    return false;
}

void UpdateRestState(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    uint kind = CellKindAtIndex(index);
    if (!IsCellularMaterial(kind))
    {
        return;
    }
    GridCell cell = Grid[index];
    uint threshold = kind == 1 ? SandRestThreshold : FluidRestThreshold;
    bool canMove = false;
    if (cell.RestFrames < threshold)
    {
        canMove = kind == 1
            ? CanMoveSand(coordinate, cell)
            : kind == 4
                ? CanMoveWater(coordinate, cell)
                : CanMoveGas(coordinate, cell);
    }
    if (canMove)
    {
        cell.RestFrames = 0;
    }
    else
    {
        cell.RestFrames = min(cell.RestFrames + 1, threshold);
        cell.VelocityX = 0;
        cell.VelocityY = 0;
    }
    Grid[index] = cell;
}

void ForceCellularRest(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    uint kind = CellKindAtIndex(index);
    if (!IsCellularMaterial(kind))
    {
        return;
    }
    GridCell cell = Grid[index];
    cell.RestFrames = kind == 1 ? SandRestThreshold : FluidRestThreshold;
    cell.VelocityX = 0;
    cell.VelocityY = 0;
    Grid[index] = cell;
}

void BuildCellMaterialMap(uint2 coordinate)
{
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = Grid[index];
    CellMaterials[index] = cell.IsActive != 0 ? cell.MaterialId : 0;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (SimulationPhase == 32)
    {
        BuildCellMaterialMap(dispatchThreadId.xy);
        return;
    }
    if (SimulationPhase == 31)
    {
        BuildPathBlockerMask(dispatchThreadId.xy);
        return;
    }
    if (SimulationPhase == 33)
    {
        BuildWaterColumnInfo(dispatchThreadId.x);
        return;
    }
    if (dispatchThreadId.x >= DispatchExtentX || dispatchThreadId.y >= DispatchExtentY)
    {
        return;
    }
    uint2 coordinate;
    if (SimulationPhase <= 1)
    {
        coordinate = uint2(
            DispatchOffsetX + dispatchThreadId.x,
            DispatchOffsetY + dispatchThreadId.y * 2);
    }
    else if (SimulationPhase <= 3)
    {
        coordinate = uint2(
            DispatchOffsetX + dispatchThreadId.x * 2,
            DispatchOffsetY + dispatchThreadId.y);
    }
    else if (SimulationPhase >= 5 && SimulationPhase <= 12)
    {
        coordinate = uint2(DispatchOffsetX, DispatchOffsetY) + dispatchThreadId.xy * 2;
    }
    else
    {
        coordinate = dispatchThreadId.xy + uint2(DispatchOffsetX, DispatchOffsetY);
    }
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    if (SimulationPhase <= 1)
    {
        if (coordinate.y + 1 < Height)
        {
            ResolveVerticalPair(coordinate);
        }
        return;
    }
    if (SimulationPhase <= 3)
    {
        if (coordinate.x + 1 < Width)
        {
            ResolveHorizontalPair(coordinate);
        }
        return;
    }
    if (SimulationPhase == 4)
    {
        UpdateRestState(coordinate);
        return;
    }
    if (SimulationPhase == 29)
    {
        ApplyWaterColumnMove(coordinate.x);
        return;
    }
    if (SimulationPhase == 30)
    {
        ForceCellularRest(coordinate);
        return;
    }
    if (SimulationPhase >= 13 && SimulationPhase <= 23)
    {
        uint stride = 1u << (SimulationPhase - 13);
        uint blockPosition = coordinate.x % (stride * 2);
        if (blockPosition < stride)
        {
            ResolveWaterColumnSpan(coordinate, stride);
        }
        return;
    }
    if (SimulationPhase == 34)
    {
        RelaxWaterPressureRoute(coordinate);
        return;
    }
    if (SimulationPhase == 36)
    {
        PlanPressurizedWaterMove(coordinate);
        return;
    }
    if (SimulationPhase == 37)
    {
        ApplyPressurizedWaterMove(coordinate.x);
        return;
    }
    if (SimulationPhase == 38)
    {
        PlanPressurizedWaterReturn(coordinate);
        return;
    }
    if (SimulationPhase == 39)
    {
        ApplyPressurizedWaterReturn(coordinate.x);
        return;
    }
    if (SimulationPhase >= 40 && SimulationPhase <= 47)
    {
        uint stride = 1u << (SimulationPhase - 39);
        uint blockPosition = coordinate.x % (stride * 2);
        if (blockPosition < stride)
        {
            ResolveWaterSpan(coordinate, stride);
        }
        return;
    }
    uint diagonalPhase = SimulationPhase - 5;
    uint orientation = diagonalPhase & 1;
    if (coordinate.x + 1 >= Width || coordinate.y + 1 >= Height)
    {
        return;
    }
    uint2 upper = orientation == 0 ? coordinate : coordinate + uint2(1, 0);
    uint2 lower = orientation == 0 ? coordinate + uint2(1, 1) : coordinate + uint2(0, 1);
    ResolveDiagonalPair(upper, lower);
}
