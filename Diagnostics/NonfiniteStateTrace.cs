using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using Phyxel.Graphics;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Opt-in expensive diagnosis only; never polls the grid during normal play.
internal static class NonfiniteStateTrace
{
    private static readonly bool Enabled=Environment.GetEnvironmentVariable("PHYXEL_TRACE_NONFINITE")=="1";
    private static byte[]? previous;
    private static readonly string[] SurfaceFrames=
        (Environment.GetEnvironmentVariable("PHYXEL_TRACE_SURFACE_FRAMES")??string.Empty).Split(',');
    internal static void Surface(GpuSimulationResources resources,uint frame,uint phase)
    {
        if(SurfaceFrames.Length==1 && SurfaceFrames[0].Length==0)return;
        if(!SurfaceFrames.Contains(frame.ToString()))return;
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/surface-trace";
        File.WriteAllBytes(Path.Combine(dir,$"surface-{frame}-{phase}.bin"),AirInventoryRegressionVerifier.Read(resources,resources.Grid.ReadBuffer));
        File.WriteAllBytes(Path.Combine(dir,$"columns-{frame}-{phase}.bin"),AirInventoryRegressionVerifier.Read(resources,resources.BodyFlags.Buffer));
    }
    internal static void Observe(GpuSimulationResources resources, uint frame, string stage)
    {
        if(!Enabled)return;
        byte[] bytes=AirInventoryRegressionVerifier.Read(resources,resources.Grid.ReadBuffer);
        var cells=MemoryMarshal.Cast<byte,GridCell>(bytes);
        for(int i=0;i<cells.Length;i++)
        {
            var c=cells[i];if(c.IsActive==0)continue;
            if(float.IsFinite(c.Temperature)&&float.IsFinite(c.Mass)&&float.IsFinite(c.FuelMass))continue;
            string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/nonfinite";
            if(previous is not null)File.WriteAllBytes(Path.Combine(dir,"last-finite.bin"),previous);
            File.WriteAllBytes(Path.Combine(dir,"first-nonfinite.bin"),bytes);
            throw new InvalidOperationException($"Nonfinite state: stage={stage} frame={frame} cell={i%resources.Width},{i/resources.Width} material={c.MaterialIndex} mass={c.Mass} fuel={c.FuelMass} T={c.Temperature}");
        }
        previous=bytes;
    }
}
