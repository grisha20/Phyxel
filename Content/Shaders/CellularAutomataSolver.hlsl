#include "PhysicsShared.hlsli"

StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<AirCell> Air : register(t1);
StructuredBuffer<uint> GasActiveTiles : register(t2);

#include "PhaseEnthalpy.hlsli"
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
RWStructuredBuffer<uint> GasLateralTransferStatistics : register(u8);
RWStructuredBuffer<GasAirImpulse> GasAirImpulses : register(u9);
RWStructuredBuffer<GasVerticalMotionStatistics> GasVerticalMotionStatisticsBuffer : register(u10);
RWStructuredBuffer<uint> GasVerticalBlockFrameMarkers : register(u11);

#define FineAirWidth Width
#define FineAirHeight Height
#define FineAirMaterialAt(p) CellMaterials[uint((p).y) * Width + uint((p).x)]
#define FineAirMaterials Materials
// SolidPass is a tagged selector: solid kernels use its pass number,
// gas kernels use the finite-carrier geometry flag set just for their dispatch.
#define FineAirBlockGranular (SolidPass != 0)
#include "FineAirGeometry.hlsli"

static const uint MetalChimneyInnerLeft = 221;
static const uint MetalChimneyInnerRight = 258;

void RecordMetalChimneyGasState(uint2 coordinate, GasMotionState state)
{
    if (coordinate.x < MetalChimneyInnerLeft || coordinate.x > MetalChimneyInnerRight)
    {
        return;
    }
    uint band = coordinate.y >= 180 && coordinate.y <= 199 ? 0u :
        coordinate.y >= 120 && coordinate.y <= 139 ? 1u :
        coordinate.y >= 60 && coordinate.y <= 79 ? 2u : 3u;
    if (band == 3u) return;
    uint ignored;
    if (band == 0u)
    {
        InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeLow.GasCellFrames, 1, ignored);
        InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeLow.GasVelocityYMillisteps, int(round(state.VelocityY * 1000.0)), ignored);
        if (abs(state.OffsetY) >= float(GasMotionSubSteps) - 0.001) InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeLow.GasOffsetYClampFrames, 1, ignored);
    }
    else if (band == 1u)
    {
        InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeMid.GasCellFrames, 1, ignored);
        InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeMid.GasVelocityYMillisteps, int(round(state.VelocityY * 1000.0)), ignored);
        if (abs(state.OffsetY) >= float(GasMotionSubSteps) - 0.001) InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeMid.GasOffsetYClampFrames, 1, ignored);
    }
    else
    {
        InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeHigh.GasCellFrames, 1, ignored);
        InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeHigh.GasVelocityYMillisteps, int(round(state.VelocityY * 1000.0)), ignored);
        if (abs(state.OffsetY) >= float(GasMotionSubSteps) - 0.001) InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeHigh.GasOffsetYClampFrames, 1, ignored);
    }
}

void RecordMetalChimneyUpwardStep(uint2 coordinate)
{
    if (coordinate.x < MetalChimneyInnerLeft || coordinate.x > MetalChimneyInnerRight) return;
    uint ignored;
    if (coordinate.y >= 180 && coordinate.y <= 199) InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeLow.GasUpwardSteps, 1, ignored);
    else if (coordinate.y >= 120 && coordinate.y <= 139) InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeMid.GasUpwardSteps, 1, ignored);
    else if (coordinate.y >= 60 && coordinate.y <= 79) InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].PipeHigh.GasUpwardSteps, 1, ignored);
}

static const uint GasLateralPathMotionHorizontal = 0;
static const uint GasLateralPathObstacleX = 1;
static const uint GasLateralPathObstacleDiagonal = 2;
static const uint GasLateralFieldsPerPath = 14;
static const float GasAirImpulseFixedPointScale = 100000.0;

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

float2 FlameAirDrift(uint2 coordinate)
{
    uint airWidth = (Width + AirCellSize - 1) / AirCellSize;
    uint airHeight = (Height + AirCellSize - 1) / AirCellSize;
    // Interpolate only nodes visible from this particle. Nearest-node reads
    // made every 4-pixel strip follow one direction through a curved channel.
    float2 position = (float2(coordinate) - float(AirCellSize / 2)) / float(AirCellSize);
    int2 origin = int2(floor(position));
    float2 fraction = frac(position), velocity = 0;
    float total = 0;
    [loop] for (int y = 0; y < 2; y++)
    [loop] for (int x = 0; x < 2; x++)
    {
        int2 node = origin + int2(x, y);
        float weight = (x == 0 ? 1 - fraction.x : fraction.x) *
            (y == 0 ? 1 - fraction.y : fraction.y);
        if (weight <= 0 || node.x < 0 || node.y < 0 || node.x >= int(airWidth) || node.y >= int(airHeight)) continue;
        int2 center = node * int(AirCellSize) + int(AirCellSize / 2);
        if (!AirFineSegmentOpen(int2(coordinate), center)) continue;
        AirCell air = Air[uint(node.y) * airWidth + uint(node.x)];
        velocity += float2(air.VelocityX, air.VelocityY) * weight;
        total += weight;
    }
    if (total > 0.000001) return velocity / total;
    int2 fallback;
    if (!AirFineNodeFor(int2(coordinate), fallback)) return 0;
    AirCell air = Air[uint(fallback.y) * airWidth + uint(fallback.x)];
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

bool GasNearTransverseWall(uint2 coordinate, float2 carrier)
{
    // CW's extra dispersion describes unresolved boundary mixing. Applying
    // it to an unconfined torch made a Diffusion=0 flame spread everywhere.
    // Search each fine pixel on the dominant transverse axis so even a
    // shifted one-pixel wall participates; grains are not channel walls.
    int2 axis = abs(carrier.y) >= abs(carrier.x) ? int2(1, 0) : int2(0, 1);
    [loop] for (int distance = 1; distance <= int(AirCellSize) * 4; distance++)
    {
        int2 first = int2(coordinate) - axis * distance;
        int2 second = int2(coordinate) + axis * distance;
        if (first.x >= 0 && first.y >= 0 && first.x < int(Width) && first.y < int(Height) &&
            CellKindAt(uint2(first)) == SimulationKindSolid) return true;
        if (second.x >= 0 && second.y >= 0 && second.x < int(Width) && second.y < int(Height) &&
            CellKindAt(uint2(second)) == SimulationKindSolid) return true;
    }
    return false;
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
    MaterialProperties material=Materials[cell.MaterialIndex];
    if(material.MoistureCapacity>0 && cell.Mass>0)
    {
        float saturation=saturate(cell.MoistureMass/(cell.Mass*material.MoistureCapacity));
        float dry=Materials[material.MoistureDryMaterialIndex].Density;
        float oil=material.FuelCapacity>0 ? saturate(cell.FuelMass/(cell.Mass*material.FuelCapacity)) : 0;
        return lerp(dry,Materials[material.MoistureWetMaterialIndex].Density,saturation)
            + oil*(material.FuelSaturatedDensity-dry);
    }
    return material.Density;
}

// Most cells need only the compact material cache. Read moisture fields only
// for absorbent grains, whose density changes continuously with saturation.
float CellRankAtIndex(uint index)
{
    uint materialId=CellMaterials[index];
    if(Materials[materialId].MoistureCapacity>0) return CellRank(Grid[index]);
    return CellRankFromMaterial(materialId);
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

// A liquid without the optional mobility contract retains its old solver.
// Rates use elapsed seconds; no oil ID or render-FPS coefficient is involved.
bool LiquidStepAllowedLoaded(uint sourceIndex, uint destinationIndex, float load)
{
    GridCell source = Grid[sourceIndex];
    MaterialProperties material = Materials[source.MaterialIndex];
    if (material.SimulationKind != SimulationKindLiquid ||
        material.LiquidFlowTemperatureSensitivity <= 0) return true;
    float rate = 18.0 * material.FlowRate * LiquidMobility(material, source.Temperature) * load;
    float chance = 1.0 - exp(-rate * max(0.0, DeltaTime));
    uint seed = sourceIndex ^ HashValue(destinationIndex + 7919u * SimulationPhase) ^ HashValue(FrameIndex);
    return HashUnitFloat(seed) < chance;
}

bool LiquidStepAllowed(uint sourceIndex, uint destinationIndex)
{
    return LiquidStepAllowedLoaded(sourceIndex, destinationIndex, 1.0);
}

// A deep liquid column drives supported downhill leveling more strongly than a film.
// This bounded local head closure keeps viscosity and does not move distant columns.
bool LiquidLevelStepAllowed(uint sourceIndex, uint destinationIndex)
{
    // Only the short surface patches use this head closure. Applying it to
    // every existing drain/column move would wash out the viscosity response.
    if (SimulationPhase != 58) return LiquidStepAllowed(sourceIndex, destinationIndex);
    uint materialIndex = CellMaterials[sourceIndex];
    if (Materials[materialIndex].LiquidFlowTemperatureSensitivity <= 0 ||
        destinationIndex / Width < sourceIndex / Width)
        return LiquidStepAllowed(sourceIndex, destinationIndex);
    uint depth = 1;
    [loop] for (uint step = 1; step < 16 && sourceIndex / Width + step < Height; step++)
    {
        if (CellMaterials[sourceIndex + step * Width] != materialIndex) break;
        depth++;
    }
    return LiquidStepAllowedLoaded(sourceIndex, destinationIndex, (float)depth);
}

void SwapCells(uint firstIndex, uint secondIndex, float horizontal, float vertical)
{
    GridCell first = Grid[firstIndex];
    GridCell second = Grid[secondIndex];
    // Free downward flight in air is gravity, not supported viscous flow.
    bool freeFall = horizontal == 0 && CellKind(first) == SimulationKindLiquid &&
        (CellKind(second) == SimulationKindNone || CellKind(second) == SimulationKindGas);
    if (!freeFall && (!LiquidStepAllowed(firstIndex, secondIndex) ||
        !LiquidStepAllowed(secondIndex, firstIndex))) return;
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
void RecordGasLateralCellMove(
    uint path,
    uint sourceIndex,
    uint targetIndex,
    float velocityX,
    uint material)
{
    int sourceX = int(sourceIndex % Width);
    int targetX = int(targetIndex % Width);
    int stepX = targetX - sourceX;
    if (stepX == 0)
    {
        return;
    }
    uint baseIndex = path * GasLateralFieldsPerPath;
    uint directionIndex = stepX < 0 ? 0u : 1u;
    uint sourceSide = sourceX < int(Width / 2) ? 0u : 1u;
    uint ignored;
    InterlockedAdd(GasLateralTransferStatistics[baseIndex + directionIndex], 1, ignored);
    InterlockedAdd(GasLateralTransferStatistics[baseIndex + 2u + sourceSide * 2u + directionIndex], 1, ignored);

    float signedVelocity = velocityX * float(stepX);
    uint agreementIndex = signedVelocity > 0.0001 ? 6u :
        signedVelocity < -0.0001 ? 7u : 8u;
    InterlockedAdd(GasLateralTransferStatistics[baseIndex + agreementIndex], 1, ignored);
    if ((Materials[material].Flags & MaterialFlagFlame) != 0)
    {
        InterlockedAdd(GasLateralTransferStatistics[baseIndex + 9u + directionIndex], 1, ignored);
        InterlockedAdd(GasLateralTransferStatistics[baseIndex + 11u + (agreementIndex - 6u)], 1, ignored);
    }
}

// Record the successful carrier step already weighted by the source
// material's motion.airDrag. This keeps the coarse accumulator atomic while
// preserving material-specific drag through the following air tick.
void RecordGasAirStep(uint sourceIndex, uint targetIndex, uint material)
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
    int2 airCoordinate;
    if (!AirFineNodeFor(int2(sourceX, sourceY), airCoordinate)) return;
    uint airIndex = uint(airCoordinate.y) * airWidth + uint(airCoordinate.x);
    int ignored;
    float airDrag = Materials[material].MotionAirDrag;
    if (stepX != 0)
    {
        InterlockedAdd(
            GasAirImpulses[airIndex].X,
            int(round(float(stepX) * airDrag * GasAirImpulseFixedPointScale)),
            ignored);
    }
    if (stepY != 0)
    {
        InterlockedAdd(
            GasAirImpulses[airIndex].Y,
            int(round(float(stepY) * airDrag * GasAirImpulseFixedPointScale)),
            ignored);
    }
}

void MoveGasCell(
    uint sourceIndex,
    uint targetIndex,
    uint lateralPath)
{
    GridCell mover = Grid[sourceIndex];
    GridCell displaced = Grid[targetIndex];
    // Only gas/empty pairs enter this helper. Moisture belongs to granular
    // fuel, so do not carry its two extra words through every gas move.
    mover.MoistureMass = 0; mover.MoistureEnergy = 0;
    displaced.MoistureMass = 0; displaced.MoistureEnergy = 0;
    GasMotionState moverMotion = GasMotion[sourceIndex];
    GasMotionState displacedMotion = GasMotion[targetIndex];

    RecordGasLateralCellMove(
        lateralPath,
        sourceIndex,
        targetIndex,
        moverMotion.VelocityX,
        mover.MaterialIndex);
    RecordGasAirStep(sourceIndex, targetIndex, mover.MaterialIndex);

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
    liquid.BodyId &= FuelBurningMarker;
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
    if (!LiquidStepAllowed(sourceIndex, destinationIndex)) return;

    // Mix total specific enthalpy, then recover a canonical phase state.
    // Averaging temperature and boiling progress separately can leave liquid
    // below 100 C carrying latent heat; the next phase pass hides the mistake.
    float destinationEnergy = destinationMass * CellSpecificEnthalpy(destination) +
        transferred * CellSpecificEnthalpy(source);
    float destinationAuxiliary = destinationMass * destination.Lifetime +
        transferred * source.Lifetime;
    source.Mass = sourceMass - transferred;
    destination.Mass = destinationMass + transferred;
    destination = SetCellSpecificEnthalpy(destination,
        destinationEnergy / max(0.000001, destination.Mass));
    if (!HasPhaseEnthalpy(Materials[destination.MaterialIndex]))
        destination.Lifetime = destinationAuxiliary / max(0.000001, destination.Mass);
    destination.VelocityX = 0;
    destination.VelocityY = 0;
    destination.BodyId = (destination.BodyId | source.BodyId) & FuelBurningMarker;
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
        source.BodyId &= FuelBurningMarker;
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

// A free grain's density does not include the granular load resting on it.
// Evaluate that load only at a buoyant grain/liquid boundary, never by
// exchanging two powders. This is a bounded cell-scale column estimate,
// not a rigid raft or a pore-pressure solver.
bool GranularLoadedToSink(uint2 coordinate, uint liquidMaterial)
{
    float liquidDensity = CellRankFromMaterial(liquidMaterial);
    if (CellKindFromMaterial(liquidMaterial) != SimulationKindLiquid ||
        CellKindAt(coordinate) != SimulationKindGranular ||
        CellRankAtIndex(FlattenCoordinate(coordinate)) >= liquidDensity || coordinate.y == 0)
        return false;
    uint above = CellKindAt(coordinate - uint2(0, 1));
    if (above != SimulationKindGranular &&
        !(above == SimulationKindLiquid && coordinate.y > 1 &&
          CellKindAt(coordinate - uint2(0, 2)) == SimulationKindGranular))
        return false;

    float load = 0;
    uint grainCount = 0;
    uint liquidGaps = 0;
    bool heavyCover = false;
    [loop]
    for (uint distance = 0; distance < 64 && distance <= coordinate.y; distance++)
    {
        uint2 loadCoordinate = coordinate - uint2(0, distance);
        uint material = CellMaterials[FlattenCoordinate(loadCoordinate)];
        if (CellKindFromMaterial(material) == SimulationKindGranular)
        {
            float density = CellRankAtIndex(FlattenCoordinate(loadCoordinate));
            load += density;
            grainCount++;
            heavyCover = heavyCover || (distance > 0 && density > liquidDensity);
            bool immersed =
                (loadCoordinate.x > 0 && CellMaterials[FlattenCoordinate(loadCoordinate - uint2(1, 0))] == liquidMaterial) ||
                (loadCoordinate.x + 1 < Width && CellMaterials[FlattenCoordinate(loadCoordinate + uint2(1, 0))] == liquidMaterial);
            if (immersed) load -= liquidDensity;
        }
        else if (material == liquidMaterial && liquidGaps == 0)
        {
            // The water exchanged with the bottom grain momentarily separates
            // it from its coating; retain the load across that one cell.
            liquidGaps++;
        }
        else break;
    }
    return grainCount > 1 && heavyCover && load > liquidDensity * 0.5;
}

bool GranularCanDisplaceLiquid(uint2 coordinate, uint granularIndex, uint liquidMaterial)
{
    GridCell granular = Grid[granularIndex];
    if (CellKindFromMaterial(liquidMaterial) != SimulationKindLiquid ||
        (CellRank(granular) <= CellRankFromMaterial(liquidMaterial) &&
         !GranularLoadedToSink(coordinate, liquidMaterial)))
    {
        return false;
    }

    // A lower water cell is still a free gravitational path when the grain
    // touches another grain directly below it. Requiring an old falling
    // impulse froze vertical cliffs and prevented soaked charcoal sinking.
    // GranularCanMoveTo retains solid/corner/powder packing restrictions.
    return true;
}

// Pressure-producing powder moves on the reaction clock. GasSubStep is
// otherwise zero in ordinary cellular passes; its high bit tags this pass.
bool IsPressurePowder(uint material)
{
    return CellKindFromMaterial(material) == SimulationKindGranular &&
        Materials[material].ReactionPressurePerMass > 0;
}

bool PowderClockAllows(uint material)
{
    return IsPressurePowder(material) == ((GasSubStep & 0x80000000u) != 0);
}

bool GranularCanMoveTo(
    uint2 source,
    uint sourceIndex,
    uint2 destination,
    uint targetMaterial)
{
    if (!PowderClockAllows(Grid[sourceIndex].MaterialIndex)) return false;
    uint targetKind = CellKindFromMaterial(targetMaterial);
    if (targetKind == SimulationKindSolid ||
        (targetKind != SimulationKindLiquid &&
         CellRankAtIndex(sourceIndex) <= CellRankAtIndex(FlattenCoordinate(destination))))
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

// Density describes a free grain's buoyancy, not permeability of a packed
// bed. Release a touching grain only where liquid already undercuts it.
bool LiquidCanDisplaceGrain(uint2 grain, uint liquidMaterial)
{
    if(grain.y+1>=Height) return false;
    // Density sorting is buoyancy only when liquid supports the grain.
    // A falling drop/film above charcoal must not exchange upward through
    // an air gap and carry the heap back into its feeding reservoir.
    bool supportedByLiquid = CellMaterials[FlattenCoordinate(grain+uint2(0,1))] == liquidMaterial;
    bool surroundedByLiquid = grain.x>0 && grain.x+1<Width &&
        CellMaterials[FlattenCoordinate(grain-uint2(1,0))] == liquidMaterial &&
        CellMaterials[FlattenCoordinate(grain+uint2(1,0))] == liquidMaterial;
    if(!supportedByLiquid && !surroundedByLiquid) return false;
    uint below=CellKindAt(grain+uint2(0,1));
    bool packed=below==SimulationKindGranular || (below==SimulationKindSolid &&
        grain.y>0 && CellKindAt(grain-uint2(0,1))==SimulationKindGranular);
    if(!packed || below==SimulationKindLiquid) return true;
    return
        (grain.x>0 && CellKindAt(grain+uint2(0,1)-uint2(1,0))==SimulationKindLiquid) ||
        (grain.x+1<Width && CellKindAt(grain+uint2(1,1))==SimulationKindLiquid);
}

bool GranularCanSpreadThroughLiquidSide(
    uint2 coordinate,
    uint granularMaterial,
    uint liquidMaterial)
{
    if (!PowderClockAllows(granularMaterial)) return false;
    if (CellKindFromMaterial(granularMaterial) != SimulationKindGranular ||
        CellKindFromMaterial(liquidMaterial) != SimulationKindLiquid ||
        CellRankAtIndex(FlattenCoordinate(coordinate)) >= CellRankFromMaterial(liquidMaterial))
    {
        return false;
    }
    bool stackedAbove = coordinate.y > 0 &&
        CellMaterials[FlattenCoordinate(coordinate - uint2(0, 1))] == granularMaterial;
    bool stackedBelow = coordinate.y + 1 < Height &&
        CellMaterials[FlattenCoordinate(coordinate + uint2(0, 1))] == granularMaterial;
    return (stackedAbove || stackedBelow) && LiquidCanDisplaceGrain(coordinate, liquidMaterial);
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
    if (!PowderClockAllows(sandMaterial)) return false;
    uint targetKind = CellKindFromMaterial(targetMaterial);
    // Same rule as GranularCanMoveTo: a grain never rolls into another powder,
    // only into empty space. Without this a heavy powder would burrow sideways
    // through a lighter heap instead of resting on its slope.
    if (targetKind == SimulationKindLiquid ||
        targetKind == SimulationKindGranular ||
        CellRankAtIndex(FlattenCoordinate(source)) <= CellRankAtIndex(FlattenCoordinate(destination)))
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

bool WaterCanEnter(uint waterMaterial, uint destinationMaterial, uint2 destination)
{
    uint destinationKind = CellKindFromMaterial(destinationMaterial);
    if (destinationKind == 2 || (destinationKind == SimulationKindGranular &&
        (!LiquidCanDisplaceGrain(destination, waterMaterial) || GranularLoadedToSink(destination, waterMaterial))))
    {
        return false;
    }
    return CellRankFromMaterial(waterMaterial) > CellRankAtIndex(FlattenCoordinate(destination));
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

bool WaterCanDrainSurfaceFilm(uint2 source, uint2 destination)
{
    if (abs(int(destination.x) - int(source.x)) != 1 || source.y + 1 >= Height)
    {
        return false;
    }
    uint bed = CellKindAt(source + uint2(0, 1));
    // A one-cell film on a descending bed has no same-row water behind it.
    // Let it join the adjacent water one row below; vertical/diagonal gravity
    // then drains the small stack. Isolated drops on a flat shelf do not pass.
    return (bed == SimulationKindSolid || bed == SimulationKindGranular) &&
        IsWaterAt(int(destination.x), int(destination.y) + 1) &&
        abs(Grid[FlattenCoordinate(source)].VelocityY) <= 8;
}

bool WaterCanFlowSide(
    uint2 source,
    uint2 destination,
    uint waterMaterial,
    uint targetMaterial)
{
    uint stride = abs(int(destination.x) - int(source.x));
    if (stride > 8 && Materials[waterMaterial].LiquidFlowTemperatureSensitivity > 0) return false;
    uint requiredDepth = stride >= 128 ? 7 : stride >= 32 ? 3 : stride >= 2 ? 1 : 0;
    if (requiredDepth > 0)
    {
        if (source.y < requiredDepth ||
            !IsWaterAt(int(source.x), int(source.y) - int(requiredDepth)))
        {
            return false;
        }
    }
    if (!WaterCanEnter(waterMaterial, targetMaterial, destination))
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
    if (WaterCanDrainSurfaceFilm(source, destination))
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
    if (stride > 8 && Materials[waterMaterial].LiquidFlowTemperatureSensitivity > 0) return false;
    uint requiredDepth = stride >= 128 ? 7 : stride >= 32 ? 3 : stride >= 2 ? 1 : 0;
    if (requiredDepth > 0)
    {
        if (source.y < requiredDepth ||
            !IsWaterAt(int(source.x), int(source.y) - int(requiredDepth)))
        {
            return false;
        }
    }
    if (!WaterCanEnter(waterMaterial, targetMaterial, destination))
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
    if (WaterCanDrainSurfaceFilm(source, destination))
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

        if (!WaterCanEnter(waterMaterial, candidateMaterial, candidate))
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

bool MoveOrdinaryWater(uint sourceIndex, uint destinationIndex, int direction)
{
    if (!LiquidLevelStepAllowed(sourceIndex, destinationIndex)) return false;
    GridCell water = Grid[sourceIndex];
    GridCell target = Grid[destinationIndex];
    MarkMovement(water, target, direction * 58, 0);
    water.BodyId = (water.BodyId & FuelBurningMarker) | ((FrameIndex + 1) & ~FuelBurningMarker);
    Grid[sourceIndex] = target;
    Grid[destinationIndex] = water;
    uint targetMaterial = CellMaterials[destinationIndex];
    CellMaterials[sourceIndex] = targetMaterial;
    CellMaterials[destinationIndex] = water.MaterialIndex;
    return true;
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
    int fallback = -1;
    int bulk = -1;
    // A lower puddle underneath a bowl must not hide the bowl's surface.
    // Choose the highest exposed bulk layer, not the lowest liquid in the
    // whole screen column. Covered columns beneath ice are not free surfaces.
    uint layerMaterial = 0;
    uint layerTop = 0;
    bool exposed = false;
    [loop]
    for (uint y = 0; y <= Height; y++)
    {
        uint material = y < Height ? CellMaterials[FlattenCoordinate(uint2(x, y))] : 0;
        if (material != layerMaterial)
        {
            if (layerMaterial != 0 && exposed)
            {
                uint bottom = y - 1;
                if (bulk < 0 && bottom >= layerTop + 2) bulk = int(bottom);
                if (fallback < 0) fallback = int(bottom);
            }
            layerMaterial = CellKindFromMaterial(material) == SimulationKindLiquid ? material : 0;
            layerTop = y;
            uint aboveKind = y > 0 ? CellKindAt(uint2(x, y - 1)) : 0;
            exposed = aboveKind == 0 || aboveKind == SimulationKindGas;
        }
    }
    return bulk >= 0 ? bulk : fallback;
}

uint WaterSurfaceY(uint2 coordinate)
{
    // A ternary caller may speculatively evaluate this with base=-1 cast to
    // uint. Bound the loop even when this column contains no liquid.
    uint top = min(coordinate.y, Height - 1);
    uint material = CellMaterials[FlattenCoordinate(uint2(coordinate.x, top))];
    if (CellKindFromMaterial(material) != SimulationKindLiquid) return top;
    while (top > 0)
    {
        if (CellMaterials[FlattenCoordinate(uint2(coordinate.x, top - 1))] != material)
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
    uint material = CellMaterials[sourceIndex];
    uint ignoredTop; uint sourceBase;
    if (!UnpackWaterColumn(WaterColumnState[sourceX], ignoredTop, sourceBase)) return;
    // Adjacent bulk plans must use the same donor rules as wide leveling.
    // Otherwise they lift the perched film off ice after leveling and create
    // a fresh surface mound; viscosity must not be bypassed here either.
    bool perchedOnBody = sourceBase + 1 < Height &&
        (Materials[CellMaterials[FlattenCoordinate(uint2(sourceX, sourceBase + 1))]].Flags & MaterialFlagDensityBody) != 0;
    if (perchedOnBody || Materials[material].LiquidFlowTemperatureSensitivity > 0 ||
        CellMaterials[FlattenCoordinate(uint2(destinationX, destinationTop))] != material) return;
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
// buffer keeps per-particle motion separate from the versioned world format.

// A dense heavy gas needs a lateral concentration gradient in addition to
// single-particle diffusion. Otherwise gravity and excluded grid occupancy
// produce a granular heap. Sample the stable grid; never transfer mass here.
float HeavyGasSideDensity(uint2 coordinate, int direction)
{
    float gas = 0;
    float accessible = 0;
    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        bool pathOpen = true;
        [unroll]
        for (int distance = 1; distance <= 3; distance++)
        {
            int2 p = int2(coordinate) + int2(direction * distance, y);
            if (p.x < 0 || p.y < 0 || p.x >= int(Width) || p.y >= int(Height))
                pathOpen = false;
            if (!pathOpen) continue;
            GridCell neighbor = Grid[uint(p.y) * Width + uint(p.x)];
            if (neighbor.IsActive != 0 && CellKind(neighbor) != SimulationKindGas)
            {
                pathOpen = false;
                continue;
            }
            accessible += 1;
            if (neighbor.IsActive != 0) gas += 1;
        }
    }
    // An impermeable side is not a vacuum. Suppress its gradient below.
    return accessible > 0 ? gas / accessible : -1;
}

float CeilingJetTangent(uint2 coordinate, float2 carrier, MaterialProperties material)
{
    // A rising packed layer impinging on a roof must turn toward an outlet.
    // Independent random tangents made neighbouring smoke packets face each
    // other and jam forever. This bounded wall-jet closure shares a direction
    // across the connected near-wall layer, without moving/deleting mass here.
    // Preserve a faster resolved tangent and the direction of strong opposing wind.
    if ((material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) == 0 ||
        material.GasBuoyancy >= 0) return 0;
    int roof = -1;
    [loop] for (int dy=1; dy<=32 && dy<=int(coordinate.y); dy++)
    {
        uint kind=CellKindAt(uint2(coordinate.x,coordinate.y-dy));
        if (kind==SimulationKindSolid) { roof=int(coordinate.y)-dy; break; }
        if (kind!=SimulationKindGas) break;
    }
    if (roof<0) return 0;
    int exits[2]={0,0};
    [unroll] for(int side=0;side<2;side++)
    {
        int direction=side==0?-1:1;
        [loop] for(int step=1;step<=256;step++)
        {
            int x=int(coordinate.x)+direction*step;
            if(x<0||x>=int(Width)) break;
            uint path=CellKindAt(uint2(x,roof+1));
            if(path!=SimulationKindNone&&path!=SimulationKindGas) break;
            uint above=CellKindAt(uint2(x,roof));
            if(above==SimulationKindNone||above==SimulationKindGas){exits[side]=step;break;}
            if(above!=SimulationKindSolid) break;
        }
    }
    if(exits[0]==0&&exits[1]==0) return 0; // closed chamber is not ventilated
    int direction=exits[0]>0&&(exits[1]==0||exits[0]<exits[1])?-1:1;
    if(exits[0]>0&&exits[0]==exits[1]) direction=carrier.x<0?-1:1;
    if(abs(carrier.x)>=1.2 && carrier.x*direction<0) return 0;
    return direction*min(GasMaximumSpeed,max(4.0*material.MotionAdvection,length(carrier)));
}

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
    float2 drift = FlameAirDrift(coordinate);
    // Preserve the historical Sandbox escape only in nearly still air. In
    // moving air the actual carrier owns tangential speed and direction.
    // TPT's collision fallback resets velocity via Collision; this retained
    // surface escape is a Phyxel game rule, not a literal TPT mechanism.
    bool surfaceCarrierMarker = abs(state.OffsetY - 0.5) < 0.001 &&
        abs(state.VelocityX) >= Materials[cell.MaterialIndex].MotionAdvection;
    bool carriesAlongSurface = coordinate.y > 0 &&
        (state.OffsetY <= -1.0 || surfaceCarrierMarker);
    if (carriesAlongSurface)
    {
        GridCell aboveCell = Grid[index - Width];
        carriesAlongSurface = aboveCell.IsActive != 0 &&
            Materials[aboveCell.MaterialIndex].SimulationKind == SimulationKindSolid;
    }
    // The surface carrier is a calibrated flame/smoke fallback. Ordinary
    // gases keep their diffusion and velocity at a surface, just as in space.
    bool legacySurfaceCarrier = (Materials[cell.MaterialIndex].Flags &
        (MaterialFlagFlame | MaterialFlagSmoke)) != 0;
    float ceilingTangent=CeilingJetTangent(coordinate,drift,Materials[cell.MaterialIndex]);
    if (SolidPass == 0 && legacySurfaceCarrier && carriesAlongSurface &&
        ceilingTangent==0 && abs(state.VelocityX) > 0.0001 && dot(drift, drift) < 0.0025)
    {
        float surfaceAdvection = Materials[cell.MaterialIndex].MotionAdvection;
        state.VelocityX = state.VelocityX > 0 ? surfaceAdvection : -surfaceAdvection;
        state.VelocityY = 0;
        state.OffsetX = 0;
        state.OffsetY = -1.0;
        GasMotion[index] = state;
        return;
    }

    MaterialProperties material = Materials[cell.MaterialIndex];
    // TPT diffusion is an unbiased random impulse on the velocity of an
    // individual particle.  It is deliberately not a mass-transfer pass:
    // splitting one gas cell into fractional neighbours turns a sparse steam
    // puff into a continuum cloud and introduces stencil-direction bias.
    // DebugReserved2 is the fixed gas tick; FrameIndex is the presentation
    // frame and repeats at 30 FPS (or skips at 100 FPS). Both forms of random
    // transport, including smoke's existing diffusion, use physical time.
    uint diffusionTick = DebugReserved2;
    uint diffusionSeed = index ^ (diffusionTick * 0x9e3779b9u);
    float2 diffusionImpulse = float2(
        HashUnitFloat(diffusionSeed) * 2.0 - 1.0,
        HashUnitFloat(diffusionSeed ^ 0x85ebca6bu) * 2.0 - 1.0) * material.GasDiffusion;
    // A resolved carrier advects a narrow tracer strip without spreading it.
    // Model unresolved transverse boundary dispersion, separately from molecular
    // diffusion: stronger flow mixes farther, with no longitudinal impulse.
    // This is a bounded game closure, not a Reynolds/turbulent-energy solver.
    // Logical ticks keep the same physical pace at every presentation FPS.
    float carrierSpeed = length(drift);
    if (carrierSpeed > 0.0001 && GasNearTransverseWall(coordinate, drift))
    {
        float transverse = (HashUnitFloat(diffusionSeed ^ 0x27d4eb2du) * 2.0 - 1.0) *
            min(1.5, carrierSpeed * 1.5);
        diffusionImpulse += float2(-drift.y, drift.x) * (transverse / carrierSpeed);
    }
    // GasBuoyancy now has TPT gravity semantics: it is a velocity increment
    // per fixed tick and negative values rise because the y axis points down.
    float2 gravity = float2(0, material.GasBuoyancy);
    if ((material.Flags & MaterialFlagThermalCarbonDioxide) != 0)
    {
        // Ideal-gas density relative to ambient dry air at equal pressure.
        // Preserve the calibrated cold CO2 increment at 20 C, but let hot
        // combustion CO2 rise and regain its weight as it cools.
        float referenceKelvin = 293.15;
        float relativeMolecularMass = 44.0095 / 28.97;
        float relativeDensity = relativeMolecularMass * referenceKelvin /
            max(1.0, cell.Temperature + 273.15);
        gravity.y = clamp(material.GasBuoyancy *
            (relativeDensity - 1.0) / (relativeMolecularMass - 1.0), -0.25, 0.25);
    }
    float lateralPressure = 0;
    if (material.GasBuoyancy > 0 && !legacySurfaceCarrier)
    {
        float leftDensity = HeavyGasSideDensity(coordinate, -1);
        float rightDensity = HeavyGasSideDensity(coordinate, 1);
        if (leftDensity >= 0 && rightDensity >= 0)
            lateralPressure = (leftDensity - rightDensity) * material.GasDiffusion * 1.5;
    }
    // Stable gases relax toward the common carrier velocity. Applying CO2's
    // TPT Advection=2 as an additive acceleration makes it overtake both air
    // and steam indefinitely even while cold. Simulation uses that bounded
    // wind response for flame/smoke too; Sandbox keeps its calibrated response.
    float airCoupling = legacySurfaceCarrier ? material.MotionAdvection :
        min(1.0, material.MotionAdvection) * (1.0 - material.MotionLoss);
    if (SolidPass != 0 && legacySurfaceCarrier)
        airCoupling = min(airCoupling, 1.0 - material.MotionLoss);
    state.VelocityX = clamp(
        state.VelocityX * material.MotionLoss +
            drift.x * airCoupling * GasAirVelocityScale + lateralPressure + diffusionImpulse.x,
        -GasMaximumSpeed,
        GasMaximumSpeed);
    state.VelocityY = clamp(
        state.VelocityY * material.MotionLoss +
            drift.y * airCoupling * GasAirVelocityScale + gravity.y + diffusionImpulse.y,
        -GasMaximumSpeed,
        GasMaximumSpeed);
    if(ceilingTangent!=0) state.VelocityX=sign(ceilingTangent)*
        max(abs(ceilingTangent),state.VelocityX*sign(ceilingTangent));
    state.OffsetX = clamp(
        state.OffsetX + state.VelocityX,
        -float(GasMotionSubSteps),
        float(GasMotionSubSteps));
    state.OffsetY = clamp(
        state.OffsetY + state.VelocityY,
        -float(GasMotionSubSteps),
        float(GasMotionSubSteps));
    if ((Materials[cell.MaterialIndex].Flags & MaterialFlagFlame) != 0)
    {
        uint ignored;
        InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].FireCellFrames, 1, ignored);
        InterlockedAdd(
            GasVerticalMotionStatisticsBuffer[0].FireVelocityYMillisteps,
            int(round(state.VelocityY * 1000.0)),
            ignored);
        if (abs(state.OffsetY) >= float(GasMotionSubSteps) - 0.001)
        {
            InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].FireOffsetYClampFrames, 1, ignored);
        }
    }
    GasMotion[index] = state;
    RecordMetalChimneyGasState(coordinate, state);
}

void ConsumeGasOffset(uint index, int stepX, int stepY)
{
    GasMotionState state = GasMotion[index];
    state.OffsetX -= float(stepX);
    state.OffsetY -= float(stepY);
    GasMotion[index] = state;
}

void SlideGasAtCeiling(uint index)
{
    GasMotionState state = GasMotion[index];
    // Collision removes the blocked normal intent, not the wind's tangent.
    // The bypass consumes one pixel; it must not leave an opposite X debt.
    state.VelocityY = 0;
    state.OffsetY = 0;
    state.OffsetX = sign(state.OffsetX) * max(0, abs(state.OffsetX) - 1);
    GasMotion[index] = state;
}

bool ConsolidateDiluteCombustionGas(uint firstIndex, uint secondIndex)
{
    // A tiny emitted CO2 amount must not become an impermeable full-cell
    // obstacle. Merge neighbouring partial packets, preserving total mass,
    // sensible heat and momentum. Painted nominal packets are never merged.
    if (SolidPass == 0) return false; // finite Simulation only
    GridCell first = Grid[firstIndex], second = Grid[secondIndex];
    if (first.IsActive == 0 || second.IsActive == 0 || first.MaterialIndex != second.MaterialIndex) return false;
    MaterialProperties material = Materials[first.MaterialIndex];
    if ((material.Flags & MaterialFlagThermalCarbonDioxide) == 0 ||
        first.Mass >= material.Density || second.Mass >= material.Density) return false;
    float total = first.Mass + second.Mass;
    if (total <= 0 || total > material.Density) return false;
    GasMotionState a = GasMotion[firstIndex], b = GasMotion[secondIndex];
    float2 step = float2(int(secondIndex % Width) - int(firstIndex % Width),
        int(secondIndex / Width) - int(firstIndex / Width));
    float2 velocity = (float2(a.VelocityX,a.VelocityY)*first.Mass +
        float2(b.VelocityX,b.VelocityY)*second.Mass)/total;
    bool keepSecond = dot(velocity,step) > 0;
    if (abs(dot(velocity,step)) < 0.0001)
        keepSecond = HashUnitFloat(firstIndex ^ (DebugReserved2 * 0x9e3779b9u)) < .5;
    GridCell merged = first;
    if (keepSecond) merged = second;
    merged.Mass = total;
    merged.MoistureMass = 0; merged.MoistureEnergy = 0;
    merged.Temperature = (first.Mass*first.Temperature + second.Mass*second.Temperature)/total;
    GasMotionState motion = a;
    if (keepSecond) motion = b;
    motion.VelocityX = velocity.x; motion.VelocityY = velocity.y;
    uint target = keepSecond ? secondIndex : firstIndex, source = keepSecond ? firstIndex : secondIndex;
    Grid[target] = merged; GasMotion[target] = motion; CellMaterials[target] = merged.MaterialIndex;
    Grid[source] = (GridCell)0; GasMotion[source] = (GasMotionState)0; CellMaterials[source] = 0;
    return true;
}

bool GasPairBranch(uint upperKind, uint lowerKind)
{
    return (upperKind == SimulationKindNone || upperKind == SimulationKindGas) &&
        (lowerKind == SimulationKindNone || lowerKind == SimulationKindGas) &&
        (upperKind == SimulationKindGas || lowerKind == SimulationKindGas);
}

float GasCellStepThreshold(uint material)
{
    // Offset is a fractional position relative to the occupied pixel centre.
    // Ordinary particles change pixel at +/-0.5, not after travelling a full
    // cell from its centre. Keep the established flame/smoke carrier intact.
    return (Materials[material].Flags & (MaterialFlagFlame | MaterialFlagSmoke)) != 0
        ? 1.0 : 0.5;
}

void ResolveGasVerticalPair(uint2 upperCoordinate)
{
    if (upperCoordinate.y + 1 >= Height)
    {
        return;
    }
    uint upperIndex = FlattenCoordinate(upperCoordinate);
    uint lowerIndex = upperIndex + Width;
    if (ConsolidateDiluteCombustionGas(upperIndex, lowerIndex)) return;
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
        if (lowerMotion.OffsetY <= -GasCellStepThreshold(lowerMaterial))
        {
            if ((Materials[lowerMaterial].Flags & MaterialFlagFlame) != 0)
            {
                uint ignored;
                InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].FireUpwardCandidates, 1, ignored);
                InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].FireUpwardSteps, 1, ignored);
            }
            ConsumeGasOffset(lowerIndex, 0, -1);
            RecordMetalChimneyUpwardStep(lowerCoordinate);
            MoveGasCell(lowerIndex, upperIndex, GasLateralPathMotionHorizontal);
        }
        return;
    }
    if (lowerKind == SimulationKindGas && lowerCoordinate.y > 0)
    {
        GasMotionState lowerMotion = GasMotion[lowerIndex];
        if (lowerMotion.OffsetY <= -1.0 &&
            (Materials[lowerMaterial].Flags & MaterialFlagFlame) != 0 &&
            upperKind == SimulationKindGas)
        {
            uint ignored;
            InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].FireUpwardCandidates, 1, ignored);
            InterlockedAdd(GasVerticalMotionStatisticsBuffer[0].FireUpwardBlockedByGas, 1, ignored);
            uint previousFrameMarker;
            uint currentFrameMarker = FrameIndex + 1u;
            InterlockedExchange(
                GasVerticalBlockFrameMarkers[lowerIndex],
                currentFrameMarker,
                previousFrameMarker);
            if (previousFrameMarker != currentFrameMarker)
            {
                InterlockedAdd(
                    GasVerticalMotionStatisticsBuffer[0].FireUpwardBlockedCellFrames,
                    1,
                    ignored);
            }
        }
    }
    if (upperKind == SimulationKindGas && GasCanEnter(upperMaterial, lowerMaterial))
    {
        GasMotionState upperMotion = GasMotion[upperIndex];
        if (upperMotion.OffsetY >= GasCellStepThreshold(upperMaterial))
        {
            ConsumeGasOffset(upperIndex, 0, 1);
            MoveGasCell(upperIndex, lowerIndex, GasLateralPathMotionHorizontal);
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
    if (Materials[material].SimulationKind != SimulationKindGas ||
        (Materials[material].Flags & (MaterialFlagFlame | MaterialFlagSmoke)) == 0)
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
                SlideGasAtCeiling(index);
                MoveGasCell(index, sideIndex, GasLateralPathObstacleX);
                return;
            }
        }
    }

    // Keep the Y-only retry explicit in the staircase.  For an upward solid
    // collision it normally fails, but it must precede the diagonal turns.
    if (GasCanEnter(material, aboveCell.MaterialIndex))
    {
        InterlockedAdd(GasObstacleBypassStatistics[2], 1, ignored);
        SlideGasAtCeiling(index);
        MoveGasCell(index, aboveIndex, GasLateralPathMotionHorizontal);
        return;
    }

    // Diagonal ordering is random and equiprobable.  The second side is still
    // tested if the first is occupied, so packing cannot impose a permanent
    // left/right preference.
    int firstDiagonalDirection = HashUnitFloat((index * 0x85ebca6bu) ^
        (DebugReserved2 * 0xc2b2ae35u)) < 0.5 ? -1 : 1;
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
            SlideGasAtCeiling(index);
            MoveGasCell(index, diagonalIndex, GasLateralPathObstacleDiagonal);
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
    if (ConsolidateDiluteCombustionGas(leftIndex, rightIndex)) return;
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

    // Move the pending integrated velocity, including air, diffusion and
    // lateral crowding. Each disjoint pair owns its two cells in this pass.
    bool leftMoves = leftKind == SimulationKindGas &&
        GasCanEnter(leftMaterial, rightMaterial) &&
        GasMotion[leftIndex].OffsetX >= GasCellStepThreshold(leftMaterial);
    if (leftMoves)
    {
        ConsumeGasOffset(leftIndex, 1, 0);
        MoveGasCell(leftIndex, rightIndex, GasLateralPathMotionHorizontal);
        return;
    }
    if (rightKind == SimulationKindGas && GasCanEnter(rightMaterial, leftMaterial))
    {
        if (GasMotion[rightIndex].OffsetX > -GasCellStepThreshold(rightMaterial))
        {
            return;
        }
        ConsumeGasOffset(rightIndex, -1, 0);
        MoveGasCell(rightIndex, leftIndex, GasLateralPathMotionHorizontal);
    }
}

bool OrdinaryGasBlocked(uint material, int2 target)
{
    if (target.x < 0 || target.y < 0 || target.x >= int(Width) || target.y >= int(Height))
    {
        return true;
    }
    GridCell neighbor = Grid[FlattenCoordinate(uint2(target))];
    return neighbor.IsActive != 0 && !GasCanEnter(material, neighbor.MaterialIndex);
}

float2 OrdinaryGasCollisionCarrier(int2 target)
{
    if (target.x < 0 || target.y < 0 || target.x >= int(Width) || target.y >= int(Height)) return 0;
    GridCell neighbor = Grid[FlattenCoordinate(uint2(target))];
    // Gas contact is relative to the common carrier, not a stationary wall.
    // Read stable Air/Grid rather than a neighbour's concurrently written motion.
    return neighbor.IsActive != 0 && CellKind(neighbor) == SimulationKindGas
        ? FlameAirDrift(uint2(target)) : float2(0, 0);
}

// Run once after all movement passes. No cell is moved here: Grid is stable,
// and each thread writes only its own motion. An occupied target may become
// free during the eight carrier substeps; only the remaining blocked intent
// is a collision. Never retain a whole-cell movement debt at a floor/wall or
// inside a dense gas, otherwise a falling gas becomes a packed powder bed.
void ResolveOrdinaryGasCollision(uint2 coordinate)
{
    uint index = FlattenCoordinate(coordinate);
    GridCell cell = Grid[index];
    if (cell.IsActive == 0) return;
    MaterialProperties material = Materials[cell.MaterialIndex];
    if (material.SimulationKind != SimulationKindGas ||
        (material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) != 0) return;
    GasMotionState state = GasMotion[index];
    if (abs(state.OffsetX) >= 0.5 && OrdinaryGasBlocked(cell.MaterialIndex,
        int2(coordinate) + int2(state.OffsetX > 0 ? 1 : -1, 0)))
    {
        float carrierX = OrdinaryGasCollisionCarrier(int2(coordinate) + int2(state.OffsetX > 0 ? 1 : -1, 0)).x;
        carrierX = clamp(carrierX, -GasMaximumSpeed, GasMaximumSpeed);
        state.VelocityX = clamp(carrierX + (state.VelocityX - carrierX) * material.MotionCollision,
            -GasMaximumSpeed, GasMaximumSpeed);
        state.OffsetX = 0;
    }
    if (abs(state.OffsetY) >= 0.5 && OrdinaryGasBlocked(cell.MaterialIndex,
        int2(coordinate) + int2(0, state.OffsetY > 0 ? 1 : -1)))
    {
        float carrierY = OrdinaryGasCollisionCarrier(int2(coordinate) + int2(0, state.OffsetY > 0 ? 1 : -1)).y;
        carrierY = clamp(carrierY, -GasMaximumSpeed, GasMaximumSpeed);
        state.VelocityY = clamp(carrierY + (state.VelocityY - carrierY) * material.MotionCollision,
            -GasMaximumSpeed, GasMaximumSpeed);
        state.OffsetY = 0;
    }
    GasMotion[index] = state;
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
    if ((GasSubStep & 0x80000000u) != 0 && !IsPressurePowder(upperMaterial)) return;
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
            (upperKind!=SimulationKindLiquid || lowerKind!=SimulationKindGranular ||
                (LiquidCanDisplaceGrain(upperCoordinate+uint2(0,1), upperMaterial) &&
                 !GranularLoadedToSink(upperCoordinate+uint2(0,1), upperMaterial))) &&
            CellRankAtIndex(upperIndex) > CellRankAtIndex(lowerIndex);
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
    if ((GasSubStep & 0x80000000u) != 0 && !IsPressurePowder(upperMaterial)) return;
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
        // once, and never consulted the material's motion.advection -- which is why
        // lowering that value from 0.9 to 0.22 and then to 0.06 barely changed
        // the width of the plume. The sideways spread was coming through here.
        return;
    }
    bool canMove = upperKind == SimulationKindGranular
        ? GranularCanMoveTo(upperCoordinate, upperIndex, lowerCoordinate, lowerMaterial)
        : upperKind == SimulationKindLiquid && WaterSupported(upperCoordinate) &&
            (lowerKind!=SimulationKindGranular || (LiquidCanDisplaceGrain(lowerCoordinate, upperMaterial) &&
                !GranularLoadedToSink(lowerCoordinate, upperMaterial))) &&
            CellRankAtIndex(upperIndex) > CellRankAtIndex(lowerIndex);
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
    if ((GasSubStep & 0x80000000u) != 0 &&
        !IsPressurePowder(leftMaterial) && !IsPressurePowder(rightMaterial)) return;
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

bool SameLiquidColumnsConnected(uint firstX, uint secondX, uint top, uint base, uint material)
{
    // Follow overlapping vertical liquid runs. A curved bowl may deepen
    // between its shallow flanks and the underside of floating ice, so a
    // single horizontal row cannot prove its actual connection.
    int direction = secondX > firstX ? 1 : -1;
    uint runTop = top;
    uint runBase = base;
    [loop]
    for (int column = int(firstX) + direction; column != int(secondX) + direction; column += direction)
    {
        int overlap = -1;
        [loop]
        for (int y = int(runBase); y >= int(runTop); y--)
            if (CellMaterials[FlattenCoordinate(uint2(column, y))] == material)
            { overlap = y; break; }
        if (overlap < 0) return false;
        runTop = uint(overlap);
        runBase = uint(overlap);
        [loop]
        while (runTop > top && CellMaterials[FlattenCoordinate(uint2(column, runTop - 1))] == material)
            runTop--;
        [loop]
        while (runBase + 1 < Height && CellMaterials[FlattenCoordinate(uint2(column, runBase + 1))] == material)
            runBase++;
    }
    return true;
}

bool FindOrdinarySurfaceDestination(
    uint sourceX,
    uint sourceTop,
    uint blockLeft,
    uint blockRight,
    out uint destinationX,
    out uint destinationTop)
{
    uint sourceMaterial = CellMaterials[FlattenCoordinate(uint2(sourceX, sourceTop))];
    bool viscous = Materials[sourceMaterial].LiquidFlowTemperatureSensitivity > 0;
    uint sourceBaseLimit; uint sourceTopIgnored;
    if (!UnpackWaterColumn(WaterColumnState[sourceX], sourceTopIgnored, sourceBaseLimit)) return false;
    uint reachableLeft = sourceX;
    while (reachableLeft > blockLeft)
    {
        uint kind = CellKindAt(uint2(reachableLeft - 1, sourceTop));
        // Existing liquid is part of the open surface, not a wall. Rejecting it
        // made a wide pool level one adjacent swap at a time and left a broad
        // ripple behind. Solids and granular matter still stop the transfer, so
        // this cannot jump through a vessel wall or a sand bank.
        uint material = CellMaterials[FlattenCoordinate(uint2(reachableLeft - 1, sourceTop))];
        bool floatingBody = kind == 2 && (Materials[material].Flags & MaterialFlagDensityBody) != 0;
        if (kind != 0 && kind != 4 && kind != 5 && !floatingBody)
        {
            break;
        }
        reachableLeft--;
    }
    uint reachableRight = sourceX;
    while (reachableRight < blockRight)
    {
        uint kind = CellKindAt(uint2(reachableRight + 1, sourceTop));
        uint material = CellMaterials[FlattenCoordinate(uint2(reachableRight + 1, sourceTop))];
        bool floatingBody = kind == 2 && (Materials[material].Flags & MaterialFlagDensityBody) != 0;
        if (kind != 0 && kind != 4 && kind != 5 && !floatingBody)
        {
            break;
        }
        reachableRight++;
    }

    uint rejected[4] = { Width, Width, Width, Width };
    [loop]
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
                top <= sourceTop + (viscous ? 0u : 1u) || top > min(sourceBaseLimit, ignoredBase) ||
                CellMaterials[FlattenCoordinate(uint2(column, top))] != sourceMaterial)
            {
                continue;
            }
            uint destinationKind = CellKindAt(uint2(column, top - 1));
            if (destinationKind != 0 && destinationKind != 5)
            {
                continue;
            }
            uint distance = max(sourceX, column) - min(sourceX, column);
            // Viscous pools still need hydrostatic leveling. Keep it local
            // and let MoveOrdinaryWater apply the temperature-dependent clock.
            if (Materials[sourceMaterial].LiquidFlowTemperatureSensitivity > 0 && distance > 8) continue;
            if (destinationX == Width || top > destinationTop ||
                (top == destinationTop && (viscous ? distance > bestDistance : distance < bestDistance)))
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
        if (verticalPathClear && SameLiquidColumnsConnected(
            sourceX, destinationX, sourceTop, sourceBaseLimit, sourceMaterial))
        {
            return true;
        }
        rejected[attempt] = destinationX;
    }
    return false;
}

bool ResolveOrdinarySurfaceTransfer(uint blockLeft, uint blockRight, bool viscousOnly)
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
        // A column beneath a body is not a free surface. Nor is a one-cell
        // film perched on top of it: choosing that film as the highest donor
        // starved every genuine surface in this block, in perpetuity.
        uint aboveKind = top > 0 ? CellKindAt(uint2(column, top - 1)) : 2;
        bool perchedOnBody = base + 1 < Height &&
            (Materials[CellMaterials[FlattenCoordinate(uint2(column, base + 1))]].Flags & MaterialFlagDensityBody) != 0;
        bool sourceReady = abs(surface.VelocityY) <= 8 &&
            (Materials[surface.MaterialIndex].LiquidFlowTemperatureSensitivity > 0) == viscousOnly &&
            // Viscous liquids use local, clocked flow; a bulk water shortcut
            // would bypass viscosity and visibly teleport a freshly fed oil.
            !perchedOnBody && (aboveKind == 0 || aboveKind == 5) &&
            top < base && CellMaterials[FlattenCoordinate(uint2(column, top + 1))] == surface.MaterialIndex;
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
    bool viscousSource = Materials[CellMaterials[FlattenCoordinate(uint2(sourceLeft,sourceTop))]].LiquidFlowTemperatureSensitivity > 0;
    // A one-cell head permits a local surface hop. Alternate plateau ends
    // so a nearest-neighbour ping-pong at one wall cannot starve the far bank.
    if(viscousSource && (FrameIndex & 1)!=0)
    {
        sourceCandidates[0]=sourceRight;
        sourceCandidates[1]=sourceLeft;
    }
    uint sourceX = Width;
    uint destinationX = Width;
    uint destinationTop = 0;
    uint bestDistance = Width + 1;
    [loop]
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
            (candidateDestinationTop == destinationTop && (viscousSource ? distance > bestDistance : distance < bestDistance)))
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
    if (!MoveOrdinaryWater(sourceIndex, destinationIndex, direction)) return false;
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
    uint transferBudget = 8;
    [loop]
    for (uint transfer = 0; transfer < transferBudget; transfer++)
    {
        if (!ResolveOrdinarySurfaceTransfer(blockLeft, blockRight, false))
        {
            break;
        }
    }
}

void ResolveOrdinaryLocalSurfaceBlock(uint x)
{
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
    uint transferBudget = 4;
    [loop]
    for (uint transfer = 0; transfer < transferBudget; transfer++)
    {
        if (!ResolveOrdinarySurfaceTransfer(blockLeft, blockRight, false))
        {
            break;
        }
    }
}

void ResolveViscousSurfaceBlock(uint x)
{
    // Disjoint short patches avoid starving an entire pool behind one plateau.
    // Alternate their seams; each actual transfer still crosses at most 8 cells.
    uint patchWidth=16, offset=(FrameIndex & 1)*8;
    int start;
    if(x==0)start=-int(offset);
    else if(x>=offset && (x-offset)%patchWidth==0)start=int(x);
    else return;
    uint left=uint(max(0,start)),right=uint(min(int(Width)-1,start+int(patchWidth)-1));
    // A hydrostatic surface patch needs a supported basin. A falling drain
    // or a deep neighbouring outlet keeps its existing viscosity/gravity path;
    // feeding it with this extra pool-head pass would accelerate cold efflux.
    uint minimumBase=Height, maximumBase=0;
    [loop] for(uint column=left;column<=right;column++)
    {
        uint top, base;
        if(!UnpackWaterColumn(WaterColumnState[column],top,base))continue;
        if(Materials[CellMaterials[FlattenCoordinate(uint2(column,top))]].LiquidFlowTemperatureSensitivity<=0)continue;
        if(base+1<Height)
        {
            uint below=CellKindAt(uint2(column,base+1));
            if(below==SimulationKindNone||below==SimulationKindGas)return;
        }
        minimumBase=min(minimumBase,base);maximumBase=max(maximumBase,base);
    }
    if(minimumBase<Height && maximumBase-minimumBase>1)return;
    [loop] for(uint transfer=0;transfer<4;transfer++)
        if(!ResolveOrdinarySurfaceTransfer(left,right,true))break;
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
    if (!LiquidLevelStepAllowed(sourceIndex, destinationIndex)) return;
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
    if (!LiquidStepAllowed(sourceWaterIndex, destinationIndex)) return;
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
    if (!LiquidStepAllowed(sourceIndex, destinationIndex))
    {
        ReleasePressureReservation(destinationIndex, reservation);
        return;
    }
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
        if (WaterCanEnter(cell.MaterialIndex, belowMaterial, coordinate+uint2(0,1)))
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
                    CellMaterials[FlattenCoordinate(diagonalCoordinate)], diagonalCoordinate))
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
        // One writer per column; the other 15 lanes need not rescan it.
        if (dispatchThreadId.y == 0) BuildWaterColumnInfo(dispatchThreadId.x);
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
    if (((SimulationPhase>=80 && SimulationPhase<=83) ||
        (SimulationPhase>=85 && SimulationPhase<=88) || SimulationPhase==90) &&
        GasActiveTiles[(coordinate.y/64)*((Width+63)/64)+coordinate.x/64]==0)
    {
        return;
    }
    if (SimulationPhase == 89)
    {
        IntegrateGasMotion(coordinate);
        return;
    }
    if (SimulationPhase == 90)
    {
        ResolveOrdinaryGasCollision(coordinate);
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
    if (SimulationPhase == 58)
    {
        ResolveViscousSurfaceBlock(coordinate.x);
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
