using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class AirThermalRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var r=coordinator.DispatchFrame(new SimulationSettings { Paused=true },
            [new() { X=40,EndX=40,Y=40,EndY=40,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }],0);
        int n=r.Width*r.Height, an=r.AirWidth*r.AirHeight;
        var ctx=r.Context;
        void Check(bool ok,string message) { if (!ok) throw new InvalidOperationException(message); }
        Vector2[] Read() => MemoryMarshal.Cast<byte,Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer)).ToArray();
        Vector2[] Fresh() { var a=new Vector2[an]; Array.Fill(a,new(293.15f*.016f,.016f)); return a; }
        var air=new AirCell[an]; var links=new uint[an];
        void SetCarrier()
        {
            // These tests prescribe a face flow, independently of the pressure
            // solver. Production now retains the solver's exact face output.
            ctx.UpdateSubresource(air,r.Air.Buffer);
            var faces=new Vector2[an];
            for(int y=0;y<r.AirHeight;y++)for(int x=0;x<r.AirWidth;x++){
                int i=y*r.AirWidth+x;
                if(x+1<r.AirWidth)faces[i].X=(air[i].VelocityX+air[i+1].VelocityX)*.5f;
                if(y+1<r.AirHeight)faces[i].Y=(air[i].VelocityY+air[i+r.AirWidth].VelocityY)*.5f;
            }
            ctx.UpdateSubresource(faces,r.AirProjectionB.Buffer);
        }
        var grid=new GridCell[n]; int gi=80*r.Width+80, ai=20*r.AirWidth+20;
        grid[gi]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),Mass=1,Temperature=800,Lifetime=2 };
        ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);
        SetCarrier(); ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer);
        ctx.UpdateSubresource(Fresh(),r.AirThermal.Buffer);
        var initial=Read();
        for(int i=0;i<300;i++) SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        var heat=Read();
        var after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double energy=(after[gi].Temperature-800)*registry[CoreMaterialIds.Fire].Properties.HeatCapacity;
        for(int i=0;i<an;i++) energy+=heat[i].X-initial[i].X;
        Check(Math.Abs(energy)<.02 && heat[ai].X/heat[ai].Y>700,"Gas/carrier exchange lost energy or did not heat air");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_EXCHANGE error={energy:F8} airC={heat[ai].X/heat[ai].Y-273.15:F4}"));

        // Vapour carries latent as well as sensible heat in Lifetime. Exchange
        // must account for that slot rather than changing temperature alone.
        grid[gi]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),Mass=.1f,Temperature=100,Lifetime=100 };
        ctx.UpdateSubresource(grid,r.Grid.ReadBuffer); ctx.UpdateSubresource(Fresh(),r.AirThermal.Buffer);
        var table=registry.CreateGpuTable(); double steamBefore=.1*PhaseEnthalpy.SpecificEnergy(grid[gi],table);
        for(int i=0;i<120;i++) SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        heat=Read(); after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double phaseError=after[gi].Mass*PhaseEnthalpy.SpecificEnergy(after[gi],table)-steamBefore;
        for(int i=0;i<an;i++) phaseError+=heat[i].X-initial[i].X;
        Check(Math.Abs(phaseError)<.01,"Carrier exchange lost phase enthalpy");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_PHASE error={phaseError:F8}"));

        // A closed, compressing carrier must preserve both capacity and energy;
        // a uniform temperature stays uniform even when the capacity moves.
        Array.Clear(grid); ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);
        for(int y=0;y<r.AirHeight;y++) for(int x=0;x<r.AirWidth;x++)
        {
            int i=y*r.AirWidth+x; air[i].VelocityX=.8f;
            if(x>0) links[i]|=1u<<3; if(x+1<r.AirWidth) links[i]|=1u<<5;
            if(y>0) links[i]|=1u<<1; if(y+1<r.AirHeight) links[i]|=1u<<7;
        }
        SetCarrier(); ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer);
        ctx.UpdateSubresource(Fresh(),r.AirThermal.Buffer);
        for(int i=0;i<120;i++) SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        heat=Read();
        double maxTemperatureError=heat.Where(a=>a.Y>1e-7).Max(a=>Math.Abs(a.X/a.Y-293.15));
        Check(maxTemperatureError<.03,"Uniform carrier became hot under compression");
        Check(Math.Abs(heat.Sum(a=>(double)a.X)-initial.Sum(a=>(double)a.X))<.1 &&
            Math.Abs(heat.Sum(a=>(double)a.Y)-initial.Sum(a=>(double)a.Y))<.001,"Closed transport lost energy/capacity");

        var hot=Fresh(); int origin=30*r.AirWidth+30; hot[origin].X=1000*hot[origin].Y;
        ctx.UpdateSubresource(hot,r.AirThermal.Buffer);
        for(int i=0;i<60;i++) SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        heat=Read(); double weight=0, centre=0;
        for(int i=0;i<an;i++) { double excess=heat[i].X-293.15*heat[i].Y; if(excess>1e-4) { weight+=excess;centre+=excess*(i%r.AirWidth); } }
        Check(centre/weight>39,"Hot carrier did not follow airflow");
        Check(Math.Abs(heat.Sum(a=>(double)a.X)-hot.Sum(a=>(double)a.X))<.1,"Moving heat was deleted");
        Check(heat.All(a=>float.IsFinite(a.X)&&float.IsFinite(a.Y)&&a.X>=-1e-6&&a.Y>=-1e-6),"Transport produced invalid energy/capacity");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_TRANSPORT uniformError={maxTemperatureError:F8} centre={centre/weight:F4}"));

        // The same reciprocal wall links used by pressure must seal heat too.
        for(int y=0;y<r.AirHeight;y++) { links[y*r.AirWidth+32]&=~(1u<<5); links[y*r.AirWidth+33]&=~(1u<<3); }
        ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer); ctx.UpdateSubresource(hot,r.AirThermal.Buffer);
        for(int i=0;i<120;i++) SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        heat=Read();
        Check(heat.Where((_,i)=>i%r.AirWidth>=33).All(a=>a.Y<1e-7 || Math.Abs(a.X/a.Y-293.15)<.03),"Heat crossed a sealed carrier link");
        // Restore persisted carrier heat into the actual GPU resource.
        var bytes=MemoryMarshal.AsBytes(hot.AsSpan()).ToArray();
        new SimulationStateSerializer().ApplyWorldSnapshot(r,new(r.Width,r.Height,new byte[n*System.Runtime.InteropServices.Marshal.SizeOf<GridCell>()],AirThermal:bytes));
        Check(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer).AsSpan().SequenceEqual(bytes),"Carrier heat changed during GPU restore");
        SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        var next=AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer);
        new SimulationStateSerializer().ApplyWorldSnapshot(r,new(r.Width,r.Height,new byte[n*System.Runtime.InteropServices.Marshal.SizeOf<GridCell>()],AirThermal:bytes));
        SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        Check(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer).AsSpan().SequenceEqual(next),"Reload did not continue the same thermal transport step");
        // Remapping across a blocked native node exercises the tile halo;
        // caching must neither miss the gas nor exchange with it twice.
        Array.Clear(grid); Array.Clear(air); Array.Clear(links);
        int remapGas=gi+1+2*r.Width, remapMetal=gi+2+2*r.Width;
        grid[remapGas]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),Mass=.3f,Temperature=800,Lifetime=2 };
        grid[remapMetal]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),Mass=7.8f,Temperature=20 };
        // This remapping control isolates gas ownership with an insulating wall.
        var remapTable=registry.CreateGpuTable();
        remapTable[registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal)].ThermalConductivity=0;
        ctx.UpdateSubresource(remapTable,r.Materials.Buffer);
        air[ai].Blocked=1;
        ctx.UpdateSubresource(grid,r.Grid.ReadBuffer); SetCarrier();
        ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer); ctx.UpdateSubresource(Fresh(),r.AirThermal.Buffer);
        for(int i=0;i<120;i++) SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        heat=Read(); after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double remapError=.3*(after[remapGas].Temperature-800);
        for(int i=0;i<an;i++) remapError+=heat[i].X-initial[i].X;
        Check(Math.Abs(remapError)<.01 && heat[ai-1].X/heat[ai-1].Y>700 && after[remapMetal].Temperature==20,
            "Cached exchange changed blocked-node gas mapping or energy");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_REMAP error={remapError:F8}"));
        ctx.UpdateSubresource(table,r.Materials.Buffer);
        // Strong four-face competition activates both donor limits. Earlier
        // uniform-wind tests alone could not catch a broken tile-edge limiter.
        Array.Clear(grid); var stress=Fresh();
        for(int y=0;y<r.AirHeight;y++) for(int x=0;x<r.AirWidth;x++)
        {
            int i=y*r.AirWidth+x;
            float capacity=.0001f+(i%11)*.003f;
            stress[i]=new((293.15f+(i%17)*100)*capacity,capacity);
            air[i]=new() { VelocityX=(x%7-3)*2,VelocityY=(y%7-3)*2 };
            links[i]=0;
            if(x>0) links[i]|=1u<<3; if(x+1<r.AirWidth) links[i]|=1u<<5;
            if(y>0) links[i]|=1u<<1; if(y+1<r.AirHeight) links[i]|=1u<<7;
        }
        ctx.UpdateSubresource(grid,r.Grid.ReadBuffer); SetCarrier();
        ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer); ctx.UpdateSubresource(stress,r.AirThermal.Buffer);
        for(int i=0;i<40;i++) SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        heat=Read();
        double stressE=stress.Sum(a=>(double)a.X),stressC=stress.Sum(a=>(double)a.Y);
        double errorE=Math.Abs(heat.Sum(a=>(double)a.X)-stressE)/stressE;
        double errorC=Math.Abs(heat.Sum(a=>(double)a.Y)-stressC)/stressC;
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_STRESS relativeEnergy={errorE:F10} relativeCapacity={errorC:F10} minEnergy={heat.Min(a=>a.X):R} minCapacity={heat.Min(a=>a.Y):R}"));
        Check(errorE<1e-5 && errorC<1e-5 && heat.All(a=>float.IsFinite(a.X)&&float.IsFinite(a.Y)&&a.X>=-1e-6&&a.Y>=-1e-6),
            "Cached donor limits lost inventory or produced negative state");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_LIMITERS relativeEnergy={errorE:F10} relativeCapacity={errorC:F10}"));
        Check(heat.Where(s=>s.Y>0).All(s=>s.X/s.Y>=293.05f&&s.X/s.Y<=1893.25f),
            "Transport created temperatures outside its initial range.");
        // Opposing diffusion and full-CFL advection used to leave a hot,
        // almost empty donor (1.16e8 K after just one step).
        Array.Clear(grid);Array.Clear(air);Array.Clear(links);
        var pair=new Vector2[an];pair[ai]=new(300,1);pair[ai+1]=new(5000,1);
        air[ai].VelocityX=air[ai+1].VelocityX=4;
        links[ai]=1u<<5;links[ai+1]=1u<<3;
        ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);SetCarrier();
        ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer);ctx.UpdateSubresource(pair,r.AirThermal.Buffer);
        for(int step=0;step<1000;step++)
        {
            SimulationDispatchCoordinator.DispatchAirHeat(r,false);heat=Read();
            Check(heat.All(s=>float.IsFinite(s.X)&&float.IsFinite(s.Y)&&s.X>=0&&s.Y>=0&&
                (s.Y==0?s.X==0:float.IsFinite(s.X/s.Y)&&s.X/s.Y>=299.9&&s.X/s.Y<=5000.1)),
                $"Depleted carrier outside range at step {step}: donor={heat[ai].X/heat[ai].Y:R}, C={heat[ai].Y:R}.");
        }
        Check(Math.Abs(heat.Sum(s=>(double)s.X)-5300)<.053&&Math.Abs(heat.Sum(s=>(double)s.Y)-2)<.00002,"Thermal CFL reserve deleted stock.");
        Console.WriteLine("PHYXEL_AIR_HEAT_CFL_PAIR_SUCCESS");
        // Surface exchange must carry pore stocks and latent heat, including
        // at a plateau, rather than discarding Q in a temperature clamp.
        Array.Clear(air); Array.Clear(links);
        SetCarrier(); ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer);
        foreach(string id in new[]{CoreMaterialIds.Water,CoreMaterialIds.Ice,CoreMaterialIds.Wood,CoreMaterialIds.Coal})
        foreach(bool warming in new[]{true,false})
        {
            Array.Clear(grid); uint material=registry.GetRequiredRuntimeIndex(id);
            grid[gi]=new(){IsActive=1,MaterialIndex=material,Mass=1,Temperature=id==CoreMaterialIds.Ice?0:100};
            if(id==CoreMaterialIds.Wood){grid[gi].MoistureMass=.15f;grid[gi].MoistureEnergy=20;grid[gi].FuelMass=.05f;}
            if(id==CoreMaterialIds.Coal)grid[gi].FuelMass=.1f;
            if(!warming && id==CoreMaterialIds.Ice) grid[gi].Lifetime=20;
            if(!warming && id==CoreMaterialIds.Water) grid[gi].Lifetime=20;
            double bodyBefore=grid[gi].Mass*PhaseEnthalpy.SpecificEnergy(grid[gi],table);
            var ambient=Fresh();float kelvin=warming?773.15f:263.15f;
            for(int i=0;i<an;i++)ambient[i].X=kelvin*ambient[i].Y;
            ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);ctx.UpdateSubresource(ambient,r.AirThermal.Buffer);
            for(uint step=0;step<20;step++)SimulationDispatchCoordinator.DispatchAirHeat(r,false,step);
            heat=Read();after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
            double bodyChange=after[gi].Mass*PhaseEnthalpy.SpecificEnergy(after[gi],table)-bodyBefore;
            double balance=bodyChange;
            for(int i=0;i<an;i++)balance+=heat[i].X-ambient[i].X;
            Check(Math.Abs(balance)<.002 && (warming?bodyChange>.01:bodyChange<-.01),"Surface pore/phase exchange failed: "+id+"/"+warming);
            Check(after[gi].MoistureMass==grid[gi].MoistureMass && after[gi].FuelMass==grid[gi].FuelMass,"Air exchange changed retained stocks");
            if(warming && id==CoreMaterialIds.Water)Check(after[gi].Temperature==100 && after[gi].Lifetime>0,"Water lost boiling plateau Q");
            if(warming && id==CoreMaterialIds.Ice)Check(after[gi].Temperature==0 && after[gi].Lifetime>0,"Ice lost fusion plateau Q");
            if(warming && id==CoreMaterialIds.Wood)Check(after[gi].Temperature==100 && after[gi].MoistureEnergy>20,"Wet wood lost drying plateau Q");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_SURFACE id={id} warming={warming} Q={bodyChange:F6} error={balance:F8}"));
        }
        // The same floor is sealed for particles, oxygen and the pressure
        // carrier. An open atmosphere at the other three edges must not reset
        // this bottom stock even though the scene uses OpenBoundaries.
        Array.Clear(grid);Array.Clear(air);Array.Clear(links);
        ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);SetCarrier();ctx.UpdateSubresource(links,r.AirFlowLinks.Buffer);
        var floorStock=Enumerable.Repeat(new Vector2(773.15f*.032f,.032f),an).ToArray();
        ctx.UpdateSubresource(floorStock,r.AirThermal.Buffer);
        SimulationDispatchCoordinator.DispatchAirHeat(r,true);
        heat=Read();int floorIndex=(r.AirHeight-1)*r.AirWidth+r.AirWidth/2;
        Check(heat[floorIndex]==floorStock[floorIndex],"Closed floor exchanged thermal stock with atmosphere");
        Console.WriteLine("PHYXEL_AIR_HEAT_CLOSED_FLOOR_SUCCESS");
        // Resetting a depleted hot outflow to nominal capacity must not
        // multiply its excess heat. An atmospheric refill supplies ambient E.
        int outlet=(r.AirHeight/2)*r.AirWidth+r.AirWidth-1;
        var outletStock=Fresh();outletStock[outlet]=new(773.15f*.004f,.004f);
        air[outlet].VelocityX=1;SetCarrier();ctx.UpdateSubresource(outletStock,r.AirThermal.Buffer);
        SimulationDispatchCoordinator.DispatchAirHeat(r,true);heat=Read();
        double outletBefore=outletStock[outlet].X-293.15*outletStock[outlet].Y;
        double outletAfter=heat[outlet].X-293.15*heat[outlet].Y;
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_HEAT_OUTLET before={outletBefore:F8} after={outletAfter:F8}"));
        Check(outletAfter<=outletBefore+1e-5 && outletAfter>=outletBefore-1e-5,
            "Atmospheric outlet created excess thermal energy");
        Console.WriteLine("PHYXEL_AIR_HEAT_SUCCESS");
    }
}
