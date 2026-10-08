// Shared by the degree prepass and the conservative heat gather. All reads
// refer to the same immutable grid; transport scratch is rebuilt every tick.
static const uint BulkSurfaceDegreeFlag = 0x80000000u;
static const uint BulkWetPathFlag = 0x40000000u;
static const uint BulkDegreeMask = 0x3fffffffu;
bool HasWetWallPath(int2 p, uint materialIndex)
{
    [unroll] for(int direction=0;direction<4;direction++)
    {
        int2 step=direction==0?int2(-1,0):direction==1?int2(1,0):direction==2?int2(0,-1):int2(0,1);
        [loop] for(int distance=1;distance<=32;distance++)
        {
            int2 q=p+step*distance;
            if(q.x<0 || q.y<0 || q.x>=(int)ThermalWidth || q.y>=(int)ThermalHeight)break;
            GridCell other=SourceGrid[q.y*ThermalWidth+q.x];
            if(other.IsActive==0)break;
            if((Materials[other.MaterialIndex].Flags & MaterialFlagSurfaceBoiling)!=0)return true;
            if(other.MaterialIndex!=materialIndex)break;
        }
    }
    return false;
}
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

bool IsBulkParticipant(int2 p, uint materialIndex)
{
    if (IsBulkInterior(p, materialIndex)) return true;
    if (p.x < 1 || p.y < 1 || p.x+1 >= (int)ThermalWidth || p.y+1 >= (int)ThermalHeight) return false;
    GridCell c = SourceGrid[p.y*ThermalWidth+p.x];
    if (c.IsActive == 0 || c.MaterialIndex != materialIndex) return false;
    [unroll] for (int d=0;d<4;d++)
    {
        int2 step = d==0?int2(-1,0):d==1?int2(1,0):d==2?int2(0,-1):int2(0,1);
        GridCell other = SourceGrid[(p.y+step.y)*ThermalWidth+p.x+step.x];
        if (other.IsActive != 0 && (Materials[other.MaterialIndex].Flags & MaterialFlagSurfaceBoiling) != 0) return true;
    }
    return false;
}
