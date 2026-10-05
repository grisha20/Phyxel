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

internal static class OilSmokeFlowRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry,
        SimulationSettings settings, Action<string> previewStatus)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/oil-smoke-flow";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_OS_BASELINE")=="1";
        bool matrix=Environment.GetEnvironmentVariable("PHYXEL_OS_MATRIX")=="1";
        string selection=Environment.GetEnvironmentVariable("PHYXEL_OS_CASES")??"oil,smoke";
        settings.Paused=true;settings.OpenBoundaries=true;settings.AirSimulation=true;
        if(float.TryParse(Environment.GetEnvironmentVariable("PHYXEL_OS_SCALE"),System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,out float scale))settings.ApplyScale(scale);
        uint oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),
            fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),
            fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=oil}],0);
        yield return r;
        int w=r.Width,h=r.Height,failures=0;var results=new List<object>();
        var physical=registry.CreateGpuTable();var isolated=physical.ToArray();
        for(int i=0;i<isolated.Length;i++)isolated[i].ThermalConductivity=0;
        var serializer=new SimulationStateSerializer();
        GridCell Cell(uint id,float t=20)=>new(){IsActive=1,MaterialIndex=id,Mass=physical[id].SimulationKind==2?physical[id].Density:1,
            Temperature=t,Lifetime=id==smoke?20:0,RestFrames=id==fixture?2u:0u};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double AirEnergy(){var a=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer));double q=0;
            for(int i=0;i<a.Length;i+=2)q+=(double)a[i]-273.15*a[i+1];return q;}
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(int fps in matrix?new[]{30,60,100}:new[]{60})
        foreach(string scene in selection.Split(','))
        {
            bool liquid=scene=="oil",pure=scene=="pure-smoke";
            previewStatus($"{mode}, {fps} FPS, {scene}");
            var g=new GridCell[w*h];
            for(int x=100;x<=380;x++)for(int y=200;y<=203;y++)g[y*w+x]=Cell(liquid?fixture:metal);
            if(liquid){
                for(int x=101;x<380;x++)for(int y=200-Math.Max(1,36-(int)Math.Round(Math.Abs(x-240)*.25));y<200;y++)g[y*w+x]=Cell(oil,40);
                for(int y=120;y<200;y++){g[y*w+100]=Cell(fixture);g[y*w+380]=Cell(fixture);}
            }
            if(pure)for(int y=204;y<=214;y++)for(int x=150;x<=330;x++)g[y*w+x]=Cell(smoke,404);
            settings.Mode=mode;settings.Paused=true;settings.AirSimulation=!liquid;settings.OpenBoundaries=!liquid&&!pure;
            serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false);r.Materials.Upload(r.Context,liquid||pure?isolated:physical);settings.Paused=false;
            double mass0=g.Where(c=>c.IsActive!=0&&c.MaterialIndex==oil).Sum(c=>(double)c.Mass);
            double smokeMass0=g.Where(c=>c.IsActive!=0&&c.MaterialIndex==smoke).Sum(c=>(double)c.Mass);
            double q0=g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
            if(pure)q0+=AirEnergy();
            int Count(GridCell[] a)=>Enumerable.Range(0,a.Length).Count(i=>i%w>=180&&i%w<=300&&i/w>=204&&i/w<=234&&a[i].IsActive!=0&&a[i].MaterialIndex==smoke);
            int stop=Count(g);double metalAtStop=20;var rows=new List<object>();
            for(int f=0;f<fps*(liquid?30:pure?6:12);f++){
                BrushDrawCommand[] brush=!liquid&&!pure&&f<fps*6?[new(){X=240,Y=232,EndX=240,EndY=232,Radius=18,Density=.82f,
                    Mode=BrushCommandMode.Material,MaterialIndex=fire,Seed=(uint)(90101+f)}]:[];
                coordinator.DispatchFrame(settings,brush,1f/fps);
                coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
                if((f+1)%fps==0){var a=Read();int count=Count(a);
                    double tm=a.Where(c=>c.IsActive!=0&&c.MaterialIndex==metal).Select(c=>(double)c.Temperature).DefaultIfEmpty(20).Average();
                    if(f+1==fps*6&&!pure){stop=count;metalAtStop=tm;}
                    int fireCenter=Enumerable.Range(0,a.Length).Count(i=>i%w>=180&&i%w<=300&&i/w>=204&&i/w<=250&&a[i].IsActive!=0&&a[i].MaterialIndex==fire);
                    rows.Add(new{seconds=(f+1)/fps,smokeCenter=count,fireCenter,metalTemperature=tm});
                }
                yield return r;
            }
            var end=Read();string label=$"{mode}-{fps}-{scene}";
            File.WriteAllBytes(Path.Combine(dir,label+".bin"),MemoryMarshal.AsBytes(end.AsSpan()).ToArray());
            File.WriteAllBytes(Path.Combine(dir,label+"-motion.bin"),AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer));
            SimulationScreenshotWriter.Save(r,Path.Combine(dir,label+".png"));
            var tops=Enumerable.Range(140,201).Select(x=>Enumerable.Range(0,h).FirstOrDefault(y=>end[y*w+x].IsActive!=0&&end[y*w+x].MaterialIndex==oil,h)).ToArray();
            int gap=tops.Max()-tops.Min(),remaining=Count(end);
            double mass=end.Where(c=>c.IsActive!=0&&c.MaterialIndex==oil).Sum(c=>(double)c.Mass);
            double q=end.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
            if(pure)q+=AirEnergy();
            double smokeMassError=Math.Abs(end.Where(c=>c.IsActive!=0&&c.MaterialIndex==smoke).Sum(c=>(double)c.Mass)-smokeMass0);
            int wallErrors=Enumerable.Range(100,281).Sum(x=>Enumerable.Range(200,4).Count(y=>end[y*w+x].MaterialIndex!=(liquid?fixture:metal)));
            double relativeEnergyError=Math.Abs(q-q0)/Math.Max(1,Math.Abs(q0));
            bool pass=liquid?gap<=4&&Math.Abs(mass-mass0)<1e-4&&relativeEnergyError<1e-5:stop>0&&remaining<=.2*stop;
            pass &= wallErrors==0&&(!pure||(smokeMassError<1e-4&&relativeEnergyError<1e-5));
            if(!pass)failures++;
            results.Add(new{label,gap,massError=Math.Abs(mass-mass0),smokeMassError,wallErrors,relativeEnergyError,stop,remaining,metalAtStop,pass,rows});
            Console.WriteLine($"PHYXEL_OS_CASE {label} gap={gap} smoke={stop}->{remaining} pass={pass}");
        }
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{width=w,height=h,scale=settings.Scale,failures,results},new JsonSerializerOptions{WriteIndented=true}));
        if(failures>0&&!baseline)throw new InvalidOperationException("Oil/smoke acceptance failed");
    }
}
