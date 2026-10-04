#include "PhysicsShared.hlsli"

// Persistent additive light field for fire, on the same coarse grid as the air.
//
// This is the single largest reason our fire looked like scattered grains while
// The Powder Toy's looks like a drawn flame. TPT does not draw the FIRE particle
// at all: its graphics function sets pixel_mode = PMODE_NONE and only deposits
// light. What the eye sees is fire_r/g/b, a coarse buffer that every particle
// adds into, which is then blurred into its neighbours, faded a little, and
// splatted over a 12x12 pixel area with a gaussian kernel. Overlapping flames
// sum; TPT clips a dense core to white while the edges trail off into red.
// The bright core is deliberate: the user prefers TPT's additive fire display.
//
// Drawing each flame cell as its own coloured pixel, and combining the glow with
// max() as we did before, can never produce that: max() cannot accumulate, so
// two flames side by side were exactly as bright as one, and the cell grid stayed
// visible as separate dots.
//
// Mirrors Renderer::render_fire:
//     r = (r * 8 + sum of the eight neighbours) / 16
//     r = r > 4 ? r - 4 : 0

cbuffer FireGlowConstants : register(b0)
{
    uint FireGlowWidth;
    uint FireGlowHeight;
    uint FireGlowGridWidth;
    uint FireGlowGridHeight;
    float FireGlowDeposit;
    float FireGlowDecay;
    float FireGlowEmberStrength;
    uint FireGlowReserved0;
};

StructuredBuffer<MaterialProperties> FireGlowMaterials : register(t0);
StructuredBuffer<GridCell> FireGlowGrid : register(t1);
RWStructuredBuffer<FireGlowCell> FireGlow : register(u0);
RWStructuredBuffer<FireGlowCell> FireGlowScratch : register(u1);

uint FireGlowIndex(uint2 coordinate)
{
    return coordinate.y * FireGlowWidth + coordinate.x;
}

// The Powder Toy indexes its flame gradient by the particle's remaining life,
// so a flame fades from pale yellow through orange to black as it dies:
//     0.00 black, 0.50 0x60300F, 0.90 0xDFBF6F, 1.00 0xAF9F0F
float3 FlameGradient(uint lifeIndex)
{
    // This is Renderer::Gradient(..., 200) for flameTable, including its
    // integer alpha and RGB divisions. Renderer::flameTableAt() clips its
    // input, so the final table entry is index 199, not a synthetic endpoint.
    uint index = min(lifeIndex, 199u);
    uint alpha;
    uint3 left;
    uint3 right;
    if (index < 100u)
    {
        left = uint3(0u, 0u, 0u);
        right = uint3(96u, 48u, 15u);
        alpha = index * 255u / 100u;
    }
    else if (index < 180u)
    {
        left = uint3(96u, 48u, 15u);
        right = uint3(223u, 191u, 111u);
        alpha = (index - 100u) * 255u / 80u;
    }
    else
    {
        left = uint3(223u, 191u, 111u);
        right = uint3(175u, 159u, 15u);
        alpha = (index - 180u) * 255u / 20u;
    }
    uint3 color = (alpha * right + (255u - alpha) * left) / 255u;
    return float3(color) / 255.0;
}

float3 FireAddContribution(float3 color)
{
    // FIRE.cpp sets firea = 255 and Renderer.cpp performs integer firea /= 8
    // before ((firea * channel) >> 8). Reproduce both 8-bit truncations.
    float fireAlpha = floor(FireGlowDeposit * 255.0);
    float3 colorBytes = floor(saturate(color) * 255.0 + 0.5);
    return floor(fireAlpha * colorBytes / 256.0) / 255.0;
}

// Тёплый серо-коричневый, как у SMKE в The Powder Toy: чуть теплее чистого
// серого, поэтому дым читается продолжением пламени, а не отдельным облаком.
// SMKE.cpp graphics(): colour 55/255 and FIRE_BLEND alpha 75/255.  FIRE_BLEND
// is not additive: each smoke particle moves the existing coarse colour toward
// 55, then render_fire() fades the field by 4/255.  Treating it as accumulated
// opacity was the source of the large pale smoke balls: a full 4x4 cell reached
// one in a few frames, whereas TPT can never exceed its charcoal source colour.
static const float FireGlowSmokeLuminance = 55.0 / 255.0;
static const float FireGlowSmokeBlend = (75.0 / 2.0) / 255.0;
static const float FireGlowSmokeDecay = 4.0 / 255.0;

// Solid fuel hot enough to be incandescent glows on its own. This is what makes
// a bed of coals sit there radiating after the flames above it have gone out.
float3 EmberGradient(float heat)
{
    float t = saturate(heat);
    return lerp(float3(0.35, 0.03, 0.0), float3(1.0, 0.55, 0.12), t) * t;
}

// Accumulate this frame's light into the field. Runs before the blur so a flame
// that appears and dies within one frame still leaves a trace.
[numthreads(8, 8, 1)]
void CSDeposit(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= FireGlowWidth || coordinate.y >= FireGlowHeight)
    {
        return;
    }

    uint index = FireGlowIndex(coordinate);
    FireGlowCell cell = FireGlow[index];
    float3 deposited = 0;
    float smokeLuminance = cell.Smoke;

    uint left = coordinate.x * AirCellSize;
    uint top = coordinate.y * AirCellSize;
    for (uint offsetY = 0; offsetY < AirCellSize; offsetY++)
    {
        uint y = top + offsetY;
        if (y >= FireGlowGridHeight)
        {
            continue;
        }
        for (uint offsetX = 0; offsetX < AirCellSize; offsetX++)
        {
            uint x = left + offsetX;
            if (x >= FireGlowGridWidth)
            {
                continue;
            }
            GridCell source = FireGlowGrid[y * FireGlowGridWidth + x];
            if (source.IsActive == 0)
            {
                continue;
            }
            MaterialProperties material = FireGlowMaterials[source.MaterialIndex];

            if ((material.Flags & MaterialFlagFlame) != 0)
            {
                // TPT indexes a 200-entry flame table directly with the
                // remaining life in 60 Hz ticks.  FIRE starts at 120..169,
                // therefore even a newborn particle is at 0.60..0.845 of the
                // table -- it is yellow, not the final bright endpoint.  Our
                // lifetime is stored in seconds, so normalising by the
                // material's own maximum incorrectly made every new cell 1.0
                // and made a blocked, dense band turn white.
                uint lifeIndex = min(199u, (uint)max(0.0, floor(source.Lifetime * 60.0)));
                // Every flame adds its own light. Summing rather than taking a
                // maximum preserves the contribution of neighbouring sources.
                deposited += FireAddContribution(FlameGradient(lifeIndex));
                continue;
            }

            // Smoke writes a grey contribution into the same light field.
            //
            // The Powder Toy does exactly this: SMKE is not drawn as a plain
            // grey pixel, it deposits with FIRE_BLEND into fire_r/g/b and is
            // then blurred along with the flame, which is what produces the
            // soft brown-grey arcs trailing away from a fire.
            //
            // One simulated cell represents one TPT particle for rendering.
            // Apply FIRE_BLEND once per live smoke cell, preserving its fixed
            // 55/255 ceiling rather than deriving brightness from mass.
            if ((material.Flags & MaterialFlagSmoke) != 0)
            {
                smokeLuminance = lerp(
                    smokeLuminance,
                    FireGlowSmokeLuminance,
                    FireGlowSmokeBlend);
                continue;
            }

            // Anything burning below the flame contributes a dull red.
            if (material.IgnitionTemperature > 0 &&
                source.Temperature > material.IgnitionTemperature)
            {
                float heat = saturate(
                    (source.Temperature - material.IgnitionTemperature) /
                    max(1.0, material.MaximumCombustionTemperature - material.IgnitionTemperature));
                deposited += EmberGradient(heat) * FireGlowDeposit * FireGlowEmberStrength;
            }
        }
    }

    // TPT's bounded additive RGB: a dense source can reach a white core.
    // Clip stored channels, so invisible excess cannot widen the halo later.
    cell.Red = saturate(cell.Red + deposited.r);
    cell.Green = saturate(cell.Green + deposited.g);
    cell.Blue = saturate(cell.Blue + deposited.b);
    cell.Smoke = smokeLuminance;
    FireGlow[index] = cell;
}

// Bleed into the neighbours and fade. Reads FireGlow, writes FireGlowScratch:
// reading and writing one buffer would be a race, because a neighbour may or
// may not have been updated already.
[numthreads(8, 8, 1)]
void CSDiffuse(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= FireGlowWidth || coordinate.y >= FireGlowHeight)
    {
        return;
    }

    int2 signedCoordinate = int2(coordinate);
    FireGlowCell centre = FireGlow[FireGlowIndex(coordinate)];
    float3 total = float3(centre.Red, centre.Green, centre.Blue) * 8.0;
    float smokeTotal = centre.Smoke * 8.0;

    for (int offsetY = -1; offsetY <= 1; offsetY++)
    {
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            if (offsetX == 0 && offsetY == 0)
            {
                continue;
            }
            int2 sample = signedCoordinate + int2(offsetX, offsetY);
            if (sample.x < 0 || sample.y < 0 ||
                sample.x >= int(FireGlowWidth) || sample.y >= int(FireGlowHeight))
            {
                continue;
            }
            // All eight neighbours weigh the same, as in Renderer::render_fire.
            // Stretching this vertically was tried and reverted: it did not
            // narrow the flame at all, because the width comes from where the
            // fire actually is, not from how its light is smeared, and it made
            // the plume look artificially drawn out.
            FireGlowCell neighbor = FireGlow[FireGlowIndex(uint2(sample))];
            total += float3(neighbor.Red, neighbor.Green, neighbor.Blue);
            smokeTotal += neighbor.Smoke;
        }
    }

    // TPT retains the denominator 16 even at the simulation boundary: absent
    // neighbours contribute zero rather than increasing the remaining weight.
    float3 blurred = total / 16.0;
    blurred = max(0.0, blurred - FireGlowDecay);
    float blurredSmoke = smokeTotal / 16.0;
    blurredSmoke = max(0.0, blurredSmoke - FireGlowSmokeDecay);

    FireGlowCell result;
    result.Red = blurred.r;
    result.Green = blurred.g;
    result.Blue = blurred.b;
    result.Smoke = blurredSmoke;
    FireGlowScratch[FireGlowIndex(coordinate)] = result;
}

// Publish the blurred field.
[numthreads(8, 8, 1)]
void CSCommitGlow(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= FireGlowWidth || coordinate.y >= FireGlowHeight)
    {
        return;
    }
    uint index = FireGlowIndex(coordinate);
    FireGlow[index] = FireGlowScratch[index];
}

[numthreads(8, 8, 1)]
void CSClearGlow(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 coordinate = dispatchThreadId.xy;
    if (coordinate.x >= FireGlowWidth || coordinate.y >= FireGlowHeight)
    {
        return;
    }
    uint index = FireGlowIndex(coordinate);
    FireGlow[index] = (FireGlowCell)0;
    FireGlowScratch[index] = (FireGlowCell)0;
}
