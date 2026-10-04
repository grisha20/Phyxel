using System;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class GasTileRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var r=coordinator.DispatchFrame(new SimulationSettings { Paused=true },
            [new() { X=40,EndX=40,Y=40,EndY=40,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire) }],0);
        int n=r.Width*r.Height;
        var grid=new GridCell[n]; var motion=new GasMotionState[n]; var map=new uint[n];
        string[] gases=[CoreMaterialIds.Fire,CoreMaterialIds.Smoke,CoreMaterialIds.Co2,CoreMaterialIds.Steam];
        void Put(int x,int y,string id)
        {
            int i=y*r.Width+x; var material=registry[id];
            grid[i]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(id),Mass=material.Properties.Density,
                Temperature=340,Lifetime=20 };
            map[i]=grid[i].MaterialIndex;
            motion[i]=new() { VelocityX=x%2==0?7.2f:-7.2f,VelocityY=y%2==0?7.2f:-7.2f,
                OffsetX=x%2==0?8:-8,OffsetY=y%2==0?8:-8 };
        }
        // Tile edges/corners, both world edges, an isolated gas, a dense mixed
        // plug and a roof collision. Large opposing offsets test the halo.
        foreach(int x in new[]{1,63,64,127,128,255,r.Width-2})
            foreach(int y in new[]{1,63,64,127,128,r.Height-2}) Put(x,y,gases[(x+y)%4]);
        for(int y=175;y<184;y++) for(int x=119;x<135;x++) Put(x,y,gases[(x+y)%4]);
        for(int x=115;x<139;x++) Put(x,174,CoreMaterialIds.Metal);
        var air=new AirCell[r.AirWidth*r.AirHeight];
        for(int y=0;y<r.AirHeight;y++) for(int x=0;x<r.AirWidth;x++)
            air[y*r.AirWidth+x]=new() { VelocityX=(x%7-3)*1.3f,VelocityY=(y%7-3)*1.3f };
        foreach(bool finite in new[]{false,true})
        {
            byte[][]? expected=null;
            foreach(bool full in new[]{true,false})
            {
                r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
                r.Context.UpdateSubresource(motion,r.GasMotion.Buffer);
                r.Context.UpdateSubresource(map,r.CellMaterials.Buffer);
                r.Context.UpdateSubresource(air,r.Air.Buffer);
                for(uint tick=1;tick<=16;tick++)
                {
                    var constants=new SimulationFrameConstants { Width=(uint)r.Width,Height=(uint)r.Height,
                        FrameIndex=tick,DebugReserved2=tick,DeltaTime=1f/60,MaximumVelocity=5000 };
                    coordinator.DispatchGasMotion(r,ref constants,true,finite,full);
                }
                byte[][] result=[AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer),
                    AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer),AirInventoryRegressionVerifier.Read(r,r.CellMaterials.Buffer)];
                if (expected is null) expected=result;
                else for(int i=0;i<result.Length;i++)
                    if(!result[i].AsSpan().SequenceEqual(expected[i]))
                        throw new InvalidOperationException($"Gas tile optimization changed buffer {i}, finite={finite}");
            }
        }
        Console.WriteLine("PHYXEL_GAS_TILES_SUCCESS byteExact=True modes=2 ticks=16 buffers=grid,motion,materials");
    }
}
