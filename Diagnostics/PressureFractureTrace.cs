using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Graphics;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Capture the first impulse before transport or gravity can hide its origin.
// Explicit opt-in only: no readback in normal play.
internal static class PressureFractureTrace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("PHYXEL_TRACE_FRACTURE") == "1";
    private static byte[]? before;
    private static int tick, events, firstBirth;
    internal static void Before(GpuSimulationResources r)
    {
        if (!Enabled) return;
        tick++;
        before = AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer);
    }
    internal static void After(GpuSimulationResources r)
    {
        if (!Enabled || before == null) return;
        byte[] bytes = AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer);
        var old = MemoryMarshal.Cast<byte, GridCell>(before);
        var cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        var table = r.Materials.UploadedValues;
        int count = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            var c = cells[i]; var o = old[i];
            if (c.IsActive == 0 || table[(int)c.MaterialIndex].SimulationKind != 2 ||
                (c.BodyId & 0x40000000u) == 0 || (o.BodyId & 0x40000000u) != 0) continue;
            count++;
            if(firstBirth==0)firstBirth=tick;
            Console.WriteLine("PHYXEL_FRACTURE_ORIGIN " + JsonSerializer.Serialize(new {
                tick, x=i%r.Width, y=i/r.Width, material=c.MaterialIndex,
                temperature=c.Temperature, vx=c.VelocityX, vy=c.VelocityY, mass=c.Mass }));
        }
        int age=tick-firstBirth+1;
        if(firstBirth>0 && age is 1 or 4 or 7 or 13 or 31 or 61 or 121 or 181)
            for(int i=0;i<cells.Length;i++)
            {
                var c=cells[i];
                if(c.IsActive!=0 && table[(int)c.MaterialIndex].SimulationKind==2 &&
                    (c.BodyId&0x40000000u)!=0 && (c.BodyId&0x3fffffffu)==age)
                    Console.WriteLine("PHYXEL_FRAGMENT_FLIGHT "+JsonSerializer.Serialize(new{
                        tick,age,x=i%r.Width,y=i/r.Width,vx=c.VelocityX,vy=c.VelocityY,temperature=c.Temperature}));
            }
        if (count == 0 || events >= 12) return;
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/fracture-trace";
        string stamp = Path.Combine(dir, $"fracture-{++events}-{tick}");
        File.WriteAllBytes(stamp + "-before.bin", before);
        File.WriteAllBytes(stamp + "-after.bin", bytes);
        File.WriteAllBytes(stamp + "-air.bin", AirInventoryRegressionVerifier.Read(r, r.Air.Buffer));
        File.WriteAllBytes(stamp + "-pulse.bin", AirInventoryRegressionVerifier.Read(r, r.ReactionPulse.ReadBuffer));
    }
}
