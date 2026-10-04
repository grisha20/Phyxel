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

internal static class OilLocalityRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/oil-locality";
        Directory.CreateDirectory(dir);
        var settings=new SimulationSettings{Paused=true,AirSimulation=false,OpenBoundaries=false};
        bool iceSupport=Environment.GetEnvironmentVariable("PHYXEL_OIL_LOCALITY_ICE")=="1";
        uint oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            frozen=registry.GetRequiredRuntimeIndex(iceSupport?CoreMaterialIds.Ice:CoreMaterialIds.FrozenOil),fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=water}],0);
        int w=r.Width,h=r.Height,checks=0,failures=0; var results=new List<object>();
        var physical=registry.CreateGpuTable(); var table=physical.ToArray();
        for(int i=0;i<table.Length;i++)table[i].ThermalConductivity=0;
        var serializer=new SimulationStateSerializer();
        GridCell Cell(uint id,float t=20)=>new(){IsActive=1,MaterialIndex=id,Mass=physical[id].Density,
            Temperature=t,RestFrames=id==fixture?2u:0u};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Check(bool ok,string label){checks++;if(!ok){failures++;Console.WriteLine("PHYXEL_OL_FAIL "+label);}}
        bool mobileOnly=Environment.GetEnvironmentVariable("PHYXEL_OIL_LOCALITY_MOBILE")=="1";
        foreach(var mode in mobileOnly?new[]{SimulationMode.Sandbox}:new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(bool hydraulic in mobileOnly?new[]{false}:new[]{false,true})
        foreach(int fps in mobileOnly?new[]{60}:new[]{30,60,100})foreach(int scene in mobileOnly?new[]{2}:new[]{0,1,2})
        {
            bool coating=scene>0,mobile=scene==2;
            var g=new GridCell[w*h];
            // Thin bottom film, long frozen slope, and continuous oil feed near
            // the lower end, matching the user's separated-patch observation.
            // The fixture backing holds the slope, isolating liquid transport
            // from rigid-body motion. Both are real catalogue materials.
            for(int x=0;x<w;x++){
                g[(h-1)*w+x]=Cell(fixture);
                for(int y=h-4;y<h-1;y++)g[y*w+x]=Cell(water);
            }
            for(int x=80;x<=320;x++){
                int y=46+(x-80)*9/10;
                for(int k=0;k<3;k++)g[(y+k)*w+x]=Cell(frozen,-10);
                if(!mobile)for(int k=3;k<5;k++)g[(y+k)*w+x]=Cell(fixture);
                if(coating&&x>=90&&x<=310)for(int k=1;k<=7;k++)g[(y-k)*w+x]=Cell(oil);
            }
            settings.Mode=mode;settings.HydraulicPressure=hydraulic;settings.Paused=true;
            serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,hydraulic);r.Materials.Upload(r.Context,table);settings.Paused=false;
            double waterStart=g.Where(c=>c.IsActive!=0&&c.MaterialIndex==water).Sum(c=>(double)c.Mass);
            double frozenStart=g.Where(c=>c.IsActive!=0&&c.MaterialIndex==frozen).Sum(c=>(double)c.Mass);
            int furthestJump=0,remoteCells=0;double massAtStop=0,maxMassError=0,energyAtStop=0,maxEnergyError=0;
            var previous=new HashSet<int>(Enumerable.Range(0,g.Length).Where(i=>g[i].IsActive!=0&&g[i].MaterialIndex==oil));
            for(int f=0;f<fps*6;f++){
                // Spawn the same 2s stream; rest of the run is source-free.
                BrushDrawCommand[] commands=f<fps*2
                    ? new[]{new BrushDrawCommand{X=310,Y=248,EndX=310,EndY=248,Radius=3,Density=.82f,
                        Mode=BrushCommandMode.Material,MaterialIndex=oil,Seed=unchecked((uint)(71001+f))}}:Array.Empty<BrushDrawCommand>();
                coordinator.DispatchFrame(settings,commands,1f/fps);
                coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
                var after=Read();var positions=new HashSet<int>(Enumerable.Range(0,after.Length).Where(i=>after[i].IsActive!=0&&after[i].MaterialIndex==oil));
                foreach(int i in positions.Where(i=>!previous.Contains(i))){
                    int x=i%w,y=i/w;
                    int nearest=f<fps*2?Math.Max(Math.Abs(x-310),Math.Abs(y-248))-3:int.MaxValue;
                    // A coarse occupancy radius catches nonlocal transfers
                    // without mistaking several local solver passes for a hop.
                    for(int dy=-32;dy<=32;dy++)for(int dx=-32;dx<=32;dx++){
                        int xx=x+dx,yy=y+dy;
                        if(xx>=0&&xx<w&&yy>=0&&yy<h&&previous.Contains(yy*w+xx))nearest=Math.Min(nearest,Math.Max(Math.Abs(dx),Math.Abs(dy)));
                    }
                    if(nearest>32)remoteCells++;
                    furthestJump=Math.Max(furthestJump,nearest==int.MaxValue?w:nearest);
                }
                double mass=positions.Sum(i=>(double)after[i].Mass);
                double energy=after.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
                if(f==fps*2-1){massAtStop=mass;energyAtStop=energy;}
                if(f>=fps*2){maxMassError=Math.Max(maxMassError,Math.Abs(mass-massAtStop));
                    maxEnergyError=Math.Max(maxEnergyError,Math.Abs(energy-energyAtStop)/Math.Max(1,Math.Abs(energyAtStop)));}
                previous=positions;
                Check(after.Where(c=>c.IsActive!=0).All(c=>float.IsFinite(c.Mass)&&float.IsFinite(c.Temperature)),"finite cells");
            }
            string label=$"{mode}-{hydraulic}-{fps}-{(mobile?"mobile":coating?"ribbon":"feed")}";
            Check(remoteCells==0,label+" nonlocal oil transfer");
            Check(maxMassError<1e-4,label+" source-free mass");
            Check(maxEnergyError<1e-5,label+" source-free energy");
            Check(previous.Any(i=>i/w>=h-5),label+" oil reached water");
            var final=Read();
            Check(Math.Abs(final.Where(c=>c.IsActive!=0&&c.MaterialIndex==water).Sum(c=>(double)c.Mass)-waterStart)<1e-4,label+" water mass");
            Check(Math.Abs(final.Where(c=>c.IsActive!=0&&c.MaterialIndex==frozen).Sum(c=>(double)c.Mass)-frozenStart)<1e-4,label+" solid mass");
            File.WriteAllBytes(Path.Combine(dir,label+".bin"),MemoryMarshal.AsBytes(final.AsSpan()).ToArray());
            results.Add(new{support=iceSupport?"ice":"frozen_oil",mode=mode.ToString(),hydraulic,fps,coating,mobile,furthestJump,remoteCells,massAtStop,maxMassError,maxEnergyError,oilCells=previous.Count});
            Console.WriteLine($"PHYXEL_OL_CASE {label} jump={furthestJump} remote={remoteCells} massError={maxMassError:E3}");
        }
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{checks,failures,results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_OIL_LOCALITY checks={checks} failures={failures}");
        if(failures>0)throw new InvalidOperationException("Oil locality regression failed");
    }
}
