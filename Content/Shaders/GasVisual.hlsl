#include "PhysicsShared.hlsli"

// Render-only RGB field. Kept separate from FIRE/SMKE so gas appearance cannot
// change their calibrated glow or any physical state. Dimensions/layout match
// FireGlowConstants; GridCell and the saved world format remain unchanged.
cbuffer GasVisualConstants : register(b0)
{
    uint VisualWidth;
    uint VisualHeight;
    uint GridWidth;
    uint GridHeight;
    float Reserved0;
    float Reserved1;
    float Reserved2;
    uint Reserved3;
};
StructuredBuffer<MaterialProperties> Materials : register(t0);
StructuredBuffer<GridCell> Grid : register(t1);
RWStructuredBuffer<float4> Field : register(u0);
RWStructuredBuffer<float4> Scratch : register(u1);

[numthreads(8, 8, 1)]
void CSDeposit(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= VisualWidth || id.y >= VisualHeight) return;
    uint index = id.y * VisualWidth + id.x;
    float3 color = Field[index].rgb;
    for (uint y = 0; y < AirCellSize; y++)
    {
        for (uint x = 0; x < AirCellSize; x++)
        {
            uint2 p = id.xy * AirCellSize + uint2(x, y);
            if (p.x >= GridWidth || p.y >= GridHeight) continue;
            GridCell source = Grid[p.y * GridWidth + p.x];
            if (source.IsActive == 0) continue;
            MaterialProperties material = Materials[source.MaterialIndex];
            if (material.SimulationKind != SimulationKindGas ||
                (material.Flags & (MaterialFlagFlame | MaterialFlagSmoke)) != 0 ||
                material.GasHazeStrength <= 0) continue;
            // A singleton contributes too. Blend toward half the material
            // color rather than accumulating unbounded opacity/light.
            float blend = 1.0 - pow(1.0 - 62.0 / 256.0, saturate(source.Mass));
            float3 sourceColor = float3(material.ColorR, material.ColorG, material.ColorB) *
                0.5 * material.GasHazeStrength;
            color = lerp(color, sourceColor, blend);
        }
    }
    Field[index] = float4(saturate(color), 0);
}

[numthreads(8, 8, 1)]
void CSDiffuse(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= VisualWidth || id.y >= VisualHeight) return;
    uint index = id.y * VisualWidth + id.x;
    float3 color = Field[index].rgb * 8.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 p = int2(id.xy) + int2(x, y);
            if ((x == 0 && y == 0) || p.x < 0 || p.y < 0 ||
                p.x >= int(VisualWidth) || p.y >= int(VisualHeight)) continue;
            color += Field[uint(p.y) * VisualWidth + uint(p.x)].rgb;
        }
    }
    Scratch[index] = float4(max(0, color / 16.0 - 4.0 / 255.0), 0);
}

[numthreads(8, 8, 1)]
void CSCommit(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= VisualWidth || id.y >= VisualHeight) return;
    uint index = id.y * VisualWidth + id.x;
    Field[index] = Scratch[index];
}
