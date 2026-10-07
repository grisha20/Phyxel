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

namespace Phyxel.Diagnostics;

internal static class PressureEditRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings, string dir)
    {
        var rows = new List<object>(); int failures = 0;
        foreach (var mode in new[] { SimulationMode.Simulation, SimulationMode.Sandbox })
        foreach (bool edited in new[] {false,true})
        foreach(int thickness in new[]{1,2,6})
        {
            const int size=128;
            settings.Width=size; settings.Height=size; settings.Mode=mode; settings.Paused=true;
            settings.SolidGravity=false; settings.Gravity=980; settings.AirSimulation=true;
            settings.HydraulicPressure=false; settings.OpenBoundaries=false;
            coordinator.ClearCurrentWorld(settings);
            uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
            var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=metal}],0);
            var table=registry.CreateGpuTable(); r.Materials.Upload(r.Context,table);
            var grid=new GridCell[size*size];
            for(int y=62;y<62+thickness;y++)for(int x=32;x<96;x++)grid[y*size+x]=new(){
                MaterialIndex=metal,IsActive=1,Mass=table[metal].Density,Temperature=30,BodyId=1};
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer); r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            coordinator.RestoreWorldActivity(r,true,false,false,true);
            coordinator.DispatchAirSimulation(r,1,mode==SimulationMode.Sandbox,false);
            var air=MemoryMarshal.Cast<byte,AirCell>(AirInventoryRegressionVerifier.Read(r,r.Air.Buffer)).ToArray();
            for(int i=0;i<air.Length;i++){air[i].Pressure=air[i].Blocked>.5f?0:80;air[i].VelocityX=air[i].VelocityY=0;}
            r.Context.UpdateSubresource(air,r.Air.Buffer);
            r.PressureMechanicsPotential=true;
            SimulationDispatchCoordinator.DispatchPressureFragments(r,new(){Width=size,Height=size,Gravity=980},true,true);
            GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
            int Count()=>Read().Count(c=>c.IsActive!=0 && table[c.MaterialIndex].SimulationKind==2 && (c.BodyId&0x40000000u)!=0);
            int initial=Count();
            // Genuine brush operation through uniformly pressurized air: no physical load across the plate.
            if(edited)r=coordinator.DispatchFrame(settings,[new(){X=64,Y=62,Radius=4,Density=1,
                MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Eraser)}],0);
            settings.Paused=false; int peak=0;
            for(int frame=1;frame<=12;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/60);
                peak=Math.Max(peak,Count());
            }
            File.WriteAllBytes(Path.Combine(dir,$"edit-{mode}-{edited}-{thickness}-grid.bin"),AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer));
            File.WriteAllBytes(Path.Combine(dir,$"edit-{mode}-{edited}-{thickness}-air.bin"),AirInventoryRegressionVerifier.Read(r,r.Air.Buffer));
            // Positive control: the same readback must detect a genuine load.
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            coordinator.DispatchAirSimulation(r,99,mode==SimulationMode.Sandbox,false);
            air=MemoryMarshal.Cast<byte,AirCell>(AirInventoryRegressionVerifier.Read(r,r.Air.Buffer)).ToArray();
            for(int i=0;i<air.Length;i++){air[i].Pressure=air[i].Blocked>.5f?0:i/r.AirWidth*4+2>=62+thickness?128:0;
                air[i].VelocityX=air[i].VelocityY=0;}
            r.Context.UpdateSubresource(air,r.Air.Buffer);
            SimulationDispatchCoordinator.DispatchPressureFragments(r,new(){Width=size,Height=size,Gravity=980},true,true);
            int loaded=Count();
            var row=new{mode=mode.ToString(),edited,thickness,initial,peak,loaded,pass=initial==0&&peak==0&&loaded>0};rows.Add(row);
            Console.WriteLine("PHYXEL_PRESSURE_EDIT "+JsonSerializer.Serialize(row));
            if(initial!=0||peak!=0||loaded==0)failures++;
            yield return r;
        }
        File.WriteAllText(Path.Combine(dir,"edit-measurements.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        if(failures>0)Environment.ExitCode=1;
        Console.WriteLine($"PHYXEL_PRESSURE_EDIT_COMPLETE failures={failures}");
    }
}
