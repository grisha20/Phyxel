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
using Phyxel.Serialization;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

internal static class ExplosiveMaterialRegressionVerifier
{
    static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    static GridCell[] Read(GpuSimulationResources r)=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
    public static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/explosive-material";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_EXPLOSIVE_BASELINE")=="1";
        uint fuse=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fuse),smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        var settings=new SimulationSettings{Width=320,Height=180,Paused=true};
        GpuSimulationResources r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=fuse}],0);
        int w=r.Width,n=w*r.Height;
        GridCell Cell(uint id,float t=30)=>new(){MaterialIndex=id,IsActive=1,Mass=1,Temperature=t,RestFrames=2};
        var rows=new List<object>();
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        {
            settings.Mode=mode;settings.Paused=true;coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=fuse}],0);
            var grid=new GridCell[n];for(int x=60;x<92;x++)grid[80*w+x]=Cell(fuse);
            grid[80*w+60].Temperature=650;r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);settings.Paused=false;
            double peak=0,integral=0,terminal=-1;
            for(int frame=1;frame<=900;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/60);
                if(frame%6==0)
                {
                    var cells=Read(r);double mass=cells.Where(c=>c.IsActive!=0&&c.MaterialIndex==smoke).Sum(c=>(double)c.Mass);
                    peak=Math.Max(peak,mass);integral+=mass*.1;
                    if(terminal<0&&(cells[80*w+91].MaterialIndex!=fuse||cells[80*w+91].Lifetime>0))terminal=frame/60.0;
                    if(frame==120||frame==240||frame==720)SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"fuse-{mode}-{frame/60}.png"));
                }
                if(frame%4==0)yield return r;
            }
            rows.Add(new{test="smoke",mode=mode.ToString(),peak,integral,terminal});
            Console.WriteLine($"PHYXEL_FUSE_SMOKE mode={mode} peak={peak:F6} integral={integral:F6} terminal={terminal:F2}s baseline={baseline}");
            if(!baseline)
            {
                string reference=Environment.GetEnvironmentVariable("PHYXEL_FUSE_SMOKE_BASELINE")??throw new InvalidDataException("Missing smoke baseline results");
                using var doc=JsonDocument.Parse(File.ReadAllText(reference));
                double previous=doc.RootElement.EnumerateArray().Single(row=>row.GetProperty("mode").GetString()==mode.ToString()).GetProperty("integral").GetDouble();
                Check(peak<=.1&&integral<=previous*.05,$"FU02 excessive smoke {mode} peak={peak} integral={integral}/{previous}");
            }
        }
        File.WriteAllText(Path.Combine(dir,"smoke.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        if(baseline){Console.WriteLine("PHYXEL_EXPLOSIVE_BASELINE_COMPLETE");yield break;}
        // TNT acceptance follows only once the source and contract have been registered.
        uint tnt=registry.GetRequiredRuntimeIndex("core:tnt"),metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);

        // Reaction ledger is measured before gas admission, so it is not inferred
        // from a pressure peak or the visual brightness of the explosion.
        var props=registry.CreateGpuTable();
        void Reaction(float dt,int tick,bool finite)
        {
            var c=r.Context;
            c.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            c.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
            c.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
            var constants=new CombustionConstants{Width=(uint)w,Height=(uint)r.Height,DeltaTime=dt,TickIndex=(uint)tick,
                // Reserved1 is the existing ABI slot for CombustionHasReactionSources.
                MaterialCount=(uint)registry.Materials.Count,FiniteOxidizer=finite?1u:0u,Reserved1=1};
            c.UpdateSubresource(ref constants,r.CombustionConstants);c.ComputeShader.Set(r.CombustionShader);
            c.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            c.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
            c.Dispatch((w+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<3;i++)c.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<6;i++)c.ComputeShader.SetUnorderedAccessView(i,null);
        }
        foreach(uint id in new[]{tnt,registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder)})foreach(bool finite in new[]{false,true})
        {
            var sample=new GridCell[n];int i=80*w+150;sample[i]=Cell(id,650);r.Context.UpdateSubresource(sample,r.Grid.ReadBuffer);
            r.Context.ClearUnorderedAccessView(r.OxidizerAvailable.UnorderedView,new RawInt4());
            r.Context.ClearUnorderedAccessView(r.ReactionPending.UnorderedView,new RawInt4());
            Reaction(1f/60,1,finite);
            float burned=1-Read(r)[i].Mass;
            var q=MemoryMarshal.Cast<byte,Vector4>(AirInventoryRegressionVerifier.Read(r,r.ReactionPending.Buffer))[i];
            Check(Math.Abs(burned-props[id].BurnRate/60)<1e-6&&Math.Abs(q.X-burned*props[id].ReactionPressurePerMass)<1e-5,
                "TN01/02 consumed mass disagrees with finite pressure source");
            for(int tick=2;tick<=20;tick++)Reaction(1f/60,tick,finite);
            var end=MemoryMarshal.Cast<byte,Vector4>(AirInventoryRegressionVerifier.Read(r,r.ReactionPending.Buffer))[i];
            Check(Math.Abs(end.X-props[id].ReactionPressurePerMass)<1e-5&&float.IsFinite(end.Y)&&end.Y>0&&end.Z>0,
                "TN02 pressure ledger grew after stock exhausted");
        }
        Check(Math.Abs(props[tnt].ReactionPressurePerMass/props[registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder)].ReactionPressurePerMass-6)<1e-6,"TN02 source ratio changed");
        Console.WriteLine("PHYXEL_TNT TN01/02 O2-zero/finite-ledger/ratio6=PASS");
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        {
            settings.Mode=mode;settings.Paused=true;coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=tnt}],0);
            var cold=new GridCell[n];for(int y=70;y<90;y++)for(int x=140;x<160;x++)cold[y*w+x]=Cell(tnt);
            r.Context.UpdateSubresource(cold,r.Grid.ReadBuffer);coordinator.RestoreWorldActivity(r,true,true,false,true);settings.Paused=false;
            for(int frame=1;frame<=600;frame++){r=coordinator.DispatchFrame(settings,[],1f/60);if(frame%4==0)yield return r;}
            var after=Read(r);
            Check(after.Select((c,i)=>(c,i)).Where(v=>cold[v.i].IsActive!=0).All(v=>v.c.MaterialIndex==tnt&&v.c.Mass==1&&v.c.Lifetime==0),"TN01 cold block reacted or moved");
            var stock=MemoryMarshal.Cast<byte,Vector4>(AirInventoryRegressionVerifier.Read(r,r.ReactionPending.Buffer)).ToArray();
            Check(stock.All(p=>p.X==0),"TN01 cold block created reaction pressure");
            Console.WriteLine($"PHYXEL_TNT TN01 cold10s mode={mode} PASS");
        }
        var timings = new Dictionary<SimulationMode,List<double>>();
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})foreach(int fps in new[]{30,60,100})
        {
            settings.Mode=mode;settings.Paused=true;settings.PressureDestruction=false;coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=tnt}],0);
            var grid=new GridCell[n];for(int y=70;y<90;y++)for(int x=140;x<160;x++)grid[y*w+x]=Cell(tnt);
            grid[80*w+150].Temperature=650;r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);settings.Paused=false;
            double spentAt=-1,peakWave=0;var quadrants=new bool[4];
            for(int frame=1;frame<=3*fps;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/fps);
                if(frame%Math.Max(1,fps/20)==0)
                {
                    var cells=Read(r);double mass=cells.Where(c=>c.IsActive!=0&&c.MaterialIndex==tnt).Sum(c=>(double)c.Mass);
                    if(mass<=20&&spentAt<0)spentAt=frame/(double)fps;
                    var wave=MemoryMarshal.Cast<byte,Vector4>(AirInventoryRegressionVerifier.Read(r,r.ReactionPulse.ReadBuffer)).ToArray();
                    peakWave=Math.Max(peakWave,wave.Max(p=>Math.Abs(p.X)));
                    for(int y=70;y<90;y++)for(int x=140;x<160;x++)
                        if(cells[y*w+x].MaterialIndex!=tnt||cells[y*w+x].Mass<.9)quadrants[(y>=80?2:0)+(x>=150?1:0)]=true;
                    if(frame==fps/2||frame==fps)SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"tnt-{mode}-{fps}-{frame}.png"));
                }
                if(frame%4==0)yield return r;
            }
            Check(spentAt>0&&spentAt<=2&&peakWave>.1&&quadrants.All(v=>v),$"TN03 {mode}/{fps} spent={spentAt} wave={peakWave}");
            if (!timings.TryGetValue(mode,out var measured)) timings[mode]=measured=[];
            measured.Add(spentAt);
            rows.Add(new{test="block",mode=mode.ToString(),fps,spentAt,peakWave});
            Console.WriteLine($"PHYXEL_TNT_BLOCK mode={mode} fps={fps} spent={spentAt:F2}s wave={peakWave:F3} PASS");
        }

        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})foreach(bool destroy in new[]{false,true})
        {
            settings.Mode=mode;settings.PressureDestruction=destroy;settings.Paused=true;coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=tnt}],0);
            var grid=new GridCell[n];
            for(int y=64;y<100;y++)for(int x=134;x<166;x++)
                if(x<138||x>=162||y<68||y>=96)grid[y*w+x]=Cell(metal);
            for(int y=76;y<88;y++)for(int x=144;x<156;x++)grid[y*w+x]=Cell(tnt);
            grid[82*w+150].Temperature=650;r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);settings.Paused=false;
            int peakFragments=0;bool outward=false;
            for(int frame=1;frame<=120;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/60);
                if(frame%3==0)
                {
                    var cells=Read(r);int fragments=cells.Count(c=>c.IsActive!=0&&c.MaterialIndex==metal&&(c.BodyId&0x40000000u)!=0);
                    peakFragments=Math.Max(peakFragments,fragments);
                    outward|=cells.Select((c,i)=>(c,i)).Any(v=>v.c.IsActive!=0&&v.c.MaterialIndex==metal&&(v.c.BodyId&0x40000000u)!=0&&
                        (v.i%w<134||v.i%w>=166||v.i/w<64||v.i/w>=100));
                }
                if(frame==6)
                {
                    settings.Paused=true;var snapshot=PressureShellRegressionVerifier.Read(r);
                    for(int k=0;k<5;k++)coordinator.DispatchFrame(settings,[],1f/60);
                    Check(snapshot.Grid.AsSpan().SequenceEqual(PressureShellRegressionVerifier.Read(r).Grid),"TN04 paused world changed");
                    string path=Path.Combine(dir,$"tnt-{mode}-{destroy}-active.json");var serializer=new SimulationStateSerializer();
                    System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)tnt,snapshot,registry)).GetAwaiter().GetResult();
                    var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;var copy=loaded.World!;
                    bool Same(byte[]? a,byte[]? b)=>(a??[]).AsSpan().SequenceEqual(b??[]);
                    Check(Same(snapshot.Grid,copy.Grid)&&Same(snapshot.Air,copy.Air)&&Same(snapshot.GasMotion,copy.GasMotion)&&
                        Same(snapshot.Oxidizer,copy.Oxidizer)&&Same(snapshot.AirThermal,copy.AirThermal)&&Same(snapshot.ReactionPending,copy.ReactionPending)&&
                        Same(snapshot.ReactionPulse,copy.ReactionPulse)&&Same(snapshot.Filters,copy.Filters),"TN04 save changed pressure or timer");
                    Check(loaded.State.PressureDestruction==destroy,"TN04 toggle not saved");
                    serializer.ApplyWorldSnapshot(r,copy);coordinator.RestoreWorldActivity(r,true,true,false,true);settings.Paused=false;
                }
                if(frame==30||frame==60)SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"shell-{mode}-{destroy}-{frame}.png"));
                if(frame%4==0)yield return r;
            }
            Check(destroy?peakFragments>0&&outward:peakFragments==0,$"TN04 {mode} destroy={destroy} fragments={peakFragments} outward={outward}");
            Console.WriteLine($"PHYXEL_TNT TN04 mode={mode} destroy={destroy} peakFragments={peakFragments} outward={outward} save=PASS");
        }
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        {
            settings.Mode=mode;settings.PressureDestruction=false;settings.Paused=true;coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=tnt}],0);
            var grid=new GridCell[n];for(int x=60;x<80;x++)grid[70*w+x]=Cell(fuse);
            for(int y=71;y<=82;y++)grid[y*w+79]=Cell(fuse);
            for(int y=80;y<=83;y++)for(int x=80;x<=84;x++)grid[y*w+x]=Cell(tnt);
            grid[70*w+60].Temperature=650;r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);settings.Paused=false;double ignition=-1;
            for(int frame=1;frame<=300;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/60);
                if(frame%6==0&&Read(r).Where(c=>c.IsActive!=0&&c.MaterialIndex==tnt).Sum(c=>(double)c.Mass)<19.9){ignition=frame/60.0;break;}
                if(frame%4==0)yield return r;
            }
            Check(ignition>1&&ignition<=5,$"TN05 fuse did not ignite TNT in {mode}: {ignition}");
            Console.WriteLine($"PHYXEL_TNT TN05 mode={mode} fuse-ignition={ignition:F2}s PASS");
        }
        foreach(var measured in timings.Values)
            Check(measured.Max()-measured.Min()<=.3,"TN03 frame-rate timing spread exceeded 0.3s");
        File.WriteAllText(Path.Combine(dir,"results.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_EXPLOSIVE_MATERIAL_COMPLETE");
    }
}
