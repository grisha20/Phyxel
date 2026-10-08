#include "PhysicsShared.hlsli"

// Coarse air field: pressure and velocity on a grid of AirCellSize x AirCellSize
// simulation cells. This is the missing substrate that makes fire read as a
// drawn plume instead of a swarm of independent sparks.
//
// The first four stages are inspired by Air::update_air in The Powder Toy:
//   1. pressure is adjusted from the divergence of velocity;
//   2. velocity is adjusted from the gradient of pressure;
//   3. both are advected with a 3x3 gaussian kernel and a semi-Lagrangian
//      backtrace;
//   4. walls zero the flow across them.
// A matching face-flux pressure projection then couples a sealed channel.
// Simulation conserves carrier volume; Sandbox admits a bounded HotAir source
// so combustion can vent without a lower inlet. Fine-grid metal blocks both.
// Coefficients are per fixed 60 Hz tick, not per render frame.
//
// Every pass reads one buffer and writes the other. Reading and writing the
// same buffer would be a data race here: on the CPU the loops run in order and
// each cell sees its neighbours' previous values, on the GPU it would see a
// mixture of old and new ones.

cbuffer AirSimulationConstants : register(b0)
{
    uint AirWidth;
    uint AirHeight;
    uint AirGridWidth;
    uint AirGridHeight;
    float AirAmbientTemperature;
    float AirHotScale;
    uint AirTickIndex;
    uint AirSandboxMode;
};

StructuredBuffer<MaterialProperties> AirMaterials : register(t0);
StructuredBuffer<GridCell> AirGrid : register(t1);
StructuredBuffer<GasMotionState> AirGasMotion : register(t2);
StructuredBuffer<float2> AirThermal : register(t3);
StructuredBuffer<float4> ReactionPulse : register(t4);
StructuredBuffer<uint> FineMaterialMap : register(t5);
RWStructuredBuffer<uint> FineMaterialMapOutput : register(u6);
RWStructuredBuffer<AirCell> Air : register(u0);
RWStructuredBuffer<AirCell> AirScratch : register(u1);
RWStructuredBuffer<GasAirImpulse> AirGasImpulse : register(u2);
RWStructuredBuffer<uint> AirFlowLinks : register(u3);
// Low-Mach pressure correction: (potential, divergence), temporary only.
// CSInject temporarily uses ProjectionB.y for the sandbox expansion source;
// CSDivergence consumes it before the Jacobi passes overwrite the scratch.
RWStructuredBuffer<float2> ProjectionA : register(u4);
RWStructuredBuffer<float2> ProjectionB : register(u5);

// The pressure grid is coarse, but a thin ordinary metal wall must still
// disconnect its two sides. Bake eight reciprocal links from the fine grid
// once per tick; all pressure, smoothing and backtrace paths use them.
#define FineAirWidth AirGridWidth
#define FineAirHeight AirGridHeight
#define FineAirMaterialAt(p) FineMaterialMap[uint((p).y) * AirGridWidth + uint((p).x)]
#define FineAirMaterials AirMaterials
#define FineAirBlockGranular (AirSandboxMode == 0)
#include "FineAirGeometry.hlsli"

// Prepare a compact authoritative snapshot before the air passes. Geometry
// samples this map repeatedly instead of striding through the full wet cell.
[numthreads(16, 16, 1)]
void CSFineMaterials(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= AirGridWidth || id.y >= AirGridHeight) return;
    uint index = id.y * AirGridWidth + id.x;
    FineMaterialMapOutput[index] = AirGrid[index].IsActive != 0 ? AirGrid[index].MaterialIndex : 0;
}

uint AirLinkBit(int2 delta)
{
    return 1u << uint((delta.y + 1) * 3 + delta.x + 1);
}

bool AirFineLinkOpen(int2 a, int2 b)
{
    int2 start = a * int(AirCellSize) + int(AirCellSize / 2);
    int2 end = b * int(AirCellSize) + int(AirCellSize / 2);
    return AirFineSegmentOpen(start, end);
}

bool AirLinkOpen(int2 a, int2 delta)
{
    int2 b = a + delta;
    if (a.x < 0 || a.y < 0 || a.x >= int(AirWidth) || a.y >= int(AirHeight) ||
        b.x < 0 || b.y < 0 || b.x >= int(AirWidth) || b.y >= int(AirHeight)) return false;
    uint aLinks = AirFlowLinks[uint(a.y) * AirWidth + uint(a.x)];
    uint bLinks = AirFlowLinks[uint(b.y) * AirWidth + uint(b.x)];
    return (aLinks & AirLinkBit(delta)) != 0 && (bLinks & AirLinkBit(-delta)) != 0;
}

bool AirPathOpen(int2 a, int2 b)
{
    int2 difference = b - a;
    int steps = max(abs(difference.x), abs(difference.y));
    int2 previous = a;
    for (int k = 1; k <= steps; k++)
    {
        int2 next = a + int2(round(float2(difference) * float(k) / float(steps)));
        if (!AirLinkOpen(previous, next - previous)) return false;
        previous = next;
    }
    return true;
}

// Air::make_kernel builds a normalised 3x3 gaussian with exp(-2*(i*i+j*j)).
// Precomputed here so the shader does not recompute it per cell.
//   centre exp(0)=1, edge exp(-2)=0.13533528, corner exp(-4)=0.01831564
//   sum = 1 + 4*0.13533528 + 4*0.01831564 = 1.61460368
// Weights must sum to exactly 1: an error of even 0.004 percent per tick is a
// steady energy leak at 60 ticks per second.
static const float AirKernelCentre = 0.61934703;
static const float AirKernelEdge = 0.08381951;
static const float AirKernelCorner = 0.01134374;

static const float AirStepPressure = 0.3;
static const float AirStepVelocity = 0.4;
// AIR_VADV: how much of the back-sampled value is blended in.
static const float AirAdvection = 0.3;

// advDistanceMult: how far back along the flow to look. A separate constant in
// The Powder Toy, and using AirAdvection for both halved the reach.
static const float AirBacktraceDistance = 0.7;
static const float AirVelocityLoss = 0.999;
static const float AirPressureLoss = 0.9999;
// Losses are near unity (PLOSS 0.9999, VLOSS 0.999), so a per-particle
// pressure source that looks small still produces a field of a few units.
// Its magnitude is data-driven by MaterialProperties.HotAir below.
static const float AirHotReference = 1500.0;

// A coarse cell packed with flame may push several times harder than a single
// pixel, so hotness is not clamped to one. The ceiling only stops absurdities.
static const float AirMaximumHotness = 4.0;

// Convection, straight out of Air::update_air, AIRC_BOUSSINESQ:
//     weight = (hv - ambientAirTemp) / 10000
//     if (weight > 0.01) weight = 0.01
//     dvy += weight * convGravY          (convGravY is -1, i.e. upward)
//
// The cap is the whole point and it is what was missing here. However much fire
// there is and however hot it burns, the air gains no more than 0.01 of upward
// velocity per tick. Injecting in proportion to the amount of flame instead let
// a dense point source pump in up to 0.12 per tick, twelve times their ceiling,
// and all that momentum had to go somewhere: it spread radially and the flame
// ballooned to many times the brush width instead of staying a narrow column.
//
// hv is the air temperature, an intensive quantity, so the mean overheat of the
// coarse cell is the right input. Summing it, as the pressure term correctly
// does, would measure how much flame is present rather than how hot it is.
// The Powder Toy divides by 10000 and caps at 0.01, but those numbers belong to
// a model where the plume is driven by a runaway feedback loop and bounded by
// particles physically leaving the cell they are dragging. A coarse 4x4 cell
// cannot reproduce that bound: the brush keeps refilling the source, drag keeps
// accumulating in the same place, and the loop becomes a relaxation oscillator
// -- it ran up to the ceiling, the flame fled the source, drag collapsed, and
// the whole thing started over every few seconds. That is precisely the jet,
// ball, jet cycle seen in play.
//
// So the plume here is driven by convection, which is a bounded driver rather
// than a feedback path, and drag is kept small.
//
// A plume is as long as its rise speed times the flame's lifetime, so with a
// fixed lifetime of about 2.4 seconds the length is purely a matter of speed.
//
// Matching The Powder Toy's measured 130 cells per second turned out to be the
// wrong target for us. Their world is 384 cells tall; ours is 270 at the
// default scale, so the same speed produces a plume that runs off the top of
// the screen. Worse, speed scales with how much flame sits in a coarse cell,
// so a slightly larger brush pushed it well past the edge.
//
// Keep the original bounded convection coefficients. Furnace circulation
// additionally depends on connected geometry, gas drag and sustained fuel heat.
static const float AirConvectionDivisor = 2000.0;
static const float AirConvectionMaximum = 0.05;
// A small gameplay increase for the combustion plume only. Keep the bounded
// forcing and dissipative wind response; do not accelerate the global clock.
static const float AirTransientConvectionGain = 1.15;
// Gameplay expansion per carrier volume, calibrated against the saved furnace
// with both an open inlet and a sealed inlet; not a finite-air thermodynamic law.
static const float AirSandboxExpansionGain = 1.5;
static const float AirSandboxMaximumExpansion = 0.0075;

// Particle drag, from the main particle loop in Simulation.cpp. FIRE and SMKE
// both use AirLoss 0.97 and AirDrag 0.04.
// Must match GasMaximumSpeed in CellularAutomataSolver.hlsl. Air only receives
// the velocity the cellular FIRE/SMKE carrier can actually realise.
static const float AirGasMaximumSpeed = 7.2;
// Must mirror CellularAutomataSolver.hlsl. One accumulator unit is a
// material-weighted velocity contribution, not a universal AirDrag.
static const float GasAirImpulseFixedPointScale = 100000.0;

// The same carrier has a stronger feedback loop than TPT's independent
// particles, so keep this just below the literal 0.04 to avoid a pulse/ball.


// The fastest a gas cell can actually travel: GasMotionSubSteps passes, each
// stepping at most one cell and capped at a 0.9 probability.
//
// Clamping to it is essential, not cosmetic. Drag has to be driven by how fast
// the flame is really moving; feeding the air's own velocity back in unclamped
// turned the term into "multiply yourself by 2.7 every tick", the field hit its
// ceiling within a dozen ticks and the pressure waves smeared that across the
// entire world. Fire in a saturated field is pushed every which way, which is
// exactly the ball that would not go away. A cell cannot outrun its own passes,
// so neither can the air it drags.
static const float FlameMaximumSpeed = 7.2;
static const float AirMaximumPressure = 256.0;
static const float AirMaximumVelocity = 64.0;
static const float AirMaximumAdvectionDistance = 2.0;
static const float AirEdgePressure = 0.0;
static const float AirEdgeVelocity = 0.0;
// Air::update_air uses vorticityCoeff=0.1.  Without confinement a coarse
// plume diffuses its shear away at the Gaussian advection step, so the air
// above a horizontal obstacle has no persistent two-sided circulation.
static const float AirVorticityCoefficient = 0.1;

uint AirIndex(uint2 coordinate)
{
    return coordinate.y * AirWidth + coordinate.x;
}

bool AirInside(int2 coordinate)
{
    return coordinate.x >= 0 && coordinate.y >= 0 &&
        coordinate.x < int(AirWidth) && coordinate.y < int(AirHeight);
}

float AirKernelWeight(int offsetX, int offsetY)
{
    int distance = abs(offsetX) + abs(offsetY);
    if (distance == 0)
    {
        return AirKernelCentre;
    }
    return distance == 1 ? AirKernelEdge : AirKernelCorner;
}

AirCell ReadAir(int2 coordinate)
{
    int2 limit = int2(int(AirWidth) - 1, int(AirHeight) - 1);
    return Air[AirIndex(uint2(clamp(coordinate, int2(0, 0), limit)))];
}

AirCell ReadScratch(int2 coordinate)
{
    int2 limit = int2(int(AirWidth) - 1, int(AirHeight) - 1);
    return AirScratch[AirIndex(uint2(clamp(coordinate, int2(0, 0), limit)))];
}

float AirVorticity(int2 coordinate)
{
    float rightY = AirLinkOpen(coordinate, int2(1, 0)) ? ReadAir(coordinate + int2(1, 0)).VelocityY : 0;
    float leftY = AirLinkOpen(coordinate, int2(-1, 0)) ? ReadAir(coordinate + int2(-1, 0)).VelocityY : 0;
    float downX = AirLinkOpen(coordinate, int2(0, 1)) ? ReadAir(coordinate + int2(0, 1)).VelocityX : 0;
    float upX = AirLinkOpen(coordinate, int2(0, -1)) ? ReadAir(coordinate + int2(0, -1)).VelocityX : 0;
    return 0.5 * (rightY - leftY - downX + upX);
}

bool AirBlockedAt(int2 coordinate)
{
    if (!AirInside(coordinate))
    {
        return true;
    }
    return ReadAir(coordinate).Blocked > 0.5;
}

// Bake fine geometry and collect connected gas pressure, heat and drag sources.
[numthreads(8, 8, 1)]
void CSInject(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= AirWidth || coordinate.y >= AirHeight)
    {
        return;
    }

    uint index = AirIndex(coordinate);
    AirCell cell = Air[index];
    // Remove last tick's compressible overlay before solving ordinary draft.
    // The pulse is evolved separately and reattached after the projection.
    float4 pulse=ReactionPulse[index];
    float leftPulse=coordinate.x>0?ReactionPulse[index-1].y:0;
    float upPulse=coordinate.y>0?ReactionPulse[index-AirWidth].z:0;
    cell.Pressure-=pulse.x;
    cell.VelocityX-=(leftPulse+pulse.y)*.5;
    cell.VelocityY-=(upPulse+pulse.z)*.5;
    uint links = 0;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        int2 delta = int2(dx, dy);
        if ((dx != 0 || dy != 0) && AirFineLinkOpen(int2(coordinate), int2(coordinate) + delta))
            links |= AirLinkBit(delta);
    }
    AirFlowLinks[index] = links;
    // The move accumulator remains available to diagnostic observers, but
    // physical drag below reads one previous velocity at its current location.
    // Clear prior move statistics exactly once, even if this node is now empty.
    AirGasImpulse[index] = (GasAirImpulse)0;

    uint left = coordinate.x * AirCellSize;
    uint top = coordinate.y * AirCellSize;
    uint counted = min(AirCellSize, AirGridWidth - left) * min(AirCellSize, AirGridHeight - top);
    uint gasCount = 0;
    uint transientCount = 0;
    float airLossProduct = 1.0;
    float airDragSum = 0;
    float2 particleDrag = float2(0, 0);
    float heat = 0;
    float hotAirInjection = 0;

    [loop] for (int offsetY = -2; offsetY <= int(AirCellSize) + 2; offsetY++)
    {
        int y = int(top) + offsetY;
        if (y < 0 || y >= int(AirGridHeight))
        {
            continue;
        }
        [loop] for (int offsetX = -2; offsetX <= int(AirCellSize) + 2; offsetX++)
        {
            int x = int(left) + offsetX;
            if (x < 0 || x >= int(AirGridWidth))
            {
                continue;
            }
            GridCell source = AirGrid[uint(y) * AirGridWidth + uint(x)];
            if (source.IsActive == 0)
            {
                continue;
            }
            MaterialProperties material = AirMaterials[source.MaterialIndex];
            if (material.SimulationKind == SimulationKindSolid || material.SimulationKind == SimulationKindLiquid) continue;
            int2 sourceNode;
            if (!AirFineNodeFor(int2(x, y), sourceNode) || any(sourceNode != int2(coordinate))) continue;
            if (material.SimulationKind == SimulationKindGas)
            {
                gasCount++;
                if ((material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) != 0) transientCount++;
                airLossProduct *= material.MotionAirLoss;
                airDragSum += material.MotionAirDrag;
                GasMotionState motion = AirGasMotion[uint(y) * AirGridWidth + uint(x)];
                // Drag responds to slip, rather than adding the carrier's own
                // velocity back into itself at every occupied particle.
                particleDrag += (clamp(float2(motion.VelocityX, motion.VelocityY),
                    -AirGasMaximumSpeed, AirGasMaximumSpeed) -
                    float2(cell.VelocityX, cell.VelocityY)) * material.MotionAirDrag;
                if (material.HotAir != 0.0 && (AirSandboxMode != 0 || (material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) == 0))
                {
                    // Sandbox admits a bounded gameplay volume source below.
                    // Simulation keeps the ordinary phase-gas pressure source;
                    // its flame/smoke buoyancy comes from transported air heat.
                    hotAirInjection += 4.0 * material.HotAir;
                }
            }
            if (material.SimulationKind == SimulationKindSolid)
            {
                // Solid temperatures do not directly inject a gas impulse.
                continue;
            }
            // Temperature drives the optional Boussinesq convection term below.
            // It is separate from the material HotAir pressure source above.
            heat += max(0.0, source.Temperature - AirAmbientTemperature);
        }
    }

    // Occupied pressure nodes are disabled; fine-grid links additionally seal
    // thin walls between nodes, independent of the old 75% occupancy threshold.
    cell.Blocked = AirFineBlocked(int2(coordinate) * int(AirCellSize) + int(AirCellSize / 2)) ? 1.0 : 0.0;

    if (cell.Blocked > 0.5)
    {
        cell.Pressure = 0;
        cell.VelocityX = 0;
        cell.VelocityY = 0;
        Air[index] = cell;
        return;
    }

    // Keep phase-gas pressure separate from temperature-driven buoyancy.
    // TPT's continuously replenished HotAir is a gameplay source, rather
    // than finite air heated once. A zero-divergence projection would erase
    // its expansion and demand an inlet for every outlet. Sandbox admits a
    // bounded volume source in that projection instead. Keep the channel's
    // fine walls so expansion takes an actual opening, not a path through metal.
    // A 4x4 carrier volume must not expand sixteen times faster merely because
    // it is drawn densely. Average the per-packet source over that volume.
    float meanOverheat = heat / max(1.0, float(counted));
    float expansion = AirSandboxMode != 0 ? min(AirSandboxMaximumExpansion,
        AirSandboxExpansionGain * max(0.0, hotAirInjection) / max(1.0, float(counted))) : 0;
    ProjectionB[index].y = expansion;
    // Balance the pressure equation's loss from the admitted divergence too,
    // otherwise continuing expansion accumulates a spurious negative pressure.
    cell.Pressure += expansion * AirStepPressure;
    if (AirSandboxMode == 0) cell.Pressure += hotAirInjection;

    // Bounded temperature-driven convection. These coefficients are calibrated
    // for our sealed fine-grid passages, rather than TPT's air-transparent
    // ordinary metal. The y axis grows downward, so rising air is negative.
    float2 thermal = AirThermal[index];
    float carrierOverheat = thermal.y > 1e-8 ? max(0, thermal.x / thermal.y - 273.15 - AirAmbientTemperature) : 0;
    // Finite stock carries chimney heat even after the flame has decayed.
    // Calibrate the whole heated circuit, not just the local particle plume.
    float convection = min(AirSandboxMode != 0 ? AirConvectionMaximum : 0.12,
        (AirSandboxMode != 0 ? meanOverheat : carrierOverheat) / AirConvectionDivisor);
    convection *= transientCount>0 ? AirTransientConvectionGain : 1.0;
    cell.VelocityY = clamp(
        cell.VelocityY - max(0.0, convection),
        -AirMaximumVelocity,
        AirMaximumVelocity);

    // Read previous particle velocity once at its current location, rather than
    // summing repeated moves whose particles have already left this air node.
    // Air is one carrier volume. Per-particle drag is proportional to loading;
    // the ambient loss acts once on that volume, not once per drawn pixel.
    // Include empty slots (loss=1) in the volume average. A single smoke
    // packet must not damp the entire 4x4 volume like sixteen packets.
    float retained = gasCount > 0 ? pow(airLossProduct, 1.0 / float(counted)) : 1.0;
    // FIRE/SMKE's additive advection has a steady wind gain of 0.9/(1-.2).
    // With sixteen packets, summed slip drag feeds that gain back sixteen
    // times while volume loss acts once: .97 + .64 * .125 > 1. Unlimited
    // sandbox smoke therefore self-accelerates in a closed-bottom chamber.
    // Bound total slip drag, without dividing it down to one particle's .04.
    // That old average made a dense flame drive its carrier too weakly.
    // For FIRE/SMKE this bound retains .97 + .16*.125 = .99 < 1, so a
    // full carrier still dissipates the additive-advection feedback.
    // Finite FIRE/SMKE now relax toward the common carrier (steady gain <=1),
    // so their slip cannot amplify wind. Sandbox retains its calibrated TPT
    // additive response and therefore needs the stricter .16 bound.
    float limit = AirSandboxMode != 0 ? .16 : .9;
    float dragAttenuation = airDragSum > limit ? limit / airDragSum : 1.0;
    float2 carrier = float2(cell.VelocityX, cell.VelocityY) * retained + particleDrag * dragAttenuation;
    cell.VelocityX = clamp(
        carrier.x,
        -AirMaximumVelocity,
        AirMaximumVelocity);
    cell.VelocityY = clamp(
        carrier.y,
        -AirMaximumVelocity,
        AirMaximumVelocity);

    cell.Pressure = clamp(cell.Pressure, -AirMaximumPressure, AirMaximumPressure);
    Air[index] = cell;
}

// Pressure follows the divergence of velocity: air flowing into a cell from
// both sides compresses it. Reads Air, writes AirScratch.
[numthreads(8, 8, 1)]
void CSPressure(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= AirWidth || coordinate.y >= AirHeight)
    {
        return;
    }

    int2 signedCoordinate = int2(coordinate);
    AirCell cell = Air[AirIndex(coordinate)];

    bool edge = coordinate.x < 1 || coordinate.y < 1 ||
        coordinate.x + 1 >= AirWidth || coordinate.y + 1 >= AirHeight;
    if (edge || cell.Blocked > 0.5)
    {
        // The border bleeds off to ambient so shockwaves leave the world
        // instead of reflecting back from it forever.
        cell.Pressure = lerp(AirEdgePressure, cell.Pressure, edge ? 0.8 : AirPressureLoss);
        if (cell.Blocked > 0.5)
        {
            cell.Pressure = 0;
            cell.VelocityX = 0;
            cell.VelocityY = 0;
        }
        AirScratch[AirIndex(coordinate)] = cell;
        return;
    }

    // Face flux is exactly zero at a wall, rather than borrowing the pressure
    // or velocity from the chamber on its other side.
    float leftFlux = AirLinkOpen(signedCoordinate, int2(-1, 0)) ?
        (cell.VelocityX + ReadAir(signedCoordinate + int2(-1, 0)).VelocityX) * 0.5 : 0;
    float rightFlux = AirLinkOpen(signedCoordinate, int2(1, 0)) ?
        (cell.VelocityX + ReadAir(signedCoordinate + int2(1, 0)).VelocityX) * 0.5 : 0;
    float upFlux = AirLinkOpen(signedCoordinate, int2(0, -1)) ?
        (cell.VelocityY + ReadAir(signedCoordinate + int2(0, -1)).VelocityY) * 0.5 : 0;
    float downFlux = AirLinkOpen(signedCoordinate, int2(0, 1)) ?
        (cell.VelocityY + ReadAir(signedCoordinate + int2(0, 1)).VelocityY) * 0.5 : 0;
    float divergence = leftFlux - rightFlux + upFlux - downFlux;

    cell.Pressure = lerp(AirEdgePressure, cell.Pressure, AirPressureLoss);
    cell.Pressure = clamp(
        cell.Pressure + divergence * AirStepPressure,
        -AirMaximumPressure,
        AirMaximumPressure);
    AirScratch[AirIndex(coordinate)] = cell;
}

// Velocity follows the gradient of pressure: air accelerates away from a high
// pressure region. Reads AirScratch, writes Air.
[numthreads(8, 8, 1)]
void CSVelocity(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= AirWidth || coordinate.y >= AirHeight)
    {
        return;
    }

    int2 signedCoordinate = int2(coordinate);
    AirCell cell = AirScratch[AirIndex(coordinate)];

    bool edge = coordinate.x < 1 || coordinate.y < 1 ||
        coordinate.x + 1 >= AirWidth || coordinate.y + 1 >= AirHeight;
    if (edge || cell.Blocked > 0.5)
    {
        cell.VelocityX = lerp(AirEdgeVelocity, cell.VelocityX, edge ? 0.9 : AirVelocityLoss);
        cell.VelocityY = lerp(AirEdgeVelocity, cell.VelocityY, edge ? 0.9 : AirVelocityLoss);
        if (cell.Blocked > 0.5)
        {
            cell.VelocityX = 0;
            cell.VelocityY = 0;
        }
        Air[AirIndex(coordinate)] = cell;
        return;
    }

    float leftP = cell.Pressure, rightP = cell.Pressure, upP = cell.Pressure, downP = cell.Pressure;
    if (AirLinkOpen(signedCoordinate, int2(-1, 0))) leftP = ReadScratch(signedCoordinate + int2(-1, 0)).Pressure;
    if (AirLinkOpen(signedCoordinate, int2(1, 0))) rightP = ReadScratch(signedCoordinate + int2(1, 0)).Pressure;
    if (AirLinkOpen(signedCoordinate, int2(0, -1))) upP = ReadScratch(signedCoordinate + int2(0, -1)).Pressure;
    if (AirLinkOpen(signedCoordinate, int2(0, 1))) downP = ReadScratch(signedCoordinate + int2(0, 1)).Pressure;
    float gradientX = leftP - rightP;
    float gradientY = upP - downP;

    cell.VelocityX = lerp(AirEdgeVelocity, cell.VelocityX, AirVelocityLoss) +
        gradientX * AirStepVelocity * 0.5;
    cell.VelocityY = lerp(AirEdgeVelocity, cell.VelocityY, AirVelocityLoss) +
        gradientY * AirStepVelocity * 0.5;

    // A wall stops the component that would cross it; tangential flow remains.
    if ((cell.VelocityX < 0 && !AirLinkOpen(signedCoordinate, int2(-1, 0))) ||
        (cell.VelocityX > 0 && !AirLinkOpen(signedCoordinate, int2(1, 0))))
    {
        cell.VelocityX = 0;
    }
    if ((cell.VelocityY < 0 && !AirLinkOpen(signedCoordinate, int2(0, -1))) ||
        (cell.VelocityY > 0 && !AirLinkOpen(signedCoordinate, int2(0, 1))))
    {
        cell.VelocityY = 0;
    }

    cell.VelocityX = clamp(cell.VelocityX, -AirMaximumVelocity, AirMaximumVelocity);
    cell.VelocityY = clamp(cell.VelocityY, -AirMaximumVelocity, AirMaximumVelocity);
    Air[AirIndex(coordinate)] = cell;
}

// Smooth with the gaussian kernel, then carry the field along its own flow by
// sampling backwards from where it came. Reads Air, writes AirScratch.
[numthreads(8, 8, 1)]
void CSAdvect(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= AirWidth || coordinate.y >= AirHeight)
    {
        return;
    }

    int2 signedCoordinate = int2(coordinate);
    AirCell cell = Air[AirIndex(coordinate)];
    if (cell.Blocked > 0.5)
    {
        AirScratch[AirIndex(coordinate)] = cell;
        return;
    }

    float velocityX = 0;
    float velocityY = 0;
    float pressure = 0;
    for (int offsetY = -1; offsetY <= 1; offsetY++)
    {
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            float weight = AirKernelWeight(offsetX, offsetY);
            int2 sample = signedCoordinate + int2(offsetX, offsetY);
            // A blocked or out-of-world neighbour contributes this cell's own
            // value, exactly as The Powder Toy does, so a wall neither drags
            // the flow nor injects a spurious gradient.
            // Written as an if rather than ?: on purpose: HLSL has no ternary
            // operator for struct types, only for scalars and vectors.
            AirCell neighbor = cell;
            if (AirInside(sample) && AirPathOpen(signedCoordinate, sample))
            {
                neighbor = ReadAir(sample);
            }
            velocityX += neighbor.VelocityX * weight;
            velocityY += neighbor.VelocityY * weight;
            pressure += neighbor.Pressure * weight;
        }
    }

    // Backtrace with bilinear sampling, as Air::update_air does.
    //
    // Truncating the source coordinate to an integer instead was a hard
    // directional bias, not a rounding detail. Take a flow of +0.2 to the right:
    // the source lands at x - 0.06, int() gives x - 1, and the cell inherits the
    // whole of its left neighbour. Mirror it to -0.2 and the source is x + 0.06,
    // int() gives x, so nothing is carried at all. One sign of velocity got
    // thirty percent of a neighbour at arbitrarily small speeds while the other
    // got none. Under an obstacle, where the flow splits symmetrically, that
    // steadily strengthened one branch and curled the plume into a hook.
    //
    // The two constants are also distinct in The Powder Toy and were conflated
    // here: advDistanceMult = 0.7 scales how far back to look, AIR_VADV = 0.3
    // is how much of the sampled value to blend in.
    float2 displacement = clamp(
        float2(velocityX, velocityY) * AirBacktraceDistance,
        -AirMaximumAdvectionDistance,
        AirMaximumAdvectionDistance);
    float sourceX = float(coordinate.x) - displacement.x;
    float sourceY = float(coordinate.y) - displacement.y;
    if (sourceX >= 1.0 && sourceY >= 1.0 &&
        sourceX <= float(AirWidth) - 2.0 && sourceY <= float(AirHeight) - 2.0)
    {
        int2 corner = int2(floor(sourceX), floor(sourceY));
        float2 f = float2(sourceX - float(corner.x), sourceY - float(corner.y));
        // TPT's bilinear advection substitutes the old value independently at
        // each blocked corner.  Rejecting the whole four-corner sample when
        // only one corner is a wall leaks the opposite corner's flow through a
        // plate edge and creates a persistent one-sided hook.
        AirCell s00 = cell;
        AirCell s10 = cell;
        AirCell s01 = cell;
        AirCell s11 = cell;
        if (AirPathOpen(signedCoordinate, corner)) s00 = ReadAir(corner);
        if (AirPathOpen(signedCoordinate, corner + int2(1, 0))) s10 = ReadAir(corner + int2(1, 0));
        if (AirPathOpen(signedCoordinate, corner + int2(0, 1))) s01 = ReadAir(corner + int2(0, 1));
        if (AirPathOpen(signedCoordinate, corner + int2(1, 1))) s11 = ReadAir(corner + int2(1, 1));

        float w00 = (1.0 - f.x) * (1.0 - f.y);
        float w10 = f.x * (1.0 - f.y);
        float w01 = (1.0 - f.x) * f.y;
        float w11 = f.x * f.y;

        float carriedX = s00.VelocityX * w00 + s10.VelocityX * w10 +
            s01.VelocityX * w01 + s11.VelocityX * w11;
        float carriedY = s00.VelocityY * w00 + s10.VelocityY * w10 +
            s01.VelocityY * w01 + s11.VelocityY * w11;
        float carriedP = s00.Pressure * w00 + s10.Pressure * w10 +
            s01.Pressure * w01 + s11.Pressure * w11;

        velocityX = lerp(velocityX, carriedX, AirAdvection);
        velocityY = lerp(velocityY, carriedY, AirAdvection);
        pressure = lerp(pressure, carriedP, AirAdvection);
    }

    // Vorticity confinement, verbatim in structure from Air.cpp.  The
    // pressure solve creates shear where the rising jet meets the plate, but
    // semi-Lagrangian advection damps that shear on a coarse grid.  Reinforce
    // the circulation normal to the vorticity gradient; this is what carries
    // SMKE out from both ends instead of letting it collapse back into the
    // centreline.  Keep the wall cells excluded just as vorticityBmap does.
    // Confinement adds energy to circulation. Unlimited sandbox combustion
    // in a closed-bottom chamber otherwise amplifies a trapped vortex even
    // when its expansion source is small. Advection retains natural eddies.
    if (AirSandboxMode == 0 && AirVorticityCoefficient > 0.0 &&
        coordinate.x > 1 && coordinate.x + 2 < AirWidth &&
        coordinate.y > 1 && coordinate.y + 2 < AirHeight)
    {
        int2 signedCoordinate = int2(coordinate);
        float w = AirVorticity(signedCoordinate);
        float centerCurl = abs(w);
        float rightCurl = AirLinkOpen(signedCoordinate, int2(1, 0)) ? abs(AirVorticity(signedCoordinate + int2(1, 0))) : centerCurl;
        float leftCurl = AirLinkOpen(signedCoordinate, int2(-1, 0)) ? abs(AirVorticity(signedCoordinate + int2(-1, 0))) : centerCurl;
        float downCurl = AirLinkOpen(signedCoordinate, int2(0, 1)) ? abs(AirVorticity(signedCoordinate + int2(0, 1))) : centerCurl;
        float upCurl = AirLinkOpen(signedCoordinate, int2(0, -1)) ? abs(AirVorticity(signedCoordinate + int2(0, -1))) : centerCurl;
        float dwx = (rightCurl - leftCurl) * 0.5;
        float dwy = (downCurl - upCurl) * 0.5;
        float norm = sqrt(dwx * dwx + dwy * dwy);
        velocityX += AirVorticityCoefficient / 5.0 * dwy / (norm + 0.001) * w;
        velocityY += AirVorticityCoefficient / 5.0 * (-dwx) / (norm + 0.001) * w;
    }

    cell.VelocityX = clamp(velocityX, -AirMaximumVelocity, AirMaximumVelocity);
    cell.VelocityY = clamp(velocityY, -AirMaximumVelocity, AirMaximumVelocity);
    cell.Pressure = clamp(pressure, -AirMaximumPressure, AirMaximumPressure);
    AirScratch[AirIndex(coordinate)] = cell;
}

// Publish the advected field. Reads AirScratch, writes Air.
[numthreads(8, 8, 1)]
void CSCommit(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= AirWidth || coordinate.y >= AirHeight)
    {
        return;
    }
    uint index = AirIndex(coordinate);
    Air[index] = AirScratch[index];
}

bool ProjectionBoundary(int2 p)
{
    return p.x < 1 || p.y < 1 || p.x + 1 >= int(AirWidth) || p.y + 1 >= int(AirHeight);
}

[numthreads(8, 8, 1)]
void CSFaces(uint3 id : SV_DispatchThreadID)
{
    int2 p = int2(id.xy);
    if (!AirInside(p)) return;
    uint i = AirIndex(id.xy);
    AirCell c = Air[i];
    AirCell faces = c;
    faces.VelocityX = AirLinkOpen(p, int2(1, 0))
        ? (c.VelocityX + ReadAir(p + int2(1, 0)).VelocityX) * 0.5 : 0;
    faces.VelocityY = AirLinkOpen(p, int2(0, 1))
        ? (c.VelocityY + ReadAir(p + int2(0, 1)).VelocityY) * 0.5 : 0;
    AirScratch[i] = faces;
}

[numthreads(8, 8, 1)]
void CSDivergence(uint3 id : SV_DispatchThreadID)
{
    int2 p = int2(id.xy);
    if (!AirInside(p)) return;
    uint i = AirIndex(id.xy);
    AirCell c = Air[i];
    if (c.Blocked > 0.5 || ProjectionBoundary(p)) { ProjectionA[i] = 0; return; }
    float l = AirLinkOpen(p, int2(-1, 0)) ? AirScratch[i - 1].VelocityX : 0;
    float r = AirScratch[i].VelocityX;
    float u = AirLinkOpen(p, int2(0, -1)) ? AirScratch[i - AirWidth].VelocityY : 0;
    float d = AirScratch[i].VelocityY;
    // Divergence and the pressure gradient act on the SAME face fluxes.
    // Their composition is exactly the Laplacian solved below. A collocated
    // central gradient instead leaves unresolved alternating pressure modes.
    ProjectionA[i] = float2(ProjectionA[i].x, r - l + d - u - ProjectionB[i].y);
}

float2 Jacobi(int2 p, bool readA)
{
    uint i = uint(p.y) * AirWidth + uint(p.x);
    if (Air[i].Blocked > 0.5 || ProjectionBoundary(p)) return 0;
    float2 own = readA ? ProjectionA[i] : ProjectionB[i];
    float sum = 0, faces = 0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        int2 delta = k == 0 ? int2(-1, 0) : k == 1 ? int2(1, 0) : k == 2 ? int2(0, -1) : int2(0, 1);
        if (!AirLinkOpen(p, delta)) continue;
        uint j = uint(p.y + delta.y) * AirWidth + uint(p.x + delta.x);
        sum += readA ? ProjectionA[j].x : ProjectionB[j].x;
        faces += 1;
    }
    float potential = faces > 0 ? (sum - own.y) / faces : 0;
    // A completely closed sandbox box has no outlet for its gameplay source.
    // Bound its correction instead of accumulating an unsolvable mean forever.
    potential = clamp(potential, -AirMaximumPressure, AirMaximumPressure);
    return float2(potential, own.y);
}
[numthreads(8, 8, 1)]
void CSJacobiAB(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= AirWidth || id.y >= AirHeight) return;
    ProjectionB[AirIndex(id.xy)] = Jacobi(int2(id.xy), true);
}
[numthreads(8, 8, 1)]
void CSJacobiBA(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= AirWidth || id.y >= AirHeight) return;
    ProjectionA[AirIndex(id.xy)] = Jacobi(int2(id.xy), false);
}

// Four exact Jacobi iterations over an 8x8 output tile and a four-cell halo.
// Separate global input/output buffers prevent races between neighbouring groups.
groupshared float TilePsi0[256];
groupshared float TilePsi1[256];
groupshared float TileDivergence[256];
groupshared uint TileFaces[256];
void JacobiFour(uint2 group, uint thread, bool readA)
{
    int2 origin=int2(group)*8-4;
    [unroll] for(uint j=thread;j<256;j+=64)
    {
        int2 p=origin+int2(j%16,j/16);
        float2 v=0;uint mask=16;
        if(AirInside(p))
        {
            uint i=uint(p.y)*AirWidth+uint(p.x);
            v=readA?ProjectionA[i]:ProjectionB[i];
            if(Air[i].Blocked<=.5 && !ProjectionBoundary(p))
            {
                mask=0;
                if(AirLinkOpen(p,int2(-1,0)))mask|=1;
                if(AirLinkOpen(p,int2(1,0)))mask|=2;
                if(AirLinkOpen(p,int2(0,-1)))mask|=4;
                if(AirLinkOpen(p,int2(0,1)))mask|=8;
            }
        }
        TilePsi0[j]=v.x;TileDivergence[j]=v.y;TileFaces[j]=mask;
    }
    GroupMemoryBarrierWithGroupSync();
    [unroll] for(uint step=1;step<=4;step++)
    {
        [unroll] for(uint j=thread;j<256;j+=64)
        {
            uint x=j%16,y=j/16;
            if(x<step||y<step||x>=16-step||y>=16-step)continue;
            uint mask=TileFaces[j];float sum=0,faces=0,value=0;
            if((mask&16)==0)
            {
                if(mask&1){sum+=step%2?TilePsi0[j-1]:TilePsi1[j-1];faces+=1;}
                if(mask&2){sum+=step%2?TilePsi0[j+1]:TilePsi1[j+1];faces+=1;}
                if(mask&4){sum+=step%2?TilePsi0[j-16]:TilePsi1[j-16];faces+=1;}
                if(mask&8){sum+=step%2?TilePsi0[j+16]:TilePsi1[j+16];faces+=1;}
                value=faces>0?clamp((sum-TileDivergence[j])/faces,-AirMaximumPressure,AirMaximumPressure):0;
            }
            if(step%2)TilePsi1[j]=value;else TilePsi0[j]=value;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    uint2 p=group*8+uint2(thread%8,thread/8);
    if(p.x>=AirWidth||p.y>=AirHeight)return;
    uint outputIndex=(thread/8+4)*16+thread%8+4;
    float2 result=(TileFaces[outputIndex]&16)?0:float2(TilePsi0[outputIndex],TileDivergence[outputIndex]);
    if(readA)ProjectionB[AirIndex(p)]=result;else ProjectionA[AirIndex(p)]=result;
}
[numthreads(8,8,1)]
void CSJacobiFourAB(uint3 group:SV_GroupID,uint thread:SV_GroupIndex){JacobiFour(group.xy,thread,true);}
[numthreads(8,8,1)]
void CSJacobiFourBA(uint3 group:SV_GroupID,uint thread:SV_GroupIndex){JacobiFour(group.xy,thread,false);}

[numthreads(8, 8, 1)]
void CSProject(uint3 id : SV_DispatchThreadID)
{
    int2 p = int2(id.xy);
    if (!AirInside(p)) return;
    uint i = AirIndex(id.xy);
    AirCell c = Air[i];
    if (c.Blocked > 0.5 || ProjectionBoundary(p)) return;
    float own = ProjectionA[i].x;
    float l = own, r = own, u = own, d = own;
    if (AirLinkOpen(p, int2(-1, 0))) l = ProjectionA[i - 1].x;
    if (AirLinkOpen(p, int2(1, 0))) r = ProjectionA[i + 1].x;
    if (AirLinkOpen(p, int2(0, -1))) u = ProjectionA[i - AirWidth].x;
    if (AirLinkOpen(p, int2(0, 1))) d = ProjectionA[i + AirWidth].x;
    float leftFlux = AirLinkOpen(p, int2(-1, 0)) ? AirScratch[i - 1].VelocityX - (own - l) : 0;
    float rightFlux = AirLinkOpen(p, int2(1, 0)) ? AirScratch[i].VelocityX - (r - own) : 0;
    float upFlux = AirLinkOpen(p, int2(0, -1)) ? AirScratch[i - AirWidth].VelocityY - (own - u) : 0;
    float downFlux = AirLinkOpen(p, int2(0, 1)) ? AirScratch[i].VelocityY - (d - own) : 0;
    c.VelocityX = (leftFlux + rightFlux) * 0.5;
    c.VelocityY = (upFlux + downFlux) * 0.5;
    if ((c.VelocityX < 0 && !AirLinkOpen(p, int2(-1, 0))) || (c.VelocityX > 0 && !AirLinkOpen(p, int2(1, 0)))) c.VelocityX = 0;
    if ((c.VelocityY < 0 && !AirLinkOpen(p, int2(0, -1))) || (c.VelocityY > 0 && !AirLinkOpen(p, int2(0, 1)))) c.VelocityY = 0;
    Air[i] = c;
}

// Zero the whole field, used on world reset and when the feature is switched
// off so a stale plume cannot reappear later.
[numthreads(8, 8, 1)]
void CSClear(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= AirWidth || coordinate.y >= AirHeight)
    {
        return;
    }
    uint index = AirIndex(coordinate);
    Air[index] = (AirCell)0;
    AirScratch[index] = (AirCell)0;
}
