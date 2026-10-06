// Shared by the degree prepass and the conservative heat gather. All reads
// refer to the same immutable grid; transport scratch is rebuilt every tick.
bool IsBulkInterior(int2 p, uint materialIndex)
{
    if (p.x < 1 || p.y < 1 || p.x + 1 >= (int)ThermalWidth || p.y + 1 >= (int)ThermalHeight) return false;
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        GridCell c = SourceGrid[(p.y + y) * ThermalWidth + p.x + x];
        if (c.IsActive == 0 || c.MaterialIndex != materialIndex) return false;
    }
    return true;
}

bool HasBulkPath(int2 a, int2 b, uint materialIndex)
{
    int distance = max(abs(b.x-a.x), abs(b.y-a.y));
    int2 step = (b-a) / distance;
    [loop] for (int path = 1; path < distance; path++)
    {
        int2 p = a + step * path;
        GridCell c = SourceGrid[p.y * ThermalWidth + p.x];
        if (c.IsActive == 0 || c.MaterialIndex != materialIndex) return false;
    }
    return true;
}
