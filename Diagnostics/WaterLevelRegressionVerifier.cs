using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class WaterLevelRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/water-level";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_WATER_LEVEL_BASELINE")=="1";
        bool full=Environment.GetEnvironmentVariable("PHYXEL_WATER_LEVEL_FULL")=="1";
        bool feedOnly=Environment.GetEnvironmentVariable("PHYXEL_WATER_LEVEL_FEED_ONLY")=="1";
        var settings=new SimulationSettings{Paused=true,AirSimulation=false,OpenBoundaries=false,SolidGravity=false};
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),ice=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice),
            frozen=registry.GetRequiredRuntimeIndex(CoreMaterialIds.FrozenOil),
            oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),
            fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=water}],0);
        int w=r.Width,h=r.Height,n=w*h,checks=0,failures=0;
        var physical=registry.CreateGpuTable();var table=physical.ToArray();
        for(int i=0;i<table.Length;i++)table[i].ThermalConductivity=0;
        var serializer=new SimulationStateSerializer();var metrics=new List<object>();
        GridCell Cell(uint id,float t=20)=>new(){IsActive=1,MaterialIndex=id,Temperature=t,
            Mass=physical[id].SimulationKind==(uint)MaterialSimulationKind.Solid?physical[id].Density:1,
            RestFrames=id==fixture?2u:0u};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Check(bool ok,string label){checks++;if(!ok){failures++;Console.WriteLine("PHYXEL_WL_FAIL "+label);}}
        double Energy(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        void Start(GridCell[] g,bool thermal=false){settings.Paused=true;
            serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,settings.HydraulicPressure);
            r.Materials.Upload(r.Context,thermal?physical:table);settings.Paused=false;}
        void Advance(int fps,int frames){for(int f=0;f<frames;f++){
            coordinator.DispatchFrame(settings,[],1f/fps);
            coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(
                AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);}}
        double Center(GridCell[] g,uint id)=>Enumerable.Range(0,n).Where(i=>g[i].IsActive!=0&&g[i].MaterialIndex==id).Average(i=>(double)(i/w));
        foreach(var mode in feedOnly?Array.Empty<SimulationMode>():full?new[]{SimulationMode.Sandbox,SimulationMode.Simulation}:new[]{SimulationMode.Sandbox})
        foreach(bool hydraulic in new[]{false,true})foreach(bool body in new[]{false,true})
        foreach(int fps in full?new[]{30,60,100}:new[]{60}){
            var g=new GridCell[n];
            // Curved bowl: broad, sloped fill; both flanks are visible surfaces.
            for(int x=80;x<=380;x++){
                int bottom=235-(int)Math.Round(.006*(x-230)*(x-230));
                for(int y=bottom;y<=bottom+3;y++)g[y*w+x]=Cell(fixture);
                int top=body?145:145+(x-230)/12;
                for(int y=top;y<bottom;y++)g[y*w+x]=Cell(water);
            }
            for(int y=h-4;y<h;y++)for(int x=0;x<w;x++)g[y*w+x]=Cell(water);
            if(body)for(int y=120;y<=160;y++)for(int x=210;x<=250;x++)
                if((x-230)*(x-230)+(y-140)*(y-140)<=400)g[y*w+x]=Cell(ice,-5);
            settings.Mode=mode;settings.HydraulicPressure=hydraulic;
            serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,hydraulic);r.Materials.Upload(r.Context,table);
            if(Environment.GetEnvironmentVariable("PHYXEL_WATER_LEVEL_PROBE")=="1"){
                var c=new SimulationFrameConstants{Width=(uint)w,Height=(uint)h,DispatchExtentX=(uint)w,
                    DispatchExtentY=(uint)h,DeltaTime=1f/60,FrameIndex=2};
                foreach(uint phase in new uint[]{32,33,56,57}){
                    Console.WriteLine("PHYXEL_WL_PROBE begin="+phase);c.SimulationPhase=phase;
                    r.Context.UpdateSubresource(ref c,r.FrameConstants);r.Context.ComputeShader.Set(r.CellularAutomataShader);
                    r.Context.ComputeShader.SetConstantBuffer(0,r.FrameConstants);
                    r.Context.ComputeShader.SetShaderResources(0,r.Materials.View,r.Air.View);
                    r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.BodyFlags.UnorderedView,
                        r.PathBlockerMasks.UnorderedView,r.CellMaterials.UnorderedView,r.WaterPressureRoutes.UnorderedView,
                        r.WaterPressureRouteScratch.UnorderedView,r.GasMotion.UnorderedView);
                    r.Context.Dispatch((w+15)/16,phase==32?(h+15)/16:1,1);
                    for(int j=0;j<8;j++)r.Context.ComputeShader.SetUnorderedAccessView(j,null);
                    r.Context.ComputeShader.SetShaderResource(0,null);r.Context.ComputeShader.SetShaderResource(1,null);
                    Read();Console.WriteLine("PHYXEL_WL_PROBE end="+phase);
                }
                return;
            }
            Console.WriteLine($"PHYXEL_WL_START {mode}/{hydraulic}/{body}/{fps}");
            settings.Paused=false;
            for(int f=0;f<fps*20;f++){
                coordinator.DispatchFrame(settings,[],1f/fps);
                coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(
                    AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
            }
            var after=Read();var tops=new List<int>();
            for(int x=125;x<=335;x++){
                if(Enumerable.Range(0,h).Any(y=>after[y*w+x].IsActive!=0&&after[y*w+x].MaterialIndex==ice))continue;
                // Only exposed water; covered ice columns are excluded.
                for(int y=60;y<230;y++)if(after[y*w+x].IsActive!=0){
                    if(after[y*w+x].MaterialIndex==water)tops.Add(y);break;
                }
            }
            int range=tops.Count==0?h:tops.Max()-tops.Min();
            string label=$"{mode}/{hydraulic}/{body}/{fps}";
            Check(range<=1,"horizontal "+label+" range="+range);
            foreach(uint id in new[]{water,ice,fixture})Check(Math.Abs(g.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass)-
                after.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass))<.001,"mass "+label+"/"+id);
            double error=Math.Abs(Energy(g)-Energy(after))/Math.Max(1,Math.Abs(Energy(g)));
            Check(error<.00005,"energy "+label);
            Check(Enumerable.Range(0,n).All(i=>g[i].IsActive==0||g[i].MaterialIndex!=fixture||
                (after[i].IsActive!=0&&after[i].MaterialIndex==fixture)),"walls "+label);
            if(body)Check(g.Count(c=>c.IsActive!=0&&c.MaterialIndex==ice)==after.Count(c=>c.IsActive!=0&&c.MaterialIndex==ice),"ice whole "+label);
            string name=$"{mode}-{hydraulic}-{body}-{fps}";
            File.WriteAllBytes(Path.Combine(dir,name+".grid"),MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
            using(var stream=File.Create(Path.Combine(dir,name+".png")))r.PresentationTexture.SaveAsPng(stream,w,h);
            metrics.Add(new{mode=mode.ToString(),hydraulic,body,fps,range,sleeping=coordinator.CellularSleeping,error});
            Console.WriteLine($"PHYXEL_WL_CASE {label} range={range} sleep={coordinator.CellularSleeping}");
        }
        // Distinct liquid feed: short-time locality, then ordinary spreading.
        settings.Mode=SimulationMode.Sandbox;settings.HydraulicPressure=false;
        var feed=new GridCell[n];
        for(int y=130;y<230;y++)for(int x=80;x<=380;x++)feed[y*w+x]=Cell(water);
        for(int x=80;x<=380;x++)feed[230*w+x]=Cell(fixture);
        for(int y=120;y<=230;y++){feed[y*w+80]=Cell(fixture);feed[y*w+380]=Cell(fixture);}
        for(int y=124;y<130;y++)for(int x=224;x<236;x++)feed[y*w+x]=Cell(oil,25);
        Console.WriteLine("PHYXEL_WL_STAGE oil start");Start(feed);Advance(60,1);var first=Read();
        Console.WriteLine("PHYXEL_WL_STAGE oil first");
        using(var stream=File.Create(Path.Combine(dir,"oil-feed-first.png")))r.PresentationTexture.SaveAsPng(stream,w,h);
        int[] OilX(GridCell[] g)=>Enumerable.Range(0,n).Where(i=>g[i].IsActive!=0&&g[i].MaterialIndex==oil).Select(i=>i%w).ToArray();
        int extent=OilX(first).Max(x=>Math.Abs(x-230));
        Check(extent<=24,"oil first-frame locality extent="+extent);
        Advance(60,11);var shortFeed=Read();int earlyExtent=OilX(shortFeed).Max(x=>Math.Abs(x-230));
        Console.WriteLine("PHYXEL_WL_STAGE oil short");
        Check(earlyExtent<=48,"oil short feed locality extent="+earlyExtent);
        Advance(60,60*4);
        var spread=Read();Check(OilX(spread).Max()-OilX(spread).Min()>12,"oil still spreads");
        Console.WriteLine("PHYXEL_WL_STAGE oil spread");
        foreach(uint id in new[]{water,oil})Check(Math.Abs(feed.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass)-
            spread.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass))<.001,"feed mass "+id);
        Check(Math.Abs(Energy(feed)-Energy(spread))/Math.Abs(Energy(feed))<.00005,"feed energy");
        using(var stream=File.Create(Path.Combine(dir,"oil-feed-spread.png")))r.PresentationTexture.SaveAsPng(stream,w,h);
        metrics.Add(new{scenario="oil feed",extent,earlyExtent});
        // Fixed cadence in air, with unchanged liquid displacement cadence.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(uint body in new[]{ice,frozen})foreach(int fps in new[]{30,60,100}){
            settings.Mode=mode;
            Console.WriteLine($"PHYXEL_WL_STAGE speed {mode}/{body}/{fps}");
            var fall=new GridCell[n];for(int y=50;y<60;y++)for(int x=224;x<236;x++)fall[y*w+x]=Cell(body,-5);
            Start(fall);Advance(fps,fps);var air=Read();double distance=Center(air,body)-Center(fall,body);
            Check(distance>=58&&distance<=62,"air speed "+fps+"/"+distance);
            Check(air.Count(c=>c.IsActive!=0&&c.MaterialIndex==body)==120,"air whole");
            Check(Math.Abs(Energy(air)-Energy(fall))<.001,"air energy");
            var bath=new GridCell[n];for(int y=90;y<235;y++)for(int x=80;x<=380;x++)bath[y*w+x]=Cell(water);
            for(int x=80;x<=380;x++)bath[235*w+x]=Cell(fixture);
            for(int y=70;y<235;y++){bath[y*w+80]=Cell(fixture);bath[y*w+380]=Cell(fixture);}
            for(int y=120;y<130;y++)for(int x=224;x<236;x++)bath[y*w+x]=Cell(body,-5);
            Start(bath);Advance(fps,fps);double rise=Center(bath,body)-Center(Read(),body);
            Check(rise>=14&&rise<=16,"water speed unchanged "+fps+"/"+rise);
            metrics.Add(new{scenario="body speed",mode=mode.ToString(),body,fps,distance,rise});
        }
        settings.Mode=SimulationMode.Sandbox;
        // No hydraulic shortcut across a full partition, at either setting.
        foreach(bool hydraulic in new[]{false,true}){
            Console.WriteLine("PHYXEL_WL_STAGE partition "+hydraulic);
            settings.HydraulicPressure=hydraulic;var split=new GridCell[n];
            for(int y=70;y<=230;y++)foreach(int x in new[]{100,230,360})split[y*w+x]=Cell(fixture);
            for(int x=100;x<=360;x++)split[230*w+x]=Cell(fixture);
            for(int y=110;y<230;y++)for(int x=101;x<230;x++)split[y*w+x]=Cell(water);
            for(int y=150;y<230;y++)for(int x=231;x<360;x++)split[y*w+x]=Cell(water);
            double left=Enumerable.Range(0,n).Where(i=>i%w<230).Sum(i=>(double)split[i].Mass);
            Start(split);Advance(60,60*3);var after=Read();
            Check(Math.Abs(left-Enumerable.Range(0,n).Where(i=>i%w<230).Sum(i=>(double)after[i].Mass))<.001,"partition "+hydraulic);
        }
        foreach(bool hydraulic in new[]{false,true}){
            Console.WriteLine("PHYXEL_WL_STAGE heat "+hydraulic);
            settings.HydraulicPressure=hydraulic;var warm=new GridCell[n];
            for(int x=80;x<=380;x++){
                int bottom=235-(int)Math.Round(.006*(x-230)*(x-230));
                for(int y=bottom;y<=bottom+3;y++)warm[y*w+x]=Cell(fixture);
                for(int y=145;y<bottom;y++)warm[y*w+x]=Cell(water);
            }
            for(int y=120;y<=160;y++)for(int x=210;x<=250;x++)
                if((x-230)*(x-230)+(y-140)*(y-140)<=400)warm[y*w+x]=Cell(ice,-5);
            Start(warm,true);Advance(60,60*20);var changing=Read();
            File.WriteAllBytes(Path.Combine(dir,"production-heat-"+hydraulic+".grid"),MemoryMarshal.AsBytes(changing.AsSpan()).ToArray());
            using(var stream=File.Create(Path.Combine(dir,"production-heat-"+hydraulic+".png")))r.PresentationTexture.SaveAsPng(stream,w,h);
            // The one-cell horizon contract applies after the source of
            // displacement has settled. Continuing fusion creates falling
            // droplets; hold the thermal state, then give transport 20 s.
            r.Materials.Upload(r.Context,table);Advance(60,60*20);
            var after=Read();var tops=new List<int>();
            for(int x=125;x<=335;x++){
                if(Enumerable.Range(0,h).Any(y=>after[y*w+x].IsActive!=0&&after[y*w+x].MaterialIndex==ice))continue;
                for(int y=60;y<230;y++)if(after[y*w+x].IsActive!=0){if(after[y*w+x].MaterialIndex==water)tops.Add(y);break;}
            }
            int range=tops.Max()-tops.Min();double error=Math.Abs(Energy(warm)-Energy(after))/Math.Abs(Energy(warm));
            Check(range<=1,"production heat surface "+hydraulic+"/"+range);
            Check(Math.Abs(warm.Sum(c=>(double)c.Mass)-after.Sum(c=>(double)c.Mass))<warm.Sum(c=>(double)c.Mass)*1e-5,"production heat mass");
            Check(error<.00005,"production heat energy "+error);
            Check(changing.Count(c=>c.IsActive!=0&&c.MaterialIndex==ice)==after.Count(c=>c.IsActive!=0&&c.MaterialIndex==ice),"fusion held during settling");
            metrics.Add(new{scenario="production heat",hydraulic,range,error,iceCells=after.Count(c=>c.IsActive!=0&&c.MaterialIndex==ice)});
            using(var stream=File.Create(Path.Combine(dir,"production-settled-"+hydraulic+".png")))r.PresentationTexture.SaveAsPng(stream,w,h);
        }
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"validation.json"),JsonSerializer.Serialize(new{checks,failures,width=w,height=h,metrics},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_WATER_LEVEL checks={checks} failures={failures}");
        if(failures>0&&!baseline)throw new InvalidOperationException("Water level checks failed");
    }
}
