using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

// Runs the production frame scheduler. Source geometry/ignition and sampling
// are identical for the recorded old implementation and the new pulse.
internal static class GunpowderRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string directory=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/gunpowder";
        Directory.CreateDirectory(directory);
        bool matrix=Environment.GetEnvironmentVariable("PHYXEL_GUNPOWDER_MATRIX")=="1";
        string selection=Environment.GetEnvironmentVariable("PHYXEL_GUNPOWDER_CASES") ?? "open,furnace";
        string[] scenes = selection.Split(',').Select(value => value.Trim()).ToArray();
        string[] supported = ["open", "cold", "closed", "vented", "furnace", "furnace-control",
            "furnace-sealed", "furnace-large", "contact", "large-contact", "separated", "cold-flame",
            "quenched", "water-cooling", "dry-cooling"];
        if (scenes.Any(scene => !supported.Contains(scene)))
            throw new ArgumentException($"Unknown gunpowder scenario: {selection}");
        var serializer=new SimulationStateSerializer();
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        foreach(int fps in matrix ? new[]{30,60,100}:new[]{60})
        foreach(string scene in scenes)
        {
            bool contact = scene is "contact" or "large-contact" or "separated" or "cold-flame" or "quenched";
            bool waterCooling = scene is "water-cooling" or "dry-cooling";
            var settings=new SimulationSettings { Mode=mode, Paused=true, SolidGravity=false, OpenBoundaries=true };
            bool furnace=scene.StartsWith("furnace",StringComparison.Ordinal);
            LoadedSimulationScene? saved=furnace ? System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(
                @"F:\GitHub\Phyxel\artifacts\furnace-user-20261002\original\scene.json",registry)).GetAwaiter().GetResult():null;
            if(furnace) settings.ApplyScale(.35f);
            coordinator.ClearCurrentWorld(settings);
            var r=coordinator.DispatchFrame(settings,[new() { X=40,EndX=40,Y=40,EndY=40,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }],0);
            var grid=furnace ? MemoryMarshal.Cast<byte,GridCell>(saved!.World!.Grid).ToArray():new GridCell[r.Width*r.Height];
            uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal), powder=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
            // Keep enclosure comparisons sealed for the whole observation:
            // a one-cell metal wall can melt under powder's released heat.
            if(scene=="closed" || scene=="vented") metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
            void Wall(int x,int y) { grid[y*r.Width+x]=new() { IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=20,RestFrames=2 }; }
            if(!furnace)
            {
                for(int x=60;x<=(scene=="separated"?260:160);x++) Wall(x,200);
                if(scene=="closed" || scene=="vented")
                {
                    for(int y=156;y<=200;y++) { Wall(60,y);Wall(160,y); }
                    for(int x=60;x<=160;x++) if(scene=="closed" || x<100 || x>120) Wall(x,156);
                }
            }
            if(scene=="furnace-sealed") for(int y=330;y<=347;y++) for(int x=473;x<=493;x++) Wall(x,y);
            serializer.ApplyWorldSnapshot(r,new(r.Width,r.Height,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,false,false,true);
            settings.Paused=false;
            int warm=furnace?30:0;
            for(int frame=0;frame<warm*fps;frame++)
            {
                BrushDrawCommand[] commands=frame>=fps && frame<6*fps ? [new() { X=443,EndX=443,Y=338,EndY=338,
                    Radius=6,Density=.82f,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),Mode=BrushCommandMode.Material,Seed=73001 }]:[];
                coordinator.DispatchFrame(settings,commands,1f/fps);
            }
            grid=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
            int added=0;
            if(scene!="furnace-control")
            {
                bool large=scene=="furnace-large";
                int edge=scene=="large-contact"?32:large?16:8,x0=furnace?(large?432:416):100;
                int y0=furnace?263:scene is "large-contact" or "separated" or "cold-flame" or "quenched" or "water-cooling" or "dry-cooling"?200-edge:188;
                for(int y=y0;y<y0+edge;y++) for(int x=x0;x<x0+edge;x++)
                {
                    int i=y*r.Width+x;
                    if(grid[i].IsActive!=0 && registry[grid[i].MaterialIndex].Properties.SimulationKind!=(uint)MaterialSimulationKind.Gas)
                        throw new InvalidOperationException("Powder protocol overlaps existing solid/liquid geometry");
                    grid[i]=new() { IsActive=1,MaterialIndex=powder,Mass=1,Temperature=waterCooling?240:scene=="cold"||contact?20:300 };
                    added++;
                }
                if(contact)
                {
                    // One ordinary flame beside the bottom of a cold heap;
                    // no repeated brush and no initial pre-heating of fuel.
                    grid[(y0+edge-1)*r.Width+x0+edge]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
                        Mass=1,Temperature=scene=="cold-flame"?20:420,Lifetime=2.8f };
                }
                if(scene=="separated")
                {
                    // Independent cold sample: eight-cell walls and an air
                    // gap prevent direct flame contact; the wall can conduct.
                    metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
                    for(int y=168;y<200;y++) for(int x=180;x<220;x++)
                        if(x<188 || x>=212 || y<176) Wall(x,y);
                    for(int y=188;y<196;y++) for(int x=196;x<204;x++)
                        grid[y*r.Width+x]=new() { IsActive=1,MaterialIndex=powder,Mass=1,Temperature=20 };
                    added+=64;
                }
                if(waterCooling)
                {
                    metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
                    for(int y=170;y<200;y++) { Wall(90,y);Wall(119,y); }
                    if(scene=="water-cooling")
                        for(int y=170;y<200;y++) for(int x=91;x<119;x++)
                            if(grid[y*r.Width+x].IsActive==0)
                                grid[y*r.Width+x]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),Mass=1,Temperature=20 };
                }
                serializer.ApplyWorldSnapshot(r,new(r.Width,r.Height,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray(),
                    Air:AirInventoryRegressionVerifier.Read(r,r.Air.Buffer),GasMotion:AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer),
                    Oxidizer:AirInventoryRegressionVerifier.Read(r,r.Oxidizer.ReadBuffer),AirThermal:AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer)));
                coordinator.RestoreWorldActivity(r,true,false,false,true);
            }
            // Give the no-powder control the same scheduler origin too.
            coordinator.RestoreWorldActivity(r,true,false,false,true);
            var rows=new List<string> { "seconds,powderMass,fireCells,pressurePeak,speedPeak,pipeUpSpeed,pipeFire,heatStock,pulsePressure,pulseSpeed,pulsePressureStock,pipeHeatFlux,outsidePulse,powderMeanTemperature,isolatedPowderMass" };
            int duration=scene=="cold"?10:18;
            for(int frame=0;frame<=duration*fps;frame++)
            {
                BrushDrawCommand[] cooling=scene=="quenched" && frame>=fps/10 ? [new() {
                    X=r.Width/2,EndX=r.Width/2,Y=r.Height/2,EndY=r.Height/2,Radius=r.Width,
                    Mode=BrushCommandMode.SetTemperature,TargetTemperature=20,Density=1 }]:[];
                if(frame>0) coordinator.DispatchFrame(settings,cooling,1f/fps);
                if(frame%(fps/10)!=0 && frame!=duration*fps) continue;
                var cells=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer));
                var air=MemoryMarshal.Cast<byte,AirCell>(AirInventoryRegressionVerifier.Read(r,r.Air.Buffer));
                var heat=MemoryMarshal.Cast<byte,System.Numerics.Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer));
                var pulse=MemoryMarshal.Cast<byte,System.Numerics.Vector4>(AirInventoryRegressionVerifier.Read(r,r.ReactionPulse.ReadBuffer));
                double mass=0,powderEnergy=0,isolatedMass=0,peakP=0,peakV=0,pipe=0,energy=0;int flames=0,pipeCount=0,pipeFire=0;
                double pp=0,pv=0,ps=0,flux=0,outside=0;
                for(int i=0;i<cells.Length;i++) if(cells[i].IsActive!=0)
                {
                    if(cells[i].MaterialIndex==powder)
                    {
                        mass+=cells[i].Mass;powderEnergy+=cells[i].Mass*cells[i].Temperature;
                        int x=i%r.Width;if(x>=188&&x<212) isolatedMass+=cells[i].Mass;
                    }
                    if(cells[i].MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire))
                    { flames++;int x=i%r.Width,y=i/r.Width;if(furnace && x>=154 && x<=184 && y>=20 && y<=150) pipeFire++; }
                    if(!float.IsFinite(cells[i].Temperature) || !float.IsFinite(cells[i].Mass)) throw new InvalidOperationException("Invalid particle state");
                }
                for(int i=0;i<air.Length;i++)
                {
                    if(!float.IsFinite(air[i].Pressure)||!float.IsFinite(air[i].VelocityX)||!float.IsFinite(air[i].VelocityY)) throw new InvalidOperationException("Invalid air state");
                    peakP=Math.Max(peakP,Math.Abs(air[i].Pressure));peakV=Math.Max(peakV,Math.Sqrt(air[i].VelocityX*air[i].VelocityX+air[i].VelocityY*air[i].VelocityY));
                    int x=i%r.AirWidth*4+2,y=i/r.AirWidth*4+2;
                    if(furnace && x>=154&&x<=184&&y>=40&&y<=150&&air[i].Blocked<.5) { pipe+=Math.Max(0,-air[i].VelocityY);pipeCount++; }
                    if(furnace && x>=154&&x<=184&&y>=40&&y<=150&&air[i].Blocked<.5)
                        flux+=Math.Max(0,heat[i].X-293.15*heat[i].Y)*Math.Max(0,-air[i].VelocityY);
                    energy+=heat[i].X-293.15*heat[i].Y;
                    pp=Math.Max(pp,Math.Abs(pulse[i].X));pv=Math.Max(pv,Math.Sqrt(pulse[i].Y*pulse[i].Y+pulse[i].Z*pulse[i].Z));ps+=Math.Abs(pulse[i].X);
                    if(scene=="closed" && (x<=60||x>=160||y<=156||y>=200)) outside+=Math.Abs(pulse[i].X);
                }
                rows.Add(FormattableString.Invariant($"{frame/(double)fps:F4},{mass:F6},{flames},{peakP:F6},{peakV:F6},{pipe/Math.Max(1,pipeCount):F6},{pipeFire},{energy:F6},{pp:F6},{pv:F6},{ps:F6},{flux/Math.Max(1,pipeCount):F6},{outside:F6},{powderEnergy/Math.Max(mass,1e-10):F6},{isolatedMass:F6}"));
                if(scene=="closed" && outside>1e-5) throw new InvalidOperationException("Reaction pressure crossed a sealed enclosure");
                if(scene=="cold" && (Math.Abs(mass-added)>.001 || flames!=0 || peakP>.001)) throw new InvalidOperationException("Cold powder reacted");
                if(frame==fps || frame==3*fps || frame==8*fps)
                    SimulationScreenshotWriter.Save(r,Path.Combine(directory,$"{mode}-{scene}-{fps}-{frame/fps}s.png"));
            }
            string name=$"{mode}-{scene}-{fps}";
            File.WriteAllLines(Path.Combine(directory,name+".csv"),rows);
            Console.WriteLine($"PHYXEL_GUNPOWDER_CASE {name} added={added}");
        }
        Console.WriteLine("PHYXEL_GUNPOWDER_SUCCESS");
    }
}
