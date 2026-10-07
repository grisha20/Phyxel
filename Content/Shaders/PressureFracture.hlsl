#include "PhysicsShared.hlsli"
static const uint PressureFragmentMarker = 0x40000000u;
StructuredBuffer<GridCell> SourceGrid : register(t0);
StructuredBuffer<MaterialProperties> Materials : register(t1);
StructuredBuffer<float4> Wave : register(t2);
StructuredBuffer<AirCell> Air : register(t3);
StructuredBuffer<uint> SourcePlans : register(t4);
StructuredBuffer<uint> SourceClaims : register(t5);
RWStructuredBuffer<GridCell> DestinationGrid : register(u0);
RWStructuredBuffer<uint> Plans : register(u1);
RWStructuredBuffer<uint> Claims : register(u2);
#define FineAirWidth Width
#define FineAirHeight Height
#define FineAirMaterialAt(p) SourceGrid[(p).y*Width+(p).x].MaterialIndex
#define FineAirMaterials Materials
#define FineAirBlockGranular (HydraulicPressure != 0)
#include "FineAirGeometry.hlsli"

bool Fragment(GridCell c)
{return c.IsActive!=0 && IsMovableSolidMaterial(Materials[c.MaterialIndex]) && (c.BodyId&PressureFragmentMarker)!=0;}

bool PlasticSolid(GridCell c)
{
    MaterialProperties m=Materials[c.MaterialIndex];
    return c.IsActive!=0 && IsMovableSolidMaterial(m) && m.MoistureReserved1>0 && m.ThermalDeviceMaximumPower>0 && !Fragment(c);
}

// Read visible air on each side, rather than sampling the same coarse node through a wall.
bool Surface(int2 p,int2 direction,out float pressure,out float wave,out int distance)
{
    pressure=0;wave=0;distance=0;
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
        wave=Wave[ni].x;pressure=Air[ni].Pressure+wave;distance=step;
        return true;
    }
    return false;
}
void Update(uint3 p,bool fracture)
{
    if(p.x>=Width||p.y>=Height)return;
    uint i=p.y*Width+p.x;GridCell c=SourceGrid[i];
    MaterialProperties m=Materials[c.MaterialIndex];
    // Plastic steps are requested only while this tick applies a wave load.
    // The negative pressure slot keeps permanent damage; velocity is transient.
    if(PlasticSolid(c) && c.Pressure<0){c.VelocityX=0;c.VelocityY=0;}
    // A solid with no melting transition (stone) has no melting-based softening.
    float softness=m.TransitionAboveMaterialIndex!=0xffffffffu
        ?saturate(c.Temperature/max(1,m.TransitionAboveTemperature)):0;
    float strength=m.MoistureReserved1*lerp(1,.6,softness);
    if(c.IsActive!=0 && IsMovableSolidMaterial(m) && m.MoistureReserved1>0 && !Fragment(c) && fracture)
    {
        float l,r,u,d,wl,wr,wu,wd;int dl,dr,du,dd;
        bool horizontal=Surface(int2(p.xy),int2(-1,0),l,wl,dl)&&Surface(int2(p.xy),int2(1,0),r,wr,dr);
        bool vertical=Surface(int2(p.xy),int2(0,-1),u,wu,du)&&Surface(int2(p.xy),int2(0,1),d,wd,dd);
        float2 force=float2(horizontal?(l-r)/max(1,dl+dr-1):0,vertical?(u-d)/max(1,du+dd-1):0);
        float2 impulse=float2(horizontal?(wl-wr)/max(1,dl+dr-1):0,vertical?(wu-wd)/max(1,du+dd-1):0);
        float stress=length(force);
        bool waveLoad=length(impulse)>strength*.25;
        float plasticity=m.ThermalDeviceMaximumPower;
        if(plasticity>0 && stress>strength*.65 && waveLoad)
        {
            float oldDamage=saturate(-c.Pressure);
            float damage=saturate(oldDamage+(stress/(strength*.65)-1)*DeltaTime/plasticity);
            c.Pressure=-damage;
            // At most four one-cell steps before tearing. No force means no creep.
            if(damage<1 && floor(damage*5)>floor(oldDamage*5))
            {
                float2 axis=abs(force.x)>=abs(force.y)?float2(sign(force.x),0):float2(0,sign(force.y));
                c.VelocityX=axis.x/DeltaTime;c.VelocityY=axis.y/DeltaTime;c.RestFrames=0;
            }
        }
        if(stress>strength && waveLoad && (plasticity==0 || c.Pressure<=-1))
        {
            c.BodyId=PressureFragmentMarker;c.RestFrames=0;
            float2 v=force/stress*clamp(stress/strength*24,12,48);
            c.VelocityX=v.x;c.VelocityY=v.y;
        }
    }
    if(Fragment(c))
    {
        c.VelocityY=clamp(c.VelocityY+Gravity*.1*DeltaTime,-60,60);
        c.VelocityX=clamp(c.VelocityX,-60,60);
        c.RestFrames=0;
        // A saved fragment owns its physical clock; loading cannot restart its movement seed.
        c.BodyId=PressureFragmentMarker | ((c.BodyId+1u)&0x3fffffffu);
    }
    DestinationGrid[i]=c;
}
[numthreads(16,16,1)]
void CSUpdate(uint3 p:SV_DispatchThreadID){Update(p,true);}
[numthreads(16,16,1)]
void CSMoveOnly(uint3 p:SV_DispatchThreadID){Update(p,false);}

bool EmptyTarget(int2 q,uint material)
{
    if(q.x<0||q.y<0||q.x>=int(Width)||q.y>=int(Height))return false;
    uint i=q.y*Width+q.x;
    return SourceGrid[i].IsActive==0 && FilterAllows(i,material,SimulationKindSolid);
}
[numthreads(16,16,1)]
void CSPlan(uint3 p:SV_DispatchThreadID)
{
    if(p.x>=Width||p.y>=Height)return;
    uint i=p.y*Width+p.x;Plans[i]=0;GridCell c=SourceGrid[i];
    bool fragment=Fragment(c);
    if(!fragment && !(PlasticSolid(c) && c.Pressure<0 && (c.VelocityX!=0 || c.VelocityY!=0)))return;
    float2 v=float2(c.VelocityX,c.VelocityY);
    int2 step=fragment?int2(HashUnitFloat(i^(c.BodyId&0x3fffffffu)*1664525u)<abs(v.x)*DeltaTime?sign(v.x):0,
                   HashUnitFloat(i^(c.BodyId&0x3fffffffu)*22695477u^17u)<abs(v.y)*DeltaTime?sign(v.y):0):int2(sign(v));
    if(all(step==0))return;
    int2 q=int2(p.xy)+step;
    if(!EmptyTarget(q,c.MaterialIndex))return;
    if(step.x!=0&&step.y!=0 && (!EmptyTarget(int2(p.x+step.x,p.y),c.MaterialIndex)||!EmptyTarget(int2(p.x,p.y+step.y),c.MaterialIndex)))return;
    uint target=q.y*Width+q.x,ignored;
    Plans[i]=target+1;InterlockedMin(Claims[target],i+1,ignored);
}
[numthreads(16,16,1)]
void CSApply(uint3 p:SV_DispatchThreadID)
{
    if(p.x>=Width||p.y>=Height)return;
    uint i=p.y*Width+p.x;GridCell c=SourceGrid[i];
    uint incoming=SourceClaims[i];
    if(incoming!=0xffffffffu)c=SourceGrid[incoming-1];
    else if(SourcePlans[i]!=0 && SourceClaims[SourcePlans[i]-1]==i+1)c=CreateEmptyCell();
    else if(Fragment(c) && SourcePlans[i]==0){c.VelocityX*=.98;c.VelocityY*=.98;}
    DestinationGrid[i]=c;
}
