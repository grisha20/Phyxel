using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class FrozenBodyRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/frozen-bodies";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_FROZEN_BODY_BASELINE")=="1";
        var settings=new SimulationSettings{Paused=true,AirSimulation=false,OpenBoundaries=false,SolidGravity=true};
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice)}],0);
        int w=r.Width,n=w*r.Height,checks=0,failures=0;
        var table=registry.CreateGpuTable();var physical=table.ToArray();
        uint ice=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice),frozen=registry.GetRequiredRuntimeIndex(CoreMaterialIds.FrozenOil),
            water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),
            fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture),metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        // Isolated transport. Production density/flags/mobility retained; only
        // heat conduction is disabled to prevent the test bath melting the ice.
        foreach(int i in Enumerable.Range(0,table.Length))table[i].ThermalConductivity=0;
        r.Materials.Upload(r.Context,table);
        var serializer=new SimulationStateSerializer();var metrics=new List<object>();
        var centers=new Dictionary<(uint,uint,SimulationMode,int),double>();
        double energyResidual=0;
        void Check(bool ok,string label){checks++;if(!ok){failures++;Console.WriteLine("PHYXEL_FB_FAIL "+label);}}
        GridCell Cell(uint id,float t=20,float progress=0)=>new(){IsActive=1,MaterialIndex=id,
            Mass=physical[id].SimulationKind==(uint)MaterialSimulationKind.Solid?physical[id].Density:1,
            Temperature=t,Lifetime=progress,RestFrames=physical[id].SimulationKind==(uint)MaterialSimulationKind.Solid&&
                (physical[id].Flags&(uint)MaterialFlags.MovableSolid)==0?2u:0u};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Start(GridCell[] g){serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false);r.Materials.Upload(r.Context,table);}
        void Advance(int fps,int seconds){settings.Paused=false;for(int f=0;f<fps*seconds;f++){
            coordinator.DispatchFrame(settings,[],1f/fps);
            // Exercise the production statistics/sleep gate, not just direct
            // shader stepping in a perpetually awake diagnostic world.
            // Complete each diagnostic frame as presentation would. Bursting
            // 15 frames without presenting filled the asynchronous phase
            // summary ring and correctly triggered conservative wake-ups.
            coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(
                AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
        }}
        double Energy(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        double Center(GridCell[] g,uint id)=>Enumerable.Range(0,n).Where(i=>g[i].IsActive!=0&&g[i].MaterialIndex==id).Average(i=>(double)(i/w));
        void Balance(GridCell[] a,GridCell[] b,string label,bool phases=false){
            double error=Math.Abs(Energy(a)-Energy(b))/Math.Max(1,Math.Abs(Energy(a)));energyResidual=Math.Max(error,energyResidual);
            Check(error<=.00005,label+" heat "+error);
            Check(Math.Abs(a.Sum(c=>(double)c.Mass)-b.Sum(c=>(double)c.Mass))<=Math.Max(1,a.Sum(c=>(double)c.Mass))*1e-5,label+" total mass");
            if(!phases)foreach(uint id in a.Where(c=>c.IsActive!=0).Select(c=>c.MaterialIndex).Distinct())
                Check(Math.Abs(a.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass)-
                    b.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass))<=
                    Math.Max(1,a.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass))*1e-5,label+" material mass "+id);
            Check(Enumerable.Range(0,n).All(i=>a[i].MaterialIndex!=fixture||a[i].IsActive==0||
                (b[i].IsActive!=0&&b[i].MaterialIndex==fixture)),label+" walls");
        }
        GridCell[] Basin(uint body,uint liquid,int top=135){
            var g=new GridCell[n];for(int y=60;y<=230;y++)for(int x=110;x<=210;x++){
                if(x==110||x==210||y==230)g[y*w+x]=Cell(fixture);
                else if(y>=90)g[y*w+x]=Cell(liquid);
            }
            for(int y=top;y<top+12;y++)for(int x=154;x<166;x++)g[y*w+x]=Cell(body,body==ice?-5:10);
            return g;
        }
        foreach(uint body in new[]{ice,frozen})foreach(uint liquid in new[]{water,oil})
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            settings.Mode=mode;var before=Basin(body,liquid);Start(before);Advance(fps,3);var mid=Read();
            double start=Center(before,body),middle=Center(mid,body);bool floats=liquid==water;
            Check(floats?middle<=start-15:middle>=start+15,$"direction {body}/{liquid}/{mode}/{fps} center={middle}");
            Advance(fps,3);var near=Read();Advance(fps,2);var after=Read();double end=Center(after,body);
            Check(Math.Abs(Center(near,body)-end)<=2,"settled "+body+"/"+liquid+"/"+mode+"/"+fps);
            Check(floats?end>=92&&end<=100:end>=221&&end<=224,"surface/bottom "+body+"/"+liquid+" "+end);
            Check(after.Count(c=>c.IsActive!=0&&c.MaterialIndex==body)==144,"body remains whole");
            Balance(before,after,"transport "+body+"/"+liquid+"/"+mode+"/"+fps);
            centers[(body,liquid,mode,fps)]=end;
            metrics.Add(new{body,liquid,mode=mode.ToString(),fps,start,middle,end});
            if(mode==SimulationMode.Sandbox&&fps==60){File.WriteAllBytes(Path.Combine(dir,$"{body}-{liquid}-before.grid"),MemoryMarshal.AsBytes(before.AsSpan()).ToArray());
                File.WriteAllBytes(Path.Combine(dir,$"{body}-{liquid}-after.grid"),MemoryMarshal.AsBytes(after.AsSpan()).ToArray());}
            Console.WriteLine($"PHYXEL_FB_MOTION body={body} liquid={liquid} mode={mode} fps={fps} start={start:F3} mid={middle:F3} end={end:F3}");
        }
        foreach(uint body in new[]{ice,frozen})foreach(uint liquid in new[]{water,oil})foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
            foreach(int fps in new[]{30,100})Check(Math.Abs(centers[(body,liquid,mode,fps)]-centers[(body,liquid,mode,60)])<=3,"FPS agreement");
        // The fluid density is a property, not an oil/water identity rule.
        table[oil].Density=1.2f;r.Materials.Upload(r.Context,table);
        var dense=Basin(ice,oil);Start(dense);Advance(60,3);Check(Center(Read(),ice)<=Center(dense,ice)-15,"custom denser liquid");
        table[oil]=physical[oil];table[oil].ThermalConductivity=0;
        foreach(uint body in new[]{ice,frozen})foreach(uint liquid in new[]{water,oil})foreach(int width in new[]{1,12}){
            var thin=Basin(body,liquid);
            for(int y=135;y<147;y++)for(int x=154;x<166;x++)thin[y*w+x]=Cell(liquid);
            for(int x=154;x<154+width;x++)thin[135*w+x]=Cell(body,body==ice?-5:10);
            Start(thin);Advance(60,3);var middle=Read();bool floats=liquid==water;
            Check(floats?Center(middle,body)<=120:Center(middle,body)>=150,"thin density direction "+body+"/"+liquid+"/"+width);
            Advance(60,5);var stable=Read();Advance(60,2);var end=Read();
            Check(Math.Abs(Center(stable,body)-Center(end,body))<=1,"thin waterline settles "+body+"/"+liquid+"/"+width);
            Check(floats?Center(end,body)>=88&&Center(end,body)<=95:Center(end,body)>=229,"thin surface/bottom");
            Balance(thin,end,"thin "+body+"/"+liquid+"/"+width);
            metrics.Add(new{scenario="thin",body,liquid,width,end=Center(end,body)});
        }
        foreach(bool roof in new[]{false,true}){
            var g=Basin(ice,water);int y=roof?134:147;
            for(int x=154;x<166;x++)g[y*w+x]=Cell(fixture);Start(g);Advance(60,3);var after=Read();
            if(roof)Check(Math.Abs(Center(after,ice)-Center(g,ice))<.01,"ceiling blocks rising body");
            Balance(g,after,"obstacle "+roof);
        }
        var touching=Basin(ice,water);for(int y=120;y<170;y++)touching[y*w+166]=Cell(metal,20);
        for(int y=170;y<=230;y++)touching[y*w+166]=Cell(fixture);
        Start(touching);Advance(60,3);var contact=Read();Check(Center(contact,ice)<=Center(touching,ice)-15,"ice is not welded to vessel");
        Check(Enumerable.Range(0,n).All(i=>touching[i].MaterialIndex!=metal||touching[i].IsActive==0||contact[i].MaterialIndex==metal),"supported metal stays");
        Balance(touching,contact,"touching vessel");
        var falling=new GridCell[n];for(int y=80;y<92;y++)for(int x=154;x<166;x++)falling[y*w+x]=Cell(frozen,10);
        Start(falling);Advance(60,3);Check(Center(Read(),frozen)>=Center(falling,frozen)+15,"free fall");Balance(falling,Read(),"free fall");
        Advance(60,20);Check(coordinator.SolidSleeping,"resting solid enters production sleep");
        settings.SolidGravity=false;Start(falling);Advance(60,2);Check(Center(Read(),frozen)>Center(falling,frozen)+15,"free body moves with construction gravity off");settings.SolidGravity=true;
        // Partial latent enthalpy is transported as part of each cell packet.
        var partial=Basin(ice,water);for(int i=0;i<n;i++)if(partial[i].IsActive!=0&&partial[i].MaterialIndex==ice){partial[i].Temperature=0;partial[i].PhaseProgress=100;}
        Start(partial);Advance(60,2);var moved=Read();Check(Center(moved,ice)<Center(partial,ice)-15,"partial melt moves");
        Check(moved.Where(c=>c.IsActive!=0&&c.MaterialIndex==ice).All(c=>c.Temperature==0&&c.PhaseProgress==100),"partial enthalpy transported");Balance(partial,moved,"partial transport");
        settings.Paused=true;var pause=Read();coordinator.DispatchFrame(settings,[],1);
        Check(MemoryMarshal.AsBytes(pause.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"pause");
        string path=Path.Combine(dir,"floating-partial.json");var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(pause.AsSpan()).ToArray());
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)ice,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        Check(loaded.World!.Grid.AsSpan().SequenceEqual(snapshot.Grid),"writer14 byte exact");
        Start(pause);Advance(60,2);var continued=Read();Start(MemoryMarshal.Cast<byte,GridCell>(loaded.World.Grid).ToArray());Advance(60,2);var resumed=Read();
        Check(Math.Abs(Center(continued,ice)-Center(resumed,ice))<=1,"reload motion rebuild");Balance(pause,resumed,"reload transport");
        // Paid phase conversion, fluid motion, and rebuilding the new solid
        // body. External heat/cooling is applied explicitly before each audit.
        void Phase(){
            var c=new PhaseTransitionConstants{Width=(uint)w,Height=(uint)r.Height,MaterialCount=(uint)table.Length,TickCount=1};
            r.Context.UpdateSubresource(ref c,r.PhaseConstants);r.Context.ComputeShader.Set(r.PhaseTransitionShader);
            r.Context.ComputeShader.SetConstantBuffer(0,r.PhaseConstants);r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.PhaseSummary.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);r.Context.ComputeShader.SetShaderResource(0,null);
            for(int j=0;j<3;j++)r.Context.ComputeShader.SetUnorderedAccessView(j,null);r.Context.ComputeShader.Set(null);
        }
        foreach(uint body in new[]{ice,frozen}){
            uint liquid=body==ice?water:oil;var g=new GridCell[n];
            for(int x=110;x<=210;x++)g[230*w+x]=Cell(fixture);
            for(int y=120;y<132;y++)for(int x=154;x<166;x++)g[y*w+x]=Cell(body,physical[body].TransitionAboveTemperature,
                physical[body].TransitionAboveLatentHeat);
            Start(g);Phase();var melted=Read();Balance(g,melted,"paid melt "+body,true);
            Check(melted.Count(c=>c.IsActive!=0&&c.MaterialIndex==liquid)==144,"full paid conversion "+body);
            Check(melted.Where(c=>c.IsActive!=0&&c.MaterialIndex==liquid).All(c=>c.BodyId==0),"melt clears solid component");
            coordinator.RestoreWorldActivity(r,true,true,false);Advance(60,1);var flow=Read();Balance(melted,flow,"melt flows "+body,true);
            Check(Center(flow,liquid)>Center(melted,liquid)+15,"melted piece is fluid "+body);
            for(int i=0;i<n;i++)if(flow[i].IsActive!=0&&flow[i].MaterialIndex==liquid){
                float t=physical[liquid].TransitionBelowTemperature;
                float h=physical[body].HeatCapacity*t+PhaseEnthalpy.SolidReference(physical[body],physical[liquid])-.1f;
                PhaseEnthalpy.SetSpecificEnergy(ref flow[i],h,physical);
            }
            Start(flow);Phase();var refrozen=Read();Balance(flow,refrozen,"paid freeze "+body,true);
            Check(refrozen.Where(c=>c.IsActive!=0&&c.MaterialIndex!=fixture).All(c=>c.MaterialIndex==body&&c.BodyId==0&&c.RestFrames==0),"refreeze normalizes movable solid "+body);
            coordinator.RestoreWorldActivity(r,true,true,false);Advance(60,1);var rebuilt=Read();Balance(refrozen,rebuilt,"refrozen rebuilt "+body);
            Check(rebuilt.Any(c=>c.IsActive!=0&&c.MaterialIndex==body&&c.BodyId!=0),"refrozen component rebuilt "+body);
        }
        // Finite hot bath, real conductivities and whole-frame phase/body
        // scheduling. Insulating walls are the only diagnostic coefficient.
        foreach(uint body in new[]{ice,frozen})foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation}){
            table=physical.ToArray();table[fixture].ThermalConductivity=0;
            var hot=Basin(body,water);for(int i=0;i<n;i++)if(hot[i].IsActive!=0&&hot[i].MaterialIndex==water)hot[i].Temperature=80;
            settings.Mode=mode;Start(hot);Advance(60,20);var melted=Read();Balance(hot,melted,"finite bath "+body+"/"+mode,true);
            double remaining=melted.Where(c=>c.IsActive!=0&&c.MaterialIndex==body).Sum(c=>(double)c.Mass);
            Check(remaining<hot.Where(c=>c.IsActive!=0&&c.MaterialIndex==body).Sum(c=>(double)c.Mass)*.75,"hot bath melts body "+body+"/"+mode);
            Check(melted.Where(c=>c.IsActive!=0&&(c.MaterialIndex==body||c.MaterialIndex==water||c.MaterialIndex==oil)).All(c=>c.Temperature<=80.001),"finite bath no extra temperature");
            metrics.Add(new{scenario="heated-bath",body,mode=mode.ToString(),remaining});
            File.WriteAllBytes(Path.Combine(dir,$"heated-{body}-{mode}.grid"),MemoryMarshal.AsBytes(melted.AsSpan()).ToArray());
        }
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{passed=failures==0,checks,failures,energyResidual,width=w,height=r.Height,metrics},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_FROZEN_BODY_RESULT passed={failures==0} checks={checks} failures={failures} baseline={baseline}");
        if(failures!=0&&!baseline)throw new InvalidOperationException("Frozen body acceptance failed.");
    }
}
