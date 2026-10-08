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

// Observation of the user's full saved charge, independent of realtime FPS tracing.
internal static class SavedBlastRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        var r = FurnaceSensorRegressionVerifier.LoadFixture(coordinator, registry, settings);
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/saved-blast";
        Directory.CreateDirectory(dir);
        int fps = int.Parse(Environment.GetEnvironmentVariable("PHYXEL_BLAST_FPS") ?? "60");
        uint metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        uint tnt = registry.GetRequiredRuntimeIndex("core:tnt");
        var initial = MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
        var shell = initial.Select((c,i)=>(c,i)).Where(v=>v.c.IsActive!=0 && v.c.MaterialIndex==metal).ToArray();
        double cx=shell.Average(v=>v.i%r.Width),cy=shell.Average(v=>v.i/r.Width);
        double radius=shell.Max(v=>Math.Sqrt(Math.Pow(v.i%r.Width-cx,2)+Math.Pow(v.i/r.Width-cy,2)));
        double initialMass=shell.Sum(v=>(double)v.c.Mass);
        Console.WriteLine($"PHYXEL_BLAST initial={shell.Length} center={cx},{cy} radius={radius} size={r.Width}x{r.Height}");
        var rows=new List<object>();
        for(int frame=0;frame<=fps*3;frame++)
        {
            if(frame>0)r=coordinator.DispatchFrame(settings,[],1f/fps);
            else coordinator.DispatchFrame(settings,[],0);
            if(frame%Math.Max(1,fps/20)==0)
            {
                var cells=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
                var fragments=cells.Select((c,i)=>(c,i)).Where(v=>v.c.IsActive!=0 && v.c.MaterialIndex==metal && (v.c.BodyId&0x40000000u)!=0).ToArray();
                double radial=0;int outward=0,outside=0;var sectors=new int[8];
                foreach(var v in fragments)
                {
                    double dx=v.i%r.Width-cx,dy=v.i/r.Width-cy,d=Math.Sqrt(dx*dx+dy*dy);
                    double vr=(dx*v.c.VelocityX+dy*v.c.VelocityY)/Math.Max(1,d);radial+=vr;
                    if(vr>24)outward++;
                    if(d>radius+12){outside++;sectors[(int)((Math.Atan2(dy,dx)+Math.PI)*4/Math.PI)%8]++;}
                }
                var row=new {seconds=frame/(double)fps,fragments=fragments.Length,outward,outside,sectors,
                    radialMean=radial/Math.Max(1,fragments.Length),tntMass=cells.Where(c=>c.IsActive!=0&&c.MaterialIndex==tnt).Sum(c=>(double)c.Mass),
                    shellMass=cells.Where(c=>c.IsActive!=0&&c.MaterialIndex==metal).Sum(c=>(double)c.Mass),
                    molten=cells.Count(c=>c.IsActive!=0&&registry[c.MaterialIndex].Id=="core:molten_metal")};
                rows.Add(row);Console.WriteLine("PHYXEL_BLAST_ROW "+JsonSerializer.Serialize(row));
                if(frame==fps && (outside<shell.Length*.4 || row.radialMean<45 || sectors.Any(n=>n==0)))
                    throw new InvalidOperationException("BD02 radial shell transport failed");
                if(frame==0||frame==fps/2||frame==fps||frame==fps*2||frame==fps*3)
                {
                    SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"world-{frame}.png"));
                    settings.RenderWithoutEffects=true;coordinator.DispatchFrame(settings,[],0);
                    SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"particles-{frame}.png"));
                    settings.RenderWithoutEffects=false;coordinator.DispatchFrame(settings,[],0);
                    File.WriteAllBytes(Path.Combine(dir,$"grid-{frame}.bin"),MemoryMarshal.AsBytes(cells.AsSpan()).ToArray());
                }
            }
            if(frame%4==0)yield return r;
        }
        File.WriteAllText(Path.Combine(dir,"blast.json"),JsonSerializer.Serialize(new {cx,cy,radius,initialMass,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_BLAST_COMPLETE");
    }
}
