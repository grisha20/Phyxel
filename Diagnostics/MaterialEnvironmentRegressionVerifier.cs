using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class MaterialEnvironmentRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/material-environment";
        Directory.CreateDirectory(dir);bool baseline=Environment.GetEnvironmentVariable("PHYXEL_ENVIRONMENT_BASELINE")=="1";
        var settings=new SimulationSettings{Paused=true,AirSimulation=false,OpenBoundaries=false,SolidGravity=false};
        uint wood=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood),water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture),
            coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),wet=registry.GetRequiredRuntimeIndex(CoreMaterialIds.WetCharcoal);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,Mode=BrushCommandMode.Material,MaterialIndex=wood}],0);
        int w=r.Width,h=r.Height,n=w*h,an=r.AirWidth*r.AirHeight,checks=0,failures=0;
        var physical=registry.CreateGpuTable();var table=physical.ToArray();var serializer=new SimulationStateSerializer();var metrics=new List<object>();
        GridCell Cell(uint id,float t=20,float mass=-1)=>new(){IsActive=1,MaterialIndex=id,Temperature=t,Mass=mass>=0?mass:physical[id].SimulationKind==(uint)MaterialSimulationKind.Solid?physical[id].Density:1,RestFrames=2};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        Vector2[] ReadHeat()=>MemoryMarshal.Cast<byte,Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer)).ToArray();
        void Upload(GridCell[] grid){serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));coordinator.RestoreWorldActivity(r,true,true,false);r.Materials.Upload(r.Context,table);}
        void Check(bool ok,string name){checks++;if(!ok){failures++;Console.WriteLine("PHYXEL_ENVIRONMENT_FAIL "+name);}}
        double Mass(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
        double Energy(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        void Advance(int fps,float seconds){settings.Paused=false;for(int f=0;f<(int)Math.Round(fps*seconds);f++){
            coordinator.DispatchFrame(settings,[],1f/fps);coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);}}

        for(int i=0;i<table.Length;i++)table[i].ThermalConductivity=0;
        foreach(var mode in Enum.GetValues<SimulationMode>())foreach(int fps in new[]{30,60,100}){
            settings.Mode=mode;settings.AirSimulation=false;var grid=new GridCell[n];int bottom=180*w+220;
            for(int y=121;y<=180;y++)grid[y*w+220]=Cell(wood);
            // A finite, trapped eight-mass reservoir gives one stable root contact.
            grid[bottom+w]=Cell(water,20,8);foreach(int d in new[]{w-1,w+1,2*w})grid[bottom+d]=Cell(fixture);
            Upload(grid);Advance(fps,20);var a=Read();float rise=a[bottom-32*w].MoistureMass;
            Check(rise>=.005,"water reaches height32 "+mode+"/"+fps);
            Check(Math.Abs(Mass(a)-Mass(grid))<.0001*Math.Max(1,Mass(grid)),"capillary mass "+mode+"/"+fps);
            Check(Math.Abs(Energy(a)-Energy(grid))<.0001*Math.Max(1,Math.Abs(Energy(grid))),"capillary energy "+mode+"/"+fps);
            Check(a.Where(c=>c.IsActive!=0&&c.MaterialIndex==wood).All(c=>c.MoistureMass<=c.Mass*.3f+.0001f),"capillary pore limit "+mode+"/"+fps);
            // Compare path-sized bands, not the last-contact tick's root spike.
            float[] profile=Enumerable.Range(0,7).Select(band=>Enumerable.Range(121+8*band,8).Average(y=>a[y*w+220].MoistureMass)).ToArray();
            Check(Enumerable.Range(0,6).All(band=>profile[band]<=profile[band+1]+.003),"capillary source-to-top band profile "+mode+"/"+fps);
            metrics.Add(new{scenario="capillary",mode=mode.ToString(),fps,seconds=20,height=32,moisture=rise,totalWater=a.Sum(c=>c.MoistureMass+(c.IsActive!=0&&c.MaterialIndex==water?c.Mass:0))});
            if(fps==60){using var png=File.Create(Path.Combine(dir,"capillary-"+mode+".png"));r.PresentationTexture.SaveAsPng(png,w,h);}
        }
        foreach(bool wall in new[]{false,true}){
            var grid=new GridCell[n];int bottom=180*w+220;for(int y=121;y<=180;y++)grid[y*w+220]=Cell(wood);
            grid[bottom+w]=Cell(water,20,8);foreach(int d in new[]{w-1,w+1,2*w})grid[bottom+d]=Cell(fixture);
            grid[160*w+220]=wall?Cell(fixture):default;Upload(grid);Advance(60,20);var a=Read();
            Check(Enumerable.Range(121,39).All(y=>a[y*w+220].MoistureMass==0),"capillary sealed break "+wall);
        }
        // A visible thick stem, immersed at both sides and its foot, exercises
        // horizontal pores together with the longitudinal capillary path.
        foreach(var mode in Enum.GetValues<SimulationMode>())
        {
            settings.Mode=mode;settings.AirSimulation=false;var grid=new GridCell[n];
            for(int y=161;y<=190;y++)for(int x=195;x<=250;x++)grid[y*w+x]=Cell(water);
            for(int y=80;y<=180;y++)for(int x=218;x<=229;x++)grid[y*w+x]=Cell(wood);
            for(int y=160;y<=191;y++){grid[y*w+194]=Cell(fixture);grid[y*w+251]=Cell(fixture);}
            for(int x=194;x<=251;x++)grid[191*w+x]=Cell(fixture);
            Upload(grid);Advance(60,20);var a=Read();
            double rise=Enumerable.Range(218,12).Average(x=>a[129*w+x].MoistureMass);
            Check(rise>=.005,"thick stem wet above waterline "+mode);
            Check(Math.Abs(Mass(a)-Mass(grid))<.0001*Mass(grid),"thick stem mass "+mode);
            Check(Math.Abs(Energy(a)-Energy(grid))<.0001*Math.Abs(Energy(grid)),"thick stem energy "+mode);
            metrics.Add(new{scenario="thick-capillary",mode=mode.ToString(),fps=60,seconds=20,heightAboveWater=32,moisture=rise});
            using var png=File.Create(Path.Combine(dir,"thick-stem-"+mode+".png"));r.PresentationTexture.SaveAsPng(png,w,h);
        }
        settings.Paused=true;
        foreach(bool flat in new[]{false,true}){
            var colors=new GridCell[n];uint[] ids=[wood,coal,wet];
            for(int row=0;row<3;row++)for(int group=0;group<3;group++)for(int y=60+row*40;y<80+row*40;y++)for(int x=80+group*40;x<100+group*40;x++){
                int i=y*w+x;colors[i]=Cell(ids[row]);colors[i].FuelMass=group==0?0:colors[i].Mass*physical[ids[row]].FuelCapacity*(group==1?.1f:1);
            }
            Upload(colors);settings.RenderWithoutEffects=flat;coordinator.DispatchFrame(settings,[],0);
            using var png=File.Create(Path.Combine(dir,"oil-color-"+(flat?"flat":"effects")+".png"));r.PresentationTexture.SaveAsPng(png,w,h);
        }
        settings.RenderWithoutEffects=false;
        GridCell[] ThermalScene(bool wall=false,bool insulate=false){var grid=new GridCell[n];
            for(int x=74;x<=101;x++){grid[127*w+x]=Cell(fixture);grid[132*w+x]=Cell(fixture);}
            for(int y=127;y<=132;y++){grid[y*w+74]=Cell(fixture);grid[y*w+101]=Cell(fixture);}
            for(int y=128;y<=131;y++){
                grid[y*w+79]=Cell(metal,500);grid[y*w+80]=Cell(metal,500);
                grid[y*w+93]=Cell(insulate?fixture:metal,20,physical[metal].Density);grid[y*w+94]=Cell(insulate?fixture:metal,20,physical[metal].Density);
                if(wall)for(int x=85;x<=88;x++)grid[y*w+x]=Cell(fixture);
            }return grid;
        }
        table=physical.ToArray();table[fixture].ThermalConductivity=0;settings.AirSimulation=true;
        foreach(var mode in Enum.GetValues<SimulationMode>())foreach(int fps in new[]{30,60,100}){
            settings.Mode=mode;var grid=ThermalScene();Upload(grid);var heat0=ReadHeat();double e0=Energy(grid)+heat0.Sum(c=>(double)c.X);
            Advance(fps,20);var a=Read();var heat=ReadHeat();double e1=Energy(a)+heat.Sum(c=>(double)c.X);
            double receiver=Enumerable.Range(128,4).Average(y=>a[y*w+93].Temperature),donor=Enumerable.Range(128,4).Average(y=>a[y*w+80].Temperature);
            Check(receiver>=20.5&&donor<=499.5,"remote heating and cooling "+mode+"/"+fps);
            Check(Math.Abs(e1-e0)<.0001*Math.Max(1,Math.Abs(e0)),"closed body plus air energy "+mode+"/"+fps);
            Check(a.Where(c=>c.IsActive!=0).All(c=>c.Temperature>=19.99&&c.Temperature<=500.01),"air temperature bounds "+mode+"/"+fps);
            metrics.Add(new{scenario="remote-heat",mode=mode.ToString(),fps,seconds=20,receiver,donor,energyError=e1-e0});
            if(mode==SimulationMode.Simulation&&fps==60){
                settings.Paused=true;var bytes=MemoryMarshal.AsBytes(a.AsSpan()).ToArray();coordinator.DispatchFrame(settings,[],1);
                Check(bytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan()))&&MemoryMarshal.AsBytes(heat.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(ReadHeat().AsSpan())),"air exchange pause exact");
                string path=Path.Combine(dir,"heated-air.json");var snapshot=new SimulationWorldSnapshot(w,h,bytes,AirThermal:MemoryMarshal.AsBytes(heat.AsSpan()).ToArray());
                Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)metal,snapshot,registry)).GetAwaiter().GetResult();var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
                Check(loaded.World!.Grid.AsSpan().SequenceEqual(bytes)&&loaded.World.AirThermal!.AsSpan().SequenceEqual(snapshot.AirThermal!),"heated body/air reload exact");
                serializer.ApplyWorldSnapshot(r,loaded.World);var savedHeat=ReadHeat();SimulationDispatchCoordinator.DispatchAirHeat(r,false);var next=Read();var nextHeat=ReadHeat();
                serializer.ApplyWorldSnapshot(r,loaded.World);SimulationDispatchCoordinator.DispatchAirHeat(r,false);
                Check(MemoryMarshal.AsBytes(Read().AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(next.AsSpan()))&&MemoryMarshal.AsBytes(ReadHeat().AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(nextHeat.AsSpan())),"heat exchange replay exact");
            }
        }
        foreach(string control in new[]{"wall","insulate","disabled"}){
            settings.Mode=SimulationMode.Simulation;settings.AirSimulation=control!="disabled";var grid=ThermalScene(control=="wall",control=="insulate");Upload(grid);Advance(60,20);var a=Read();
            Check(Enumerable.Range(128,4).All(y=>Math.Abs(a[y*w+93].Temperature-20)<.03),"remote control "+control);
        }
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{passed=failures==0,checks,failures,baseline,metrics},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_ENVIRONMENT_RESULT passed={failures==0} checks={checks} failures={failures}");
        if(failures>0&&!baseline)throw new InvalidOperationException("Material environment failed");
    }
}
