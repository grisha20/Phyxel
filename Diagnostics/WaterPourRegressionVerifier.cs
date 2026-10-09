using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Actual held material-brush commands, not an already-filled saved bath.
internal static class WaterPourRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!;
        Directory.CreateDirectory(dir);
        int fps=int.Parse(Environment.GetEnvironmentVariable("PHYXEL_POUR_FPS")??"100");
        int seconds=int.Parse(Environment.GetEnvironmentVariable("PHYXEL_POUR_SECONDS")??"10");
        int radius=int.Parse(Environment.GetEnvironmentVariable("PHYXEL_POUR_RADIUS")??"33");
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        GpuSimulationResources r;
        if(Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SCENE") is not null)
            r=FurnaceSensorRegressionVerifier.LoadFixture(coordinator,registry,settings);
        else
        {
            settings.Width=968;settings.Height=564;settings.Scale=.5f;settings.Paused=true;
            settings.AirSimulation=Environment.GetEnvironmentVariable("PHYXEL_POUR_AIR")!="0";
            settings.OpenBoundaries=false;settings.SolidGravity=false;settings.HydraulicPressure=false;
            r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=water}],0);
            r.Context.UpdateSubresource(new GridCell[r.Width*r.Height],r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);
        }
        if(Enum.TryParse<SimulationMode>(Environment.GetEnvironmentVariable("PHYXEL_POUR_MODE"),out var mode))settings.Mode=mode;
        if(Environment.GetEnvironmentVariable("PHYXEL_VERIFY_LIQUID_KERNELS")=="1")
        {
            LiquidSurfaceKernelVerifier.Run(r,registry);
            yield break;
        }
        settings.Paused=false;
        var command=new BrushDrawCommand { X=887,Y=60,EndX=887,EndY=60,Radius=radius,Density=.82f,MaterialIndex=water };
        var rows=new List<object>();
        using var timer=new GpuStageTimer(r.Device);
        var wall=Stopwatch.StartNew();
        double frameCpuMs=0;
        double firstMass=0,peakMass=0;
        int deepestPour=-1,lastTop=-1;
        for(int frame=0;frame<=fps*seconds;frame++)
        {
            // Exclude initial driver/pipeline warm-up from held-brush timing.
            if(frame==fps){timer.ResetStatistics();r.CellularTimer?.ResetStatistics();}
            bool pouring=frame>=fps && frame<fps*7;
            if(frame>0)
            {
                timer.Begin(r.Context);
                long cpuStart=Stopwatch.GetTimestamp();
                coordinator.DispatchFrame(settings,pouring?[command]:[],1f/fps);
                frameCpuMs=Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                timer.End(r.Context);
            }
            if(frame%fps==0)
            {
                var bytes=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);
                var grid=MemoryMarshal.Cast<byte,GridCell>(bytes).ToArray();
                var stream=grid.Select((c,i)=>(c,i)).Where(p=>p.c.IsActive!=0&&p.c.MaterialIndex==water&&p.i%r.Width>800).ToArray();
                double streamMass=stream.Sum(p=>(double)p.c.Mass);
                int top=stream.Length==0?-1:stream.Min(p=>p.i/r.Width);
                int bottom=stream.Length==0?-1:stream.Max(p=>p.i/r.Width);
                if(frame==0)firstMass=streamMass;
                peakMass=Math.Max(peakMass,streamMass);
                if(pouring)deepestPour=Math.Max(deepestPour,bottom);
                lastTop=top;
                var row=new {seconds=frame/(double)fps,wallSeconds=wall.Elapsed.TotalSeconds,pouring,
                    streamCells=stream.Length,mass=streamMass,minY=top,maxY=bottom,
                    gpu=timer.Statistics,cellular=r.CellularTimer?.Statistics,convection=r.WaterConvectionTimer?.Statistics,
                    phases=r.CellularPhaseTimers?.ToDictionary(p=>p.Key,p=>p.Value.Statistics),
                    thermal=coordinator.ThermalGpuTiming,air=coordinator.AirGpuTiming,gas=coordinator.GasMotionGpuTiming,
                    combustion=coordinator.CombustionGpuTiming,frameCpuMs};
                rows.Add(row);Console.WriteLine("PHYXEL_POUR "+JsonSerializer.Serialize(row));
                File.WriteAllBytes(Path.Combine(dir,$"grid-{frame/fps}.bin"),bytes);
                SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"world-{frame/fps}.png"));
                if(grid.Any(c=>c.IsActive!=0&&(!float.IsFinite(c.Temperature)||!float.IsFinite(c.Mass)||c.Mass<=0)))
                    throw new InvalidOperationException("Pour produced invalid physical state.");
            }
            yield return r;
        }
        bool behaviorPass=peakMass>firstMass+1000 && deepestPour>=r.Height-2 && (lastTop<0 || lastTop>command.Y+radius);
        if(!behaviorPass)
            throw new InvalidOperationException("Pour must add bulk water, reach the lower edge and clear the source after release.");
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new {fps,radius,behaviorPass,settings.Mode,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_POUR_REPLAY_COMPLETE");
    }
}
