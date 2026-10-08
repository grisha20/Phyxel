#include "PhysicsShared.hlsli"

cbuffer ContactConstants : register(b0)
{
    float ContactDeltaTime;
    uint ContactWidth;
    uint ContactHeight;
    uint ContactTickIndex;
};

StructuredBuffer<MaterialProperties> Materials : register(t0);
RWStructuredBuffer<GridCell> Grid : register(u0);
RWStructuredBuffer<uint> CellMaterials : register(u1);
RWStructuredBuffer<GasMotionState> GasMotion : register(u2);
RWStructuredBuffer<uint> ContactSummary : register(u3);

#include "PhaseEnthalpy.hlsli"

bool IsLiquidContact(uint2 coordinate, MaterialProperties source)
{
    uint index = coordinate.y * ContactWidth + coordinate.x;
    GridCell neighbour = Grid[index];
    return neighbour.IsActive != 0 &&
        Materials[neighbour.MaterialIndex].SimulationKind == SimulationKindLiquid &&
        (source.ThermalDeviceMaximumPower == 0 ||
            neighbour.MaterialIndex + 1 == (uint)source.ThermalDeviceMaximumPower);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= ContactWidth || dispatchThreadId.y >= ContactHeight)
    {
        return;
    }

    uint2 coordinate = dispatchThreadId.xy;
    uint index = coordinate.y * ContactWidth + coordinate.x;
    GridCell cell = Grid[index];
    if (cell.IsActive == 0)
    {
        return;
    }

    MaterialProperties source = Materials[cell.MaterialIndex];
    if (source.MoistureCapacity > 0) return; // Conserved path below owns these materials.
    if (source.SimulationKind != SimulationKindGranular ||
        source.ContactLiquidIntoMaterialIndex == 0xffffffffu ||
        source.ContactLiquidRatePerSecond <= 0)
    {
        return;
    }

    bool touchingLiquid =
        (coordinate.x > 0 && IsLiquidContact(coordinate - uint2(1, 0), source)) ||
        (coordinate.x + 1 < ContactWidth && IsLiquidContact(coordinate + uint2(1, 0), source)) ||
        (coordinate.y > 0 && IsLiquidContact(coordinate - uint2(0, 1), source)) ||
        (coordinate.y + 1 < ContactHeight && IsLiquidContact(coordinate + uint2(0, 1), source));
    if (!touchingLiquid)
    {
        return;
    }

    float probability = 1.0 - exp(-source.ContactLiquidRatePerSecond * ContactDeltaTime);
    uint seed = index ^ (ContactTickIndex * 0x9e3779b9u) ^ 0x68bc21ebu;
    if (HashUnitFloat(seed) >= saturate(probability))
    {
        return;
    }

    cell.MaterialIndex = source.ContactLiquidIntoMaterialIndex;
    cell.Lifetime = 0; // Wet fuel loses the retained coal ignition state.
    cell.BodyId = 0;
    cell.Pressure = 0;
    cell.RestFrames = 0;
    Grid[index] = cell;
    CellMaterials[index] = cell.MaterialIndex;
}

// Mirrors MaterialFlags.NonAbsorbableLiquid; only this handler uses the tag.
static const uint MaterialFlagNonAbsorbableLiquid = 1u << 14;

float FuelPoreFraction(GridCell c, MaterialProperties m)
{
    return m.FuelCapacity>0 && c.Mass>0 ? saturate(c.FuelMass/(c.Mass*m.FuelCapacity)) : 0;
}
float WaterPoreFraction(GridCell c, MaterialProperties m)
{
    return m.MoistureCapacity>0 && c.Mass>0 ? saturate(c.MoistureMass/(c.Mass*m.MoistureCapacity)) : 0;
}
bool TransferAbsorbedFuel(inout GridCell first, inout GridCell second,uint firstIndex,uint secondIndex)
{
    MaterialProperties ma=Materials[first.MaterialIndex], mb=Materials[second.MaterialIndex];
    bool fa=first.IsActive!=0 && ma.FuelCapacity>0 && first.Mass>0;
    bool fb=second.IsActive!=0 && mb.FuelCapacity>0 && second.Mass>0;
    if(!fa && !fb) return false;
    bool fromFirst;
    float amount;
    MaterialProperties carrier;
    if(fa && fb)
    {
        uint firstLiquid=RetainedLiquidIndex(first,ma),secondLiquid=RetainedLiquidIndex(second,mb);
        if(first.FuelMass>0 && second.FuelMass>0 && firstLiquid!=secondLiquid) return false;
        uint species=first.FuelMass>0?firstLiquid:secondLiquid;
        if(first.FuelMass<=0 && second.FuelMass<=0)return false;
        if((Materials[species].Flags & MaterialFlagNonAbsorbableLiquid)!=0)return false;
        if(((ma.Flags & MaterialFlagUniversalPores)==0 && species!=ma.FuelLiquidMaterialIndex) ||
           ((mb.Flags & MaterialFlagUniversalPores)==0 && species!=mb.FuelLiquidMaterialIndex))return false;
        float ca=first.Mass*ma.FuelCapacity, cb=second.Mass*mb.FuelCapacity;
        float difference=first.FuelMass/ca-second.FuelMass/cb;
        fromFirst=difference>0;
        amount=abs(difference)*ca*cb/(ca+cb);
        amount=min(amount,min(ma.FuelAbsorptionRate,mb.FuelAbsorptionRate)*min(first.Mass,second.Mass)*ContactDeltaTime*4);
        carrier=Materials[species];
    }
    else
    {
        fromFirst=fb;
        GridCell receiver=first, donor=second;
        MaterialProperties mr=ma;
        if(fb) { receiver=second; donor=first; mr=mb; }
        if(donor.IsActive==0 || donor.Mass<=0 || Materials[donor.MaterialIndex].SimulationKind!=SimulationKindLiquid ||
           (Materials[donor.MaterialIndex].Flags & MaterialFlagNonAbsorbableLiquid)!=0 ||
           donor.MaterialIndex==mr.MoistureLiquidMaterialIndex || donor.Lifetime!=0 ||
           ((mr.Flags & MaterialFlagUniversalPores)==0 && donor.MaterialIndex!=mr.FuelLiquidMaterialIndex) ||
           (receiver.FuelMass>0 && RetainedLiquidIndex(receiver,mr)!=donor.MaterialIndex))return false;
        amount=mr.FuelAbsorptionRate*receiver.Mass*ContactDeltaTime*4;
        carrier=Materials[donor.MaterialIndex];
    }
    GridCell donor=first, receiver=second;
    MaterialProperties md=ma, mr=mb;
    bool donorAbsorbent=fa;
    if(!fromFirst) { donor=second; receiver=first; md=mb; mr=ma; donorAbsorbent=fb; }
    amount=min(amount,min(donorAbsorbent ? donor.FuelMass : donor.Mass,
        max(0,receiver.Mass*mr.FuelCapacity*(1-WaterPoreFraction(receiver,mr))-receiver.FuelMass)));
    if(amount<=0) return false;
    float ed=donor.Mass*CellSpecificEnthalpy(donor), er=receiver.Mass*CellSpecificEnthalpy(receiver);
    float carried=amount*carrier.HeatCapacity*donor.Temperature;
    uint retained=donorAbsorbent ? RetainedLiquidIndex(donor,md) : donor.MaterialIndex;
    if(!FilterPathAllows(firstIndex,secondIndex,retained,SimulationKindLiquid,ContactWidth))return false;
    if(donorAbsorbent) { donor.FuelMass-=amount;if(donor.FuelMass<=0)donor.RetainedLiquidMaterialIndex=0; } else donor.Mass-=amount;
    receiver.RetainedLiquidMaterialIndex=retained;
    receiver.FuelMass+=amount;
    receiver=SetCellSpecificEnthalpy(receiver,(er+carried)/receiver.Mass);
    receiver.RestFrames=0;
    if(donorAbsorbent || donor.Mass>0)
    {
        donor=SetCellSpecificEnthalpy(donor,(ed-carried)/donor.Mass);
        donor.RestFrames=0;
    }
    else donor=CreateEmptyCell();
    first=donor; second=receiver;
    if(!fromFirst) { first=receiver; second=donor; }
    return true;
}

// Disjoint adjacent pairs: horizontal-even, vertical-even, horizontal-odd,
// vertical-odd. No cell is written by two invocations, so shared water cannot
// be spent twice. Each face is visited every four fixed thermal ticks.
bool EmitSurfaceVapour(inout GridCell first, inout GridCell second, uint a, uint b)
{
    bool firstLiquid=first.IsActive!=0 && (Materials[first.MaterialIndex].Flags & MaterialFlagSurfaceBoiling)!=0;
    bool secondLiquid=second.IsActive!=0 && (Materials[second.MaterialIndex].Flags & MaterialFlagSurfaceBoiling)!=0;
    if(firstLiquid==secondLiquid) return false;
    GridCell liquid=first, outlet=second;
    if(!firstLiquid) {liquid=second;outlet=first;}
    MaterialProperties m=Materials[liquid.MaterialIndex];
    uint source=firstLiquid?a:b, target=firstLiquid?b:a;
    if(!FilterPathAllows(source,target,m.TransitionAboveMaterialIndex,SimulationKindGas,ContactWidth))return false;
    if(liquid.Lifetime<=0 || liquid.Mass<=0 ||
        (outlet.IsActive!=0 && outlet.MaterialIndex!=m.TransitionAboveMaterialIndex)) return false;
    float energy=liquid.Mass*CellSpecificEnthalpy(liquid);
    float releasedSpecific=m.HeatCapacity*m.TransitionAboveTemperature+m.TransitionAboveLatentHeat;
    // The ordinary phase pass owns a completely paid parcel, including any
    // sensible superheat. Splitting it here would discard the residual heat
    // when no liquid mass remains and bypass normal phase normalization.
    if(energy>=liquid.Mass*releasedSpecific) return false;
    float amount=min(liquid.Mass,liquid.Mass*liquid.Lifetime/m.TransitionAboveLatentHeat);
    // Match the gas transport resolution, and never create a liquid remainder
    // below the transport resolution. Keep its paid heat until a
    // resolvable portion or the complete phase transition can be emitted.
    if(amount<liquid.Mass) amount=min(amount,max(0,liquid.Mass-.0005));
    if(amount<=0 || (amount<.0005 && outlet.IsActive==0)) return false;
    GridCell vapour=CreateEmptyCell();
    vapour.MaterialIndex=m.TransitionAboveMaterialIndex;vapour.IsActive=1;
    vapour.Mass=amount;
    vapour=SetCellSpecificEnthalpy(vapour,releasedSpecific);
    if(outlet.IsActive!=0)
    {
        float total=outlet.Mass*CellSpecificEnthalpy(outlet)+amount*releasedSpecific;
        outlet.Mass+=amount;
        outlet=SetCellSpecificEnthalpy(outlet,total/outlet.Mass);
        outlet.RestFrames=0;
    }
    else outlet=vapour;
    liquid.Mass-=amount;
    if(liquid.Mass>0) liquid=SetCellSpecificEnthalpy(liquid,(energy-amount*releasedSpecific)/liquid.Mass);
    else liquid=CreateEmptyCell();
    liquid.RestFrames=0;
    first=liquid;second=outlet;
    if(!firstLiquid) {first=outlet;second=liquid;}
    InterlockedOr(ContactSummary[0],PhaseSummaryPhaseOccurred | PhaseSummaryTargetGas |
        PhaseSummaryTargetCellular | PhaseSummaryTouchesLiquid);
    return true;
}

// A paid bubble can nucleate inside a full pool. The neighbour receives all
// displaced liquid and its energy; the bubble stays at its origin and must
// travel/condense through the existing gas-liquid transport, never teleport.
bool NucleateVapour(inout GridCell first,inout GridCell second,uint a,uint b)
{
    if(first.IsActive==0 || second.IsActive==0 || first.MaterialIndex!=second.MaterialIndex)return false;
    MaterialProperties m=Materials[first.MaterialIndex];
    if((m.Flags & MaterialFlagSurfaceBoiling)==0)return false;
    // An unresolved displaced parcel cannot nucleate repeatedly and collapse
    // the pool into a handful of arbitrarily massive cells.
    if(first.Mass>1.0001 || second.Mass>1.0001)return false;
    bool sourceFirst=first.Lifetime>=second.Lifetime;
    GridCell source=first, receiver=second;
    if(!sourceFirst){source=second;receiver=first;}
    if(source.Temperature<m.TransitionAboveTemperature-.001 || source.Lifetime<=0)return false;
    float amount=min(.25*source.Mass,source.Mass*source.Lifetime/m.TransitionAboveLatentHeat);
    if(amount<.02 || source.Mass-amount<.0005)return false;
    uint sourceIndex=sourceFirst?a:b,receiverIndex=sourceFirst?b:a;
    if(!FilterAllows(sourceIndex,m.TransitionAboveMaterialIndex,SimulationKindGas) ||
       !FilterAllows(receiverIndex,source.MaterialIndex,SimulationKindLiquid) ||
       !FilterPathAllows(sourceIndex,receiverIndex,source.MaterialIndex,SimulationKindLiquid,ContactWidth))return false;
    float vapourSpecific=m.HeatCapacity*m.TransitionAboveTemperature+m.TransitionAboveLatentHeat;
    float energy=source.Mass*CellSpecificEnthalpy(source)+receiver.Mass*CellSpecificEnthalpy(receiver);
    receiver.Mass+=source.Mass-amount;
    receiver=SetCellSpecificEnthalpy(receiver,(energy-amount*vapourSpecific)/receiver.Mass);
    receiver.RestFrames=0;
    source=CreateEmptyCell();source.IsActive=1;source.MaterialIndex=m.TransitionAboveMaterialIndex;source.Mass=amount;
    source=SetCellSpecificEnthalpy(source,vapourSpecific);
    first=source;second=receiver;
    if(!sourceFirst){first=receiver;second=source;}
    InterlockedOr(ContactSummary[0],PhaseSummaryPhaseOccurred | PhaseSummaryTargetGas |
        PhaseSummaryTargetCellular | PhaseSummaryTouchesLiquid);
    return true;
}

[numthreads(16, 16, 1)]
void CSMoisture(uint3 id : SV_DispatchThreadID)
{
    uint2 p=id.xy;
    if(p.x>=ContactWidth || p.y>=ContactHeight || ContactDeltaTime<=0) return;
    bool capillary=(ContactTickIndex&0x80000000u)!=0;
    uint phase=ContactTickIndex&3, axis=capillary?1:phase&1;
    uint stride=capillary?8:1, parity=capillary?(ContactTickIndex&1):phase>>1;
    if((((axis==0?p.x:p.y)/stride)&1)!=parity) return;
    uint2 q=p+(axis==0 ? uint2(stride,0) : uint2(0,stride));
    if(q.x>=ContactWidth || q.y>=ContactHeight) return;
    uint a=p.y*ContactWidth+p.x, b=q.y*ContactWidth+q.x;
    GridCell first=Grid[a], second=Grid[b];
    if(!capillary)
    {
        bool emptyA=first.IsActive==0,emptyB=second.IsActive==0;
        bool gasA=Materials[first.MaterialIndex].SimulationKind==SimulationKindGas;
        bool gasB=Materials[second.MaterialIndex].SimulationKind==SimulationKindGas;
        if(EmitSurfaceVapour(first,second,a,b) || NucleateVapour(first,second,a,b))
        {
            Grid[a]=first; Grid[b]=second;
            CellMaterials[a]=first.IsActive!=0?first.MaterialIndex:0;
            CellMaterials[b]=second.IsActive!=0?second.MaterialIndex:0;
            if(emptyA || (!gasA && Materials[first.MaterialIndex].SimulationKind==SimulationKindGas)) GasMotion[a]=(GasMotionState)0;
            if(emptyB || (!gasB && Materials[second.MaterialIndex].SimulationKind==SimulationKindGas)) GasMotion[b]=(GasMotionState)0;
            return;
        }
    }
    if(capillary)
    {
        // Fast longitudinal redistribution only inside a continuous porous
        // solid. Every intermediate cell must be a compatible capillary path.
        uint liquid=Materials[first.MaterialIndex].MoistureLiquidMaterialIndex;
        [loop] for(uint offset=0;offset<=stride;offset++)
        {
            GridCell path=Grid[(p.y+offset)*ContactWidth+p.x];
            MaterialProperties mp=Materials[path.MaterialIndex];
            if(path.IsActive==0 || mp.SimulationKind!=SimulationKindSolid ||
                mp.MoistureReserved0<=0 || mp.MoistureLiquidMaterialIndex!=liquid) return;
        }
    }
    else if(TransferAbsorbedFuel(first,second,a,b))
    {
        Grid[a]=first; Grid[b]=second;
        CellMaterials[a]=first.IsActive!=0?first.MaterialIndex:0;
        CellMaterials[b]=second.IsActive!=0?second.MaterialIndex:0;
        // Continue with the water stock too: oil redistribution must not
        // monopolize this pair while its gradient approaches equilibrium.
    }
    bool firstFuel=first.IsActive!=0 && Materials[first.MaterialIndex].MoistureCapacity>0;
    bool secondFuel=second.IsActive!=0 && Materials[second.MaterialIndex].MoistureCapacity>0;
    uint poreWater = firstFuel ? Materials[first.MaterialIndex].MoistureLiquidMaterialIndex
        : secondFuel ? Materials[second.MaterialIndex].MoistureLiquidMaterialIndex : 0;
    if ((Materials[poreWater].Flags & MaterialFlagNonAbsorbableLiquid) != 0) return;
    if(firstFuel && secondFuel)
    {
        if(!FilterPathAllows(a,b,Materials[first.MaterialIndex].MoistureLiquidMaterialIndex,SimulationKindLiquid,ContactWidth))return;
        MaterialProperties ma=Materials[first.MaterialIndex], mb=Materials[second.MaterialIndex];
        if(first.Mass<=0 || second.Mass<=0 ||
            ma.MoistureLiquidMaterialIndex!=mb.MoistureLiquidMaterialIndex) return;
        float ca=first.Mass*ma.MoistureCapacity, cb=second.Mass*mb.MoistureCapacity;
        float difference=first.MoistureMass/ca-second.MoistureMass/cb;
        bool fromFirst=difference>0;
        // Never pump the top wetter than its source. Adjacent transport still
        // redistributes in both directions; this longer path speeds the rise.
        if(capillary && fromFirst) return;
        GridCell donor=first, receiver=second;
        MaterialProperties donorMaterial=ma, receiverMaterial=mb;
        if(!fromFirst) { donor=second; receiver=first; donorMaterial=mb; receiverMaterial=ma; }
        float amount=min(abs(difference)*ca*cb/(ca+cb),min(donor.MoistureMass,
            max(0,receiver.Mass*receiverMaterial.MoistureCapacity*(1-FuelPoreFraction(receiver,receiverMaterial))-receiver.MoistureMass)));
        float rate=capillary?min(ma.MoistureReserved0,mb.MoistureReserved0):
            min(ma.MoistureAbsorptionRate,mb.MoistureAbsorptionRate);
        amount=min(amount,rate*min(first.Mass,second.Mass)*ContactDeltaTime*(capillary?2:4));
        // Keep sub-resolution water at its source; never discard it.
        if(amount<.0001*min(first.Mass,second.Mass) || amount<=0) return;
        MaterialProperties carrier=Materials[ma.MoistureLiquidMaterialIndex];
        float donorEnergy=donor.Mass*CellSpecificEnthalpy(donor);
        float receiverEnergy=receiver.Mass*CellSpecificEnthalpy(receiver);
        // Water carries sensible heat and its paid vaporization share. Excess
        // heat retained in a sealed grain stays with that grain.
        float carriedEnergy=amount*(carrier.HeatCapacity*donor.Temperature+
            min(donor.MoistureEnergy,donor.MoistureMass*carrier.TransitionAboveLatentHeat)/donor.MoistureMass);
        donor.MoistureMass-=amount;
        receiver.MoistureMass+=amount;
        receiver.MaterialIndex=receiverMaterial.MoistureWetMaterialIndex;
        receiver.Lifetime=0; receiver.Pressure=0; receiver.RestFrames=0;
        donor.RestFrames=0;
        if(donor.MoistureMass<=0)
        {
            donor.MoistureMass=0; donor.MoistureEnergy=0;
            donor.MaterialIndex=donorMaterial.MoistureDryMaterialIndex;
        }
        donor=SetCellSpecificEnthalpy(donor,(donorEnergy-carriedEnergy)/donor.Mass);
        receiver=SetCellSpecificEnthalpy(receiver,(receiverEnergy+carriedEnergy)/receiver.Mass);
        first=donor; second=receiver;
        if(!fromFirst) { first=receiver; second=donor; }
        Grid[a]=first; Grid[b]=second;
        CellMaterials[a]=first.MaterialIndex; CellMaterials[b]=second.MaterialIndex;
        return;
    }
    if(firstFuel==secondFuel) return;
    uint fuelIndex=firstFuel?a:b, otherIndex=firstFuel?b:a;
    GridCell fuel=first, other=second;
    if(!firstFuel) { fuel=second; other=first; }
    MaterialProperties m=Materials[fuel.MaterialIndex];
    MaterialProperties liquid=Materials[m.MoistureLiquidMaterialIndex];
    float energy=fuel.Mass*CellSpecificEnthalpy(fuel);
    // Normalize old hot-wet worlds even if every outlet is occupied.
    bool changed=fuel.MoistureMass>0 && fuel.Temperature>liquid.TransitionAboveTemperature;
    if(changed) fuel=SetCellSpecificEnthalpy(fuel,energy/fuel.Mass);
    if(other.IsActive!=0 && other.MaterialIndex==m.MoistureLiquidMaterialIndex && other.Mass>0 &&
       FilterPathAllows(fuelIndex,otherIndex,m.MoistureLiquidMaterialIndex,SimulationKindLiquid,ContactWidth))
    {
        float amount=min(other.Mass,min(max(0,m.MoistureCapacity*fuel.Mass*(1-FuelPoreFraction(fuel,m))-fuel.MoistureMass),
            m.MoistureAbsorptionRate*fuel.Mass*ContactDeltaTime*4));
        if(amount>0)
        {
            energy+=amount*CellSpecificEnthalpy(other);
            fuel.MoistureMass+=amount;
            other.Mass-=amount;
            if(other.Mass<=0) other=CreateEmptyCell();
            fuel.MaterialIndex=m.MoistureWetMaterialIndex;
            fuel.Lifetime=0; fuel.Pressure=0; fuel.RestFrames=0;
            fuel=SetCellSpecificEnthalpy(fuel,energy/fuel.Mass);
            changed=true;
        }
    }
    else if((other.IsActive==0 || other.MaterialIndex==liquid.TransitionAboveMaterialIndex) &&
        fuel.MoistureMass>0 && fuel.Temperature>=liquid.TransitionAboveTemperature-.001)
    {
        // Only paid heat can leave as vapour. Existing vapour accepts more;
        // another species or a sealed solid pore keeps the full energy ledger.
        float amount=min(fuel.MoistureMass,min(fuel.MoistureEnergy/liquid.TransitionAboveLatentHeat,
            m.MoistureDryingRate*fuel.Mass*ContactDeltaTime*4));
        // Final single-precision cancellation can leave a microscopic wet
        // tail at exactly boiling. Pay that tail from sensible heat, allowing
        // at most 0.0001 C cooling; never discard water or change the energy ledger.
        if(fuel.MoistureMass <= m.MoistureDryingRate*fuel.Mass*ContactDeltaTime*4 &&
            fuel.MoistureMass*liquid.TransitionAboveLatentHeat-fuel.MoistureEnergy <= CellEffectiveCapacity(fuel)*.0001)
            amount=fuel.MoistureMass;
        if(amount>0 && FilterPathAllows(fuelIndex,otherIndex,liquid.TransitionAboveMaterialIndex,SimulationKindGas,ContactWidth))
        {
            uint vapourIndex=liquid.TransitionAboveMaterialIndex;
            GridCell emitted=CreateEmptyCell(); emitted.IsActive=1; emitted.MaterialIndex=vapourIndex; emitted.Mass=amount;
            emitted.Temperature=liquid.TransitionAboveTemperature;
            float releasedSpecific=CellSpecificEnthalpy(emitted);
            if(other.IsActive!=0)
            {
                float combinedEnergy=other.Mass*CellSpecificEnthalpy(other)+amount*releasedSpecific;
                other.Mass+=amount;
                other=SetCellSpecificEnthalpy(other,combinedEnergy/other.Mass);
                other.RestFrames=0;
            }
            else other=emitted;
            InterlockedOr(ContactSummary[0], PhaseSummaryPhaseOccurred | PhaseSummaryTargetGas | PhaseSummaryTargetCellular);
            energy-=amount*releasedSpecific;
            fuel.MoistureMass-=amount;
            if(fuel.MoistureMass<=0)
            {
                fuel.MoistureMass=0; fuel.MoistureEnergy=0;
                fuel.MaterialIndex=m.MoistureDryMaterialIndex;
            }
            fuel=SetCellSpecificEnthalpy(fuel,energy/fuel.Mass);
            fuel.RestFrames=0;
            changed=true;
        }
    }
    if(fuel.MoistureMass==0 && fuel.MaterialIndex!=m.MoistureDryMaterialIndex)
    {
        fuel.MaterialIndex=m.MoistureDryMaterialIndex;
        fuel.Lifetime=0; fuel.RestFrames=0; changed=true;
    }
    if(changed)
    {
        Grid[fuelIndex]=fuel; Grid[otherIndex]=other;
        CellMaterials[fuelIndex]=fuel.MaterialIndex;
        CellMaterials[otherIndex]=other.IsActive!=0?other.MaterialIndex:0;
        if(other.IsActive!=0 && (firstFuel ? second.IsActive : first.IsActive)==0)
            GasMotion[otherIndex]=(GasMotionState)0;
    }
}
