using System;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Isolate motion from reaction/lifetime. Reports real displacement over four
// gas seconds, not a render-frame speed or a copy of the velocity formula.
internal static class TransientPaceRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var r=coordinator.DispatchFrame(new SimulationSettings { Paused=true },
            [new() { X=40,EndX=40,Y=40,EndY=40,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire) }],0);
        var rows=new System.Collections.Generic.List<string> { "mode,material,count,meanRise,meanVy" };
        foreach(bool finite in new[]{false,true}) foreach(string id in new[]{CoreMaterialIds.Fire,CoreMaterialIds.Smoke})
        {
            var grid=new GridCell[r.Width*r.Height]; var map=new uint[grid.Length];
            uint material=registry.GetRequiredRuntimeIndex(id); int count=0, sourceY=200;
            for(int x=40;x<r.Width-40;x+=8)
            {
                int i=sourceY*r.Width+x; count++;
                grid[i]=new() { IsActive=1,MaterialIndex=material,Mass=1,Temperature=340,Lifetime=20 };
                map[i]=material;
            }
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(map,r.CellMaterials.Buffer);
            r.Context.UpdateSubresource(new AirCell[r.AirWidth*r.AirHeight],r.Air.Buffer);
            r.Context.UpdateSubresource(new GasMotionState[grid.Length],r.GasMotion.Buffer);
            for(uint tick=1;tick<=240;tick++)
            {
                var constants=new SimulationFrameConstants { Width=(uint)r.Width,Height=(uint)r.Height,
                    FrameIndex=tick,DebugReserved2=tick,DeltaTime=1f/60,MaximumVelocity=5000 };
                coordinator.DispatchGasMotion(r,ref constants,true,finite);
            }
            var cells=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer));
            var motion=MemoryMarshal.Cast<byte,GasMotionState>(AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer));
            int found=0; double ySum=0,vy=0,mass=0;
            for(int i=0;i<cells.Length;i++) if(cells[i].IsActive!=0 && cells[i].MaterialIndex==material)
            { found++; mass+=cells[i].Mass; ySum+=i/r.Width; vy+=motion[i].VelocityY; }
            if(found!=count || Math.Abs(mass-count)>1e-5) throw new InvalidOperationException("Pace probe lost particles");
            // Recorded pre-change rise was 37.00 / 37.26 pixels. A modest
            // 10..25% increase is the gameplay acceptance band, not an SI law.
            double rise=sourceY-ySum/count;
            if(rise<40.986 || rise>46.25) throw new InvalidOperationException("Transient pace left the agreed modest increase band");
            string row=FormattableString.Invariant($"{(finite?"Simulation":"Sandbox")},{id},{count},{sourceY-ySum/count:F6},{vy/count:F6}");
            rows.Add(row); Console.WriteLine("PHYXEL_TRANSIENT_PACE "+row);
        }
        string? directory=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR");
        if(!string.IsNullOrWhiteSpace(directory)) { Directory.CreateDirectory(directory); File.WriteAllLines(Path.Combine(directory,"pace.csv"),rows); }
        Console.WriteLine("PHYXEL_TRANSIENT_PACE_SUCCESS");
    }
}
