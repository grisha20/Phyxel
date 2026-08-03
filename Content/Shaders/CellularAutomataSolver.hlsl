#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<AirCell> Air : register(t1);
RWStructuredBuffer<GridCell> Grid : register(u0);
struct WaterPressureRouteData
{
    uint Route;
    uint SourceIndex;
};
// Shared with the solid-body flags. During the hydraulic phases the first ten
// rows store packed column bounds, transfer slots, and the pressure activity count.
RWStructuredBuffer<uint> WaterColumnState : register(u1);
RWStructuredBuffer<uint> PathBlockerMasks : register(u2);
RWStructuredBuffer<uint> CellMaterials : register(u3);
RWStructuredBuffer<WaterPressureRouteData> WaterPressureRoutes : register(u4);
RWStructuredBuffer<WaterPressureRouteData> WaterPressureRouteScratch : register(u5);
RWStructuredBuffer<GasMotionState> GasMotion : register(u6);
RWStructuredBuffer<uint> GasObstacleBypassStatistics : register(u7);
RWStructuredBuffer<GasAirImpulse> GasAirImpulses : register(u8);

static const uint SandRestThreshold = 30;
static const uint FluidRestThreshold = 60;
static const uint OrdinaryHorizontalSearch = 8;
static const uint OrdinarySurfaceBlockWidth = 2048;
static const uint OrdinaryLocalSurfaceBlockWidth = 256;
static const float OrdinaryLandingFrames = 1;
static const uint PathBlockerTileWidth = 32;
// Keeps the vertical connectivity scan bounded even for tall maps. Column
// balancing itself is deliberately limited to immediately adjacent columns.
static const uint HydraulicConnectionSearchDepth = 128;
static const uint HydraulicHeadRouteTolerance = 0;
// Keep pressure move/return hysteresis wider than the one-cell local balance
// tolerance so the two solvers cannot undo each other every frame.
static const uint HydraulicSurfaceTolerance = 2;
static const uint HydraulicTransfersPerColumn = 16;
static const uint HydraulicActivityRow = HydraulicTransfersPerColumn + 1;
static const uint PressureChannelHalfWidth = 32;
static const uint PressureChannelWallSpan = 4;
static const uint PressureRouteSourceBits = 11;
static const uint PressureRouteSourceMask = (1u << PressureRouteSourceBits) - 1u;
static const uint PressureRouteDistanceMask = 0x7fffffffu >> PressureRouteSourceBits;
static const uint PressureRouteReservation = 0x80000000u;

uint FarColumnMoveCounterIndex()
{
    return ((Width + PathBlockerTileWidth - 1) / PathBlockerTileWidth) * Height;
}

uint PressurePlanCounterIndex()
{
    return FarColumnMoveCounterIndex() + 1;
}

// --- Flame carried by the air field -----------------------------------------
// A flame cell on its own can only guess where to go, which is why it used to
// rise by an independent coin flip and looked like a swarm of sparks. The air
// field is smooth across neighbouring cells, so once every flame reads its
// drift from the same field they move as one body and the plume grows tongues.
// The Powder Toy does the same thing through Advection: part.vx += 0.9 * vx.

// These are The Powder Toy's own numbers rather than coefficients picked by
// eye, because every coefficient invented here so far has been wrong by an
// order of magnitude in one direction or the other.
//
// A particle there ends each frame at
//     v = v * Loss + Advection * v_air + Gravity
// with FIRE and SMKE both using Advection = 0.9 and Loss = 0.20, and FIRE
// carrying Gravity = -0.1, a negative value meaning it floats. Since Loss keeps
// only a fifth of the previous velocity, v settles at roughly
//     0.9 * v_air + Gravity / (1 - Loss)
// measured in cells per frame. Our cells move in whole steps, so that value
// becomes the probability of stepping one cell this frame -- the two agree on
// average, and no arbitrary scale factor is needed anywhere.
static const float GasAdvection = 0.9;

// Equal to the vertical response, as in The Powder Toy, where both axes share
// Advection 0.9.
//
// This was held far lower for a while, down to 0.06, in an attempt to keep the
// plume narrow. That was a mistake: the width being measured belonged to the
// glow field, which had no upper bound and bled its surplus outward, and a
// leftover diagonal pass was moving flame sideways without consulting this
// value at all. With the glow clamped, that pass removed, the substep seed
// fixed and convection cut fourfold, none of those reasons survive.
//
// Keeping it low also produced a plainly wrong behaviour. When a plume meets an
// obstacle the pressure underneath drives the air sideways and downward. At a
// fifteen to one split the flame could barely follow the sideways flow yet
// followed the downward one at full strength, so it dived under the plate
// instead of spreading along it. Air is isotropic; the response to it has to be
// isotropic too.
static const float GasLateralAdvection = 0.9;

// Gravity -0.1 divided by (1 - Loss) = 0.8. What a candle does in still air.
static const float FlameOwnRise = 0.125;
// The carrier can consume one cell in each of eight motion passes. Each pass
// admits its intended move with a maximum 0.9 probability, so 8 * 0.9 = 7.2
// cells per fixed tick is the structural ceiling. This is not a visual cap.
static const float GasMaximumSpeed = 7.2;
// A cellular row has one slot per pixel, unlike TPT's particles with
// independent fractional coordinates.  At a solid surface a compact FIRE
// column therefore needs a very short queue transfer to hand its leading
// particle to the first available slot instead of remaining a jammed ruler.
static const uint GasSurfaceQueueSearch = 3;
// Gas velocity is measured directly in simulation cells per fixed tick, the
// same unit used by the air field and TPT's particle update.  Do not use a
// visual speed multiplier here: it makes FIRE look like a plasma jet and
// hides, rather than fixes, missing surface flow.
static const float GasAirVelocityScale = 1.0;

// FIRE and SMKE both have Gravity = -0.1 in The Powder Toy.  The registry's
// buoyancy sign is also used by the continuum pass, so use its positive sign
// only as the compact marker for the two light, rising gases.  CO2 remains a
// passive/heavy gas and is still moved by the air field and density ordering.
float GasOwnRise(uint materialId)
{
    if ((Materials[materialId].Flags & MaterialFlagFlame) != 0)
    {
        return FlameOwnRise;
    }
    return Materials[materialId].GasBuoyancy > 0 ? FlameOwnRise : 0.0;
}

float2 FlameAirDrift(uint2 coordinate)
{
    uint airWidth = (Width + AirCellSize - 1) / AirCellSize;
    uint airHeight = (Height + AirCellSize - 1) / AirCellSize;
    uint2 airCoordinate = min(
        coordinate / AirCellSize,
        uint2(airWidth - 1, airHeight - 1));
    AirCell air = Air[airCoordinate.y * airWidth + airCoordinate.x];
    return float2(air.VelocityX, air.VelocityY);
}

uint CellKind(GridCell cell)
{
    return cell.IsActive == 0 ? 0 : Materials[cell.MaterialIndex].SimulationKind;
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
    return Materials[cell.MaterialIndex].Density;
}

bool IsSolid(GridCell cell)
{
    return CellKind(cell) == 2;
}

// Weight bands from The Powder Toy: FIRE is 2, SMKE, WTRV and CO2 are 1.
// can_move is zero when the mover's weight does not exceed the target's, so
// flame displaces any ordinary gas while gases never displace each other --
// their layering by density stays the gas solver's responsibility.
// Declared here rather than beside the flame constants because it needs
// CellKindFromMaterial, and HLSL resolves functions strictly top to bottom.
bool GasCanEnter(uint moverMaterial, uint targetMaterial)
{
    uint targetKind = CellKindFromMaterial(targetMaterial);
    if (targetKind == SimulationKindNone)
    {
        return true;
    }
    if (targetKind != SimulationKindGas)
    {
        return false;
    }
    bool moverFlame = (Materials[moverMaterial].Flags & MaterialFlagFlame) != 0;
    bool targetFlame = (Materials[targetMaterial].Flags & MaterialFlagFlame) != 0;
    return moverFlame && !targetFlame;
}

void MarkMovement(inout GridCell first, inout GridCell second, float horizontal, float vertical)
{
    first.RestFrames = 0;
    second.RestFrames = 0;
    first.VelocityX = horizontal;
    first.VelocityY = vertical;
    second.VelocityX = -horizontal;
    second.VelocityY = -vertical;
    if (HydraulicPressure == 0)
    {
        if (CellKind(first) == 4) first.Pressure = 0;
        if (CellKind(second) == 4) second.Pressure = 0;
    }
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

// Gas passes know which cell is the mover. The generic SwapCells API predates
// them and stores the supplied velocity on its first argument before swapping;
// gas calls supplied (target, source), so the moved particle received the
// opposite marker. A right-moving FIRE consequently inherited "move left" and
// returned on the next tick, making obstacle bypass visually cancel itself.
// Keep the generic helper untouched for water/granular physics and give gas an
// explicit source -> target operation.
// TPT's AirDrag is applied when a particle actually traverses space. Record
// the successful one-cell carrier step in the source air slot; CSInject
// consumes this fixed-point impulse on the following air tick.
void RecordGasAirStep(uint sourceIndex, uint targetIndex)
{
    int sourceX = int(sourceIndex % Width);
    int sourceY = int(sourceIndex / Width);
    int targetX = int(targetIndex % Width);
    int targetY = int(targetIndex / Width);
    int stepX = targetX - sourceX;
    int stepY = targetY - sourceY;
    if (stepX == 0 && stepY == 0)
    {
        return;
    }

    uint airWidth = (Width + AirCellSize - 1) / AirCellSize;
    uint airIndex = (uint(sourceY) / AirCellSize) * airWidth + uint(sourceX) / AirCellSize;
    int ignored;
    if (stepX != 0)
    {
        InterlockedAdd(GasAirImpulses[airIndex].X, stepX, ignored);
    }
    if (stepY != 0)
    {
        InterlockedAdd(GasAirImpulses[airIndex].Y, stepY, ignored);
    }
}

void MoveGasCell(
    uint sourceIndex,
    uint targetIndex)
{
    GridCell mover = Grid[sourceIndex];
    GridCell displaced = Grid[targetIndex];
    GasMotionState moverMotion = GasMotion[sourceIndex];
    GasMotionState displacedMotion = GasMotion[targetIndex];

    RecordGasAirStep(sourceIndex, targetIndex);

    mover.RestFrames = 0;
    displaced.RestFrames = 0;

    Grid[sourceIndex] = displaced;
    Grid[targetIndex] = mover;
    GasMotion[sourceIndex] = displacedMotion;
    GasMotion[targetIndex] = moverMotion;
    // Publish from the authoritative cells instead of swapping possibly stale
    // cache entries. Later gas phases in this same tick must see the move.
    CellMaterials[sourceIndex] = displaced.IsActive != 0
        ? displaced.MaterialIndex
        : 0;
    CellMaterials[targetIndex] = mover.MaterialIndex;
}

void ExchangeGranularWithLiquid(
    uint granularIndex,
    uint liquidIndex,
    float horizontal,
    float vertical)
{
    GridCell granular = Grid[granularIndex];
    GridCell liquid = Grid[liquidIndex];

    granular.RestFrames = 0;
    granular.VelocityX = horizontal;
    granular.VelocityY = vertical;

    liquid.RestFrames = 0;
    liquid.VelocityX = 0;
    liquid.VelocityY = 0;
    liquid.BodyId = 0;
    if (HydraulicPressure == 0)
    {
        liquid.Pressure = 0;
    }

    Grid[granularIndex] = liquid;
    Grid[liquidIndex] = granular;
    CellMaterials[granularIndex] = liquid.MaterialIndex;
    CellMaterials[liquidIndex] = granular.MaterialIndex;
}

void TransferLiquidMass(
    uint sourceIndex,
    GridCell source,
    uint destinationIndex,
    GridCell destination,
    float amount)
{
    float sourceMass = source.Mass;
    float destinationMass = destination.Mass;
    float transferred = min(amount, min(sourceMass, max(0, 1.0 - destinationMass)));
    if (transferred <= 0.000001)
    {
        return;
    }

    float heatCapacity = max(0.01, Materials[source.MaterialIndex].HeatCapacity);
    float destinationEnergy = destinationMass * heatCapacity * destination.Temperature +
        transferred * heatCapacity * source.Temperature;
    float destinationLifetime = destinationMass * destination.Lifetime +
        transferred * source.Lifetime;
    source.Mass = sourceMass - transferred;
    destination.Mass = destinationMass + transferred;
    destination.Temperature = destinationEnergy /
        max(0.000001, destination.Mass * heatCapacity);
    destination.Lifetime = destinationLifetime / max(0.000001, destination.Mass);
    destination.VelocityX = 0;
    destination.VelocityY = 0;
    destination.BodyId = 0;
    destination.RestFrames = 0;
    if (HydraulicPressure == 0)
    {
        destination.Pressure = 0;
    }

    if (source.Mass <= 0.000001)
    {
        source = CreateEmptyCell();
    }
    else
    {
        source.VelocityX = 0;
        source.VelocityY = 0;
        source.BodyId = 0;
        source.RestFrames = 0;
        if (HydraulicPressure == 0)
        {
            source.Pressure = 0;
        }
    }
    Grid[sourceIndex] = source;
    Grid[destinationIndex] = destination;
    CellMaterials[sourceIndex] = source.IsActive != 0 ? source.MaterialIndex : 0;
    CellMaterials[destinationIndex] = destination.MaterialIndex;
}

bool ConsolidateLiquidDown(uint upperIndex, uint lowerIndex)
{
    GridCell upper = Grid[upperIndex];
    GridCell lower = Grid[lowerIndex];
    if (upper.IsActive == 0 || lower.IsActive == 0 ||
        Materials[upper.MaterialIndex].SimulationKind != SimulationKindLiquid ||
        upper.MaterialIndex != lower.MaterialIndex ||
        upper.Mass <= 0.000001 || lower.Mass >= 0.999999)
    {
        return false;
    }
    TransferLiquidMass(upperIndex, upper, lowerIndex, lower, 1.0 - lower.Mass);
    return true;
}

bool ConsolidateLiquidSide(uint leftIndex, uint rightIndex)
{
    GridCell left = Grid[leftIndex];
    GridCell right = Grid[rightIndex];
    if (left.IsActive == 0 || right.IsActive == 0 ||
        Materials[left.MaterialIndex].SimulationKind != SimulationKindLiquid ||
        left.MaterialIndex != right.MaterialIndex ||
        left.Mass <= 0.000001 || right.Mass <= 0.000001 ||
        left.Mass >= 0.999999 || right.Mass >= 0.999999)
    {
        return false;
    }
    bool preferRight = HashUnitFloat(leftIndex ^ 0x68bc21ebu) < 0.5;
    if (preferRight)
    {
        TransferLiquidMass(leftIndex, left, rightIndex, right, 1.0 - right.Mass);
    }
    else
    {
        TransferLiquidMass(rightIndex, right, leftIndex, left, 1.0 - left.Mass);
    }
    return true;
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

bool GranularCanDisplaceLiquid(uint2 coordinate, uint granularIndex, uint liquidMaterial)
{
    GridCell granular = Grid[granularIndex];
    if (CellKindFromMaterial(liquidMaterial) != SimulationKindLiquid ||
        CellRankFromMaterial(granular.MaterialIndex) <= CellRankFromMaterial(liquidMaterial) ||
        granular.RestFrames >= SandRestThreshold)
    {
        return false;
    }

    // An unsupported grain has just entered a liquid column even if the brush
    // created it with zero velocity. Once it reaches granular/solid support it
    // may keep forming a slope only while it still carries a falling impulse.
    bool supported = SandSupported(coordinate);
    bool hasFallingImpulse = granular.VelocityY > 8;
    return !supported || hasFallingImpulse;
}

bool GranularCanMoveTo(
    uint2 source,
    uint sourceIndex,
    uint2 destination,
    uint targetMaterial)
{
    uint targetKind = CellKindFromMaterial(targetMaterial);
    if (targetKind == SimulationKindSolid ||
        CellRankFromMaterial(Grid[sourceIndex].MaterialIndex) <= CellRankFromMaterial(targetMaterial))
    {
        return false;
    }

    // Powders never sink through other powders, even a much lighter one.
    // The Powder Toy achieves this by quantising Weight into bands and giving
    // every powder the same band (SAND 90, BCOL 90, STNE 90), because
    // can_move is 0 whenever the mover's weight is not greater than the
    // target's. Comparing continuous density instead made stone coal (1.4)
    // drop straight through a charcoal pile (0.2) the instant it was poured,
    // which is not how a heap of grains behaves without vibration.
    // Powder versus liquid and gas stays density-driven: that is what keeps
    // sand sinking, dry charcoal floating, and the whole water gate intact.
    if (targetKind == SimulationKindGranular)
    {
        return false;
    }

    bool diagonal = source.x != destination.x;
    if (diagonal)
    {
        uint2 firstCorner = uint2(source.x, destination.y);
        uint2 secondCorner = uint2(destination.x, source.y);
        if (CellKindAt(firstCorner) == SimulationKindSolid &&
            CellKindAt(secondCorner) == SimulationKindSolid)
        {
            return false;
        }
    }

    if (targetKind == SimulationKindLiquid)
    {
        return GranularCanDisplaceLiquid(source, sourceIndex, targetMaterial);
    }

    return !diagonal || SandSupported(source);
}

bool GranularCanSpreadThroughLiquidSide(
    uint2 coordinate,
    uint granularMaterial,
    uint liquidMaterial)
{
    if (CellKindFromMaterial(granularMaterial) != SimulationKindGranular ||
        CellKindFromMaterial(liquidMaterial) != SimulationKindLiquid ||
        CellRankFromMaterial(granularMaterial) >= CellRankFromMaterial(liquidMaterial))
    {
        return false;
    }
    bool stackedAbove = coordinate.y > 0 &&
        CellMaterials[FlattenCoordinate(coordinate - uint2(0, 1))] == granularMaterial;
    bool stackedBelow = coordinate.y + 1 < Height &&
        CellMaterials[FlattenCoordinate(coordinate + uint2(0, 1))] == granularMaterial;
    return stackedAbove || stackedBelow;
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
    uint targetKind = CellKindFromMaterial(targetMaterial);
    // Same rule as GranularCanMoveTo: a grain never rolls into another powder,
    // only into empty space. Without this a heavy powder would burrow sideways
    // through a lighter heap instead of resting on its slope.
    if (targetKind == SimulationKindLiquid ||
        targetKind == SimulationKindGranular ||
        CellRankFromMaterial(sandMaterial) <= CellRankFromMaterial(targetMaterial))
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

bool OrdinaryWaterCanLeaveLedge(uint2 source)
{
    uint sourceIndex = FlattenCoordinate(source);
    return HydraulicPressure == 0 &&
        !IsWaterAt(int(source.x), int(source.y) - 1) &&
        abs(Grid[sourceIndex].VelocityY) <= 8 &&
        Grid[sourceIndex].Pressure >= OrdinaryLandingFrames;
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
    if (!WaterCanEnter(waterMaterial, targetMaterial))
    {
        return false;
    }
    if (!WaterSupported(destination))
    {
        return stride == 1 && OrdinaryWaterCanLeaveLedge(source);
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
    if (!WaterCanEnter(waterMaterial, targetMaterial))
    {
        return false;
    }
    if (!WaterSupported(destination))
    {
        return stride == 1 && OrdinaryWaterCanLeaveLedge(source);
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

bool FindOrdinaryWaterDestination(
    uint2 source,
    uint waterMaterial,
    int direction,
    out uint2 destination)
{
    destination = source;
    if (HydraulicPressure != 0 || !WaterSupported(source))
    {
        return false;
    }

    uint sourceIndex = FlattenCoordinate(source);
    bool hasWaterAbove = IsWaterAt(int(source.x), int(source.y) - 1);
    bool supportedByFallingWater = false;
    if (source.y + 1 < Height)
    {
        uint belowIndex = sourceIndex + Width;
        supportedByFallingWater = CellKindAtIndex(belowIndex) == 4 &&
            abs(Grid[belowIndex].VelocityY) > 8;
    }
    if (Grid[sourceIndex].Pressure < OrdinaryLandingFrames ||
        hasWaterAbove || supportedByFallingWater ||
        abs(Grid[sourceIndex].VelocityY) > 8)
    {
        return false;
    }

    // A surface edge is pushed by water behind or directly below it. The latter
    // lets the narrow apex of a mound drain away, while an isolated droplet on
    // a solid floor does not skate forever.
    bool hasWaterBehind = IsWaterAt(int(source.x) - direction, int(source.y));
    bool hasWaterBelow = IsWaterAt(int(source.x), int(source.y) + 1);
    if (!hasWaterBehind && !hasWaterBelow)
    {
        return false;
    }

    bool found = false;
    for (uint distance = 1; distance <= OrdinaryHorizontalSearch; distance++)
    {
        int x = int(source.x) + direction * int(distance);
        if (x < 0 || x >= int(Width))
        {
            break;
        }
        uint2 candidate = uint2(x, source.y);
        uint candidateMaterial = CellMaterials[FlattenCoordinate(candidate)];
        uint candidateKind = CellKindFromMaterial(candidateMaterial);

        // Check every traversed cell. Water, solids, granular matter and every
        // other material terminate the search; nothing can be jumped over.
        if (candidateKind != 0 && candidateKind != 5)
        {
            break;
        }

        if (!WaterCanEnter(waterMaterial, candidateMaterial))
        {
            break;
        }
        bool meaningfulDrop = WaterSupported(candidate);
        if (!meaningfulDrop && candidate.y + 2 < Height)
        {
            uint firstBelowKind = CellKindAt(candidate + uint2(0, 1));
            uint secondBelowKind = CellKindAt(candidate + uint2(0, 2));
            meaningfulDrop = (firstBelowKind == 0 || firstBelowKind == 5) &&
                (secondBelowKind == 0 || secondBelowKind == 5);
        }
        if (meaningfulDrop)
        {
            destination = candidate;
            found = true;
        }
    }
    return found;
}

void MoveOrdinaryWater(uint sourceIndex, uint destinationIndex, int direction)
{
    GridCell water = Grid[sourceIndex];
    GridCell target = Grid[destinationIndex];
    MarkMovement(water, target, direction * 58, 0);
    water.BodyId = FrameIndex + 1;
    Grid[sourceIndex] = target;
    Grid[destinationIndex] = water;
    uint targetMaterial = CellMaterials[destinationIndex];
    CellMaterials[sourceIndex] = targetMaterial;
    CellMaterials[destinationIndex] = water.MaterialIndex;
}

void ResolveOrdinaryWaterBlock(uint2 coordinate)
{
    if (HydraulicPressure != 0 || coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    uint blockStart = (coordinate.x / 16) * 16;
    uint lane = SimulationPhase - 48;
    if (coordinate.x != blockStart + lane || blockStart + 8 >= Width)
    {
        return;
    }

    uint2 left = uint2(blockStart + lane, coordinate.y);
    uint2 right = uint2(blockStart + lane + 8, coordinate.y);
    uint leftIndex = FlattenCoordinate(left);
    uint rightIndex = FlattenCoordinate(right);
    uint leftKind = CellKindAtIndex(leftIndex);
    uint rightKind = CellKindAtIndex(rightIndex);
    if ((leftKind == 4) == (rightKind == 4))
    {
        return;
    }

    uint2 source = leftKind == 4 ? left : right;
    uint sourceIndex = leftKind == 4 ? leftIndex : rightIndex;
    int direction = leftKind == 4 ? 1 : -1;
    uint2 destination;
    if (!FindOrdinaryWaterDestination(
        source,
        CellMaterials[sourceIndex],
        direction,
        destination))
    {
        return;
    }
    MoveOrdinaryWater(sourceIndex, FlattenCoordinate(destination), direction);
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
        PathBlockerMasks[PressurePlanCounterIndex()] = 0;
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

// --- Gas and flame motion -----------------------------------------------
// Fire and the ordinary gases are one medium, not separate effects. In The
// Powder Toy SMKE carries Advection 0.9, exactly the same as FIRE, and CO2
// carries 2.0: the air moves the whole gas layer as a single body, which is why
// a plume reads as one tongue rather than a spray of grains.
//
// TPT particles retain a fractional position and update their velocity once
// per frame.  A stochastic neighbour hop cannot reproduce that: it turns a
// smooth, symmetric air field into a random walk.  The separate GasMotion
// buffer is the missing per-particle state while GridCell remains the stable
// serialized 40-byte world format.

void IntegrateGasMotion(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = Grid[index];
    if (cell.IsActive == 0 || Materials[cell.MaterialIndex].SimulationKind != SimulationKindGas)
    {
        GasMotion[index] = (GasMotionState)0;
        return;
    }

    GasMotionState state = GasMotion[index];
    // A cell which has already collided with the underside of a solid carries
    // a pending normal segment (OffsetY == -1).  Letting the coarse air field
    // overwrite its just-selected tangent on the very next tick made the
    // direction flip toward the centre of a symmetric plume: a one-cell
    // right-hand escape was immediately pulled left again, so the front never
    // travelled along the plate.  A particle in TPT keeps its collision
    // fallback direction until it has cleared the surface.  Retain that
    // tangent only while the immediate upper cell is still solid, then return
    // to ordinary air advection at the plate edge.
    // OffsetY==0.5 is an internal, one-tick surface-carrier marker.  It is
    // deliberately neither a pending upward nor downward cell step, so the
    // four checkerboard collision phases cannot move the same particle twice
    // in one 60 Hz tick after it changes parity.
    bool surfaceCarrierMarker = abs(state.OffsetY - 0.5) < 0.001 &&
        abs(state.VelocityX) >= GasAdvection;
    bool carriesAlongSurface = coordinate.y > 0 &&
        (state.OffsetY <= -1.0 || surfaceCarrierMarker);
    if (carriesAlongSurface)
    {
        GridCell aboveCell = Grid[index - Width];
        carriesAlongSurface = aboveCell.IsActive != 0 &&
            Materials[aboveCell.MaterialIndex].SimulationKind == SimulationKindSolid;
    }
    if (carriesAlongSurface && abs(state.VelocityX) > 0.0001)
    {
        state.VelocityX = state.VelocityX > 0 ? GasAdvection : -GasAdvection;
        state.VelocityY = 0;
        state.OffsetX = 0;
        state.OffsetY = -1.0;
        GasMotion[index] = state;
        return;
    }

    float2 drift = FlameAirDrift(coordinate);
    float2 gravity = float2(0, -GasOwnRise(cell.MaterialIndex));
    state.VelocityX = clamp(
        state.VelocityX * 0.20 + drift.x * GasAdvection * GasAirVelocityScale + gravity.x,
        -GasMaximumSpeed,
        GasMaximumSpeed);
    state.VelocityY = clamp(
        state.VelocityY * 0.20 + drift.y * GasAdvection * GasAirVelocityScale + gravity.y,
        -GasMaximumSpeed,
        GasMaximumSpeed);
    state.OffsetX = clamp(
        state.OffsetX + state.VelocityX,
        -float(GasMotionSubSteps),
        float(GasMotionSubSteps));
    state.OffsetY = clamp(
        state.OffsetY + state.VelocityY,
        -float(GasMotionSubSteps),
        float(GasMotionSubSteps));
    GasMotion[index] = state;
}

void ConsumeGasOffset(uint index, int stepX, int stepY)
{
    GasMotionState state = GasMotion[index];
    state.OffsetX -= float(stepX);
    state.OffsetY -= float(stepY);
    GasMotion[index] = state;
}

void StopGasMotion(uint index)
{
    GasMotion[index] = (GasMotionState)0;
}

bool GasPairBranch(uint upperKind, uint lowerKind)
{
    return (upperKind == SimulationKindNone || upperKind == SimulationKindGas) &&
        (lowerKind == SimulationKindNone || lowerKind == SimulationKindGas) &&
        (upperKind == SimulationKindGas || lowerKind == SimulationKindGas);
}

void ResolveGasVerticalPair(uint2 upperCoordinate)
{
    if (upperCoordinate.y + 1 >= Height)
    {
        return;
    }
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = upperIndex + Width;
    GridCell upperCell = Grid[upperIndex];
    GridCell lowerCell = Grid[lowerIndex];
    uint upperMaterial = upperCell.IsActive != 0 ? upperCell.MaterialIndex : 0;
    uint lowerMaterial = lowerCell.IsActive != 0 ? lowerCell.MaterialIndex : 0;
    uint upperKind = CellKindFromMaterial(upperMaterial);
    uint lowerKind = CellKindFromMaterial(lowerMaterial);
    if (!GasPairBranch(upperKind, lowerKind))
    {
        return;
    }

    // Each cell reads the air where it actually is. Sampling one fixed cell of
    // the pair looked harmless but was not: four simulation cells share one air
    // cell, so roughly every fourth pair straddles a boundary and the mover
    // ended up steered by its neighbour's flow instead of its own. The error
    // always favoured the same side, which is enough to curl a plume into a
    // hook rather than letting it spread evenly.
    uint2 lowerCoordinate = uint2(upperCoordinate.x, upperCoordinate.y + 1);

    if (lowerKind == SimulationKindGas && GasCanEnter(lowerMaterial, upperMaterial))
    {
        GasMotionState lowerMotion = GasMotion[lowerIndex];
        if (lowerMotion.OffsetY <= -1.0)
        {
            ConsumeGasOffset(lowerIndex, 0, -1);
            MoveGasCell(lowerIndex, upperIndex);
        }
        return;
    }
    if (upperKind == SimulationKindGas && GasCanEnter(upperMaterial, lowerMaterial))
    {
        GasMotionState upperMotion = GasMotion[upperIndex];
        if (upperMotion.OffsetY >= 1.0)
        {
            ConsumeGasOffset(upperIndex, 0, 1);
            MoveGasCell(upperIndex, lowerIndex);
        }
    }
}

// Обход препятствия. Частица в The Powder Toy, которой перекрыли путь, не
// останавливается: она выбирает сторону случайным r = +-1 и пробует два
// повёрнутых диагональных направления (Simulation.cpp, MovementPhase).
// Это не диффузия — в свободном пространстве случайности нет вовсе, выбор
// возникает только при столкновении.
//
// Без этого пламя, упёршееся в пластину снизу, могло растечься вдоль неё лишь
// за счёт бокового ветра грубой сетки: вертикальная пара видела solid,
// отклоняла ход и на этом останавливалась. В TPT же множество частиц у
// препятствия статистически делится в обе стороны, и получается тот самый
// симметричный веер вдоль пластины.
void ResolveGasObstacleBypass(uint2 coordinate)
{
    if (coordinate.y == 0)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    // Grid is authoritative here. CellMaterials is a shared acceleration map
    // and can legitimately be repurposed/one pass behind after brushing or a
    // solid-body pass; using it as the collision source made this function
    // return before it ever saw the FIRE particle.
    GridCell sourceCell = Grid[index];
    if (sourceCell.IsActive == 0)
    {
        return;
    }
    uint material = sourceCell.MaterialIndex;
    if (Materials[material].SimulationKind != SimulationKindGas)
    {
        return;
    }

    // Collision is a local event.  The previous scan through an entire gas
    // plug made every cell below the plate believe it had already struck the
    // metal; that manufactured a shelf rather than TPT's surface flow.
    GasMotionState motion = GasMotion[index];
    if (motion.OffsetY > -1.0)
    {
        return;
    }
    uint aboveIndex = index - Width;
    GridCell aboveCell = Grid[aboveIndex];
    if (aboveCell.IsActive == 0 ||
        Materials[aboveCell.MaterialIndex].SimulationKind != SimulationKindSolid)
    {
        return;
    }
    uint ignored;
    InterlockedAdd(GasObstacleBypassStatistics[0], 1, ignored);

    // TPT only attempts the X leg when the blocked particle actually had an
    // X intention.  A purely vertical collision must not acquire a random
    // sideways move here; its sign is exactly the sign of VelocityX.
    if (abs(motion.VelocityX) > 0.0001)
    {
        int direction = motion.VelocityX > 0.0 ? 1 : -1;
        int targetX = int(coordinate.x) + direction;
        if (targetX >= 0 && targetX < int(Width))
        {
            uint sideIndex = FlattenCoordinate(uint2(uint(targetX), coordinate.y));
            GridCell sideCell = Grid[sideIndex];
            uint sideMaterial = sideCell.IsActive != 0 ? sideCell.MaterialIndex : 0;
            if (GasCanEnter(material, sideMaterial))
            {
                InterlockedAdd(GasObstacleBypassStatistics[1], 1, ignored);
                StopGasMotion(index);
                MoveGasCell(index, sideIndex);
                return;
            }
        }
    }

    // Keep the Y-only retry explicit in the staircase.  For an upward solid
    // collision it normally fails, but it must precede the diagonal turns.
    if (GasCanEnter(material, aboveCell.MaterialIndex))
    {
        InterlockedAdd(GasObstacleBypassStatistics[2], 1, ignored);
        StopGasMotion(index);
        MoveGasCell(index, aboveIndex);
        return;
    }

    // Diagonal ordering is random and equiprobable.  The second side is still
    // tested if the first is occupied, so packing cannot impose a permanent
    // left/right preference.
    int firstDiagonalDirection = HashUnitFloat((index * 0x85ebca6bu) ^
        (FrameIndex * 0xc2b2ae35u)) < 0.5 ? -1 : 1;
    [unroll]
    for (int attempt = 0; attempt < 2; attempt++)
    {
        int direction = attempt == 0 ? firstDiagonalDirection : -firstDiagonalDirection;
        int targetX = int(coordinate.x) + direction;
        if (targetX < 0 || targetX >= int(Width))
        {
            continue;
        }
        uint diagonalIndex = FlattenCoordinate(uint2(uint(targetX), coordinate.y - 1));
        GridCell diagonalCell = Grid[diagonalIndex];
        uint diagonalMaterial = diagonalCell.IsActive != 0 ? diagonalCell.MaterialIndex : 0;
        if (GasCanEnter(material, diagonalMaterial))
        {
            InterlockedAdd(GasObstacleBypassStatistics[3], 1, ignored);
            StopGasMotion(index);
            MoveGasCell(index, diagonalIndex);
            return;
        }
    }
    InterlockedAdd(GasObstacleBypassStatistics[4], 1, ignored);
}

void ResolveGasHorizontalPair(uint2 leftCoordinate)
{
    if (leftCoordinate.x + 1 >= Width)
    {
        return;
    }
    uint leftIndex = FlattenCoordinate(leftCoordinate);
    uint rightIndex = leftIndex + 1;
    GridCell leftCell = Grid[leftIndex];
    GridCell rightCell = Grid[rightIndex];
    uint leftMaterial = leftCell.IsActive != 0 ? leftCell.MaterialIndex : 0;
    uint rightMaterial = rightCell.IsActive != 0 ? rightCell.MaterialIndex : 0;
    uint leftKind = CellKindFromMaterial(leftMaterial);
    uint rightKind = CellKindFromMaterial(rightMaterial);
    if (!GasPairBranch(leftKind, rightKind))
    {
        return;
    }

    // Sideways drift comes purely from the air, so nothing moves sideways
    // unless something is actually pushing it. This applies to every gas, not
    // only flame: smoke that stayed put while the fire blew sideways was the
    // clearest sign the two were not being treated as one medium.
    // Same rule as the vertical pass: each cell reads the air under itself.
    bool leftMoves = leftKind == SimulationKindGas &&
        GasCanEnter(leftMaterial, rightMaterial) &&
        GasMotion[leftIndex].OffsetX >= 1.0;
    if (leftMoves)
    {
        ConsumeGasOffset(leftIndex, 1, 0);
        MoveGasCell(leftIndex, rightIndex);
        return;
    }
    if (rightKind == SimulationKindGas && GasCanEnter(rightMaterial, leftMaterial))
    {
        if (GasMotion[rightIndex].OffsetX > -1.0)
        {
            return;
        }
        ConsumeGasOffset(rightIndex, -1, 0);
        MoveGasCell(rightIndex, leftIndex);
    }
}

// Anything that reaches the left, right or top edge is swallowed, so a plume
// leaves the world instead of piling up against an invisible lid. Without this
// the scene simply filled with fire and nothing could be judged. The floor is
// deliberately left alone: a sandbox needs a ground to build on.
void ClearOpenBoundary(uint2 coordinate)
{
    if (OpenBoundaries == 0)
    {
        return;
    }
    bool onBoundary =
        coordinate.x < OpenBoundaryMargin ||
        coordinate.x + OpenBoundaryMargin >= Width ||
        coordinate.y < OpenBoundaryMargin;
    if (!onBoundary)
    {
        return;
    }
    uint index = FlattenCoordinate(coordinate);
    if (Grid[index].IsActive == 0)
    {
        return;
    }
    Grid[index] = CreateEmptyCell();
    CellMaterials[index] = 0;
}

void ResolveVerticalPair(uint2 upperCoordinate)
{
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = upperIndex + Width;
    uint upperMaterial = CellMaterials[upperIndex];
    uint lowerMaterial = CellMaterials[lowerIndex];
    uint upperKind = CellKindFromMaterial(upperMaterial);
    uint lowerKind = CellKindFromMaterial(lowerMaterial);
    if (upperKind == SimulationKindLiquid && lowerKind == SimulationKindLiquid &&
        upperMaterial == lowerMaterial && ConsolidateLiquidDown(upperIndex, lowerIndex))
    {
        return;
    }
    if (upperKind == 2 || lowerKind == 2)
    {
        return;
    }
    if ((upperKind == 0 || upperKind == 5) &&
        (lowerKind == 0 || lowerKind == 5) &&
        (upperKind == 5 || lowerKind == 5))
    {
        // Gas motion is handled by its own passes, which run several times per
        // frame. Doing it here as well would move a gas cell an extra step and
        // tie its speed to the water schedule.
        return;
    }
    bool canMove = upperKind == SimulationKindGranular
        ? GranularCanMoveTo(
            upperCoordinate,
            upperIndex,
            upperCoordinate + uint2(0, 1),
            lowerMaterial)
        : upperKind != SimulationKindNone &&
            CellRankFromMaterial(upperMaterial) > CellRankFromMaterial(lowerMaterial);
    if (canMove)
    {
        if (upperKind == SimulationKindGranular && lowerKind == SimulationKindLiquid)
        {
            ExchangeGranularWithLiquid(upperIndex, lowerIndex, 0, 60);
            return;
        }
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
    if (upperKind == SimulationKindLiquid && lowerKind == SimulationKindLiquid &&
        upperMaterial == lowerMaterial && ConsolidateLiquidDown(upperIndex, lowerIndex))
    {
        return;
    }
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
        // Gas motion, flame included, belongs to the dedicated gas passes.
        //
        // This branch used to keep its own random diagonal hop for flame, left
        // behind when the vertical and horizontal branches were moved out. It
        // ran in parallel with the new passes, moved flame up and sideways at
        // once, and never consulted GasLateralAdvection -- which is why
        // lowering that value from 0.9 to 0.22 and then to 0.06 barely changed
        // the width of the plume. The sideways spread was coming through here.
        return;
    }
    bool canMove = upperKind == SimulationKindGranular
        ? GranularCanMoveTo(upperCoordinate, upperIndex, lowerCoordinate, lowerMaterial)
        : upperKind == SimulationKindLiquid && WaterSupported(upperCoordinate) &&
            CellRankFromMaterial(upperMaterial) > CellRankFromMaterial(lowerMaterial);
    if (canMove)
    {
        float direction = lowerCoordinate.x > upperCoordinate.x ? 32 : -32;
        if (upperKind == SimulationKindGranular && lowerKind == SimulationKindLiquid)
        {
            ExchangeGranularWithLiquid(upperIndex, lowerIndex, direction, 48);
            return;
        }
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
    if (leftKind == SimulationKindLiquid && rightKind == SimulationKindLiquid &&
        leftMaterial == rightMaterial && ConsolidateLiquidSide(leftIndex, rightIndex))
    {
        return;
    }
    if (leftKind == 2 || rightKind == 2)
    {
        return;
    }
    if (leftKind == SimulationKindGranular && rightKind == SimulationKindLiquid &&
        GranularCanSpreadThroughLiquidSide(leftCoordinate, leftMaterial, rightMaterial))
    {
        ExchangeGranularWithLiquid(leftIndex, rightIndex, 24, 0);
        return;
    }
    if (rightKind == SimulationKindGranular && leftKind == SimulationKindLiquid &&
        GranularCanSpreadThroughLiquidSide(
            leftCoordinate + uint2(1, 0), rightMaterial, leftMaterial))
    {
        ExchangeGranularWithLiquid(rightIndex, leftIndex, -24, 0);
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
        // Handled by the dedicated gas passes, see ResolveGasHorizontalPair.
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
    // A deep connection only proves that two columns belong to the same body
    // of water; it does not prove that a surface particle can cross every wall
    // between distant columns. Keep this shortcut strictly local and let the
    // regular horizontal/diagonal passes carry water over longer distances.
    if (leftCoordinate.y != 0 || stride != 1)
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

bool FindOrdinarySurfaceDestination(
    uint sourceX,
    uint sourceTop,
    uint blockLeft,
    uint blockRight,
    out uint destinationX,
    out uint destinationTop)
{
    uint reachableLeft = sourceX;
    while (reachableLeft > blockLeft)
    {
        uint kind = CellKindAt(uint2(reachableLeft - 1, sourceTop));
        // Existing liquid is part of the open surface, not a wall. Rejecting it
        // made a wide pool level one adjacent swap at a time and left a broad
        // ripple behind. Solids and granular matter still stop the transfer, so
        // this cannot jump through a vessel wall or a sand bank.
        if (kind != 0 && kind != 4 && kind != 5)
        {
            break;
        }
        reachableLeft--;
    }
    uint reachableRight = sourceX;
    while (reachableRight < blockRight)
    {
        uint kind = CellKindAt(uint2(reachableRight + 1, sourceTop));
        if (kind != 0 && kind != 4 && kind != 5)
        {
            break;
        }
        reachableRight++;
    }

    uint rejected[4] = { Width, Width, Width, Width };
    for (uint attempt = 0; attempt < 4; attempt++)
    {
        destinationX = Width;
        destinationTop = 0;
        uint bestDistance = Width + 1;
        for (uint column = reachableLeft; column <= reachableRight; column++)
        {
            bool wasRejected = false;
            for (uint rejectedIndex = 0; rejectedIndex < attempt; rejectedIndex++)
            {
                wasRejected = wasRejected || rejected[rejectedIndex] == column;
            }
            uint top;
            uint ignoredBase;
            if (wasRejected ||
                !UnpackWaterColumn(WaterColumnState[column], top, ignoredBase) ||
                top <= sourceTop + 1)
            {
                continue;
            }
            uint destinationKind = CellKindAt(uint2(column, top - 1));
            if (destinationKind != 0 && destinationKind != 5)
            {
                continue;
            }
            uint distance = max(sourceX, column) - min(sourceX, column);
            if (destinationX == Width || top > destinationTop ||
                (top == destinationTop && distance < bestDistance))
            {
                destinationX = column;
                destinationTop = top;
                bestDistance = distance;
            }
        }
        if (destinationX == Width)
        {
            return false;
        }

        // Only the selected candidate pays for a vertical scan. Water droplets
        // in the open shaft are harmless; solid and granular shelves are true
        // blockers and force the search to try the next-lowest column.
        bool verticalPathClear = true;
        for (uint y = sourceTop; y < destinationTop; y++)
        {
            uint kind = CellKindAt(uint2(destinationX, y));
            if (kind != 0 && kind != 4 && kind != 5)
            {
                verticalPathClear = false;
                break;
            }
        }
        if (verticalPathClear)
        {
            return true;
        }
        rejected[attempt] = destinationX;
    }
    return false;
}

bool ResolveOrdinarySurfaceTransfer(uint blockLeft, uint blockRight)
{
    uint sourceLeft = Width;
    uint sourceRight = Width;
    uint sourceTop = Height;
    for (uint column = blockLeft; column <= blockRight; column++)
    {
        uint top;
        uint base;
        if (!UnpackWaterColumn(WaterColumnState[column], top, base))
        {
            continue;
        }
        uint surfaceIndex = FlattenCoordinate(uint2(column, top));
        GridCell surface = Grid[surfaceIndex];
        // Horizontal swaps reset the ordinary landing marker every frame even
        // when the column is already a stable pool. Vertical velocity is the
        // reliable distinction here: a falling stream is fast, a resting
        // surface is not.
        bool sourceReady = abs(surface.VelocityY) <= 8;
        if (sourceReady && (sourceLeft == Width || top < sourceTop))
        {
            sourceLeft = column;
            sourceRight = column;
            sourceTop = top;
        }
        else if (sourceReady && top == sourceTop)
        {
            sourceRight = column;
        }
    }
    if (sourceLeft == Width)
    {
        return false;
    }

    uint sourceCandidates[2] = { sourceLeft, sourceRight };
    uint sourceX = Width;
    uint destinationX = Width;
    uint destinationTop = 0;
    uint bestDistance = Width + 1;
    for (uint sourceCandidate = 0; sourceCandidate < 2; sourceCandidate++)
    {
        uint candidateSourceX = sourceCandidates[sourceCandidate];
        uint candidateDestinationX;
        uint candidateDestinationTop;
        if (!FindOrdinarySurfaceDestination(
            candidateSourceX,
            sourceTop,
            blockLeft,
            blockRight,
            candidateDestinationX,
            candidateDestinationTop))
        {
            continue;
        }
        uint distance = max(candidateSourceX, candidateDestinationX) -
            min(candidateSourceX, candidateDestinationX);
        if (sourceX == Width || candidateDestinationTop > destinationTop ||
            (candidateDestinationTop == destinationTop && distance < bestDistance))
        {
            sourceX = candidateSourceX;
            destinationX = candidateDestinationX;
            destinationTop = candidateDestinationTop;
            bestDistance = distance;
        }
    }
    if (sourceX == Width || destinationX == Width)
    {
        return false;
    }
    uint ignoredTop;
    uint sourceBase;
    uint destinationBase;
    if (!UnpackWaterColumn(WaterColumnState[sourceX], ignoredTop, sourceBase) ||
        !UnpackWaterColumn(WaterColumnState[destinationX], ignoredTop, destinationBase))
    {
        return false;
    }
    uint sourceIndex = FlattenCoordinate(uint2(sourceX, sourceTop));
    uint destinationIndex = FlattenCoordinate(uint2(destinationX, destinationTop - 1));
    uint destinationSurfaceIndex = FlattenCoordinate(uint2(destinationX, destinationTop));
    uint destinationKind = CellKindAtIndex(destinationIndex);
    GridCell destinationSurface = Grid[destinationSurfaceIndex];
    if (CellKindAtIndex(sourceIndex) != 4 ||
        (destinationKind != 0 && destinationKind != 5) ||
        abs(destinationSurface.VelocityY) > 8)
    {
        return false;
    }
    int direction = destinationX > sourceX ? 1 : -1;
    MoveOrdinaryWater(sourceIndex, destinationIndex, direction);
    WaterColumnState[sourceX] = sourceTop == sourceBase
        ? 0
        : PackWaterColumn(sourceTop + 1, sourceBase);
    WaterColumnState[destinationX] = PackWaterColumn(
        destinationTop - 1,
        destinationBase);
    return true;
}

void ResolveOrdinarySurfaceBlock(uint x)
{
    if (HydraulicPressure != 0)
    {
        return;
    }
    int blockStart;
    if ((FrameIndex & 1) == 0)
    {
        if ((x % OrdinarySurfaceBlockWidth) != 0)
        {
            return;
        }
        blockStart = int(x);
    }
    else
    {
        if (x == 0)
        {
            blockStart = -int(OrdinarySurfaceBlockWidth / 2);
        }
        else if ((x % OrdinarySurfaceBlockWidth) == OrdinarySurfaceBlockWidth / 2)
        {
            blockStart = int(x);
        }
        else
        {
            return;
        }
    }
    uint blockLeft = uint(max(blockStart, 0));
    uint blockRight = uint(min(
        blockStart + int(OrdinarySurfaceBlockWidth) - 1,
        int(Width) - 1));
    if (blockRight <= blockLeft)
    {
        return;
    }
    // Keep this serial pass short. Repeating a small budget over several frames
    // produces the same surface without a long single-frame GPU stall.
    uint transferBudget = 2;
    for (uint transfer = 0; transfer < transferBudget; transfer++)
    {
        if (!ResolveOrdinarySurfaceTransfer(blockLeft, blockRight))
        {
            break;
        }
    }
}

void ResolveOrdinaryLocalSurfaceBlock(uint x)
{
    if (HydraulicPressure != 0)
    {
        return;
    }
    int blockStart;
    if ((FrameIndex & 1) == 0)
    {
        if ((x % OrdinaryLocalSurfaceBlockWidth) != 0)
        {
            return;
        }
        blockStart = int(x);
    }
    else
    {
        if (x == 0)
        {
            blockStart = -int(OrdinaryLocalSurfaceBlockWidth / 2);
        }
        else if ((x % OrdinaryLocalSurfaceBlockWidth) ==
            OrdinaryLocalSurfaceBlockWidth / 2)
        {
            blockStart = int(x);
        }
        else
        {
            return;
        }
    }
    uint blockLeft = uint(max(blockStart, 0));
    uint blockRight = uint(min(
        blockStart + int(OrdinaryLocalSurfaceBlockWidth) - 1,
        int(Width) - 1));
    if (blockRight <= blockLeft)
    {
        return;
    }
    uint transferBudget = 1;
    for (uint transfer = 0; transfer < transferBudget; transfer++)
    {
        if (!ResolveOrdinarySurfaceTransfer(blockLeft, blockRight))
        {
            break;
        }
    }
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
    CellMaterials[destinationIndex] = water.MaterialIndex;
    if (HydraulicPressure != 0 &&
        max(sourceX, destinationX) - min(sourceX, destinationX) > 1)
    {
        uint ignored;
        InterlockedAdd(PathBlockerMasks[FarColumnMoveCounterIndex()], 1, ignored);
    }
    WaterColumnState[sourceX] = sourceTop == sourceBase
        ? 0
        : PackWaterColumn(sourceTop + 1, sourceBase);
    WaterColumnState[destinationX] = PackWaterColumn(destinationTop - 1, destinationBase);
}

uint PackPressureRoute(uint sourceX, uint distance)
{
    if (sourceX >= Width || sourceX + 1 > PressureRouteSourceMask)
    {
        return 0;
    }
    return (min(distance, PressureRouteDistanceMask) << PressureRouteSourceBits) |
        (sourceX + 1);
}

bool UnpackPressureRoute(uint route, out uint sourceX, out uint distance)
{
    if (route == 0 || (route & PressureRouteReservation) != 0)
    {
        sourceX = 0;
        distance = 0;
        return false;
    }
    uint encodedSource = route & PressureRouteSourceMask;
    if (encodedSource == 0)
    {
        sourceX = 0;
        distance = 0;
        return false;
    }
    sourceX = encodedSource - 1;
    distance = (route >> PressureRouteSourceBits) & PressureRouteDistanceMask;
    return sourceX < Width;
}

WaterPressureRouteData MakePressureRoute(uint sourceIndex, uint distance)
{
    WaterPressureRouteData result;
    result.Route = PackPressureRoute(sourceIndex % Width, distance);
    result.SourceIndex = sourceIndex;
    return result;
}

WaterPressureRouteData EmptyPressureRoute()
{
    WaterPressureRouteData result;
    result.Route = 0;
    result.SourceIndex = 0;
    return result;
}

bool WaterSurfaceHasStableAnchor(uint2 coordinate)
{
    if (coordinate.y == 0 || coordinate.y + 1 >= Height ||
        CellKindAt(coordinate) != 4 ||
        CellKindAt(coordinate - uint2(0, 1)) != 0 ||
        CellKindAt(coordinate + uint2(0, 1)) != 4)
    {
        return false;
    }
    if (coordinate.x > 0 && CellKindAt(coordinate - uint2(1, 0)) == 4 &&
        CellKindAt(uint2(coordinate.x - 1, coordinate.y + 1)) != 4)
    {
        return false;
    }
    if (coordinate.x + 1 < Width && CellKindAt(coordinate + uint2(1, 0)) == 4 &&
        CellKindAt(coordinate + uint2(1, 1)) != 4)
    {
        return false;
    }
    return true;
}

uint PressureSourceHead(WaterPressureRouteData routeData)
{
    uint sourceX;
    uint distance;
    if (!UnpackPressureRoute(routeData.Route, sourceX, distance) ||
        routeData.SourceIndex >= Width * Height || routeData.SourceIndex % Width != sourceX)
    {
        return Height;
    }
    uint sourceBase = routeData.SourceIndex / Width;
    if (CellKindAt(uint2(sourceX, sourceBase)) != 4 ||
        !WaterSupported(uint2(sourceX, sourceBase)))
    {
        return Height;
    }
    uint sourceTop = sourceBase;
    while (sourceTop > 0 && CellKindAt(uint2(sourceX, sourceTop - 1)) == 4)
    {
        sourceTop--;
    }
    if (sourceBase < sourceTop + 3 ||
        !WaterSurfaceHasStableAnchor(uint2(sourceX, sourceTop)))
    {
        return Height;
    }
    return sourceTop;
}

uint PressureOwnSourceHead(uint2 coordinate, out uint sourceIndex)
{
    sourceIndex = 0;
    if (coordinate.y > 0 && CellKindAt(coordinate - uint2(0, 1)) == 4)
    {
        return Height;
    }
    uint sourceBase = coordinate.y;
    while (sourceBase + 1 < Height && CellKindAt(uint2(coordinate.x, sourceBase + 1)) == 4)
    {
        sourceBase++;
    }
    if (sourceBase < coordinate.y + 3 || !WaterSupported(uint2(coordinate.x, sourceBase)) ||
        !WaterSurfaceHasStableAnchor(coordinate))
    {
        return Height;
    }
    sourceIndex = FlattenCoordinate(uint2(coordinate.x, sourceBase));
    return coordinate.y;
}

WaterPressureRouteData ReadPressureRoute(uint index)
{
    if (SimulationPhase == 34)
    {
        return WaterPressureRoutes[index];
    }
    return WaterPressureRouteScratch[index];
}

void WritePressureRoute(uint index, WaterPressureRouteData route)
{
    if (SimulationPhase == 34)
    {
        WaterPressureRouteScratch[index] = route;
    }
    else
    {
        WaterPressureRoutes[index] = route;
    }
}

bool PressureRouteIsConnected(uint2 coordinate, WaterPressureRouteData routeData)
{
    uint sourceX;
    uint distance;
    uint sourceHead = PressureSourceHead(routeData);
    if (!UnpackPressureRoute(routeData.Route, sourceX, distance) || sourceHead >= Height ||
        CellKindAt(coordinate) != 4)
    {
        return false;
    }
    uint coordinateIndex = FlattenCoordinate(coordinate);
    uint currentSourceIndex = FlattenCoordinate(uint2(sourceX, sourceHead));
    // The route itself is the connectivity proof: every relaxation step is
    // copied from an orthogonally adjacent water cell with the same exact
    // source and a smaller distance. Brush/topology edits clear all routes,
    // so a newly created disconnected puddle cannot inherit this chain.
    return CellKindAtIndex(coordinateIndex) == 4 &&
        CellKindAtIndex(currentSourceIndex) == 4;
}

bool WaterRemovalPreservesConnectivity(uint2 coordinate)
{
    if (!WaterSurfaceHasStableAnchor(coordinate))
    {
        return false;
    }
    // The cell below is the surviving anchor. Any horizontal water neighbor
    // must have a diagonal route to that anchor, so removing this surface cell
    // cannot split the component proven above. This remains safe when several
    // source columns are processed concurrently.
    // A reserved empty neighbor was planned from this water cell. Keep the
    // live attachment in place until that destination has been filled.
    for (int offsetY = -1; offsetY <= 1; offsetY++)
    {
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            if (offsetX == 0 && offsetY == 0)
            {
                continue;
            }
            int2 neighbor = int2(coordinate) + int2(offsetX, offsetY);
            if (neighbor.x < 0 || neighbor.y < 0 ||
                neighbor.x >= int(Width) || neighbor.y >= int(Height))
            {
                continue;
            }
            WaterPressureRouteData neighborRoute = WaterPressureRoutes[
                FlattenCoordinate(uint2(neighbor))];
            if ((neighborRoute.Route & PressureRouteReservation) != 0)
            {
                return false;
            }
        }
    }
    return true;
}

void RelaxWaterPressureRoute(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    if (CellKindAtIndex(index) != 4)
    {
        WritePressureRoute(index, EmptyPressureRoute());
        return;
    }
    WaterPressureRouteData bestRoute = EmptyPressureRoute();
    uint bestHead = Height;
    uint bestDistance = PressureRouteDistanceMask;
    uint bestSourceIndex = Width * Height;
    // Every exposed, supported vertical segment seeds its own route. This is
    // essential when a spiral crosses the same X coordinate several times.
    // Neighbor routes still need a strictly smaller distance, so stale routes
    // left by a dried bridge collapse instead of becoming remote connections.
    uint ownSourceIndex;
    uint ownHead = PressureOwnSourceHead(coordinate, ownSourceIndex);
    if (ownHead < Height)
    {
        bestRoute = MakePressureRoute(ownSourceIndex, 0);
        bestHead = ownHead;
        bestDistance = 0;
        bestSourceIndex = ownSourceIndex;
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
        WaterPressureRouteData candidateRoute = ReadPressureRoute(candidateIndex);
        uint candidateHead = PressureSourceHead(candidateRoute);
        uint candidateSourceX;
        uint candidateDistance;
        if (!UnpackPressureRoute(candidateRoute.Route, candidateSourceX, candidateDistance) ||
            candidateDistance >= PressureRouteDistanceMask)
        {
            continue;
        }
        if (candidateHead >= Height)
        {
            continue;
        }
        uint nextDistance = candidateDistance + 1;
        if (candidateHead + HydraulicHeadRouteTolerance < bestHead ||
            (candidateHead == bestHead &&
                (nextDistance < bestDistance ||
                    (nextDistance == bestDistance && candidateRoute.SourceIndex < bestSourceIndex))))
        {
            bestRoute = MakePressureRoute(candidateRoute.SourceIndex, nextDistance);
            bestHead = candidateHead;
            bestDistance = nextDistance;
            bestSourceIndex = candidateRoute.SourceIndex;
        }
    }
    WaterPressureRouteData outputRoute = bestRoute;
    if (bestHead >= Height)
    {
        outputRoute = EmptyPressureRoute();
    }
    WritePressureRoute(index, outputRoute);
    GridCell routedCell = Grid[index];
    routedCell.Pressure = bestHead < Height ? float(bestHead + 1) : 0;
    Grid[index] = routedCell;
}

bool IsSolidAt(int2 coordinate)
{
    return coordinate.x >= 0 && coordinate.y >= 0 &&
        coordinate.x < int(Width) && coordinate.y < int(Height) &&
        CellKindAt(uint2(coordinate)) == 2;
}

bool HasPressureWallPairAt(int2 coordinate, int2 normal)
{
    bool wallOnLeft = false;
    bool wallOnRight = false;
    for (uint distance = 1; distance <= PressureChannelHalfWidth; distance++)
    {
        int2 first = coordinate + normal * int(distance);
        int2 second = coordinate - normal * int(distance);
        if (!wallOnLeft && IsSolidAt(first))
        {
            wallOnLeft = true;
        }
        if (!wallOnRight && IsSolidAt(second))
        {
            wallOnRight = true;
        }
        if (wallOnLeft && wallOnRight)
        {
            break;
        }
    }
    return wallOnLeft && wallOnRight;
}

bool HasPressureChannelWalls(uint2 coordinate, int2 movement)
{
    int2 normal = int2(-movement.y, movement.x);
    int2 firstSlice = int2(coordinate);
    int2 secondSlice = firstSlice - movement * int(PressureChannelWallSpan);
    // A pair of isolated solid pixels is not a pipe. Confirm the same channel
    // at a second slice inside the water before allowing a remote pressure move.
    return HasPressureWallPairAt(firstSlice, normal) &&
        HasPressureWallPairAt(secondSlice, normal);
}

void PlanPressurizedWaterMove(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    // Communicating-vessel pressure is an explicit opt-in feature. Ordinary
    // Powder Toy-style water must never rise into sealed air pockets.
    if (HydraulicPressure == 0 || coordinate.y == 0 || CellKindAtIndex(index) != 4)
    {
        return;
    }
    WaterPressureRouteData route = WaterPressureRoutes[index];
    uint sourceHead = PressureSourceHead(route);
    uint sourceX;
    uint routeDistance;
    if (sourceHead >= Height ||
        !UnpackPressureRoute(route.Route, sourceX, routeDistance) ||
        routeDistance >= PressureRouteDistanceMask)
    {
        return;
    }
    int sideDirection = ((FrameIndex + coordinate.x) & 1) == 0 ? -1 : 1;
    int2 offsets[5] =
    {
        int2(0, -1),
        int2(sideDirection, -1),
        int2(-sideDirection, -1),
        int2(sideDirection, 0),
        int2(-sideDirection, 0)
    };
    for (uint attempt = 0; attempt < 5; attempt++)
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
        bool movesStraightUp = offsets[attempt].x == 0 && offsets[attempt].y == -1;
        if (!movesStraightUp && !HasPressureChannelWalls(destination, offsets[attempt]))
        {
            continue;
        }
        if (offsets[attempt].x != 0 && offsets[attempt].y != 0 &&
            CellKindAt(uint2(destinationCoordinate.x, coordinate.y)) == 2 &&
            CellKindAt(uint2(coordinate.x, destinationCoordinate.y)) == 2)
        {
            continue;
        }
        // A validated, strictly descending route is enough for vertical
        // pressure in a wide or curved vessel. Sideways shortcuts still need
        // two confirmed channel slices; gravity owns open lateral spreading.
        if (!PressureRouteIsConnected(coordinate, route))
        {
            return;
        }
        uint destinationRoute = PackPressureRoute(sourceX, routeDistance + 1);
        uint reservation = PressureRouteReservation | destinationRoute;
        uint previousReservation;
        InterlockedCompareExchange(
            WaterPressureRoutes[destinationIndex].Route,
            0,
            reservation,
            previousReservation);
        if (previousReservation != 0)
        {
            continue;
        }
        WaterPressureRoutes[destinationIndex].SourceIndex = route.SourceIndex;
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
                uint ignored;
                InterlockedAdd(PathBlockerMasks[PressurePlanCounterIndex()], 1, ignored);
                return;
            }
        }
        uint ignored;
        InterlockedCompareExchange(
            WaterPressureRoutes[destinationIndex].Route,
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
    WaterPressureRouteData route = WaterPressureRoutes[index];
    uint sourceHead = PressureSourceHead(route);
    uint sourceX;
    uint routeDistance;
    if (sourceHead >= Height || coordinate.y + HydraulicSurfaceTolerance >= sourceHead ||
        !UnpackPressureRoute(route.Route, sourceX, routeDistance) ||
        !PressureRouteIsConnected(coordinate, route))
    {
        return;
    }
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

void ApplyPressurizedWaterReturnSlot(uint sourceX, uint lane, uint donorParity)
{
    uint slot = Width * (lane + 1) + sourceX;
    uint encodedSource = WaterColumnState[slot];
    if ((encodedSource & 0x80000000u) == 0)
    {
        return;
    }
    uint sourceWaterIndex = (encodedSource & 0x7fffffffu) - 1;
    uint sourceWaterX = sourceWaterIndex % Width;
    if ((sourceWaterX & 1) != donorParity)
    {
        return;
    }
    WaterColumnState[slot] = 0;
    WaterPressureRouteData route = WaterPressureRoutes[sourceWaterIndex];
    uint routeSourceX;
    uint routeDistance;
    uint sourceTop = PressureSourceHead(route);
    if (!UnpackPressureRoute(route.Route, routeSourceX, routeDistance) ||
        routeSourceX != sourceX || sourceTop == 0 || sourceTop >= Height)
    {
        return;
    }
    uint sourceWaterY = sourceWaterIndex / Width;
    if (sourceWaterY + HydraulicSurfaceTolerance >= sourceTop)
    {
        return;
    }
    uint destinationIndex = FlattenCoordinate(uint2(sourceX, sourceTop - 1));
    uint2 sourceWaterCoordinate = uint2(sourceWaterX, sourceWaterY);
    if (CellKindAtIndex(sourceWaterIndex) != 4 || CellKindAtIndex(destinationIndex) != 0 ||
        !PressureRouteIsConnected(sourceWaterCoordinate, route) ||
        !WaterRemovalPreservesConnectivity(sourceWaterCoordinate))
    {
        return;
    }
    GridCell water = Grid[sourceWaterIndex];
    GridCell empty = CreateEmptyCell();
    float horizontal = sourceX == sourceWaterX ? 0 : sourceX > sourceWaterX ? 54 : -54;
    MarkMovement(water, empty, horizontal, 38);
    Grid[sourceWaterIndex] = empty;
    Grid[destinationIndex] = water;
    CellMaterials[sourceWaterIndex] = 0;
    CellMaterials[destinationIndex] = water.MaterialIndex;
    WaterPressureRoutes[sourceWaterIndex] = EmptyPressureRoute();
    WaterPressureRoutes[destinationIndex] = MakePressureRoute(route.SourceIndex, 0);
    uint ignored;
    InterlockedAdd(WaterColumnState[Width * HydraulicActivityRow], 1, ignored);
}

void ApplyPressurizedWaterReturn(uint sourceX, uint donorParity)
{
    for (uint lane = 0; lane < HydraulicTransfersPerColumn; lane++)
    {
        ApplyPressurizedWaterReturnSlot(sourceX, lane, donorParity);
    }
}

void ReleasePressureReservation(uint destinationIndex, uint reservation)
{
    uint ignored;
    InterlockedCompareExchange(
        WaterPressureRoutes[destinationIndex].Route,
        reservation,
        0,
        ignored);
    if (ignored == reservation)
    {
        WaterPressureRoutes[destinationIndex].SourceIndex = 0;
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
    WaterPressureRouteData reservationData = WaterPressureRoutes[destinationIndex];
    uint reservation = reservationData.Route;
    WaterPressureRouteData route = reservationData;
    route.Route &= 0x7fffffffu;
    uint routeSourceX;
    uint routeDistance;
    if ((reservation & PressureRouteReservation) == 0)
    {
        return;
    }
    if (!UnpackPressureRoute(route.Route, routeSourceX, routeDistance) ||
        routeSourceX != sourceX)
    {
        ReleasePressureReservation(destinationIndex, reservation);
        return;
    }
    uint sourceTop = PressureSourceHead(route);
    if (sourceTop >= Height)
    {
        ReleasePressureReservation(destinationIndex, reservation);
        return;
    }
    uint sourceIndex = FlattenCoordinate(uint2(sourceX, sourceTop));
    uint2 sourceCoordinate = uint2(sourceX, sourceTop);
    if (CellKindAtIndex(sourceIndex) != 4 || CellKindAtIndex(destinationIndex) != 0 ||
        !WaterRemovalPreservesConnectivity(sourceCoordinate))
    {
        ReleasePressureReservation(destinationIndex, reservation);
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
    CellMaterials[destinationIndex] = water.MaterialIndex;
    WaterPressureRoutes[sourceIndex] = EmptyPressureRoute();
    WaterPressureRoutes[destinationIndex] = route;
    uint ignored;
    InterlockedAdd(
        WaterColumnState[Width * HydraulicActivityRow],
        1,
        ignored);
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
    uint sourceIndex = FlattenCoordinate(coordinate);
    uint2 belowCoordinate = coordinate + uint2(0, 1);
    uint belowMaterial = CellMaterials[FlattenCoordinate(belowCoordinate)];
    uint belowKind = CellKindFromMaterial(belowMaterial);
    if (GranularCanMoveTo(coordinate, sourceIndex, belowCoordinate, belowMaterial))
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
        uint2 diagonalCoordinate = uint2(x, coordinate.y + 1);
        uint diagonalMaterial = CellMaterials[FlattenCoordinate(diagonalCoordinate)];
        if (GranularCanMoveTo(
            coordinate,
            sourceIndex,
            diagonalCoordinate,
            diagonalMaterial))
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
                if (SandCanRoll(coordinate, side, cell.MaterialIndex, targetMaterial))
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
        uint sideMaterial = CellMaterials[FlattenCoordinate(uint2(x, coordinate.y))];
        if (GranularCanSpreadThroughLiquidSide(
            coordinate, cell.MaterialIndex, sideMaterial))
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
        uint belowMaterial = CellMaterials[FlattenCoordinate(coordinate + uint2(0, 1))];
        if (WaterCanEnter(cell.MaterialIndex, belowMaterial))
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
                    cell.MaterialIndex,
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
                cell.MaterialIndex,
                CellMaterials[FlattenCoordinate(sideCoordinate)],
                hasWaterAbove,
                hasWaterLeft,
                hasWaterRight))
            {
                return true;
            }
        }
        if (HydraulicPressure != 0)
        {
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
                        cell.MaterialIndex,
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
        else
        {
            uint2 ignoredDestination;
            if (FindOrdinaryWaterDestination(
                coordinate,
                cell.MaterialIndex,
                -1,
                ignoredDestination) ||
                FindOrdinaryWaterDestination(
                    coordinate,
                    cell.MaterialIndex,
                    1,
                    ignoredDestination))
            {
                return true;
            }
        }
    }
    return false;
}

bool CanMoveGas(uint2 coordinate, GridCell cell)
{
    bool flame = (Materials[cell.MaterialIndex].Flags & MaterialFlagFlame) != 0;
    for (int yOffset = -1; yOffset <= 1; yOffset++)
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
            if (neighborKind == 0)
            {
                return true;
            }
            if (!flame && neighborKind == SimulationKindGas)
            {
                GridCell neighbor = Grid[neighborIndex];
                bool neighborFlame =
                    (Materials[neighbor.MaterialIndex].Flags & MaterialFlagFlame) != 0;
                if (!neighborFlame &&
                    (neighbor.MaterialIndex != cell.MaterialIndex ||
                        abs(neighbor.Mass - cell.Mass) > 0.001))
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
    if (kind == 4 && HydraulicPressure == 0)
    {
        bool freeSurface = !IsWaterAt(
            int(coordinate.x),
            int(coordinate.y) - 1);
        bool supportedByFallingWater = false;
        if (coordinate.y + 1 < Height)
        {
            uint belowIndex = index + Width;
            supportedByFallingWater = CellKindAtIndex(belowIndex) == 4 &&
                abs(Grid[belowIndex].VelocityY) > 8;
        }
        if (freeSurface && WaterSupported(coordinate) &&
            !supportedByFallingWater)
        {
            // A free-surface particle resting on settled water has landed.
            // A particle in a falling column keeps its vertical motion.
            cell.VelocityY = 0;
            cell.Pressure = min(
                cell.Pressure + 1,
                OrdinaryLandingFrames);
        }
        else
        {
            cell.Pressure = 0;
        }
    }
    if (canMove)
    {
        cell.RestFrames = 0;
    }
    else
    {
        // Rest state is evaluated once per rendered frame. The solver used to
        // run this pass after both cellular substeps, so advance by two to keep
        // the existing sleep timing while avoiding the duplicate global scan.
        cell.RestFrames = min(cell.RestFrames + 2, threshold);
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
    CellMaterials[index] = cell.IsActive != 0 ? cell.MaterialIndex : 0;
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
    else if (SimulationPhase == 80 || SimulationPhase == 81)
    {
        // Gas vertical pairs, same row parity as phases 0 and 1.
        coordinate = uint2(
            DispatchOffsetX + dispatchThreadId.x,
            DispatchOffsetY + dispatchThreadId.y * 2);
    }
    else if (SimulationPhase == 82 || SimulationPhase == 83)
    {
        // Gas horizontal pairs, same column parity as phases 2 and 3.
        coordinate = uint2(
            DispatchOffsetX + dispatchThreadId.x * 2,
            DispatchOffsetY + dispatchThreadId.y);
    }
    else if (SimulationPhase >= 85 && SimulationPhase <= 88)
    {
        // Обход препятствия: та же чётность строк, что у вертикальных пар,
        // иначе две соседние клетки могли бы шагнуть в одну и ту же цель.
        coordinate = uint2(
            DispatchOffsetX + dispatchThreadId.x * 2,
            DispatchOffsetY + dispatchThreadId.y * 2);
    }
    else
    {
        coordinate = dispatchThreadId.xy + uint2(DispatchOffsetX, DispatchOffsetY);
    }
    if (coordinate.x >= Width || coordinate.y >= Height)
    {
        return;
    }
    if (SimulationPhase == 89)
    {
        IntegrateGasMotion(coordinate);
        return;
    }
    if (SimulationPhase == 84)
    {
        ClearOpenBoundary(coordinate);
        return;
    }
    if (SimulationPhase == 80 || SimulationPhase == 81)
    {
        ResolveGasVerticalPair(coordinate);
        return;
    }
    if (SimulationPhase == 82 || SimulationPhase == 83)
    {
        ResolveGasHorizontalPair(coordinate);
        return;
    }
    if (SimulationPhase >= 85 && SimulationPhase <= 88)
    {
        ResolveGasObstacleBypass(coordinate);
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
    if (SimulationPhase >= 48 && SimulationPhase <= 55)
    {
        ResolveOrdinaryWaterBlock(coordinate);
        return;
    }
    if (SimulationPhase == 56)
    {
        ResolveOrdinarySurfaceBlock(coordinate.x);
        return;
    }
    if (SimulationPhase == 57)
    {
        ResolveOrdinaryLocalSurfaceBlock(coordinate.x);
        return;
    }
    if (SimulationPhase == 30)
    {
        ForceCellularRest(coordinate);
        return;
    }
    if (SimulationPhase == 13)
    {
        uint pairOffset = FrameIndex & 1;
        if (coordinate.x >= pairOffset &&
            ((coordinate.x - pairOffset) & 1) == 0)
        {
            ResolveWaterColumnSpan(coordinate, 1);
        }
        return;
    }
    if (SimulationPhase == 34 || SimulationPhase == 35)
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
        if ((coordinate.x & 1) == 0)
        {
            ApplyPressurizedWaterMove(coordinate.x);
        }
        return;
    }
    if (SimulationPhase == 38)
    {
        PlanPressurizedWaterReturn(coordinate);
        return;
    }
    if (SimulationPhase == 39)
    {
        ApplyPressurizedWaterReturn(coordinate.x, 0);
        return;
    }
    if (SimulationPhase == 70)
    {
        ApplyPressurizedWaterReturn(coordinate.x, 1);
        return;
    }
    if (SimulationPhase == 71)
    {
        if ((coordinate.x & 1) != 0)
        {
            ApplyPressurizedWaterMove(coordinate.x);
        }
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
