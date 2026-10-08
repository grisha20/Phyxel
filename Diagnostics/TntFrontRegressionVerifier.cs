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

namespace Phyxel.Diagnostics;

internal static class TntFrontRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/tnt-front";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_TNT_FRONT_BASELINE")=="1";
        var settings=new SimulationSettings{Width=320,Height=180,Paused=true};
        uint tnt=registry.GetRequiredRuntimeIndex("core:tnt");
        var results=new List<object>();
        if(!baseline)
        {
            string loader=Path.Combine(dir,"loader");Directory.CreateDirectory(loader);
            var source=JsonNode.Parse(File.ReadAllText(registry["core:tnt"].SourcePath!))!;
            foreach(string invalid in new[]{"no-progressive","no-pressure","no-radial","too-fast"})
            {
                var bad=source.DeepClone();
                if(invalid=="no-progressive")bad["flags"]=new JsonArray("self-oxidizing","radial-ignition");
                if(invalid=="no-pressure")bad["combustion"]!["pressurePerMass"]=0;
                if(invalid=="no-radial")bad["flags"]=new JsonArray("self-oxidizing","progressive-ignition");
                if(invalid=="too-fast")bad["combustion"]!["spreadRate"]=601;
                string path=Path.Combine(loader,"invalid.json");File.WriteAllText(path,bad.ToJsonString());
                bool rejected=false;try{MaterialFileLoader.LoadCore(loader,100);}catch(InvalidDataException){rejected=true;}
                File.Delete(path);if(!rejected)throw new InvalidOperationException($"RF loader accepted {invalid}");
            }
            Console.WriteLine("PHYXEL_TNT_FRONT loader4 PASS");
            var s=new SimulationSettings{Width=320,Height=180,Paused=true};
            var r=coordinator.DispatchFrame(s,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=tnt}],0);
            foreach(bool wall in new[]{false,true})
            {
                var cells=new GridCell[r.Width*r.Height];
                uint barrier=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
                for(int y=80;y<=100;y++)for(int x=150;x<=170;x++)
                    cells[y*r.Width+x]=new(){IsActive=1,MaterialIndex=x==160?(wall?barrier:0):tnt,Mass=1,Temperature=30};
                cells[90*r.Width+158].Temperature=650;cells[90*r.Width+158].Lifetime=1f/60;
                r.Context.UpdateSubresource(cells,r.Grid.ReadBuffer);
                r.Context.ClearUnorderedAccessView(r.CombustionSummary.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4(1<<9,0,0,0));
                var before=AirInventoryRegressionVerifier.Read(r,r.ReactionPending.Buffer);
                SimulationDispatchCoordinator.DispatchPowderFront(r,new(){Width=(uint)r.Width,Height=(uint)r.Height});
                var after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
                if(after.Where((c,i)=>i%r.Width>160).Any(c=>c.Lifetime>0))throw new InvalidOperationException("RF02 front crossed a gap/wall.");
                if(after.Select((c,i)=>(c,i)).Any(v=>v.c.Mass!=cells[v.i].Mass||v.c.Temperature!=cells[v.i].Temperature)||
                    !before.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.ReactionPending.Buffer)))
                    throw new InvalidOperationException("RF02 ignition manufactured mass/heat/pressure.");
                if(!after.Where((c,i)=>i%r.Width<158).Any(c=>c.Lifetime>0))throw new InvalidOperationException("RF02 front did not reach connected fuel.");
            }
            Console.WriteLine("PHYXEL_TNT_FRONT RF02 gap/wall/no-free-heat-stock PASS");
        }
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        foreach(int fps in new[]{30,60,100})
        {
            settings.Mode=mode; settings.Paused=true; settings.PressureDestruction=false;
            coordinator.ClearCurrentWorld(settings);
            var r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=tnt}],0);
            int w=r.Width, cx=160,cy=90,radius=64;
            var grid=new GridCell[w*r.Height];
            for(int y=cy-radius;y<=cy+radius;y++)for(int x=cx-radius;x<=cx+radius;x++)
                if((x-cx)*(x-cx)+(y-cy)*(y-cy)<=radius*radius)
                    grid[y*w+x]=new(){IsActive=1,MaterialIndex=tnt,Mass=1,Temperature=30,RestFrames=2};
            double initial=grid.Sum(c=>(double)c.Mass);
            grid[cy*w+cx].Temperature=650;
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true); settings.Paused=false;
            double spent=-1,anisotropy=0; int shapeSamples=0;
            using var timer=new GpuStageTimer(r.Device);
            for(int frame=1;frame<=2*fps;frame++)
            {
                timer.Begin(r.Context);r=coordinator.DispatchFrame(settings,[],1f/fps);timer.End(r.Context);
                if(!baseline && fps==60 && frame==3)
                {
                    settings.Paused=true;
                    var snapshot=PressureShellRegressionVerifier.Read(r);
                    if(!MemoryMarshal.Cast<byte,GridCell>(snapshot.Grid).ToArray().Any(c=>c.MaterialIndex==tnt&&c.Lifetime>0))
                        throw new InvalidOperationException("RF03 fixture has no active front.");
                    coordinator.DispatchFrame(settings,[],1f/60);
                    if(!snapshot.Grid.AsSpan().SequenceEqual(PressureShellRegressionVerifier.Read(r).Grid))
                        throw new InvalidOperationException("RF03 paused front advanced.");
                    var serializer=new SimulationStateSerializer();string path=Path.Combine(dir,$"front-{mode}.json");
                    System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)tnt,snapshot,registry)).GetAwaiter().GetResult();
                    var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
                    if(!snapshot.Grid.AsSpan().SequenceEqual(loaded.World!.Grid))throw new InvalidOperationException("RF03 saved arrival ages changed.");
                    serializer.ApplyWorldSnapshot(r,loaded.World);coordinator.RestoreWorldActivity(r,true,true,false,true);
                    settings.Paused=false;Console.WriteLine($"PHYXEL_TNT_FRONT RF03 {mode} active-front pause/save/load PASS");
                }
                if(frame%Math.Max(1,fps/30)==0)
                {
                    var cells=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
                    double mass=cells.Where(c=>c.MaterialIndex==tnt&&c.IsActive!=0).Sum(c=>(double)c.Mass);
                    if(mass<=initial*.05&&spent<0)spent=frame/(double)fps;
                    // Measure arrival, not flame bloom or buoyant products. Sixteen radial probes.
                    var radii=new List<double>();
                    for(int ray=0;ray<16;ray++)
                    {
                        double angle=ray*Math.PI/8; int reached=0;
                        for(int d=1;d<radius;d++)
                        {
                            int x=cx+(int)Math.Round(Math.Cos(angle)*d), y=cy+(int)Math.Round(Math.Sin(angle)*d);
                            var c=cells[y*w+x];
                            if(c.MaterialIndex!=tnt||c.Mass<.999f||c.Lifetime>0) reached=d;
                        }
                        radii.Add(reached);
                    }
                    if(radii.Min()>=16&&radii.Max()<=52)
                    {anisotropy=Math.Max(anisotropy,radii.Max()/radii.Min());shapeSamples++;}
                    if(frame==Math.Max(1,fps/10)||frame==fps/2)
                        SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"disk-{mode}-{fps}-{frame}.png"));
                }
                if(frame%4==0)yield return r;
            }
            var row=new{mode=mode.ToString(),fps,spent,anisotropy,shapeSamples,gpu=timer.Statistics};results.Add(row);
            Console.WriteLine($"PHYXEL_TNT_FRONT {JsonSerializer.Serialize(row)}");
            if(!baseline&&(spent<=0||spent>.35||shapeSamples<1||anisotropy>1.12))
                throw new InvalidOperationException("Radial TNT front fails speed/isotropy contract.");
        }
        File.WriteAllText(Path.Combine(dir,"results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_TNT_FRONT_COMPLETE");
    }
}
