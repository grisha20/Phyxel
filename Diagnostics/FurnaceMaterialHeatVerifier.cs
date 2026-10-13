using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX.Mathematics.Interop;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Diagnostics;

internal static class FurnaceMaterialHeatVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!;
        Directory.CreateDirectory(dir);
        if(Environment.GetEnvironmentVariable("PHYXEL_FH_BEHAVIOR") is { } behavior)
        {
            foreach(var resource in FurnaceBehaviorVerifier.Run(coordinator,registry,settings,dir,behavior))yield return resource;
            yield break;
        }
        if(Environment.GetEnvironmentVariable("PHYXEL_FH_REPLAY")=="1")
        {
            foreach(var resource in Replay(coordinator,registry,settings,dir))yield return resource;
            yield break;
        }
        settings.Width=96;settings.Height=96;settings.Paused=true;settings.AirSimulation=false;
        uint fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint stone=registry.GetRequiredRuntimeIndex("core:stone");
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=stone}],0);
        var table=registry.CreateGpuTable();
        for(int i=0;i<table.Length;i++)
        {
            table[i].AmbientCoolingRate=0;
            table[i].Flags&=~(uint)(MaterialFlags.ThermalHeater|MaterialFlags.ThermalCooler);
        }
        r.Materials.Upload(r.Context,table);
        var rows=new List<object>();int checks=0;
        GridCell Cell(uint id,float t,float mass)=>new(){MaterialIndex=id,IsActive=1,Mass=mass,Temperature=t};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Energy(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        foreach(var definition in registry.Materials)
        {
            uint id=definition.RuntimeIndex;var m=table[id];
            if(m.SimulationKind is (uint)MaterialSimulationKind.None or (uint)MaterialSimulationKind.Tool)
            {
                rows.Add(new{id=definition.Id,mode="N/A",reason="Not a material particle"});continue;
            }
            foreach(string mode in new[]{"flame-contact","flame-gap","wall-contact","carrier"})
            {
                var grid=new GridCell[r.Width*r.Height];
                var indices=new List<int>();
                float mass=m.SimulationKind==(uint)MaterialSimulationKind.Solid?m.Density:1;
                for(int y=47;y<=49;y++)for(int x=47;x<=49;x++)
                {int i=y*r.Width+x;grid[i]=Cell(id,m.InitialTemperature,mass);indices.Add(i);}
                if(mode!="carrier")for(int y=47;y<=49;y++)
                {
                    uint donor=mode=="wall-contact"?stone:fire;
                    grid[y*r.Width+(mode=="flame-gap"?38:46)]=Cell(donor,2000,donor==fire?.2f:table[stone].Density);
                }
                var air=new AirCell[r.AirWidth*r.AirHeight];
                var links=new uint[air.Length];var heat=new Vector2[air.Length];
                if(mode=="carrier")for(int y=0;y<r.AirHeight;y++)for(int x=0;x<r.AirWidth;x++)
                {
                    int i=y*r.AirWidth+x;heat[i]=new((2000+273.15f)*.016f,.016f);
                    if(x>0)links[i]|=1u<<3;if(x+1<r.AirWidth)links[i]|=1u<<5;
                    if(y>0)links[i]|=1u<<1;if(y+1<r.AirHeight)links[i]|=1u<<7;
                }
                r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
                r.Context.UpdateSubresource(air,r.Air.Buffer);
                r.Context.UpdateSubresource(links,r.AirFlowLinks.Buffer);
                r.Context.UpdateSubresource(heat,r.AirThermal.Buffer);
                r.Context.ClearUnorderedAccessView(r.AirProjectionB.UnorderedView,new RawInt4());
                double initial=Energy(grid),airInitial=heat.Sum(s=>(double)s.X-273.15*s.Y);
                double initialSample=indices.Sum(i=>(double)grid[i].Mass*PhaseEnthalpy.SpecificEnergy(grid[i],table));
                double firstHeat=0;
                for(uint tick=0;tick<20;tick++)
                {
                    if(mode=="carrier")for(uint a=0;a<3;a++)SimulationDispatchCoordinator.DispatchAirHeat(r,false,tick*3+a);
                    coordinator.DispatchThermalDiffusion(r,false,tick,false);
                    if(tick==0){var first=Read();firstHeat=indices.Sum(i=>(double)first[i].Mass*PhaseEnthalpy.SpecificEnergy(first[i],table))-initialSample;}
                }
                var after=Read();var thermal=MemoryMarshal.Cast<byte,Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer)).ToArray();
                double received=indices.Sum(i=>(double)after[i].Mass*PhaseEnthalpy.SpecificEnergy(after[i],table))-initialSample;
                double energyError=Math.Abs(Energy(after)-initial+thermal.Sum(s=>(double)s.X-273.15*s.Y)-airInitial);
                bool gapSupported=m.SimulationKind is (uint)MaterialSimulationKind.Solid or (uint)MaterialSimulationKind.Granular;
                bool expected=m.ThermalConductivity>0&&(mode!="flame-gap"||gapSupported);
                double massError=Math.Abs(after.Sum(c=>(double)c.Mass)-grid.Sum(c=>(double)c.Mass));
                bool pass=(!expected||received>1e-6)&&energyError<Math.Max(.01,Math.Abs(initial)+Math.Abs(airInitial))*.00005+.01 &&
                    massError<=1e-6 && after.Where(c=>c.IsActive!=0).All(c=>float.IsFinite(c.Temperature)&&c.Mass>0&&c.Temperature<=2000.001);
                checks++;if(!pass)throw new InvalidOperationException($"FH heat/budget failed {definition.Id}/{mode}: dH={received} error={energyError}");
                var row=new{id=definition.Id,mode,m.ThermalConductivity,m.HeatCapacity,mass,
                    startTemperature=m.InitialTemperature,firstHeat,received,
                    surfaceTemperature=indices.Where(i=>i%r.Width==47).Average(i=>after[i].Temperature),
                    coreTemperature=after[48*r.Width+48].Temperature,
                    phaseProgress=indices.Sum(i=>(double)after[i].PhaseProgress),energyError,massError,expected,pass};
                rows.Add(row);Console.WriteLine("PHYXEL_FH_ROW "+JsonSerializer.Serialize(row));
                if(definition.Id=="core:tnt")SimulationScreenshotWriter.Save(r,Path.Combine(dir,mode+".png"));
            }
            yield return r;
        }
        // A passive copy above separates material heating from regulation.
        // These actual device cells now verify the active power/target rules.
        table=registry.CreateGpuTable();
        for(int i=0;i<table.Length;i++)table[i].AmbientCoolingRate=0;
        r.Materials.Upload(r.Context,table);
        foreach(string deviceId in new[]{CoreMaterialIds.Heater,CoreMaterialIds.Cooler})
        foreach(float initialTemperature in new[]{0f,1000f})
        {
            uint id=registry.GetRequiredRuntimeIndex(deviceId);var m=table[id];
            var grid=new GridCell[r.Width*r.Height];int index=48*r.Width+48;
            grid[index]=Cell(id,initialTemperature,m.Density);
            grid[index].DeviceTargetTemperature=m.ThermalDeviceTargetTemperature;
            grid[index].DeviceMaximumPower=m.ThermalDeviceMaximumPower;
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.DispatchThermalDiffusion(r,false,0,false);var after=Read();
            double capacity=m.Density*m.HeatCapacity;
            double required=capacity*(m.ThermalDeviceTargetTemperature-initialTemperature);
            double limit=m.ThermalDeviceMaximumPower*SimulationDispatchCoordinator.FixedThermalStep;
            double expected=(m.Flags&(uint)MaterialFlags.ThermalHeater)!=0?Math.Clamp(required,0,limit):Math.Clamp(required,-limit,0);
            double actual=Energy(after)-Energy(grid);
            if(Math.Abs(actual-expected)>.001)throw new InvalidOperationException("FH device power/target mismatch "+deviceId);
            checks++;rows.Add(new{id=deviceId,mode="active-regulator",initialTemperature,expectedHeat=expected,actualHeat=actual,pass=true});
        }
        File.WriteAllText(Path.Combine(dir,"catalog.json"),JsonSerializer.Serialize(new{checks,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_FH_CATALOG_SUCCESS checks={checks}");
    }

    private static IEnumerable<GpuSimulationResources> Replay(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings,string dir)
    {
        var r=FurnaceSensorRegressionVerifier.LoadFixture(coordinator,registry,settings);
        if(r.Width!=968||r.Height!=564)throw new InvalidOperationException("FH needs the unchanged sensor furnace.");
        settings.Paused=false;uint tnt=registry.GetRequiredRuntimeIndex("core:tnt");
        string scene=Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SCENE")!;
        string world=Path.ChangeExtension(scene,".world");
        string worldHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(world)));
        string jsonHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scene)));
        int fps=60;var rows=new List<object>();double firstReaction=-1,previousObservation=-1,initialMass=0;
        double firstReactionLowerBound=-1;
        int stride=Marshal.SizeOf<GridCell>(),patchWidth=9;
        using var staging=new Buffer(r.Device,new BufferDescription { SizeInBytes=patchWidth*patchWidth*stride,
            Usage=ResourceUsage.Staging,CpuAccessFlags=CpuAccessFlags.Read,BindFlags=BindFlags.None });
        for(int frame=0;frame<=fps*72;frame++)
        {
            bool insert=frame==fps*60;
            coordinator.DispatchFrame(settings,insert?[new(){X=445,Y=345,Radius=3,Density=1,MaterialIndex=tnt}]:[],1f/fps);
            if(frame>=fps*60 && (frame%fps==0 || firstReaction<0 && frame%(fps/10)==0))
            {
                var patch=ReadPatch(r,staging,441,341,patchWidth,stride);
                var cells=patch.Where(c=>c.IsActive!=0&&c.MaterialIndex==tnt).ToArray();
                double mass=cells.Sum(c=>(double)c.Mass);
                double seconds=frame/(double)fps-60;
                if(insert)initialMass=mass;
                if(firstReaction<0&&(cells.Any(c=>c.Lifetime>0)||mass<initialMass-1e-5))
                {firstReaction=seconds;firstReactionLowerBound=previousObservation;}
                var row=new{seconds=frame/(double)fps-60,cells=cells.Length,mass,
                    meanTemperature=cells.Length>0?cells.Average(c=>c.Temperature):0,
                    maximumTemperature=cells.Length>0?cells.Max(c=>c.Temperature):0,
                    reacting=cells.Count(c=>c.Lifetime>0),firstReaction};
                rows.Add(row);Console.WriteLine("PHYXEL_FH_REPLAY_ROW "+JsonSerializer.Serialize(row));
                if(frame%fps==0)
                {
                    File.WriteAllBytes(Path.Combine(dir,$"grid-{frame/fps-60}.bin"),AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer));
                    SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"world-{frame/fps-60}.png"));
                }
                if(insert&&cells.Length==0)throw new InvalidOperationException("FH brush did not place TNT in the channel.");
                previousObservation=seconds;
            }
            yield return r;
        }
        if(worldHash!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(world))) ||
           jsonHash!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scene))))throw new InvalidOperationException("FH changed user save.");
        File.WriteAllText(Path.Combine(dir,"replay.json"),JsonSerializer.Serialize(new{fps,worldHash,jsonHash,
            firstReactionLowerBound,firstReactionUpperBound=firstReaction,rows},new JsonSerializerOptions{WriteIndented=true}));
        if(firstReaction<0 || firstReaction>12)throw new InvalidOperationException("TNT did not react within the FH observation.");
        Console.WriteLine("PHYXEL_FH_REPLAY_COMPLETE");
    }

    private static GridCell[] ReadPatch(GpuSimulationResources r,Buffer staging,int x,int y,int width,int stride)
    {
        for(int row=0;row<width;row++)
        {
            int start=((y+row)*r.Width+x)*stride;
            r.Context.CopySubresourceRegion(r.Grid.ReadBuffer,0,new ResourceRegion(start,0,0,start+width*stride,1,1),
                staging,0,row*width*stride);
        }
        var mapping=r.Context.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
        try {
            var bytes=new byte[width*width*stride];Marshal.Copy(mapping.DataPointer,bytes,0,bytes.Length);
            return MemoryMarshal.Cast<byte,GridCell>(bytes).ToArray();
        }finally{r.Context.UnmapSubresource(staging,0);}
    }
}
