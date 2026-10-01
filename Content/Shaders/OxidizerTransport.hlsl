#include "PhysicsShared.hlsli"
#include "OxidizerShared.hlsli"

cbuffer OxidizerConstants : register(b0)
{
    uint OxygenWidth;
    uint OxygenHeight;
    uint OpenEdges;
    float OxygenDeltaTime;
};
StructuredBuffer<GridCell> Cells : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<float> SourceOxygen : register(t2);
StructuredBuffer<float> Demand : register(t3);
RWStructuredBuffer<float> DestinationOxygen : register(u0);

float Capacity(uint index)
{
    GridCell cell = Cells[index];
    return OxidizerCapacity(cell, Materials[cell.MaterialIndex]);
}
float Available(uint index) { return min(saturate(SourceOxygen[index]), Capacity(index)); }

float NeighborSum(uint2 p)
{
    uint i = p.y * OxygenWidth + p.x;
    float sum = 0;
    if (p.x > 0) sum += saturate(SourceOxygen[i - 1]);
    if (p.x + 1 < OxygenWidth) sum += saturate(SourceOxygen[i + 1]);
    if (p.y > 0) sum += saturate(SourceOxygen[i - OxygenWidth]);
    if (p.y + 1 < OxygenHeight) sum += saturate(SourceOxygen[i + OxygenWidth]);
    return sum;
}

float FaceExchange(uint neighbor, float own)
{
    if (Capacity(neighbor) <= 0) return 0;
    return Available(neighbor) - own;
}

[numthreads(16,16,1)]
void CSTransport(uint3 tid : SV_DispatchThreadID)
{
    uint2 p = tid.xy;
    if (p.x >= OxygenWidth || p.y >= OxygenHeight) return;
    uint i = p.y * OxygenWidth + p.x;
    float cap = Capacity(i);
    if (cap <= 0) { DestinationOxygen[i] = 0; return; }
    // OpenBoundaries opens the sides and ceiling; the map floor stays solid.
    if (OpenEdges != 0 && (p.x == 0 || p.y == 0 || p.x + 1 == OxygenWidth))
    { DestinationOxygen[i] = cap; return; }
    float own = Available(i), flux = 0;
    if (p.x > 0) flux += FaceExchange(i - 1, own);
    if (p.x + 1 < OxygenWidth) flux += FaceExchange(i + 1, own);
    if (p.y > 0) flux += FaceExchange(i - OxygenWidth, own);
    if (p.y + 1 < OxygenHeight) flux += FaceExchange(i + OxygenWidth, own);
    // Explicit four-face diffusion: coefficient <= 1/4 ensures positivity.
    // Fine-grid faces keep a one-pixel wall sealed at every coarse-grid offset.
    DestinationOxygen[i] = clamp(own + min(0.24, 12 * OxygenDeltaTime) * flux, 0, cap);
}

float ConsumerShare(uint2 consumer, float own)
{
    uint i = consumer.y * OxygenWidth + consumer.x;
    float total = NeighborSum(consumer);
    return total > 0 ? Demand[i] * own / total : 0;
}

[numthreads(16,16,1)]
void CSConsume(uint3 tid : SV_DispatchThreadID)
{
    uint2 p = tid.xy;
    if (p.x >= OxygenWidth || p.y >= OxygenHeight) return;
    uint i = p.y * OxygenWidth + p.x;
    // SourceOxygen is the immutable transport result used by combustion.
    // Fuel cells have zero oxygen; only their four adjacent donors pay.
    float own = saturate(SourceOxygen[i]), used = 0;
    if (own > 0)
    {
        if (p.x > 0) used += ConsumerShare(uint2(p.x - 1, p.y), own);
        if (p.x + 1 < OxygenWidth) used += ConsumerShare(p + uint2(1,0), own);
        if (p.y > 0) used += ConsumerShare(uint2(p.x, p.y - 1), own);
        if (p.y + 1 < OxygenHeight) used += ConsumerShare(p + uint2(0,1), own);
    }
    GridCell cell = Cells[i];
    float flameUse = cell.IsActive != 0 && cell.BodyId != SelfOxidizingFlameMarker &&
        (Materials[cell.MaterialIndex].Flags & MaterialFlagFlame) != 0
        ? min(own / 5, 0.15 * OxygenDeltaTime) : 0;
    DestinationOxygen[i] = max(0, own - used - flameUse);
}
