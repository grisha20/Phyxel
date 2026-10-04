using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

internal static class OilRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/oil";
        Directory.CreateDirectory(dir);
        var settings=new SimulationSettings {Paused=true,AirSimulation=false,OpenBoundaries=false};
        uint oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture),fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var table=registry.CreateGpuTable();
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=(ushort)oil}],0);
        int w=r.Width,n=w*r.Height,checks=0;bool passed=true;
        var serializer=new SimulationStateSerializer();
        GridCell Cell(uint id,float t=20,float mass=1)=>new(){IsActive=1,MaterialIndex=id,Mass=mass,Temperature=t};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Mass(GridCell[] g,uint id)=>g.Where(c=>c.IsActive!=0).Sum(c=>
            (c.MaterialIndex==id || (id==oil&&(c.MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.FrozenOil)||
                c.MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.OilVapour)))?(double)c.Mass:0)+(id==oil?c.FuelMass:0));
        double Energy(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        void Check(bool ok,string label){checks++;if(!ok){passed=false;Console.WriteLine("PHYXEL_OIL_CHECK_FAILED "+label);}}
        void Balance(GridCell[] before,GridCell[] after,string label){
            foreach(uint id in new[]{oil,water})Check(Math.Abs(Mass(after,id)-Mass(before,id))<=.0001*Math.Max(1,Mass(before,id)),label+" mass "+id);
            Check(Math.Abs(Energy(after)-Energy(before))<=.0001*Math.Max(1,Math.Abs(Energy(before))),label+" energy");
        }
        void Capture(GridCell[] g,string name)=>File.WriteAllBytes(Path.Combine(dir,name+".grid"),MemoryMarshal.AsBytes(g.AsSpan()).ToArray());
        void Start(GridCell[] grid,SimulationMode mode){
            settings.Mode=mode;settings.Paused=false;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false);
        }
        void Step(int frames,int fps){for(int f=0;f<frames;f++)coordinator.DispatchFrame(settings,[],1f/fps);}
        GridCell[] Basin(){
            var g=new GridCell[n];
            for(int y=85;y<=190;y++)foreach(int x in new[]{100,180})g[y*w+x]=Cell(fixture);
            for(int x=100;x<=180;x++)g[190*w+x]=Cell(fixture);
            return g;
        }
        // Start with the lighter liquid entirely below the heavier one.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            var before=Basin();
            for(int y=150;y<190;y++)for(int x=101;x<180;x++)before[y*w+x]=Cell(y<170?water:oil);
            Start(before,mode);Step(fps*10,fps);var after=Read();
            double top=Enumerable.Range(0,n).Where(i=>i/w<170&&after[i].IsActive!=0&&after[i].MaterialIndex==oil).Sum(i=>(double)after[i].Mass);
            Check(top>=.95*Mass(before,oil),"layers "+mode+" "+fps+" top="+top);
            Balance(before,after,"layers "+mode+" "+fps);Capture(after,$"layers-{mode}-{fps}");
            Console.WriteLine($"PHYXEL_OIL_LAYERS mode={mode} fps={fps} top={top:F3}/{Mass(before,oil):F3}");
        }
        // Exercise the actual empty-only brush on a water surface.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation}){
            var g=Basin();for(int y=170;y<190;y++)for(int x=101;x<180;x++)g[y*w+x]=Cell(water);
            Start(g,mode);
            for(int f=0;f<360;f++)coordinator.DispatchFrame(settings,f<60?[new(){X=140,Y=150,EndX=140,EndY=150,
                Radius=3,Density=.82f,Mode=BrushCommandMode.Material,MaterialIndex=(ushort)oil}]:[],1f/60);
            var after=Read();Check(Mass(after,oil)>0,"oil brush empty "+mode);
            Check(Math.Abs(Mass(after,water)-Mass(g,water))<.0001,"oil brush altered water "+mode);
            double meanOil=Enumerable.Range(0,n).Where(i=>after[i].IsActive!=0&&after[i].MaterialIndex==oil).Average(i=>(double)(i/w));
            double meanWater=Enumerable.Range(0,n).Where(i=>after[i].IsActive!=0&&after[i].MaterialIndex==water).Average(i=>(double)(i/w));
            Check(meanOil<meanWater,"brush layering "+mode);Capture(after,"brush-"+mode);
        }
        settings.HydraulicPressure=true;
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation}){
            var before=Basin();
            for(int y=150;y<190;y++)for(int x=101;x<180;x++)before[y*w+x]=Cell(y<170?water:oil);
            Start(before,mode);Step(600,60);var after=Read();
            double top=Enumerable.Range(0,n).Where(i=>i/w<170&&after[i].IsActive!=0&&after[i].MaterialIndex==oil).Sum(i=>(double)after[i].Mass);
            Check(top>=.95*Mass(before,oil),"hydraulic layers "+mode);Balance(before,after,"hydraulic layers "+mode);
        }
        settings.HydraulicPressure=false;
        foreach(var sample in new[]{(CoreMaterialIds.Coal,0f,true),(CoreMaterialIds.WetCharcoal,.35f,false),
            (CoreMaterialIds.StoneCoal,0f,false),(CoreMaterialIds.Sand,0f,false),
            (CoreMaterialIds.Coal,.1f,true),(CoreMaterialIds.Coal,.25f,false)}){
            var (id,moisture,floats)=sample;
            uint grain=registry.GetRequiredRuntimeIndex(id);var before=Basin();
            for(int y=130;y<190;y++)for(int x=101;x<180;x++)before[y*w+x]=Cell(oil);
            before[155*w+140]=Cell(grain);
            before[155*w+140].MoistureMass=moisture;
            Start(before,SimulationMode.Sandbox);
            Step(15,60);
            if(floats)
            {
                var early=Read();int earlyAt=Array.FindIndex(early,c=>c.IsActive!=0&&c.MaterialIndex==grain);
                Check(earlyAt>=0 && earlyAt/w<155,"initial light grain no longer floats "+id);
            }
            Step(345,60);var after=Read();
            uint dry=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),wet=registry.GetRequiredRuntimeIndex(CoreMaterialIds.WetCharcoal);
            int at=Array.FindIndex(after,c=>c.IsActive!=0&&(c.MaterialIndex==grain||
                (id==CoreMaterialIds.Coal&&c.MaterialIndex==wet)||(id==CoreMaterialIds.WetCharcoal&&c.MaterialIndex==dry)));
            // The newly stored oil increases density: these absorbent grains
            // start light, then sink in oil after filling their available pores.
            Check(at>=0 && at/w>155,"late buoyancy "+id+" moisture="+moisture+" y="+at/w);
            if(floats)Check(after[at].FuelMass>0,"Absorbent grain sank without storing oil");
            Check(Math.Abs(after.Sum(c=>(double)c.MoistureMass)-before.Sum(c=>(double)c.MoistureMass))<1e-6,"oil absorbed as water "+id);
            Balance(before,after,"buoyancy "+id);Console.WriteLine($"PHYXEL_OIL_BUOYANCY id={id} moisture={moisture} y={at/w}");
        }
        // Directly dispatch one fixed reaction tick to isolate the surface and
        // oxidizer rules from motion, thermal diffusion and random ignition.
        (double burn,double demand) Reaction(string cover,bool finite,float oxygen,float temperature=300){
            int center=100*w+140;var g=new GridCell[n];var supply=new float[n];g[center]=Cell(oil,temperature);
            foreach(int neighbor in new[]{center-1,center+1,center-w,center+w}){
                if(cover=="metal")g[neighbor]=Cell(metal);
                else if(cover=="water")g[neighbor]=Cell(water);
                else if(cover=="oil")g[neighbor]=Cell(oil);
                else if(cover=="co2")g[neighbor]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Co2));
                else supply[neighbor]=oxygen;
            }
            var ctx=r.Context;ctx.UpdateSubresource(g,r.Grid.ReadBuffer);ctx.UpdateSubresource(supply,r.OxidizerAvailable.Buffer);
            ctx.ClearUnorderedAccessView(r.CombustionSummary.UnorderedView,new RawInt4());
            ctx.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            ctx.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
            ctx.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
            var c=new CombustionConstants{Width=(uint)w,Height=(uint)r.Height,MaterialCount=(uint)registry.Materials.Count,
                DeltaTime=.05f,TickIndex=1,FiniteOxidizer=finite?1u:0u};
            ctx.UpdateSubresource(ref c,r.CombustionConstants);ctx.ComputeShader.Set(r.CombustionShader);
            ctx.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            ctx.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView);
            ctx.Dispatch((w+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<3;i++)ctx.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<5;i++)ctx.ComputeShader.SetUnorderedAccessView(i,null);ctx.ComputeShader.Set(null);
            var after=Read();var demand=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.OxidizerDemand.Buffer));
            return(1-after[center].Mass,demand[center]);
        }
        foreach(bool finite in new[]{false,true}){
            var open=Reaction("air",finite,1);Check(Math.Abs(open.burn-.009)<2e-7,"open reaction finite="+finite+" burn="+open.burn);
            Check(Math.Abs(open.demand-(finite?.036:0))<2e-7,"reaction oxygen ledger");
            foreach(string cover in new[]{"metal","water","oil"})Check(Reaction(cover,finite,1).burn==0,"covered reaction "+cover+" finite="+finite);
            Check(Reaction("air",finite,1,20).burn==0,"cold reaction finite="+finite);
        }
        Check(Reaction("co2",true,0).burn==0,"CO2 did not extinguish oil");
        Check(Reaction("air",true,0).burn==0,"starved oil consumed fuel");

        GridCell[] Pool(){
            var g=new GridCell[n];for(int x=110;x<=133;x++)g[161*w+x]=Cell(fixture);
            for(int y=150;y<=161;y++)foreach(int x in new[]{110,133})g[y*w+x]=Cell(fixture);
            for(int y=155;y<161;y++)for(int x=111;x<133;x++)g[y*w+x]=Cell(oil);
            return g;
        }
        // This is an open pool with ambient air transport. Disabling the air
        // solver isolates local inventory and is a separate starvation case.
        settings.AirSimulation=true;settings.OpenBoundaries=true;
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            var before=Pool();Start(before,mode);Step(fps*5,fps);var cold=Read();
            Check(Math.Abs(Mass(cold,oil)-Mass(before,oil))<.001,"cold pool burned "+mode+" "+fps);
            // OP contract replaces legacy instantaneous ignition at20°C:
            // preheat below auto ignition but above the contact threshold.
            // Keep the original >=5% loss/live-fire criteria and count all
            // three oil phases, so boiling cannot masquerade as consumption.
            for(int i=0;i<n;i++)if(before[i].IsActive!=0&&before[i].MaterialIndex==oil)before[i].Temperature=150;
            before[154*w+112]=Cell(fire,700);before[154*w+112].Lifetime=table[fire].MaximumLifetime;
            Start(before,mode);Step(fps*5,fps);var after=Read();double loss=Mass(before,oil)-Mass(after,oil);
            Check(loss>=.05*Mass(before,oil),"edge ignition too weak "+mode+" "+fps+" loss="+loss);
            Check(after.Any(c=>c.IsActive!=0&&c.MaterialIndex==fire&&c.Lifetime>0),"pool has no live flame "+mode+" "+fps);
            Capture(after,$"pool-{mode}-{fps}");Console.WriteLine($"PHYXEL_OIL_POOL mode={mode} fps={fps} consumed={loss:F3}/{Mass(before,oil):F3}");
        }
        // Oil burning on water uses the same liquid ID boundaries and gas face.
        var demo=Basin();
        for(int y=150;y<190;y++)for(int x=101;x<180;x++)demo[y*w+x]=Cell(y<160?oil:water,y<160?150:20);
        demo[149*w+110]=Cell(fire,700);demo[149*w+110].Lifetime=table[fire].MaximumLifetime;
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation}){
            Start(demo,mode);Step(300,60);var after=Read();
            Check(Mass(after,oil)<Mass(demo,oil),"oil on water did not ignite "+mode);
            Check(Mass(after,water)>0 && after.Any(c=>c.IsActive!=0&&c.MaterialIndex==fire&&c.Lifetime>0),"water/fire lost "+mode);
            Capture(after,"on-water-"+mode);
        }
        var demoSnapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(demo.AsSpan()).ToArray());
        settings.Mode=SimulationMode.Sandbox;settings.Paused=false;
        Task.Run(()=>serializer.SaveAsync(Path.Combine(dir,"oil-on-water-demo.json"),settings,(ushort)oil,demoSnapshot,registry)).GetAwaiter().GetResult();
        var save=Pool();for(int y=155;y<161;y++)for(int x=111;x<133;x++)save[y*w+x]=Cell(oil,400,.37f);
        var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(save.AsSpan()).ToArray());
        string path=Path.Combine(dir,"partial-oil.json");
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)oil,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        Check(snapshot.Grid.AsSpan().SequenceEqual(loaded.World!.Grid),"oil save/load changed cells");
        Start(save,SimulationMode.Sandbox);settings.Paused=true;Step(60,60);var paused=Read();
        Check(snapshot.Grid.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(paused.AsSpan())),"pause changed oil");
        Start(MemoryMarshal.Cast<byte,GridCell>(loaded.World.Grid).ToArray(),SimulationMode.Sandbox);Step(60,60);
        Check(Mass(Read(),oil)<Mass(save,oil),"reload oil did not resume burning");
        File.WriteAllText(Path.Combine(dir,"layout.txt"),$"{w} {r.Height} 52 {oil} {water} {fixture} {fire}");
        Console.WriteLine($"PHYXEL_OIL_RESULT passed={passed} checks={checks}");if(!passed)Environment.ExitCode=1;
    }
}
