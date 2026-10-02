// The caller supplies FineAirWidth/Height, FineAirMaterialAt and FineAirMaterials.
// Use the same fine geometry for air links, particle sampling and drag injection:
// a coarse 4x4 node can otherwise mix particles on opposite sides of a thin wall.
bool AirFineBlocked(int2 p)
{
    if (p.x < 0 || p.y < 0 || p.x >= int(FineAirWidth) || p.y >= int(FineAirHeight)) return true;
    uint material = FineAirMaterialAt(p);
    if (material == 0) return false;
    MaterialProperties m = FineAirMaterials[material];
    return m.SimulationKind == SimulationKindSolid || m.SimulationKind == SimulationKindLiquid ||
        (m.Flags & MaterialFlagBlocksAir) != 0;
}

bool AirFineSegmentOpen(int2 a, int2 b)
{
    int2 difference = b - a;
    int steps = max(abs(difference.x), abs(difference.y));
    int2 previous = a;
    if (AirFineBlocked(a)) return false;
    [loop] for (int k = 1; k <= steps; k++)
    {
        int2 p = a + int2(round(float2(difference) * float(k) / float(steps)));
        if (AirFineBlocked(p)) return false;
        if (p.x != previous.x && p.y != previous.y &&
            (AirFineBlocked(int2(p.x, previous.y)) || AirFineBlocked(int2(previous.x, p.y)))) return false;
        previous = p;
    }
    return true;
}

// A wall may split the particle's native coarse cell or occupy its node.
// Use the nearest visible node on the same side, at most four fine cells away.
// The same mapping is used when reading flow, injecting heat and recording drag.
bool AirFineNodeFor(int2 p, out int2 node)
{
    int2 baseNode = p / int(AirCellSize);
    node = baseNode;
    int2 center = baseNode * int(AirCellSize) + int(AirCellSize / 2);
    if (AirFineSegmentOpen(p, center)) return true;
    int bestDistance = 1000;
    [loop] for (int dy = -1; dy <= 1; dy++)
    [loop] for (int dx = -1; dx <= 1; dx++)
    {
        int2 candidate = baseNode + int2(dx, dy);
        int2 target = candidate * int(AirCellSize) + int(AirCellSize / 2);
        int2 difference = target - p;
        int distance = dot(difference, difference);
        if (max(abs(difference.x), abs(difference.y)) <= int(AirCellSize) &&
            distance < bestDistance && AirFineSegmentOpen(p, target))
        {
            bestDistance = distance;
            node = candidate;
        }
    }
    return bestDistance < 1000;
}

#undef FineAirWidth
#undef FineAirHeight
#undef FineAirMaterialAt
#undef FineAirMaterials
