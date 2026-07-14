#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> WaterMoves : register(u1);

static const uint SandRestThreshold = 30;
static const uint FluidRestThreshold = 60;
static const float GasMinimumMass = 0.01;
static const float GasTransferThreshold = 0.03;

uint CellKind(GridCell cell)
{
    return cell.IsActive == 0 ? 0 : Materials[cell.MaterialId].SimulationKind;
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
}

bool SandSupported(uint2 coordinate)
{
    if (coordinate.y + 1 >= Height)
    {
        return true;
    }
    GridCell below = Grid[FlattenCoordinate(coordinate + uint2(0, 1))];
    uint kind = CellKind(below);
    return kind == 1 || kind == 2;
}

uint SolidDistanceBelow(uint2 coordinate)
{
    for (uint distance = 1; distance <= 16 && coordinate.y + distance < Height; distance++)
    {
        if (IsSolid(Grid[FlattenCoordinate(coordinate + uint2(0, distance))]))
        {
            return distance;
        }
    }
    return 17;
}

bool SandCanRoll(uint2 source, uint2 destination, GridCell sand, GridCell target)
{
    if (CellRank(sand) <= CellRank(target))
    {
        return false;
    }
    uint sourceDistance = SolidDistanceBelow(source);
    uint destinationDistance = SolidDistanceBelow(destination);
    return sourceDistance <= 16 && destinationDistance > sourceDistance;
}

bool WaterSupported(uint2 coordinate)
{
    if (coordinate.y + 1 >= Height)
    {
        return true;
    }
    GridCell below = Grid[FlattenCoordinate(coordinate + uint2(0, 1))];
    uint kind = CellKind(below);
    return below.IsActive != 0 && kind != 5;
}

bool WaterCanEnter(GridCell water, GridCell destination)
{
    if (IsSolid(destination))
    {
        return false;
    }
    return CellRank(water) > CellRank(destination);
}

bool IsWaterAt(int x, int y)
{
    if (x < 0 || y < 0 || x >= int(Width) || y >= int(Height))
    {
        return false;
    }
    return CellKind(Grid[FlattenCoordinate(uint2(x, y))]) == 4;
}

bool WaterCanFlowSide(
    uint2 source,
    uint2 destination,
    GridCell water,
    GridCell target)
{
    if (!WaterCanEnter(water, target) || !WaterSupported(destination))
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

bool WaterPathClear(uint2 first, uint2 second)
{
    uint start = min(first.x, second.x) + 1;
    uint end = max(first.x, second.x);
    uint row = first.y * Width;
    for (uint x = start; x < end; x++)
    {
        uint kind = CellKind(Grid[row + x]);
        if (kind != 0 && kind != 4 && kind != 5)
        {
            return false;
        }
    }
    return true;
}

bool GasPathClear(uint2 first, uint2 second)
{
    uint start = min(first.x, second.x) + 1;
    uint end = max(first.x, second.x);
    uint row = first.y * Width;
    for (uint x = start; x < end; x++)
    {
        uint kind = CellKind(Grid[row + x]);
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
        if (IsSolid(Grid[FlattenCoordinate(coordinate - uint2(0, distance))]))
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
        if (CellKind(Grid[FlattenCoordinate(uint2(x, y))]) == 4)
        {
            return y;
        }
    }
    return -1;
}

uint WaterSurfaceY(uint2 coordinate)
{
    uint top = coordinate.y;
    for (uint depth = 0; top > 0 && depth < 256; depth++)
    {
        if (CellKind(Grid[FlattenCoordinate(uint2(coordinate.x, top - 1))]) != 4)
        {
            break;
        }
        top--;
    }
    return top;
}

void BalanceWaterColumns(uint2 first, uint2 second)
{
    uint firstTop = WaterSurfaceY(first);
    uint secondTop = WaterSurfaceY(second);
    uint2 source;
    uint2 destination;
    if (firstTop + 2 < secondTop)
    {
        source = first;
        destination = uint2(second.x, secondTop - 1);
    }
    else if (secondTop + 2 < firstTop)
    {
        source = second;
        destination = uint2(first.x, firstTop - 1);
    }
    else
    {
        return;
    }
    uint sourceIndex = FlattenCoordinate(source);
    uint destinationIndex = FlattenCoordinate(destination);
    if (CellKind(Grid[destinationIndex]) != 0)
    {
        return;
    }
    WaterMoves[sourceIndex] = destinationIndex + 1;
}

void ApplyWaterColumnMove(uint2 coordinate)
{
    uint sourceIndex = FlattenCoordinate(coordinate);
    uint encodedDestination = WaterMoves[sourceIndex];
    if (encodedDestination == 0)
    {
        return;
    }
    uint destinationIndex = encodedDestination - 1;
    GridCell water = Grid[sourceIndex];
    if (CellKind(water) != 4 || CellKind(Grid[destinationIndex]) != 0)
    {
        return;
    }
    GridCell empty = CreateEmptyCell();
    MarkMovement(water, empty, 54, -36);
    Grid[sourceIndex] = empty;
    Grid[destinationIndex] = water;
}

void ResolveVerticalPair(uint2 upperCoordinate)
{
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = upperIndex + Width;
    GridCell upper = Grid[upperIndex];
    GridCell lower = Grid[lowerIndex];
    if (IsSolid(upper) || IsSolid(lower))
    {
        return;
    }
    if (IsGasOrEmpty(upper) && IsGasOrEmpty(lower) &&
        (CellKind(upper) == 5 || CellKind(lower) == 5))
    {
        RelaxGasPair(upperIndex, lowerIndex, 0.72, 0, -36);
        return;
    }
    if (upper.IsActive != 0 && CellRank(upper) > CellRank(lower))
    {
        SwapCells(upperIndex, lowerIndex, 0, 60);
    }
}

void ResolveDiagonalPair(uint2 upperCoordinate, uint2 lowerCoordinate)
{
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = FlattenCoordinate(lowerCoordinate);
    GridCell upper = Grid[upperIndex];
    GridCell lower = Grid[lowerIndex];
    if (IsSolid(upper) || IsSolid(lower))
    {
        return;
    }
    uint2 firstCorner = uint2(upperCoordinate.x, lowerCoordinate.y);
    uint2 secondCorner = uint2(lowerCoordinate.x, upperCoordinate.y);
    if (IsSolid(Grid[FlattenCoordinate(firstCorner)]) &&
        IsSolid(Grid[FlattenCoordinate(secondCorner)]))
    {
        return;
    }
    if (IsGasOrEmpty(upper) && IsGasOrEmpty(lower) &&
        (CellKind(upper) == 5 || CellKind(lower) == 5))
    {
        float direction = lowerCoordinate.x > upperCoordinate.x ? 24 : -24;
        RelaxGasPair(upperIndex, lowerIndex, 0.62, direction, -28);
        return;
    }
    uint upperKind = CellKind(upper);
    bool supported = upperKind == 1
        ? SandSupported(upperCoordinate)
        : upperKind == 4 && WaterSupported(upperCoordinate);
    if (supported && upper.IsActive != 0 && CellRank(upper) > CellRank(lower))
    {
        float direction = lowerCoordinate.x > upperCoordinate.x ? 32 : -32;
        SwapCells(upperIndex, lowerIndex, direction, 48);
    }
}

void ResolveHorizontalPair(uint2 leftCoordinate)
{
    uint leftIndex = FlattenCoordinate(leftCoordinate);
    uint rightIndex = leftIndex + 1;
    GridCell left = Grid[leftIndex];
    GridCell right = Grid[rightIndex];
    if (IsSolid(left) || IsSolid(right))
    {
        return;
    }
    if (CellKind(left) == 1 && SandCanRoll(
        leftCoordinate,
        leftCoordinate + uint2(1, 0),
        left,
        right))
    {
        SwapCells(leftIndex, rightIndex, 30, 0);
        return;
    }
    if (CellKind(right) == 1 && SandCanRoll(
        leftCoordinate + uint2(1, 0),
        leftCoordinate,
        right,
        left))
    {
        SwapCells(leftIndex, rightIndex, -30, 0);
        return;
    }
    if (IsGasOrEmpty(left) && IsGasOrEmpty(right) &&
        (CellKind(left) == 5 || CellKind(right) == 5))
    {
        RelaxGasPair(leftIndex, rightIndex, 0.5, 28, 0);
        return;
    }
    if (CellKind(left) == 4 && WaterCanFlowSide(
        leftCoordinate,
        leftCoordinate + uint2(1, 0),
        left,
        right))
    {
        SwapCells(leftIndex, rightIndex, 42, 0);
        return;
    }
    if (CellKind(right) == 4 && WaterCanFlowSide(
        leftCoordinate + uint2(1, 0),
        leftCoordinate,
        right,
        left))
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
    GridCell left = Grid[leftIndex];
    GridCell right = Grid[rightIndex];
    uint leftKind = CellKind(left);
    uint rightKind = CellKind(right);
    uint2 rightCoordinate = uint2(rightX, leftCoordinate.y);
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
    if (!WaterPathClear(leftCoordinate, rightCoordinate))
    {
        return;
    }
    if (leftKind == 4 && rightKind == 4)
    {
        return;
    }
    if (leftKind == 4 && WaterCanFlowSide(
        leftCoordinate,
        rightCoordinate,
        left,
        right))
    {
        SwapCells(leftIndex, rightIndex, 52, 0);
        return;
    }
    if (rightKind == 4 && WaterCanFlowSide(
        rightCoordinate,
        leftCoordinate,
        right,
        left))
    {
        SwapCells(leftIndex, rightIndex, -52, 0);
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
    int leftBase = WaterBaseY(leftCoordinate.x);
    int rightBase = WaterBaseY(rightX);
    if (leftBase < 0 || rightBase < 0 || abs(leftBase - rightBase) > 8)
    {
        return;
    }
    uint connectionY = uint(min(leftBase, rightBase));
    uint2 leftConnection = uint2(leftCoordinate.x, connectionY);
    uint2 rightConnection = uint2(rightX, connectionY);
    if (CellKind(Grid[FlattenCoordinate(leftConnection)]) != 4 ||
        CellKind(Grid[FlattenCoordinate(rightConnection)]) != 4 ||
        !WaterPathClear(leftConnection, rightConnection))
    {
        return;
    }
    BalanceWaterColumns(
        uint2(leftCoordinate.x, leftBase),
        uint2(rightX, rightBase));
}

bool CanMoveSand(uint2 coordinate, GridCell cell)
{
    if (coordinate.y + 1 >= Height)
    {
        return false;
    }
    GridCell below = Grid[FlattenCoordinate(coordinate + uint2(0, 1))];
    if (!IsSolid(below) && CellRank(cell) > CellRank(below))
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
        GridCell diagonal = Grid[FlattenCoordinate(uint2(x, coordinate.y + 1))];
        if (!IsSolid(diagonal) && CellRank(cell) > CellRank(diagonal))
        {
            return true;
        }
    }
    for (int direction = -1; direction <= 1; direction += 2)
    {
        int x = int(coordinate.x) + direction;
        if (x < 0 || x >= int(Width))
        {
            continue;
        }
        uint2 side = uint2(x, coordinate.y);
        if (SandCanRoll(coordinate, side, cell, Grid[FlattenCoordinate(side)]))
        {
            return true;
        }
    }
    return false;
}

bool CanMoveWater(uint2 coordinate, GridCell cell)
{
    if (coordinate.y + 1 < Height)
    {
        GridCell below = Grid[FlattenCoordinate(coordinate + uint2(0, 1))];
        if (WaterCanEnter(cell, below))
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
                if (WaterCanEnter(cell, Grid[FlattenCoordinate(diagonalCoordinate)]))
                {
                    return true;
                }
            }
        }
    }
    for (int direction = -1; direction <= 1; direction += 2)
    {
        int x = int(coordinate.x) + direction;
        if (x < 0 || x >= int(Width))
        {
            continue;
        }
        uint2 sideCoordinate = uint2(x, coordinate.y);
        if (WaterCanFlowSide(
            coordinate,
            sideCoordinate,
            cell,
            Grid[FlattenCoordinate(sideCoordinate)]))
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
            if (WaterPathClear(coordinate, destination) && WaterCanFlowSide(
                coordinate,
                destination,
                cell,
                Grid[FlattenCoordinate(destination)]))
            {
                return true;
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
            GridCell neighbor = Grid[FlattenCoordinate(uint2(x, y))];
            if (neighbor.IsActive == 0 && cell.Mass > GasTransferThreshold * 2 ||
                CellKind(neighbor) == 5 &&
                abs(neighbor.Mass - cell.Mass) > GasTransferThreshold * 2)
            {
                return true;
            }
        }
    }
    return false;
}

void UpdateRestState(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = Grid[index];
    uint kind = CellKind(cell);
    if (!IsCellularMaterial(kind))
    {
        return;
    }
    bool canMove = kind == 1
        ? CanMoveSand(coordinate, cell)
        : kind == 4
            ? CanMoveWater(coordinate, cell)
            : CanMoveGas(coordinate, cell);
    if (canMove)
    {
        cell.RestFrames = 0;
    }
    else
    {
        uint threshold = kind == 1 ? SandRestThreshold : FluidRestThreshold;
        cell.RestFrames = min(cell.RestFrames + 1, threshold);
        cell.VelocityX = 0;
        cell.VelocityY = 0;
    }
    cell.Pressure = 0;
    Grid[index] = cell;
}

void ForceCellularRest(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = Grid[index];
    uint kind = CellKind(cell);
    if (!IsCellularMaterial(kind))
    {
        return;
    }
    cell.RestFrames = kind == 1 ? SandRestThreshold : FluidRestThreshold;
    cell.VelocityX = 0;
    cell.VelocityY = 0;
    cell.Pressure = 0;
    Grid[index] = cell;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy + uint2(DispatchOffsetX, DispatchOffsetY);
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    if (SimulationPhase <= 1)
    {
        if ((coordinate.y & 1) == SimulationPhase && coordinate.y + 1 < Height)
        {
            ResolveVerticalPair(coordinate);
        }
        return;
    }
    if (SimulationPhase <= 3)
    {
        if ((coordinate.x & 1) == SimulationPhase - 2 && coordinate.x + 1 < Width)
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
        ApplyWaterColumnMove(coordinate);
        return;
    }
    if (SimulationPhase == 30)
    {
        ForceCellularRest(coordinate);
        return;
    }
    if (SimulationPhase >= 13)
    {
        bool columnPhase = SimulationPhase <= 20;
        uint stride = 1u << (columnPhase ? SimulationPhase - 12 : SimulationPhase - 20);
        uint blockPosition = coordinate.x % (stride * 2);
        if (blockPosition < stride)
        {
            if (columnPhase)
            {
                ResolveWaterColumnSpan(coordinate, stride);
            }
            else
            {
                ResolveWaterSpan(coordinate, stride);
            }
        }
        return;
    }
    uint diagonalPhase = SimulationPhase - 5;
    uint orientation = diagonalPhase & 1;
    uint xParity = (diagonalPhase >> 1) & 1;
    uint yParity = (diagonalPhase >> 2) & 1;
    if ((coordinate.x & 1) != xParity || (coordinate.y & 1) != yParity ||
        coordinate.x + 1 >= Width || coordinate.y + 1 >= Height)
    {
        return;
    }
    uint2 upper = orientation == 0 ? coordinate : coordinate + uint2(1, 0);
    uint2 lower = orientation == 0 ? coordinate + uint2(1, 1) : coordinate + uint2(0, 1);
    ResolveDiagonalPair(upper, lower);
}
