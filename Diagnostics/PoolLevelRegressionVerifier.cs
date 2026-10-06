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

internal static class PoolLevelRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/pool-level";
        Directory.CreateDirectory(dir);
        bool matrix=Environment.GetEnvironmentVariable("PHYXEL_POOL_MATRIX")=="1";
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_POOL_BASELINE")=="1";
        var settings=new SimulationSettings{Paused=true,AirSimulation=false,OpenBoundaries=false};
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,Mode=BrushCommandMode.Material,
            MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water)}],0);
        int w=r.Width,h=r.Height,n=w*h,failures=0,checks=0;
        var physical=registry.CreateGpuTable();var insulated=physical.ToArray();
        for(int i=0;i<insulated.Length;i++)insulated[i].ThermalConductivity=0;
        var serializer=new SimulationStateSerializer();var metrics=new List<object>();
        GridCell Cell(string id,float t=30)=>new(){IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(id),Temperature=t,
            Mass=physical[registry.GetRequiredRuntimeIndex(id)].SimulationKind==(uint)MaterialSimulationKind.Solid?physical[registry.GetRequiredRuntimeIndex(id)].Density:1};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Check(bool ok,string label){checks++;if(!ok){failures++;Console.WriteLine("PHYXEL_POOL_FAIL "+label);}}
        double Energy(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        if(Environment.GetEnvironmentVariable("PHYXEL_POOL_GATES")=="1")
        {
            // Directly exercise the new closure; the old local solver may not
            // mask a forbidden transfer or repair a damaged one-cell cache.
            var gated=physical.ToArray();for(int i=0;i<gated.Length;i++)gated[i].ThermalConductivity=0;
            r.Materials.Upload(r.Context,gated);
            void Start(GridCell[] g){serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
                coordinator.RestoreWorldActivity(r,true,false,false);
                r.Context.UpdateSubresource(g.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);}
            bool openFloor=false;
            void Step(uint tick){
                var c=new SimulationFrameConstants{Width=(uint)w,Height=(uint)h,FrameIndex=tick,DeltaTime=1f/120,
                    DispatchExtentX=(uint)w,DispatchExtentY=(uint)h,SimulationPhase=33,OpenBoundaries=openFloor?1u:0u};
                r.Context.UpdateSubresource(ref c,r.FrameConstants);r.Context.ComputeShader.Set(r.CellularAutomataShader);
                r.Context.ComputeShader.SetConstantBuffer(0,r.FrameConstants);r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
                r.Context.ComputeShader.SetShaderResource(15,r.Filters.View);
                r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.BodyFlags.UnorderedView,
                    r.PathBlockerMasks.UnorderedView,r.CellMaterials.UnorderedView,r.WaterPressureRoutes.UnorderedView,
                    r.WaterPressureRouteScratch.UnorderedView,r.GasMotion.UnorderedView);
                r.Context.Dispatch((w+15)/16,1,1);r.Context.ComputeShader.Set(r.LiquidSurfaceBalanceShader);r.Context.Dispatch(1,1,1);
                for(int i=0;i<7;i++)r.Context.ComputeShader.SetUnorderedAccessView(i,null);r.Context.ComputeShader.SetShaderResource(0,null);
                r.Context.ComputeShader.SetShaderResource(15,null);r.Context.ComputeShader.Set(null);}
            foreach(string gate in new[]{"wall","filter","drain","slot","shaft","floorless","world-floor","film","gas-filter","jet","open"}){
                openFloor=gate=="floorless";
                var g=new GridCell[n];for(int x=100;x<=200;x++){
                    g[200*w+x]=Cell("core:steel");int top=x<150?160:180;
                    if(gate=="slot")top=160+(x-100)/8;
                    if(gate=="film")top=x<150?199:200;
                    for(int y=top;y<200;y++)g[y*w+x]=Cell("core:water");}
                for(int y=140;y<=200;y++){g[y*w+99]=Cell("core:steel");g[y*w+201]=Cell("core:steel");}
                if(gate=="wall")for(int y=140;y<201;y++)g[y*w+150]=Cell("core:steel");
                if(gate=="drain")for(int x=100;x<=200;x++)g[200*w+x]=default;
                if(gate=="slot")for(int x=148;x<=151;x++)g[200*w+x]=default;
                if(gate=="shaft")for(int x=148;x<=151;x++){
                    for(int y=200;y<225;y++)g[y*w+x]=Cell("core:water");
                    g[225*w+x]=Cell("core:steel");}
                if(gate is "floorless" or "world-floor"){
                    for(int x=100;x<=200;x++)for(int y=200;y<h;y++)g[y*w+x]=Cell("core:water");
                    for(int y=200;y<h;y++){g[y*w+99]=Cell("core:steel");g[y*w+201]=Cell("core:steel");}}
                if(gate=="jet")for(int y=140;y<160;y++){
                    g[y*w+125]=Cell("core:water");g[y*w+125].VelocityY=60;}
                int gasSource=160*w+100,gasTarget=179*w+200;
                if(gate is "open" or "gas-filter"){g[gasTarget]=Cell(CoreMaterialIds.Steam,122);g[gasTarget].Mass=.05f;}
                Start(g);
                if(gate=="open"){
                    var motion=new GasMotionState[n];motion[gasTarget]=new(){VelocityX=1,VelocityY=-2,OffsetX=.3f,OffsetY=.4f};
                    r.Context.UpdateSubresource(motion,r.GasMotion.Buffer);
                }
                Array.Clear(r.FilterMap);r.FilterCount=0;
                if(gate=="filter")for(int y=140;y<201;y++){r.FilterMap[y*w+150]=FilterRules.Closed;r.FilterCount++;}
                if(gate=="gas-filter"){r.FilterMap[gasSource]=FilterRules.Liquid;r.FilterCount=1;}
                r.UploadFilters();
                double Left(GridCell[] a)=>Enumerable.Range(0,n).Where(i=>i%w<150&&a[i].IsActive!=0&&a[i].MaterialIndex==registry.GetRequiredRuntimeIndex("core:water")).Sum(i=>(double)a[i].Mass);
                double left=Left(g);for(uint i=0;i<(gate is "open" or "gas-filter" or "jet" or "world-floor"?1:120);i++)Step(i);var a=Read();
                Check(gate is "open" or "jet" or "world-floor"?Left(a)<left:Left(a)==left,"gate "+gate);
                Check(Math.Abs(Energy(g)-Energy(a))<.01,"gate Q "+gate);
                Check(gate is "open" or "jet" or "world-floor"||MemoryMarshal.AsBytes(g.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(a.AsSpan())),"gate unchanged "+gate);
                if(gate=="jet")Check(a[140*w+125].VelocityY==60&&a[140*w+125].IsActive!=0,"incoming jet packet");
                Console.WriteLine($"PHYXEL_POOL_GATE {gate} leftBefore={left} leftAfter={Left(a)}");
                if(gate=="open"){
                    var gas=MemoryMarshal.Cast<byte,GasMotionState>(AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer)).ToArray();
                    Check(a[gasSource].MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam)&&
                        gas[gasSource].VelocityX==1&&gas[gasSource].VelocityY==-2&&gas[gasSource].OffsetX==.3f&&gas[gasSource].OffsetY==.4f,"gas packet");
                    var snapshot=new SimulationWorldSnapshot(w,h,MemoryMarshal.AsBytes(a.AsSpan()).ToArray());var path=Path.Combine(dir,"pool-reload.json");
                    Task.Run(()=>serializer.SaveAsync(path,settings,registry.GetRequiredRuntimeIndex("core:water"),snapshot,registry)).GetAwaiter().GetResult();
                    var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
                    Check(snapshot.Grid.AsSpan().SequenceEqual(loaded.World!.Grid),"pool codec");
                    Step(120);var memory=Read();Start(MemoryMarshal.Cast<byte,GridCell>(loaded.World.Grid).ToArray());Step(120);var reload=Read();
                    Check(MemoryMarshal.AsBytes(memory.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(reload.AsSpan())),"pool resume");
                }
            }
            Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
            foreach(string id in new[]{"core:molten_metal","core:molten_steel","core:molten_cast_iron"})
            {
                var g=new GridCell[n];float tf=physical[registry.GetRequiredRuntimeIndex(id)].TransitionBelowTemperature;
                for(int x=100;x<=200;x++){
                    g[200*w+x]=Cell("core:steel");for(int y=x<150?160:180;y<200;y++){
                        var c=Cell(id,tf);c.Mass=.25f+.25f*((x+y)%4);c.Lifetime=-50*(x%3);g[y*w+x]=c;}}
                for(int y=140;y<=200;y++){g[y*w+99]=Cell("core:steel");g[y*w+201]=Cell("core:steel");}
                Start(g);Step(0);var a=Read();
                double Stock(GridCell[] cells)=>cells.Where(c=>c.IsActive!=0&&c.MaterialIndex==registry.GetRequiredRuntimeIndex(id)).Sum(c=>(double)c.Mass);
                Check(Math.Abs(Stock(a)-Stock(g))<.0001&&Math.Abs(Energy(a)-Energy(g))<.01,"partial parcels "+id);
                var before=g.Where(c=>c.IsActive!=0&&c.MaterialIndex==registry.GetRequiredRuntimeIndex(id)).Select(c=>(c.Mass,c.Lifetime)).OrderBy(c=>c.Mass).ThenBy(c=>c.Lifetime).ToArray();
                var after=a.Where(c=>c.IsActive!=0&&c.MaterialIndex==registry.GetRequiredRuntimeIndex(id)).Select(c=>(c.Mass,c.Lifetime)).OrderBy(c=>c.Mass).ThenBy(c=>c.Lifetime).ToArray();
                Check(before.SequenceEqual(after),"latent packets "+id);
            }
            Console.WriteLine($"PHYXEL_POOL_COMPLETE checks={checks} failures={failures} baseline={baseline}");
            if(failures>0)throw new InvalidOperationException("Pool gates failed");return;
        }
        string[] ids=Environment.GetEnvironmentVariable("PHYXEL_POOL_IDS")?.Split(',')??new[]{"core:water","core:oil","core:molten_metal","core:molten_steel","core:molten_cast_iron"};
        foreach(string id in ids)foreach(var mode in matrix?new[]{SimulationMode.Sandbox,SimulationMode.Simulation}:new[]{SimulationMode.Simulation})
        foreach(bool hydraulic in matrix?new[]{false,true}:new[]{false})foreach(int fps in matrix?new[]{30,60,100}:new[]{60})
        foreach(bool upper in new[]{false,true}){
            settings.Mode=mode;settings.HydraulicPressure=hydraulic;
            float t=id.Contains("molten")?physical[registry.GetRequiredRuntimeIndex(id)].InitialTemperature:30;
            var g=new GridCell[n];
            void Basin(int left,int right,int bottom,int top,bool slope){
                for(int x=left;x<=right;x++){
                    for(int y=bottom;y<bottom+3;y++)g[y*w+x]=Cell("core:steel");
                    int surface=top+(slope?(x-left)/12:0);
                    for(int y=surface;y<bottom;y++)g[y*w+x]=Cell(id,t);}
                for(int y=top-20;y<bottom;y++){g[y*w+left-1]=Cell("core:steel");g[y*w+right+1]=Cell("core:steel");}}
            // A small higher, already flat vessel must not monopolize the
            // whole-width donor search and starve the wide lower vessel.
            if(upper)Basin(40,70,70,50,false);
            Basin(120,400,235,175,true);
            settings.Paused=true;serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,false,hydraulic);r.Materials.Upload(r.Context,insulated);settings.Paused=false;
            uint material=registry.GetRequiredRuntimeIndex(id);double mass0=g.Where(c=>c.IsActive!=0&&c.MaterialIndex==material).Sum(c=>(double)c.Mass);
            for(int frame=0;frame<fps*20;frame++)coordinator.DispatchFrame(settings,[],1f/fps);
            var a=Read();var tops=new List<int>();for(int x=125;x<=395;x++)
                for(int y=140;y<235;y++)if(a[y*w+x].IsActive!=0&&a[y*w+x].MaterialIndex==material){tops.Add(y);break;}
            int range=tops.Count==271?tops.Max()-tops.Min():h;
            string label=$"{id}/{mode}/{hydraulic}/{fps}/upper={upper}";
            Check(range<=1,"level "+label+" range="+range);
            double mass=a.Where(c=>c.IsActive!=0&&c.MaterialIndex==material).Sum(c=>(double)c.Mass);
            Check(Math.Abs(mass-mass0)<.002,"mass "+label);
            double q=Energy(a)-Energy(g);Check(Math.Abs(q)<Math.Max(.01,Math.Abs(Energy(g))*.00005),"Q "+label);
            Check(Enumerable.Range(0,n).All(i=>g[i].MaterialIndex!=registry.GetRequiredRuntimeIndex("core:steel")||
                (a[i].IsActive!=0&&a[i].MaterialIndex==g[i].MaterialIndex)),"walls "+label);
            if(upper)Check(Math.Abs(a.Take(100*w).Where(c=>c.IsActive!=0&&c.MaterialIndex==material).Sum(c=>(double)c.Mass)-620)<.001,"isolated vessel "+label);
            metrics.Add(new{id,mode=mode.ToString(),hydraulic,fps,upper,range,massError=mass-mass0,Q=q});
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_POOL_CASE {label} range={range} Q={q:E6} massError={mass-mass0:E6}"));
            SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{id.Split(':')[1]}-{mode}-{hydraulic}-{fps}-{upper}.png"));
        }
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_POOL_COMPLETE checks={checks} failures={failures} baseline={baseline}");
        if(failures>0&&!baseline)throw new InvalidOperationException("Pool level failed");
    }
}
