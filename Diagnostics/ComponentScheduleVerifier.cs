using System;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Exact control-flow check; the reference retains all 24 direct iterations.
internal static class ComponentScheduleVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, GpuSimulationResources r, MaterialRegistry registry)
    {
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint ice=registry.GetRequiredRuntimeIndex("core:ice");
        uint wood=registry.GetRequiredRuntimeIndex("core:wood");
        uint steam=registry.GetRequiredRuntimeIndex("core:steam");
        uint steel=registry.GetRequiredRuntimeIndex("core:steel");
        uint support=registry.GetRequiredRuntimeIndex("core:fixture");
        int checks=0;
        for(int scenario=0;scenario<11;scenario++) for(int parity=0;parity<2;parity++)
        {
            var grid=new GridCell[r.Width*r.Height];
            var origins=new uint[grid.Length];
            for(int i=0;i<grid.Length;i++)
            {
                int x=i%r.Width,y=i/r.Width;
                uint material=scenario switch
                {
                    0=>0, 1=>water, 2=>steam, 3=>steel,
                    4=>x<60&&y>30&&y<80?ice:water,
                    5=>x%49<12&&y%37<13?ice:0,
                    6=>x==y || x==y+1?ice:water,
                    7=>ice,
                    9=>support,
                    10=>wood,
                    _=>i==grid.Length-1?ice:water
                };
                grid[i]=new GridCell { MaterialIndex=material, IsActive=material==0?0u:1u,
                    Mass=material==0?0:1, Temperature=20+(i%23), VelocityX=i%7, VelocityY=-(i%5),
                    RestFrames=(uint)(i%4), BodyId=scenario==7?0x40000001u:
                        scenario==6?17u:0u };
                origins[i]=(uint)(i%31);
            }
            var constants=new SimulationFrameConstants { Width=(uint)r.Width,Height=(uint)r.Height,FrameIndex=(uint)parity };
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(origins,r.SolidOrigins.ReadBuffer);
            coordinator.DispatchComponentLabeling(r,ref constants,reference:true);
            var expectedGrid=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);
            var expectedParents=AirInventoryRegressionVerifier.Read(r,r.ComponentParents.Buffer);
            var expectedOrigins=AirInventoryRegressionVerifier.Read(r,r.SolidOrigins.ReadBuffer);
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(origins,r.SolidOrigins.ReadBuffer);
            coordinator.DispatchComponentLabeling(r,ref constants);
            if(!expectedGrid.SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)) ||
               !expectedParents.SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.ComponentParents.Buffer)) ||
               !expectedOrigins.SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.SolidOrigins.ReadBuffer)))
                throw new InvalidOperationException($"Component indirect mismatch scenario={scenario} parity={parity}");
            var schedule=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.ComponentSchedule.Buffer));
            bool active=scenario is 3 or 4 or 5 or 6 or 8;
            if(schedule[0]!=(active?(uint)((r.Width+15)/16):0u) ||
               schedule[3]!=(active?(uint)((grid.Length+255)/256):0u))
                throw new InvalidOperationException($"Component schedule scenario={scenario} parity={parity} actual={schedule[0]},{schedule[3]} active={active}.");
            checks+=4;
        }
        Console.WriteLine($"PHYXEL_COMPONENT_SCHEDULE_SUCCESS checks={checks}");
    }
}
