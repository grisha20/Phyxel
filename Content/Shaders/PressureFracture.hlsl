#include "PhysicsShared.hlsli"
static const uint PressureFragmentMarker = 0x40000000u;
static const uint BlockedFragmentPlan = 0x80000000u;
StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<float4> Wave : register(t2);
StructuredBuffer<AirCell> Air : register(t3);
StructuredBuffer<uint> SourcePlans : register(t4);
StructuredBuffer<uint> SourceClaims : register(t5);
StructuredBuffer<GasMotionState> SourceMotion : register(t6);
StructuredBuffer<uint> SourceRelease : register(t7);
StructuredBuffer<uint> PressureRoots : register(t8);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);
RWStructuredBuffer<uint> Plans : register(u1);
RWStructuredBuffer<uint> Claims : register(u2);
RWStructuredBuffer<GasMotionState> DestinationMotion : register(u3);
RWStructuredBuffer<uint> ReleaseAmounts : register(u4);
RWStructuredBuffer<float4> DestinationWave : register(u5);
#define FineAirWidth Width
#define FineAirHeight Height
#define FineAirMaterialAt(p) SourceGrid[(p).y*Width+(p).x].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular false
#include "FineAirGeometry.hlsli"

bool Fragment(GridCell c)
{return c.IsActive!=0 && IsMovableSolidMaterial(Materials[c.MaterialIndex]) && (c.BodyId&PressureFragmentMarker)!=0;}

// Read visible air on each side, rather than sampling the same coarse node through a wall.
bool Surface(int2 p,int2 direction,out float pressure,out int distance,out uint nodeIndex)
{
    pressure=0;distance=0;nodeIndex=0;
    [loop]for(int step=1;step<=24;step++)
    {
        int2 q=p+direction*step;
        if(q.x<0||q.y<0||q.x>=int(Width)||q.y>=int(Height)||!FilterAirAllows(q.y*Width+q.x))return false;
        GridCell c=SourceGrid[q.y*Width+q.x];
        if(c.IsActive!=0 && IsMovableSolidMaterial(Materials[c.MaterialIndex]))continue;
        int2 node;
        if(AirFineBlocked(q)||!AirFineNodeFor(q,node))return false;
        uint aw=(Width+AirCellSize-1)/AirCellSize;
        uint ni=node.y*aw+node.x;
        // Free carrier nodes already contain the reaction overlay. Pore nodes
        // have no carrier and receive the compressible wave on its own.
        pressure=Air[ni].Blocked<.5?Air[ni].Pressure:Wave[ni].x;distance=step;nodeIndex=ni;
        return true;
    }
    return false;
}

// Solve the normal load from four directional pressure projections. Diagonal
// paths use their actual length; a circular shell is not an axis-only wall.
void LoadProjection(int2 p,int2 direction,inout float3 metric,inout float2 load,out uint source,out float release)
{
    source=0;release=0;
    float l,r;int dl,dr;uint ln,rn;
    if(!Surface(p,-direction,l,dl,ln)||!Surface(p,direction,r,dr,rn))return;
    uint lr=PressureRoots[ln+1],rr=PressureRoots[rn+1];
    // Low-Mach draft potential is not confined gas pressure. Its gradient
    // within a connected pocket cannot tear an internal baffle apart.
    // An open carrier value is counterpressure, never an inward crushing
    // source: it is a draft potential, not dynamic pressure or an EOS.
    // Preserve uniform counterpressure and full closed-to-closed loading.
    float lc=lr!=rr && Air[ln].Blocked<.5 ? Air[ln].Pressure-Wave[ln].x:0;
    float rc=lr!=rr && Air[rn].Blocked<.5 ? Air[rn].Pressure-Wave[rn].x:0;
    float carrierDifference=lc-rc;
    if(lr==0)carrierDifference=min(0,carrierDifference);
    if(rr==0)carrierDifference=max(0,carrierDifference);
    l=Wave[ln].x+max(0,carrierDifference);
    r=Wave[rn].x+max(0,-carrierDifference);
    float lengthStep=length(float2(direction));float2 n=float2(direction)/lengthStep;
    float gradient=(l-r)/(max(1,dl+dr-1)*lengthStep);
    metric+=float3(n.x*n.x,n.y*n.y,n.x*n.y);load+=gradient*n;
    // Transfer only the accumulated carrier reservoir. The reactive overlay
    // is already compressible and must never be issued a second time.
    source=carrierDifference>0?ln:rn;
    release=carrierDifference*(l-r)>0?abs(carrierDifference):0;
}

float2 FragmentCarrier(int2 p)
{
    float2 velocity=0;float count=0;
    [unroll]for(int k=0;k<4;k++)
    {
        int2 d=k==0?int2(-1,0):k==1?int2(1,0):k==2?int2(0,-1):int2(0,1);
        int2 q=p+d,node;
        if(!AirFineBlocked(q) && AirFineNodeFor(q,node))
        {
            uint aw=(Width+AirCellSize-1)/AirCellSize;AirCell a=Air[node.y*aw+node.x];
            if(a.Blocked<.5){velocity+=float2(a.VelocityX,a.VelocityY);count++;}
        }
    }
    return count>0?clamp(velocity/count*60,-180,180):0;
}

void Update(uint3 p,bool fracture,bool advect)
{
    if(p.x>=Width||p.y>=Height)return;
    uint i=p.y*Width+p.x;GridCell c=SourceGrid[i];
    MaterialProperties m=Materials[c.MaterialIndex];
    // Compatibility with PM saves: retire damage and its pending movement.
    if(c.IsActive!=0 && IsMovableSolidMaterial(m) && m.MoistureReserved1>0 && c.Pressure<0)
    {c.Pressure=0;if(!Fragment(c)){c.VelocityX=0;c.VelocityY=0;}}
    // A solid with no melting transition (stone) has no melting-based softening.
    float softness=m.TransitionAboveMaterialIndex!=0xffffffffu
        ?saturate(c.Temperature/max(1,m.TransitionAboveTemperature)):0;
    float strength=m.MoistureReserved1*lerp(1,.6,softness);
    if(c.IsActive!=0 && IsMovableSolidMaterial(m) && m.MoistureReserved1>0 && !Fragment(c) && fracture)
    {
        float3 metric=0;float2 load=0;uint4 source;float4 release;
        LoadProjection(int2(p.xy),int2(1,0),metric,load,source.x,release.x);
        LoadProjection(int2(p.xy),int2(0,1),metric,load,source.y,release.y);
        LoadProjection(int2(p.xy),int2(1,1),metric,load,source.z,release.z);
        LoadProjection(int2(p.xy),int2(1,-1),metric,load,source.w,release.w);
        float determinant=metric.x*metric.y-metric.z*metric.z;
        float2 force=determinant>.0001
            ?float2(metric.y*load.x-metric.z*load.y,metric.x*load.y-metric.z*load.x)/determinant
            :load/max(.0001,metric.x+metric.y);
        float stress=length(force);
        if(stress>strength)
        {
            c.BodyId=PressureFragmentMarker;c.RestFrames=0;c.Pressure=0;
            float carrierLoad=max(max(release.x,release.y),max(release.z,release.w));
            // Game-unit pressure work / packet mass, bounded by the existing
            // transport cap. No extra reactive source or heat is generated.
            float launch=max(stress/strength*75,48*sqrt(2*carrierLoad/max(.25,c.Mass)));
            float2 v=force/stress*clamp(launch,60,180);
            c.VelocityX=v.x;c.VelocityY=v.y;
            [unroll]for(int k=0;k<4;k++)if(release[k]>0)
            {uint ignored;InterlockedMax(ReleaseAmounts[source[k]],asuint(release[k]),ignored);}
        }
    }
    if(Fragment(c))
    {
        if(advect)
        {
            float2 wind=FragmentCarrier(int2(p.xy));
            float response=min(4,2.4/max(.25,c.Mass))*DeltaTime;
            c.VelocityX=lerp(c.VelocityX,wind.x,response);
            c.VelocityY=lerp(c.VelocityY,wind.y,response);
        }
        c.VelocityY=clamp(c.VelocityY+Gravity*.1*DeltaTime,-180,180);
        c.VelocityX=clamp(c.VelocityX,-180,180);
        c.RestFrames=0;
        // A saved fragment owns its physical clock; loading cannot restart its movement seed.
        c.BodyId=PressureFragmentMarker | ((c.BodyId+1u)&0x3fffffffu);
    }
    DestinationGrid[i]=c;
}
[numthreads(16,16,1)]
void CSUpdate(uint3 p:SV_DispatchThreadID){Update(p,true,true);}
[numthreads(16,16,1)]
void CSMoveOnly(uint3 p:SV_DispatchThreadID){Update(p,false,false);}
[numthreads(16,16,1)]
void CSAdvectOnly(uint3 p:SV_DispatchThreadID){Update(p,false,true);}

// One owner per coarse node. Air.P total is unchanged; increasing Wave.X
// withdraws exactly this stock from the carrier on the next CSInject.
// Max merging prevents one plate's pixels from issuing the reservoir repeatedly.
[numthreads(8,8,1)]
void CSRelease(uint3 p:SV_DispatchThreadID)
{
    uint aw=(Width+AirCellSize-1)/AirCellSize,ah=(Height+AirCellSize-1)/AirCellSize;
    if(p.x>=aw||p.y>=ah)return;uint i=p.y*aw+p.x;float4 packet=Wave[i];
    float amount=asfloat(SourceRelease[i]);
    if(amount>0 && Air[i].Blocked<.5)
        packet.x+=min(amount,max(0,Air[i].Pressure-packet.x));
    DestinationWave[i]=packet;
}

bool FreeTarget(int2 q,uint source,uint material)
{
    if(q.x<0||q.y<0||q.x>=int(Width)||q.y>=int(Height))return false;
    uint i=q.y*Width+q.x;
    GridCell target=SourceGrid[i];
    bool gas=target.IsActive!=0 && Materials[target.MaterialIndex].SimulationKind==SimulationKindGas;
    return FilterAllows(i,material,SimulationKindSolid) &&
        (target.IsActive==0 || (gas && FilterAllows(source,target.MaterialIndex,SimulationKindGas)));
}
[numthreads(16,16,1)]
void CSPlan(uint3 p:SV_DispatchThreadID)
{
    if(p.x>=Width||p.y>=Height)return;
    uint i=p.y*Width+p.x;Plans[i]=0;GridCell c=SourceGrid[i];
    if(!Fragment(c))return;
    float2 v=float2(c.VelocityX,c.VelocityY);
    float2 travel=min(abs(v)*DeltaTime,3);
    int2 step=int2(sign(v))*int2(floor(travel)+float2(
        HashUnitFloat(i^(c.BodyId&0x3fffffffu)*1664525u)<frac(travel.x)?1:0,
        HashUnitFloat(i^(c.BodyId&0x3fffffffu)*22695477u^17u)<frac(travel.y)?1:0));
    int2 q=int2(p.xy),previous=q;int steps=max(abs(step.x),abs(step.y));
    [loop]for(int k=1;k<=steps;k++)
    {
        int2 next=int2(p.xy)+int2(round(float2(step)*float(k)/float(steps)));
        if(!FreeTarget(next,i,c.MaterialIndex))break;
        if(next.x!=previous.x && next.y!=previous.y &&
            (!FreeTarget(int2(next.x,previous.y),i,c.MaterialIndex)||!FreeTarget(int2(previous.x,next.y),i,c.MaterialIndex)))break;
        q=next;previous=next;
    }
    // Slow debris settles as loose grains instead of retaining rigid shape.
    if(all(q==int2(p.xy)) && step.y>0 && v.y>=0 && abs(v.x)<24 && Gravity>0 &&
        !FreeTarget(int2(p.x,p.y+1),i,c.MaterialIndex))
    {
        int side=HashUnitFloat(i^(c.BodyId&0x3fffffffu))<.5?-1:1;
        [unroll]for(int attempt=0;attempt<2;attempt++)
        {
            int2 next=int2(p.xy)+int2(side,1);
            if(FreeTarget(next,i,c.MaterialIndex) && FreeTarget(int2(p.x+side,p.y),i,c.MaterialIndex)){q=next;break;}
            side=-side;
        }
    }
    if(all(q==int2(p.xy))){if(steps>0)Plans[i]=BlockedFragmentPlan;return;}
    uint target=q.y*Width+q.x,ignored;
    Plans[i]=target+1;InterlockedMin(Claims[target],i+1,ignored);
}
[numthreads(16,16,1)]
void CSApply(uint3 p:SV_DispatchThreadID)
{
    if(p.x>=Width||p.y>=Height)return;
    uint i=p.y*Width+p.x;GridCell c=SourceGrid[i];GasMotionState motion=SourceMotion[i];
    uint incoming=SourceClaims[i];
    if(incoming!=0xffffffffu){c=SourceGrid[incoming-1];motion=(GasMotionState)0;}
    else if(SourcePlans[i]!=0 && SourcePlans[i]!=BlockedFragmentPlan && SourceClaims[SourcePlans[i]-1]==i+1)
    {uint target=SourcePlans[i]-1;c=SourceGrid[target];motion=SourceMotion[target];}
    else if(Fragment(c) && SourcePlans[i]==BlockedFragmentPlan){c.VelocityX*=.85;c.VelocityY*=.85;}
    DestinationGrid[i]=c;DestinationMotion[i]=motion;
}
