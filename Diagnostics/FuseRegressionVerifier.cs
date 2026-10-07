using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

// Isolated reaction tests establish the cord front; complete coordinator runs
// test heat, emissions, transport, fixed time steps and the powder endpoint.
internal static class FuseRegressionVerifier
{
    static void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
    static GridCell[] Read(GpuSimulationResources r) => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
    public static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/fuse";
        Directory.CreateDirectory(dir);
        uint fuse=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fuse), powder=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var settings=new SimulationSettings { Width=320,Height=180,Paused=true,Mode=SimulationMode.Simulation };
        var r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=fuse}],0);
        int w=r.Width,n=w*r.Height;
        var results=new List<object>();
        var times=new Dictionary<SimulationMode,List<double>>();
        string validation=Path.Combine(dir,"loader");Directory.CreateDirectory(validation);
        var source=JsonNode.Parse(File.ReadAllText(registry[CoreMaterialIds.Fuse].SourcePath!))!;
        foreach(string invalid in new[]{"movable","granular","no-oxidizer","no-combustion","zero-spread","lifecycle"})
        {
            var bad=source.DeepClone();
            if(invalid=="movable")bad["flags"]!.AsArray().Add("movable-solid");
            if(invalid=="granular")bad["kind"]="granular";
            if(invalid=="no-oxidizer")bad["flags"]=new JsonArray("progressive-ignition");
            if(invalid=="no-combustion")bad["combustion"]=null;
            if(invalid=="zero-spread")bad["combustion"]!["spreadRate"]=0;
            if(invalid=="lifecycle")bad["lifecycle"]=JsonNode.Parse("{\"minimum\":1,\"maximum\":2,\"decayInto\":\"core:empty\"}");
            File.WriteAllText(Path.Combine(validation,"invalid.json"),bad.ToJsonString());
            bool rejected=false;try{MaterialFileLoader.LoadCore(validation,100);}catch(InvalidDataException){rejected=true;}
            Check(rejected,$"FI07 invalid {invalid} accepted");
        }
        File.Delete(Path.Combine(validation,"invalid.json"));
        Console.WriteLine("PHYXEL_FUSE FI07 loader6=PASS");

        foreach(string channel in new[]{"smokeRate","gasRate","flameRate"})
        foreach(float invalid in new[]{-1f,101f,float.PositiveInfinity})
        {
            var bad=source.DeepClone();
            bad["emissions"]![channel]=float.IsFinite(invalid)?JsonValue.Create(invalid):JsonValue.Create("Infinity");
            string path=Path.Combine(validation,"invalid.json");File.WriteAllText(path,bad.ToJsonString());
            bool rejected=false;try{MaterialFileLoader.LoadCore(validation,100);}catch(InvalidDataException){rejected=true;}
            Check(rejected,$"FU invalid emission {channel}/{invalid} accepted");File.Delete(path);
        }
        string valid=Path.Combine(validation,"zero.json");File.WriteAllText(valid,source.ToJsonString());
        Check(MaterialFileLoader.LoadCore(validation,100).Count==1,"FU disabled smoke/gas rejected");File.Delete(valid);
        Console.WriteLine("PHYXEL_FUSE FU loader-zero/rate-bounds9=PASS");

        GridCell Cell(uint id,float t=30) => new(){IsActive=1,MaterialIndex=id,Mass=1,Temperature=t,RestFrames=2};
        void Reaction(float dt,int tick,bool finite)
        {
            var c=r.Context;
            c.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            c.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
            c.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
            var constants=new CombustionConstants { Width=(uint)w,Height=(uint)r.Height,
                MaterialCount=(uint)registry.Materials.Count,DeltaTime=dt,TickIndex=(uint)tick,FiniteOxidizer=finite?1u:0u };
            c.UpdateSubresource(ref constants,r.CombustionConstants);
            c.ComputeShader.Set(r.CombustionShader);
            c.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            c.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
            c.Dispatch((w+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<3;i++)c.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<6;i++)c.ComputeShader.SetUnorderedAccessView(i,null);
        }
        void Upload(GridCell[] grid) { r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer); r.Context.ClearUnorderedAccessView(r.OxidizerAvailable.UnorderedView,new RawInt4()); }
        var cold=new GridCell[n];for(int x=50;x<82;x++)cold[70*w+x]=Cell(fuse);
        Upload(cold);for(int tick=1;tick<=600;tick++)Reaction(1f/60,tick,true);
        Check(MemoryMarshal.AsBytes(cold.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read(r).AsSpan())),"FI01 cold cord changed");
        Console.WriteLine("PHYXEL_FUSE FI01 cold=PASS");
        foreach(var direction in new[]{(1,0),(-1,0),(0,1),(0,-1),(1,1)})
        {
            var grid=new GridCell[n];int[] path=Enumerable.Range(0,32).Select(k=>(80+k*direction.Item2)*w+100+k*direction.Item1).ToArray();
            foreach(int i in path)grid[i]=Cell(fuse);grid[path[0]].Temperature=650;Upload(grid);
            double terminal=-1;int frontAtHalfSecond=0;
            for(int tick=1;tick<=900;tick++)
            {
                Reaction(1f/60,tick,true);
                if(tick%6!=0)continue;
                var after=Read(r);
                int front=Array.FindLastIndex(path,i=>after[i].MaterialIndex!=fuse||after[i].Lifetime>0);
                if(tick==30)frontAtHalfSecond=front+1;
                if(terminal<0 && (after[path[^1]].MaterialIndex!=fuse||after[path[^1]].Lifetime>0))terminal=tick/60.0;
                if(terminal>0&&tick>=30)break;
            }
            Check(terminal>=1&&terminal<=3&&frontAtHalfSecond>1&&frontAtHalfSecond<16,$"FI02 direction{direction} terminal={terminal} frontHalfSecond={frontAtHalfSecond}");
            results.Add(new{test="isolated",direction,terminal,frontAtHalfSecond});
            Console.WriteLine($"PHYXEL_FUSE FI02 direction={direction} terminal={terminal:F2}s frontHalfSecond={frontAtHalfSecond} PASS");
        }
        foreach(bool finite in new[]{false,true})
        {
            var grid=new GridCell[n];int a=80*w+100;grid[a]=Cell(fuse,650);
            foreach(int i in new[]{a-1,a+1,a-w,a+w})grid[i]=Cell(metal);
            Upload(grid);Reaction(1f/60,1,finite);var after=Read(r)[a];
            Check(Math.Abs(after.Mass-(1-2.0/60))<1e-6&&after.Lifetime>0,"FI03 sealed no-O2 cord did not burn");
            grid[a]=after;grid[a].Lifetime=.5f;grid[a].Temperature=50;Upload(grid);Reaction(1f/60,2,finite);
            Check(Read(r)[a].Lifetime==0&&Read(r)[a].Mass==grid[a].Mass,"FI03 chilled cord kept burning");
        }

        var contact=new GridCell[n];int ci=80*w+100;
        contact[ci]=Cell(fuse,100);contact[ci-1]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),650);contact[ci-1].Lifetime=2;
        Upload(contact);bool lit=false;
        for(int tick=1;tick<=120;tick++)
        {
            Reaction(1f/60,tick,true);var c=Read(r)[ci];
            if(c.Lifetime<=0)continue;
            Check(c.Temperature<200&&c.Mass<1,"FI03 contact ignition injected threshold heat");lit=true;break;
        }
        Check(lit,"FI03 adjacent flame failed to ignite");
        var cpu=Cell(fuse,650);var table=registry.CreateGpuTable();
        Check(CombustionRuntime.TryApply(ref cpu,table,1f/60,out _,out var burn)&&Math.Abs(burn-2.0/60)<1e-7&&cpu.Lifetime>0,"FI03 CPU burn timer failed");
        cpu.Lifetime=.5f;cpu.Temperature=50;
        Check(!CombustionRuntime.TryApply(ref cpu,table,1f/60,out _,out _)&&cpu.Lifetime==0,"FI03 CPU quench failed");
        Console.WriteLine("PHYXEL_FUSE FI03 flame-contact/CPU=PASS");
        var gap=new GridCell[n];for(int x=60;x<=68;x++)gap[60*w+x]=Cell(fuse);gap[60*w+70]=Cell(fuse);gap[60*w+60].Temperature=650;
        Upload(gap);for(int tick=1;tick<=600;tick++)Reaction(1f/60,tick,true);
        Check(Read(r)[60*w+70].Mass==1&&Read(r)[60*w+70].Lifetime==0,"FI02 internal front crossed gap");
        Console.WriteLine("PHYXEL_FUSE FI02 gap=PASS FI03 sealed/quenched=PASS");
        // Complete pipeline: 20-cell horizontal cord plus twelve-cell turn down,
        // ending at a small supported powder pocket. No forced velocities or heat.
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        foreach(int fps in new[]{30,60,100})
        {
            settings.Mode=mode;settings.Paused=true;
            coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=fuse}],0);
            var grid=new GridCell[n];var path=new List<int>();
            for(int x=60;x<80;x++)path.Add(70*w+x);
            for(int y=71;y<=82;y++)path.Add(y*w+79);
            foreach(int i in path)grid[i]=Cell(fuse);
            grid[path[0]].Temperature=650;
            for(int y=80;y<=83;y++)for(int x=80;x<=82;x++)grid[y*w+x]=Cell(powder);
            for(int x=79;x<=83;x++)grid[84*w+x]=Cell(metal);
            for(int y=79;y<=84;y++)grid[y*w+83]=Cell(metal);
            Upload(grid);coordinator.RestoreWorldActivity(r,true,true,false,true);settings.Paused=false;
            double powderTime=-1;bool saved=false,continuation=false;
            var serializer=new SimulationStateSerializer();
            for(int frame=1;frame<=20*fps;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/fps);
                if(frame%(fps/10)==0)
                {
                    var after=Read(r);
                    double mass=after.Where(c=>c.IsActive!=0&&c.MaterialIndex==powder).Sum(c=>(double)c.Mass);
                    if(mass<11.9&&powderTime<0)powderTime=frame/(double)fps;
                    if(frame==fps*4/5)
                    {
                        double fuseMass=after.Where(c=>c.MaterialIndex==fuse&&c.IsActive!=0).Sum(c=>(double)c.Mass);
                        Check(fuseMass>10&&fuseMass<32,"FI04 full cord instantaneous or stalled");
                        SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{mode}-{fps}-effects.png"));
                        settings.RenderWithoutEffects=true;coordinator.DispatchFrame(settings,[],0);
                        SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{mode}-{fps}-plain.png"));
                        settings.RenderWithoutEffects=false;
                    }
                }
                if(frame==fps*4/5)
                {
                    settings.Paused=true;var before=Read(r);for(int i=0;i<5;i++)coordinator.DispatchFrame(settings,[],1f/fps);
                    Check(MemoryMarshal.AsBytes(before.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read(r).AsSpan())),"FI05 paused grid changed");
                    serializer.BeginWorldCapture(r);SimulationWorldSnapshot? snapshot;
                    while(!serializer.TryCompleteWorldCapture(r,out snapshot))System.Threading.Thread.Yield();
                    string file=Path.Combine(dir,$"{mode}-{fps}-active.json");
                    System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(file,settings,(ushort)fuse,snapshot!,registry)).GetAwaiter().GetResult();
                    var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(file,registry)).GetAwaiter().GetResult()!;
                    var copy=loaded.World!;
                    bool Same(byte[]? a,byte[]? b)=>(a??[]).AsSpan().SequenceEqual(b??[]);
                    Check(Same(snapshot!.Grid,copy.Grid)&&Same(snapshot.Air,copy.Air)&&Same(snapshot.GasMotion,copy.GasMotion)&&
                        Same(snapshot.Oxidizer,copy.Oxidizer)&&Same(snapshot.AirThermal,copy.AirThermal)&&Same(snapshot.ReactionPending,copy.ReactionPending)&&
                        Same(snapshot.ReactionPulse,copy.ReactionPulse)&&Same(snapshot.Filters,copy.Filters),"FI05 save changed snapshot");
                    Check(before.Any(c=>c.MaterialIndex==fuse&&c.Lifetime>0),"FI05 no active cord at save");
                    serializer.ApplyWorldSnapshot(r,copy);coordinator.RestoreWorldActivity(r,true,true,false,true);
                    settings.Paused=false;saved=true;
                }
                if(frame>fps*4/5&&powderTime>0)continuation=true;
                if(frame%4==0)yield return r;
                if(powderTime>0&&frame>=4*fps)break;
            }
            Check(powderTime>1&&powderTime<=5&&saved&&continuation,$"FI04/05 {mode}/{fps} powder={powderTime}");
            if(!times.ContainsKey(mode))times[mode]=[];times[mode].Add(powderTime);
            results.Add(new{test="full",mode=mode.ToString(),fps,powderTime,saved,continuation});
            Console.WriteLine($"PHYXEL_FUSE FI04/05 mode={mode} fps={fps} powder={powderTime:F2}s save/resume=PASS");
        }

        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        {
            settings.Mode=mode;settings.Paused=true;coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=fuse}],0);
            var grid=new GridCell[n];for(int x=100;x<112;x++)grid[80*w+x]=Cell(fuse);
            Upload(grid);coordinator.RestoreWorldActivity(r,true,true,false,true);
            r=coordinator.DispatchFrame(settings,[new(){X=98,Y=80,Radius=2,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire)}],0);
            Check(Read(r)[80*w+100].MaterialIndex==fuse&&Read(r)[80*w+100].Mass==1,"FI08 flame brush overwrote cord");
            settings.Paused=false;double ignition=-1;
            for(int frame=1;frame<=300;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/60);
                if(frame%6==0&&Read(r)[80*w+100].MaterialIndex==fuse&&Read(r)[80*w+100].Mass<.999f){ignition=frame/60.0;break;}
                if(frame%4==0)yield return r;
            }
            Check(ignition>0&&ignition<=5,$"FI08 cold cord brush flame did not ignite: {mode}");
            results.Add(new{test="brush",mode=mode.ToString(),ignition});
            Console.WriteLine($"PHYXEL_FUSE FI08 mode={mode} brush-ignition={ignition:F2}s PASS");
        }
        foreach(var row in times)Check(row.Value.Max()-row.Value.Min()<=.3,$"FI04 frame-rate dependence {row.Key}");
        File.WriteAllText(Path.Combine(dir,"results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_FUSE_COMPLETED");
    }
}
