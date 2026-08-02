#include "PhysicsShared.hlsli"

// Coarse air field: pressure and velocity on a grid of AirCellSize x AirCellSize
// simulation cells. This is the missing substrate that makes fire read as a
// drawn plume instead of a swarm of independent sparks.
//
// The Powder Toy runs the same four steps in Air::update_air, in this order:
//   1. pressure is adjusted from the divergence of velocity;
//   2. velocity is adjusted from the gradient of pressure;
//   3. both are advected with a 3x3 gaussian kernel and a semi-Lagrangian
//      backtrace;
//   4. walls zero the flow across them.
// Constants are taken from SimulationConfig.h and are per fixed 60 Hz tick,
// not per second, so this pass must run on the fixed schedule.
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
    uint AirReserved0;
};

StructuredBuffer<MaterialProperties> AirMaterials : register(t0);
StructuredBuffer<GridCell> AirGrid : register(t1);
StructuredBuffer<GasMotionState> AirGasMotion : register(t2);
RWStructuredBuffer<AirCell> Air : register(u0);
RWStructuredBuffer<AirCell> AirScratch : register(u1);

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
// Injection is deliberately tiny and self-limiting. The Powder Toy writes it as
//     pv += 4.0f * HotAir * (3.5f - pv)
// with HotAir = 0.001 for FIRE, so the term fades out as pressure approaches
// the ceiling and a flame settles at a pressure of about 3.5 instead of running
// away. Losses are near unity (PLOSS 0.9999, VLOSS 0.999), so a steady value is
// roughly injection divided by the loss per tick: a constant that looks small
// still produces a field of a few units. Feeding the raw temperature in here
// instead pinned the velocity to its ceiling within nine ticks.
static const float AirHotReference = 1500.0;

// A coarse cell packed with flame may push several times harder than a single
// pixel, so hotness is not clamped to one. The ceiling only stops absurdities.
static const float AirMaximumHotness = 4.0;
static const float AirHotInjection = 0.004;

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
// These are scaled for the 4x4 cellular carrier.  Literal TPT values
// (10000/0.01) leave this model with a one-cell-tall flame because its
// particles do not retain TPT's sub-cell velocity; 800/1.0 restores the same
// observable rise distance while pressure still supplies the lateral turn.
static const float AirConvectionDivisor = 10000.0;
static const float AirConvectionMaximum = 0.01;

// Particle drag, from the main particle loop in Simulation.cpp. FIRE and SMKE
// both use AirLoss 0.97 and AirDrag 0.04.
static const float AirParticleLoss = 0.97;
// Must match GasMaximumSpeed in CellularAutomataSolver.hlsl. Air only receives
// the velocity the cellular FIRE/SMKE carrier can actually realise.
static const float AirGasMaximumSpeed = 1.0;

// The same carrier has a stronger feedback loop than TPT's independent
// particles, so keep this just below the literal 0.04 to avoid a pulse/ball.
static const float AirParticleDrag = 0.04;

// Mirrors FlameOwnRise and GasAdvection in CellularAutomataSolver.hlsl:
// Gravity -0.1 over (1 - Loss 0.20), and Advection 0.9.
static const float FlameRiseSpeed = 0.125;
static const float GasAdvectionResponse = 0.9;

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
static const float FlameMaximumSpeed = 1.0;
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
    float rightY = ReadAir(coordinate + int2(1, 0)).VelocityY;
    float leftY = ReadAir(coordinate + int2(-1, 0)).VelocityY;
    float downX = ReadAir(coordinate + int2(0, 1)).VelocityX;
    float upX = ReadAir(coordinate + int2(0, -1)).VelocityX;
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

// Sample the fine simulation grid and produce, for one coarse cell, how much of
// it is solid and how much heat it is releasing into the air.
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

    uint left = coordinate.x * AirCellSize;
    uint top = coordinate.y * AirCellSize;
    uint solid = 0;
    uint counted = 0;
    uint flameCount = 0;
    uint fireOrSmokeCount = 0;
    uint gasCount = 0;
    float gasVelocityX = 0;
    float gasVelocityY = 0;
    float heat = 0;

    for (uint offsetY = 0; offsetY < AirCellSize; offsetY++)
    {
        uint y = top + offsetY;
        if (y >= AirGridHeight)
        {
            continue;
        }
        for (uint offsetX = 0; offsetX < AirCellSize; offsetX++)
        {
            uint x = left + offsetX;
            if (x >= AirGridWidth)
            {
                continue;
            }
            counted++;
            GridCell source = AirGrid[y * AirGridWidth + x];
            if (source.IsActive == 0)
            {
                continue;
            }
            MaterialProperties material = AirMaterials[source.MaterialIndex];
            if (material.SimulationKind == SimulationKindGas)
            {
                // The deterministic carrier owns the actual gas velocity.
                // TPT applies AirLoss/AirDrag to FIRE and SMKE alike, so the
                // air must see that persistent velocity rather than a one-shot
                // cellular move marker.
                GasMotionState motion = AirGasMotion[y * AirGridWidth + x];
                gasCount++;
                gasVelocityX += clamp(motion.VelocityX,
                    -AirGasMaximumSpeed, AirGasMaximumSpeed);
                gasVelocityY += clamp(motion.VelocityY,
                    -AirGasMaximumSpeed, AirGasMaximumSpeed);
                if ((material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) != 0)
                {
                    fireOrSmokeCount++;
                }
            }
            if (material.SimulationKind == SimulationKindSolid)
            {
                // Only a material explicitly marked as airtight blocks the
                // field. In The Powder Toy the air map is blocked by special
                // walls alone -- ordinary METL is not in it, so the coarse
                // field passes straight through a metal bar and it is the FIRE
                // particles themselves that collide with it. Treating every
                // solid as a wall turned a plate into a 4x4 pressure chamber
                // that does not exist there, with a boundary that depended on
                // how the metal happened to align to the grid.
                if ((material.Flags & MaterialFlagBlocksAir) != 0)
                {
                    solid++;
                }
                continue;
            }
            // Anything hotter than the room pushes the air around it outward.
            // The Powder Toy stores this as a per-element HotAir constant; here
            // it is derived from the cell temperature, which needs no change to
            // the material layout and behaves the same way for flame, because a
            // flame cell is by definition the hottest thing in the scene.
            heat += max(0.0, source.Temperature - AirAmbientTemperature);
            if ((material.Flags & MaterialFlagFlame) != 0)
            {
                flameCount++;
            }
        }
    }

    // A coarse cell blocks the flow once it is mostly solid. Anything less and
    // air still finds its way between the grains.
    float solidFraction = counted > 0 ? float(solid) / float(counted) : 0;
    cell.Blocked = solidFraction >= 0.75 ? 1.0 : 0.0;

    if (cell.Blocked > 0.5)
    {
        cell.Pressure = 0;
        cell.VelocityX = 0;
        cell.VelocityY = 0;
        Air[index] = cell;
        return;
    }

    // Heat is summed over the coarse cell, never averaged over its area.
    // Averaging silently divided a lone flame pixel by the sixteen sub-cells
    // around it, so a 1800 degree flame arrived as 111 degrees and the field it
    // produced was thirteen times too weak to see. The Powder Toy adds
    // pv += 4.0f * HotAir once per particle, with no area term at all: two
    // flames in one cell push twice as hard as one.
    // The pressure ceiling below is what keeps this from running away.
    // FIRE.cpp and SMKE.cpp both set HotAir = 0.001. Simulation.cpp applies
    // pv += 4 * HotAir for every such particle, with no source-cell ceiling.
    // The former 3.5 cap erased precisely the pressure gradient that turns a
    // plume sideways below a plate. Pressure transport/loss below is its only
    // limiter, as it is in TPT.
    cell.Pressure += AirHotInjection * float(fireOrSmokeCount);

    // Convection. Driven by the mean temperature of the cell and hard-capped,
    // exactly as The Powder Toy does it. The y axis grows downward, so rising
    // air is a subtraction.
    float meanOverheat = heat / max(1.0, float(counted));
    float convection = min(AirConvectionMaximum, meanOverheat / AirConvectionDivisor);
    cell.VelocityY = clamp(
        cell.VelocityY - max(0.0, convection),
        -AirMaximumVelocity,
        AirMaximumVelocity);

    // Drag: a rising flame pulls the air along with it. The Powder Toy applies
    //     vy = vy*AirLoss + AirDrag*vy_particle
    // once per particle, with AirLoss 0.97 and AirDrag 0.04 for FIRE. Applying
    // it N times in closed form gives the expressions below.
    //
    // This is the feedback that builds a column, and leaving it out is why the
    // flame stayed a ball. Convection alone cannot win: the gaussian kernel in
    // the advection pass keeps only 0.619 of an isolated cell's velocity each
    // tick, a 38 percent loss, while the capped convection term adds at most
    // 0.01 -- so velocity settled near 0.026 and the air never moved. Drag
    // instead grows with the flame's own speed, so the column feeds itself.
    if (gasCount > 0)
    {
        float retained = pow(AirParticleLoss, float(gasCount));
        float meanVelocityX = gasVelocityX / float(gasCount);
        float meanVelocityY = gasVelocityY / float(gasCount);
        // Closed form of TPT's per-particle AirLoss/AirDrag update.  Do not
        // add a separate "flame rise" target here: that created vertical
        // velocity even after a particle had collided with the plate, erased
        // the below/above discontinuity, and therefore erased the pressure
        // gradient that turns the flow sideways.
        // On TPT's sub-pixel particle set this term averages to zero for a
        // centred brush.  A Phyxel 4x4 slot is a packed occupancy cell: feeding
        // its incidental one-sided hop back into x velocity created a runaway
        // hook.  Keep lateral air sourced by the pressure gradient below; it
        // is the deterministic, mirror-symmetric part of Air::update_air.
        float targetX = 0.0;
        float targetY = meanVelocityY * (AirParticleDrag / (1.0 - AirParticleLoss));
        cell.VelocityX = clamp(
            cell.VelocityX * retained + targetX * (1.0 - retained),
            -AirMaximumVelocity,
            AirMaximumVelocity);
        cell.VelocityY = clamp(
            cell.VelocityY * retained + targetY * (1.0 - retained),
            -AirMaximumVelocity,
            AirMaximumVelocity);
    }

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

    float divergence =
        ReadAir(signedCoordinate + int2(-1, 0)).VelocityX -
        ReadAir(signedCoordinate + int2(1, 0)).VelocityX +
        ReadAir(signedCoordinate + int2(0, -1)).VelocityY -
        ReadAir(signedCoordinate + int2(0, 1)).VelocityY;

    cell.Pressure = lerp(AirEdgePressure, cell.Pressure, AirPressureLoss);
    cell.Pressure = clamp(
        cell.Pressure + divergence * AirStepPressure * 0.5,
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

    float gradientX =
        ReadScratch(signedCoordinate + int2(-1, 0)).Pressure -
        ReadScratch(signedCoordinate + int2(1, 0)).Pressure;
    float gradientY =
        ReadScratch(signedCoordinate + int2(0, -1)).Pressure -
        ReadScratch(signedCoordinate + int2(0, 1)).Pressure;

    cell.VelocityX = lerp(AirEdgeVelocity, cell.VelocityX, AirVelocityLoss) +
        gradientX * AirStepVelocity * 0.5;
    cell.VelocityY = lerp(AirEdgeVelocity, cell.VelocityY, AirVelocityLoss) +
        gradientY * AirStepVelocity * 0.5;

    // A wall stops the component of the flow that would cross it. Checking both
    // neighbours, not just the cell itself, prevents air from streaming along
    // the inside face of a container.
    if (AirBlockedAt(signedCoordinate + int2(-1, 0)) ||
        AirBlockedAt(signedCoordinate + int2(1, 0)))
    {
        cell.VelocityX = 0;
    }
    if (AirBlockedAt(signedCoordinate + int2(0, -1)) ||
        AirBlockedAt(signedCoordinate + int2(0, 1)))
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
            if (AirInside(sample) && !AirBlockedAt(sample))
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
        if (!AirBlockedAt(corner)) s00 = ReadAir(corner);
        if (!AirBlockedAt(corner + int2(1, 0))) s10 = ReadAir(corner + int2(1, 0));
        if (!AirBlockedAt(corner + int2(0, 1))) s01 = ReadAir(corner + int2(0, 1));
        if (!AirBlockedAt(corner + int2(1, 1))) s11 = ReadAir(corner + int2(1, 1));

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
    if (AirVorticityCoefficient > 0.0 &&
        coordinate.x > 1 && coordinate.x + 2 < AirWidth &&
        coordinate.y > 1 && coordinate.y + 2 < AirHeight)
    {
        int2 signedCoordinate = int2(coordinate);
        float dwx = (abs(AirVorticity(signedCoordinate + int2(1, 0))) -
            abs(AirVorticity(signedCoordinate + int2(-1, 0)))) * 0.5;
        float dwy = (abs(AirVorticity(signedCoordinate + int2(0, 1))) -
            abs(AirVorticity(signedCoordinate + int2(0, -1)))) * 0.5;
        float w = AirVorticity(signedCoordinate);
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
