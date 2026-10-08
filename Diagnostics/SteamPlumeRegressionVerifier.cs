using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Finite puffs: no burner, continuous emission, ambient sink or forced wind.
internal static class SteamPlumeRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry, GpuSimulationResources r)
    {
        uint steam=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam), wall=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        int w=r.Width,n=w*r.Height;
        bool observe=Environment.GetEnvironmentVariable("PHYXEL_STEAM_PLUME_OBSERVE")=="1";
        var rows=new System.Collections.Generic.List<object>();
        bool allPass=true;
        foreach(bool finite in new[]{false,true}) foreach(string geometry in new[]{"open","roof","sealed"})
        {
            var grid=new GridCell[n];
            void Wall(int x,int y)=>grid[y*w+x]=new(){IsActive=1,MaterialIndex=wall,Mass=1,Temperature=122};
            if(geometry!="open")
            {
                for(int x=100;x<=300;x++)Wall(x,80);
                for(int y=80;y<=150;y++)Wall(100,y);
                if(geometry=="sealed")
                {
                    for(int y=80;y<=150;y++)Wall(300,y);
                    for(int x=100;x<=300;x++)Wall(x,150);
                }
            }
            int originY=geometry=="open"?220:110;
            for(int y=originY;y<originY+6;y++)for(int x=155;x<165;x++)
                grid[y*w+x]=new(){IsActive=1,MaterialIndex=steam,Mass=.05f,Temperature=122};
            double initialMass=grid.Where(c=>c.MaterialIndex==steam && c.IsActive!=0).Sum(c=>(double)c.Mass);
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);
            r.Context.UpdateSubresource(new GasMotionState[n],r.GasMotion.Buffer);
            r.Context.UpdateSubresource(new AirCell[r.AirWidth*r.AirHeight],r.Air.Buffer);
            for(uint tick=1;tick<=240;tick++)
            {
                var constants=new SimulationFrameConstants {Width=(uint)w,Height=(uint)r.Height,
                    FrameIndex=tick,DebugReserved2=tick,DeltaTime=1f/60,MaximumVelocity=5000};
                coordinator.DispatchGasMotion(r,ref constants,false,finite);
            }
            var result=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
            var parcels=result.Select((c,i)=>(c,i)).Where(p=>p.c.IsActive!=0 && p.c.MaterialIndex==steam).ToArray();
            double mass=parcels.Sum(p=>(double)p.c.Mass);
            double meanY=parcels.Sum(p=>(double)p.c.Mass*(p.i/w))/mass;
            double escaped=parcels.Where(p=>p.i/w<80).Sum(p=>(double)p.c.Mass)/mass;
            bool walls=grid.Select((c,i)=>(c,i)).Where(p=>p.c.IsActive!=0 && p.c.MaterialIndex==wall)
                .All(p=>result[p.i].Equals(p.c));
            bool contained=geometry!="sealed" || parcels.All(p=>p.i%w>100 && p.i%w<300 && p.i/w>80 && p.i/w<150);
            bool pass=Math.Abs(mass-initialMass)<1e-5 && walls && contained &&
                (geometry=="sealed" || (geometry=="open"?meanY<70:escaped>.9));
            var row=new{geometry,finite,seconds=4,mass,meanY,escaped,walls,contained,pass};
            allPass &= pass;
            rows.Add(row);Console.WriteLine("PHYXEL_STEAM_PLUME "+JsonSerializer.Serialize(row));
        }
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!,"plume.json"),
            JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        if(!observe && !allPass)
            throw new InvalidOperationException("Steam plume failed rise, roof outlet or sealed conservation.");
    }
}
