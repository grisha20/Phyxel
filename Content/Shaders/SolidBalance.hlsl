#include "PhysicsShared.hlsli"

// Scratch only: moments and contact span are rebuilt on each body tick.
struct BodyBalanceData
{
    uint WeightLo; uint WeightHi;
    uint XLo; uint XHi; uint YLo; uint YHi;
    uint LiftLo; uint LiftHi; uint LiftXLo; uint LiftXHi;
    uint LeftSupport; uint RightSupport; uint SupportY; uint Contacts; uint FirstCell;
};
struct BodyRotationPlan
{
    int OldAngle; int NewAngle; int OldX; int OldY; int NewX; int NewY; int Direction; uint Padding;
};
StructuredBuffer<GridCell> Grid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<BodyBalanceData> SourceBalance : register(t2);
StructuredBuffer<uint> SourceTargets : register(t3);
StructuredBuffer<uint> SourceBlocked : register(t4);
StructuredBuffer<uint> Geometry : register(t5);
StructuredBuffer<uint> Origins : register(t6);
StructuredBuffer<GasMotionState> SourceParticleMotion : register(t7);
StructuredBuffer<BodyRotationPlan> SourcePlans : register(t8);
RWStructuredBuffer<BodyBalanceData> Balance : register(u0);
RWStructuredBuffer<uint> Targets : register(u1);
RWStructuredBuffer<uint> Blocked : register(u2);
RWStructuredBuffer<GridCell> Destination : register(u3);
RWStructuredBuffer<uint> DestinationOrigins : register(u4);
RWStructuredBuffer<GasMotionState> DestinationParticleMotion : register(u5);
RWStructuredBuffer<BodyRotationPlan> Plans : register(u6);

bool BodyCell(GridCell c)
{
    return c.IsActive != 0 && c.BodyId != 0 && IsMovableSolidMaterial(Materials[c.MaterialIndex]);
}
bool Enabled(GridCell c)
{
    return BodyCell(c) && (SolidGravity != 0 || (Materials[c.MaterialIndex].Flags & MaterialFlagDensityBody) != 0);
}
bool Obstacle(GridCell c, uint body)
{
    return c.IsActive != 0 && (!BodyCell(c) || c.BodyId != body) &&
        Materials[c.MaterialIndex].SimulationKind != SimulationKindLiquid &&
        Materials[c.MaterialIndex].SimulationKind != SimulationKindGas;
}
float RowLiquid(int2 p, uint body)
{
    float density = 0;
    for (int dir = -1; dir <= 1; dir += 2)
        for (int x = p.x + dir; x >= 0 && x < int(Width); x += dir)
        {
            GridCell s = Grid[uint(p.y) * Width + uint(x)];
            if (BodyCell(s) && s.BodyId == body) continue;
            if (s.IsActive != 0 && Materials[s.MaterialIndex].SimulationKind == SimulationKindLiquid)
                density = max(density, ValidatedMaterialDensity(Materials[s.MaterialIndex]));
            break;
        }
    return density;
}
float Wide(uint lo, uint hi) { return float(lo) + float(hi) * 4294967296.0; }

[numthreads(16,16,1)]
void AnalyzeBalance(uint3 thread : SV_DispatchThreadID)
{
    uint2 p = thread.xy;
    if (p.x >= Width || p.y >= Height) return;
    GridCell c = Grid[FlattenCoordinate(p)];
    if (!Enabled(c)) return;
    uint b = c.BodyId - 1, old, ignored;
    InterlockedMax(Balance[b].FirstCell,Width*Height-FlattenCoordinate(p),ignored);
    uint m = max(1u, uint(round((max(c.Mass,0.001)+max(c.MoistureMass,0.0)+max(c.FuelMass,0.0))*256.0)));
    InterlockedAdd(Balance[b].WeightLo,m,old); if (old > 0xffffffffu-m) InterlockedAdd(Balance[b].WeightHi,1,ignored);
    uint mx=m*p.x,my=m*p.y;
    InterlockedAdd(Balance[b].XLo,mx,old); if (old > 0xffffffffu-mx) InterlockedAdd(Balance[b].XHi,1,ignored);
    InterlockedAdd(Balance[b].YLo,my,old); if (old > 0xffffffffu-my) InterlockedAdd(Balance[b].YHi,1,ignored);
    bool compact=(Materials[c.MaterialIndex].Flags & MaterialFlagDensityBody)!=0;
    if(compact || (Geometry[b]&2)!=0)
    {
        uint lift=uint(round(RowLiquid(int2(p),c.BodyId)*256.0)),lx=lift*p.x;
        InterlockedAdd(Balance[b].LiftLo,lift,old); if(old>0xffffffffu-lift) InterlockedAdd(Balance[b].LiftHi,1,ignored);
        InterlockedAdd(Balance[b].LiftXLo,lx,old); if(old>0xffffffffu-lx) InterlockedAdd(Balance[b].LiftXHi,1,ignored);
    }
    if(p.y+1>=Height || Obstacle(Grid[FlattenCoordinate(p)+Width],c.BodyId))
    {
        InterlockedMax(Balance[b].LeftSupport,((Width-p.x)<<16)|p.y,ignored);
        InterlockedMax(Balance[b].RightSupport,(p.x<<16)|p.y,ignored);
        InterlockedMax(Balance[b].SupportY,p.y,ignored);
        InterlockedAdd(Balance[b].Contacts,1,ignored);
    }
}

int Rotation(GridCell c,out int2 pivot)
{
    pivot=0;
    if(!Enabled(c)) return 0;
    uint b=c.BodyId-1;
    BodyBalanceData d=SourceBalance[b];
    float weight=Wide(d.WeightLo,d.WeightHi);
    if(weight<=0) return 0;
    float cx=Wide(d.XLo,d.XHi)/weight,cy=Wide(d.YLo,d.YHi)/weight;
    // Existing thin-wall vessel buoyancy is a separate approximation. Do not
    // apply compact-volume torque to a hollow metal vessel.
    if((Geometry[b]&2)!=0 && Wide(d.LiftLo,d.LiftHi)>0 &&
        (Materials[c.MaterialIndex].Flags & MaterialFlagDensityBody)==0) return 0;
    if(d.Contacts!=0)
    {
        float left=Width-(d.LeftSupport>>16),right=d.RightSupport>>16;
        if(cx>right+0.75){pivot=int2(int(right),int(d.RightSupport&0xffff));return 1;}
        if(cx<left-0.75){pivot=int2(int(left),int(d.LeftSupport&0xffff));return -1;}
        return 0;
    }
    float lift=Wide(d.LiftLo,d.LiftHi);
    if(lift<=0) return 0;
    float lx=Wide(d.LiftXLo,d.LiftXHi)/lift;
    pivot=int2(int(round(cx)),int(round(cy)));
    return abs(cx-lx)>0.75 ? (cx>lx?1:-1) : 0;
}

// Three integer shears are a reversible lattice permutation: no cell is
// rounded onto another cell or lost. This is a raster approximation of a
// small rigid rotation, not resampling a picture into occupied world cells.
// Always rotate canonical coordinates, never rotate yesterday's raster.
// Split the total angle into an exact quarter-turn and <=45 degree residual.
int2 RotateCanonical(int2 p,int angle)
{
    angle=((angle%64)+64)%64;
    int quarter=(angle+8)/16;int residual=angle-quarter*16;
    quarter=quarter%4;
    if(quarter==1)p=int2(-p.y,p.x);
    else if(quarter==2)p=-p;
    else if(quarter==3)p=int2(p.y,-p.x);
    float radians=float(residual)*0.098174770424681;
    float a=tan(radians*0.5),b=sin(radians);
    p.x-=int(round(a*p.y));p.y+=int(round(b*p.x));p.x-=int(round(a*p.y));
    return p;
}
int2 InverseCanonical(int2 p,int angle)
{
    angle=((angle%64)+64)%64;
    int quarter=(angle+8)/16;int residual=angle-quarter*16;quarter%=4;
    float radians=float(residual)*0.098174770424681;
    float a=tan(radians*0.5),b=sin(radians);
    p.x+=int(round(a*p.y));p.y-=int(round(b*p.x));p.x+=int(round(a*p.y));
    if(quarter==1)p=int2(p.y,-p.x);
    else if(quarter==2)p=-p;
    else if(quarter==3)p=int2(-p.y,p.x);
    return p;
}
static const uint OriginMask=0x03ffffffu;
int OriginAngle(uint index){return int(Origins[index]>>26);}
int2 Canonical(uint index){uint original=(Origins[index]&OriginMask)-1;return int2(original%Width,original/Width);}
int2 Target(uint index,BodyRotationPlan plan)
{
    return RotateCanonical(Canonical(index),plan.NewAngle)+int2(plan.NewX,plan.NewY);
}
bool InWorld(int2 p){return p.x>=0&&p.y>=0&&p.x<int(Width)&&p.y<int(Height);}

[numthreads(256,1,1)]
void InitializeOrigins(uint3 thread : SV_DispatchThreadID)
{
    uint index=thread.x;if(index>=Width*Height)return;
    DestinationOrigins[index]=BodyCell(Grid[index])?(Origins[index]!=0?Origins[index]:index+1):0;
}

[numthreads(256,1,1)]
void BuildRotationPlans(uint3 thread : SV_DispatchThreadID)
{
    uint b=thread.x;if(b>=Width*Height)return;
    BodyRotationPlan plan=(BodyRotationPlan)0;
    if(SourceBalance[b].FirstCell==0){Plans[b]=plan;return;}
    uint index=Width*Height-SourceBalance[b].FirstCell;
    GridCell c=Grid[index];int2 pivot;int dir=Rotation(c,pivot);
    if(dir==0||Origins[index]==0){Plans[b]=plan;return;}
    int angle=OriginAngle(index);
    int2 oldOffset=int2(index%Width,index/Width)-RotateCanonical(Canonical(index),angle);
    int2 originalPivot=InverseCanonical(pivot-oldOffset,angle);
    int nextAngle=(angle+dir+64)%64;
    int2 nextOffset=pivot-RotateCanonical(originalPivot,nextAngle);
    plan.OldAngle=angle;plan.NewAngle=nextAngle;plan.OldX=oldOffset.x;plan.OldY=oldOffset.y;
    plan.NewX=nextOffset.x;plan.NewY=nextOffset.y;plan.Direction=dir;
    Plans[b]=plan;
}

[numthreads(16,16,1)]
void PlanRotation(uint3 thread : SV_DispatchThreadID)
{
    int2 p=int2(thread.xy);
    if(!InWorld(p)) return;
    uint index=FlattenCoordinate(uint2(p));GridCell c=Grid[index];
    if(!BodyCell(c))return;
    BodyRotationPlan plan=SourcePlans[c.BodyId-1];if(plan.Direction==0)return;
    int2 offset=p-RotateCanonical(Canonical(index),OriginAngle(index));uint ignored;
    if(OriginAngle(index)!=plan.OldAngle || any(offset!=int2(plan.OldX,plan.OldY)))
    {InterlockedOr(Blocked[c.BodyId-1],2,ignored);return;}
    int2 target=Target(index,plan);
    if(!InWorld(target)){InterlockedOr(Blocked[c.BodyId-1],1,ignored);return;}
    if(!FilterPathAllows(index,FlattenCoordinate(uint2(target)),c.MaterialIndex,SimulationKindSolid,Width)){InterlockedOr(Blocked[c.BodyId-1],1,ignored);return;}
    int steps=max(abs(target.x-p.x),abs(target.y-p.y));
    // Check the swept segment too, so thin walls cannot be skipped.
    for(int step=1;step<=steps;step++)
    {
        int2 q=int2(round(float2(p)+float2(target-p)*(float(step)/float(steps))));
        if(Obstacle(Grid[FlattenCoordinate(uint2(q))],c.BodyId))
        {InterlockedOr(Blocked[c.BodyId-1],1,ignored);return;}
    }
    uint previous;
    InterlockedCompareExchange(Targets[FlattenCoordinate(uint2(target))],0,index+1,previous);
    if(previous!=0&&previous!=index+1)
    {
        InterlockedOr(Blocked[c.BodyId-1],1,ignored);
        GridCell other=Grid[previous-1];InterlockedOr(Blocked[other.BodyId-1],1,ignored);
    }
}

[numthreads(16,16,1)]
void ApplyRotation(uint3 thread : SV_DispatchThreadID)
{
    int2 p=int2(thread.xy);if(!InWorld(p))return;
    uint index=FlattenCoordinate(uint2(p)),source=SourceTargets[index];
    GridCell current=Grid[index];int2 pivot;int dir;
    DestinationOrigins[index]=BodyCell(current)?Origins[index]:0;
    DestinationParticleMotion[index]=(GasMotionState)0;
    if(current.IsActive!=0 && Materials[current.MaterialIndex].SimulationKind==SimulationKindGas)
        DestinationParticleMotion[index]=SourceParticleMotion[index];
    if((SolidPass&2)==0)
    {
        dir=Rotation(current,pivot);
        if(dir!=0 && SourceBlocked[current.BodyId-1]==0)current.RestFrames=0;
        Destination[index]=current;return;
    }
    if(source!=0)
    {
        GridCell incoming=Grid[source-1];
        if(SourceBlocked[incoming.BodyId-1]==0)
        {
            incoming.RestFrames=0;Destination[index]=incoming;
            DestinationParticleMotion[index]=(GasMotionState)0;
            DestinationOrigins[index]=(Origins[source-1]&OriginMask)|(uint(SourcePlans[incoming.BodyId-1].NewAngle)<<26);return;
        }
    }
    BodyRotationPlan plan=(BodyRotationPlan)0;
    if(BodyCell(current))plan=SourcePlans[current.BodyId-1];
    if(BodyCell(current)&&(SourceBlocked[current.BodyId-1]&2)!=0)
    {
        DestinationOrigins[index]=index+1;current.RestFrames=0;
        Destination[index]=current;return;
    }
    if(plan.Direction!=0&&SourceBlocked[current.BodyId-1]==0)
    {
        // Each permutation chain ends in one displaced liquid/gas/empty
        // packet. Pull it to the vacated source; conserve the full packet.
        int2 q=Target(index,plan);
        for(uint step=0;step<Width*Height;step++)
        {
            GridCell s=Grid[FlattenCoordinate(uint2(q))];
            if(!BodyCell(s)||s.BodyId!=current.BodyId)
            {s.RestFrames=0;Destination[index]=s;DestinationOrigins[index]=0;
                DestinationParticleMotion[index]=(GasMotionState)0;
                if(s.IsActive!=0 && Materials[s.MaterialIndex].SimulationKind==SimulationKindGas)
                    DestinationParticleMotion[index]=SourceParticleMotion[FlattenCoordinate(uint2(q))];return;}
            q=Target(FlattenCoordinate(uint2(q)),plan);
        }
    }
    Destination[index]=current;
}
