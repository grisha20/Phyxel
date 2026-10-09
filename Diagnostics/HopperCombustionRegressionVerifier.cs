using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

// Isolated GPU kernels: thermal/motion are deliberately absent so ignition
// cannot borrow a hidden temperature jump and reaction energy is measurable.
internal static class HopperCombustionRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/hopper-kernels";
        Directory.CreateDirectory(dir);
        settings.Width=64;settings.Height=64;settings.Paused=true;
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        uint fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint steel=registry.GetRequiredRuntimeIndex("core:steel");
        uint oil=registry.GetRequiredRuntimeIndex("core:oil");
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=coal}],0);
        int n=r.Width*r.Height, center=30*r.Width+30;
        var rows=new List<object>();int checks=0;
        void Check(bool pass,string name){checks++;if(!pass)throw new InvalidOperationException(name);}
        T[] Read<T>(SharpDX.Direct3D11.Buffer buffer) where T:unmanaged =>
            MemoryMarshal.Cast<byte,T>(AirInventoryRegressionVerifier.Read(r,buffer)).ToArray();
        GridCell Cell(uint material,float temperature,float mass=1,float life=0)=>new(){
            IsActive=1,MaterialIndex=material,Mass=mass,Temperature=temperature,Lifetime=life};
        double Energy(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>{
            double capacity=c.Mass*registry[c.MaterialIndex].Properties.HeatCapacity;
            if(c.FuelMass>0)capacity+=c.FuelMass*registry[c.RetainedLiquidMaterialIndex].Properties.HeatCapacity;
            return capacity*(c.Temperature+273.15);
        });
        foreach(uint fuel in new[]{coal,registry.GetRequiredRuntimeIndex("core:stone_coal")})
        foreach(string scenario in new[]{"cold-contact","cold-latch","buried","no-oxygen","hot-wall-no-oxygen",
            "inert-co2","inert-steam","smolder","hot","burnout","epsilon-residue","collision","absorbed-oil"})
        foreach(bool finiteOxygen in new[]{false,true})
        {
            if(scenario=="absorbed-oil"&&registry[fuel].Properties.FuelCapacity<=0)continue;
            var grid=new GridCell[n];var oxygen=new float[n];Array.Fill(oxygen,1);
            float mass=scenario=="burnout"?.0005f:scenario=="epsilon-residue"?.00055f:1;
            grid[center]=Cell(fuel,scenario=="cold-contact"?30:scenario=="cold-latch"?100:700,mass,
                scenario is "cold-latch" or "buried"?1:0);
            if(scenario=="cold-contact")grid[center-r.Width]=Cell(fire,700,1,2);
            if(scenario=="buried")foreach(int offset in new[]{-1,1,-r.Width,r.Width})grid[center+offset]=Cell(steel,700,7.8f);
            if(scenario is "no-oxygen" or "hot-wall-no-oxygen")Array.Fill(oxygen,0);
            if(scenario=="hot-wall-no-oxygen"){
                grid[center]=Cell(fuel,30);grid[center+1]=Cell(steel,1000,7.8f);
            }
            if(scenario.StartsWith("inert-",StringComparison.Ordinal)){
                uint inert=registry.GetRequiredRuntimeIndex(scenario=="inert-co2"?CoreMaterialIds.Co2:CoreMaterialIds.Steam);
                foreach(int offset in new[]{-1,1,-r.Width,r.Width})grid[center+offset]=Cell(inert,700);
                Array.Fill(oxygen,0);
            }
            if(scenario=="smolder")grid[center].Temperature=registry[fuel].Properties.IgnitionTemperature+10;
            if(scenario=="collision")grid[center+2]=Cell(fuel,700);
            if(scenario=="absorbed-oil"){
                grid[center].FuelMass=.2f;grid[center].RetainedLiquidMaterialIndex=oil;
            }
            double before=Energy(grid), initialFuel=grid.Where(c=>c.MaterialIndex==fuel).Sum(c=>(double)c.Mass);
            double initialOil=grid.Sum(c=>(double)c.FuelMass);
            var c=r.Context;c.UpdateSubresource(grid,r.Grid.ReadBuffer);
            c.UpdateSubresource(oxygen,r.OxidizerAvailable.Buffer);
            c.UpdateSubresource(new Vector4[n],r.ReactionPending.Buffer);
            c.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            c.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
            c.ClearUnorderedAccessView(r.CombustionSummary.UnorderedView,new RawInt4());
            c.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
            var constants=new CombustionConstants{Width=(uint)r.Width,Height=(uint)r.Height,
                MaterialCount=(uint)registry.Count,DeltaTime=1f/60,TickIndex=17,FiniteOxidizer=finiteOxygen?1u:0u,Reserved1=1};
            c.UpdateSubresource(ref constants,r.CombustionConstants);
            c.ComputeShader.Set(r.CombustionShader);c.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            c.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
            c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<3;i++)c.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<6;i++)c.ComputeShader.SetUnorderedAccessView(i,null);
            coordinator.DispatchEmissionResolve(r);
            var after=Read<GridCell>(r.Grid.ReadBuffer);var pending=Read<Vector4>(r.ReactionPending.Buffer);
            double consumed=initialFuel-after.Where(cell=>cell.IsActive!=0&&cell.MaterialIndex==fuel).Sum(cell=>(double)cell.Mass);
            double oilConsumed=initialOil-after.Sum(cell=>(double)cell.FuelMass);
            double expected=before+consumed*registry[fuel].Properties.HeatPerMass+oilConsumed*registry[oil].Properties.HeatPerMass;
            double actual=Energy(after)+pending.Sum(p=>(double)p.Y);
            double error=Math.Abs(actual-expected)/Math.Max(1,Math.Abs(expected));
            bool blocked=scenario is "cold-contact" or "cold-latch" or "buried" or "hot-wall-no-oxygen" or "inert-co2" or "inert-steam" ||
                finiteOxygen&&scenario=="no-oxygen";
            rows.Add(new{fuel,scenario,finiteOxygen,consumed,oilConsumed,error,temperature=after[center].Temperature,
                pendingHeat=pending.Sum(p=>(double)p.Y),products=after.Count(cell=>cell.IsActive!=0&&cell.MaterialIndex!=fuel&&cell.MaterialIndex!=steel)});
            File.WriteAllText(Path.Combine(dir,"kernels.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
            Check(error<=1e-4,$"Heat budget {fuel}/{scenario}/{finiteOxygen}: {error}");
            Check(blocked?consumed==0:consumed>0,$"Reaction gate {fuel}/{scenario}/{finiteOxygen}: {consumed}");
            if(scenario=="cold-contact")Check(after[center].Temperature==30,"Cold contact temperature jump");
            if(scenario=="smolder")Check(!after.Any(cell=>cell.IsActive!=0&&cell.MaterialIndex==fire),"Low-temperature char oxidation created FIRE");
            Check(pending.All(p=>p.X==0),"Coal created mechanical pressure");
            yield return r;
        }
        // Mapping/clearing must agree even near tile borders, fine walls and
        // filters: every released stock has one receiver or remains pending.
        foreach(bool sealedStock in new[]{false,true})
        {
            var grid=new GridCell[n];var stock=new Vector4[n];var filters=new uint[n+1];
            if(sealedStock)for(int i=0;i<n;i++)grid[i]=Cell(steel,20,7.8f);
            for(int y=1;y<r.Height-1;y+=3)for(int x=1;x<r.Width-1;x+=3){
                int i=y*r.Width+x;grid[i]=Cell(coal,500);stock[i]=new(0,100+i%7,.1f,0);
                if(!sealedStock&&x%2==0){filters[0]++;filters[i+1]=FilterRules.Closed;}
            }
            var c=r.Context;c.UpdateSubresource(grid,r.Grid.ReadBuffer);c.UpdateSubresource(filters,r.Filters.Buffer);
            c.UpdateSubresource(stock,r.ReactionPending.Buffer);
            int an=r.AirWidth*r.AirHeight;
            c.UpdateSubresource(new Vector2[an],r.AirThermal.Buffer);
            foreach(var buffer in r.ReactionPulse.Buffers)c.UpdateSubresource(new Vector4[an],buffer);
            var constants=new AirSimulationConstants{AirWidth=(uint)r.AirWidth,AirHeight=(uint)r.AirHeight,
                AirGridWidth=(uint)r.Width,AirGridHeight=(uint)r.Height,AirSandboxMode=0};
            c.UpdateSubresource(ref constants,r.AirConstants);c.ComputeShader.SetConstantBuffer(0,r.AirConstants);
            c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Grid.ReadView,r.ReactionPulse.ReadView,r.AirFlowLinks.View);
            c.ComputeShader.SetUnorderedAccessViews(0,r.ReactionPending.UnorderedView,r.ReactionPulse.WriteUnorderedView,
                r.ReactionPulseScratch.UnorderedView,r.Air.UnorderedView,r.AirThermal.UnorderedView,r.AirProjectionB.UnorderedView);
            c.ComputeShader.Set(r.ReactionGatherShader);c.Dispatch((r.AirWidth+7)/8,(r.AirHeight+7)/8,1);
            c.ComputeShader.Set(r.ReactionClearMappedShader);c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<4;i++)c.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<6;i++)c.ComputeShader.SetUnorderedAccessView(i,null);
            var pending=Read<Vector4>(r.ReactionPending.Buffer);var heat=Read<Vector2>(r.AirThermal.Buffer);
            double beforeE=stock.Sum(p=>(double)p.Y),beforeC=stock.Sum(p=>(double)p.Z);
            Check(Math.Abs(heat.Sum(p=>(double)p.X)+pending.Sum(p=>(double)p.Y)-beforeE)/beforeE<1e-5,"Surface heat mapping lost or duplicated E");
            Check(Math.Abs(heat.Sum(p=>(double)p.Y)+pending.Sum(p=>(double)p.Z)-beforeC)/beforeC<1e-5,"Surface heat mapping lost or duplicated C");
            Check(sealedStock?heat.All(p=>p==Vector2.Zero):heat.Any(p=>p.X>0),"Surface gas receiver gating");
            for(int i=0;i<n;i++)if(filters[i+1]!=0)Check(pending[i]==stock[i],"Heat crossed closed source filter");
            yield return r;
        }
        // Full production ticks: retained phase-gas pressure must survive the
        // low-Mach split, while an outlet releases the compressible stock.
        var steamRows=new List<object>();
        var pressures=new List<double>();
        foreach(bool vent in new[]{false,true})
        {
            settings.Paused=true;settings.Mode=SimulationMode.Simulation;
            settings.OpenBoundaries=true;settings.AirSimulation=true;
            coordinator.ClearCurrentWorld(settings);
            var grid=new GridCell[n];
            uint steam=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
            for(int y=16;y<48;y++)for(int x=16;x<48;x++){
                bool wall=x<18||x>=46||y<18||y>=46;
                if(vent&&y<18&&x>=24&&x<40)continue;
                grid[y*r.Width+x]=wall?Cell(fixture,200,1):Cell(steam,200,.03f,2256);
            }
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            settings.Paused=false;
            for(int frame=0;frame<180;frame++)r=coordinator.DispatchFrame(settings,[],1f/60);
            var air=Read<AirCell>(r.Air.Buffer);
            double mean=Enumerable.Range(5,6).SelectMany(y=>Enumerable.Range(5,6).Select(x=>(double)air[y*r.AirWidth+x].Pressure)).Average();
            var phaseRow=new{vent,seconds=3,meanPressure=mean,minimum=air.Min(a=>a.Pressure),maximum=air.Max(a=>a.Pressure),
                steamCells=Read<GridCell>(r.Grid.ReadBuffer).Count(g=>g.IsActive!=0&&g.MaterialIndex==steam)};
            Console.WriteLine("PHYXEL_STEAM_PRESSURE "+JsonSerializer.Serialize(phaseRow));
            pressures.Add(mean);steamRows.Add(phaseRow);
            File.WriteAllText(Path.Combine(dir,"steam-pressure.json"),JsonSerializer.Serialize(steamRows,new JsonSerializerOptions{WriteIndented=true}));
            if(!vent)Check(mean>0,"Steam pressure source disappeared in low-Mach split");
            else Check(phaseRow.steamCells<grid.Count(g=>g.IsActive!=0&&g.MaterialIndex==steam),"Steam did not leave through the outlet");
            yield return r;
        }
        Check(pressures[0]>Math.Abs(pressures[1])*1.2,"Steam outlet did not relieve pressure");
        File.WriteAllText(Path.Combine(dir,"steam-pressure.json"),JsonSerializer.Serialize(steamRows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_HOPPER_KERNELS_SUCCESS checks={checks}");
    }
}
