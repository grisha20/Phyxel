using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class LiquidTemperatureRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/liquid-temperature";
        Directory.CreateDirectory(dir);
        bool baseline = Environment.GetEnvironmentVariable("PHYXEL_LIQUID_TEMPERATURE_BASELINE") == "1";
        var settings = new SimulationSettings { Paused = true, AirSimulation = false, OpenBoundaries = false };
        var r = coordinator.DispatchFrame(settings, [new() { X = 20, EndX = 20, Y = 20, EndY = 20,
            Radius = 1, Density = 1, Mode = BrushCommandMode.Material,
            MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water) }], 0);
        int w = r.Width, n = w * r.Height, checks = 0, failures = 0;
        double maximumEnergyResidual = 0;
        uint fixture = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var table = registry.CreateGpuTable();
        var physicalTable = table.ToArray();
        // Isolate parcel transport from conduction and external heating.
        foreach (int i in Enumerable.Range(0, table.Length)) table[i].ThermalConductivity = 0;
        var serializer = new SimulationStateSerializer();
        void Check(bool condition, string name) { checks++; if (!condition) { failures++; Console.WriteLine("PHYXEL_LT_FAIL " + name); } }
        GridCell Cell(uint id, float t = 20, uint tag = 0) => new() { IsActive = 1,
            MaterialIndex = id, Mass = 1, Temperature = t, BodyId = tag, RestFrames = 60 };
        GridCell[] Read() => MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
        void Start(GridCell[] g) {
            serializer.ApplyWorldSnapshot(r, new(w, r.Height, MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r, true, true, false);
        }
        double Energy(GridCell[] g) => g.Where(c => c.IsActive != 0).Sum(c => (double)c.Mass * PhaseEnthalpy.SpecificEnergy(c, physicalTable));
        void Balance(GridCell[] a, GridCell[] b, string name) {
            maximumEnergyResidual = Math.Max(maximumEnergyResidual,Math.Abs(Energy(a)-Energy(b))/Math.Max(1,Math.Abs(Energy(a))));
            Check(Math.Abs(Energy(a) - Energy(b)) <= Math.Max(1, Math.Abs(Energy(a))) * .0001, name + " heat");
            foreach (uint id in a.Where(c => c.IsActive != 0).Select(c => c.MaterialIndex).Distinct())
                Check(Math.Abs(a.Where(c => c.IsActive != 0 && c.MaterialIndex == id).Sum(c => (double)c.Mass) -
                    b.Where(c => c.IsActive != 0 && c.MaterialIndex == id).Sum(c => (double)c.Mass)) < .0001, name + " mass " + id);
            for (int i = 0; i < n; i++) if (a[i].MaterialIndex == fixture && a[i].IsActive != 0)
                Check(b[i].MaterialIndex == fixture && b[i].IsActive != 0, name + " wall " + i);
        }
        var mixMetrics = new Dictionary<string, object>();
        foreach (string material in new[] { CoreMaterialIds.Water, CoreMaterialIds.Oil }) {
            uint id = registry.GetRequiredRuntimeIndex(material);
            GridCell[] Basin(int kind) {
                var g = new GridCell[n];
                for (int y = 80; y <= 129; y++) for (int x = 80; x <= 185; x++) {
                    bool wall = x == 80 || x == 185 || y == 80 || y == 129 || (kind == 3 && x == 132);
                    bool hot = kind == 0 ? x < 97 && y >= 124 : kind == 1 ? y < 90 : kind == 3 && x < 132;
                    g[y*w+x] = wall ? Cell(fixture) : Cell(id, kind == 2 ? 50 : hot ? 95 : 20, (uint)(y*w+x+1));
                }
                return g;
            }
            byte[]? fpsReference = null;
            foreach (int fps in new[] { 30, 60, 100 }) {
                r.Materials.Upload(r.Context, table);
                var before = Basin(0); Start(before);
                uint tick = 0;
                for (int frame = 1; frame <= fps * 30; frame++) {
                    uint target = (uint)Math.Floor(frame * 20.0 / fps + 1e-8);
                    while (tick < target) coordinator.DispatchThermalDiffusion(r, false, ++tick, true);
                }
                var after = Read(); Balance(before, after, material + " mix " + fps);
                byte[] raw = MemoryMarshal.AsBytes(after.AsSpan()).ToArray();
                if (fpsReference is null) fpsReference = raw;
                else Check(raw.AsSpan().SequenceEqual(fpsReference), material + " fixed ticks FPS " + fps);
                double Mean(int left, int right) => Enumerable.Range(0,n).Where(i => i/w>=81 && i/w<91 && i%w>=left && i%w<right)
                    .Average(i => (double)after[i].Temperature);
                double left = Mean(81,133), right = Mean(133,185), upper = (left+right)/2;
                mixMetrics[material+"-"+fps] = new { upper, contrast = Math.Abs(left-right), left, right };
                if(!baseline)Check(material==CoreMaterialIds.Water
                    ? upper>=25.759 && Math.Abs(left-right)<=11.5384615385*.75
                    : upper>=20.25 && Math.Abs(left-right)<=1,material+" LT02 "+fps);
                File.WriteAllBytes(Path.Combine(dir,$"mix-{material.Split(':')[1]}-{fps}.grid"),raw);
                Console.WriteLine($"PHYXEL_LT_MIX material={material} fps={fps} upper={upper:F6} contrast={Math.Abs(left-right):F6}");
            }
            foreach (int kind in new[] {1,2,3}) {
                var before = Basin(kind); Start(before);
                for (uint tick = 1; tick <= 200; tick++) coordinator.DispatchThermalDiffusion(r,false,tick,true);
                var after = Read(); Balance(before,after,material+" stable "+kind);
                Check(before.Select(c=>c.BodyId).SequenceEqual(after.Select(c=>c.BodyId)),material+" stable/barrier tags "+kind);
                Check(Enumerable.Range(0,n).All(i=>Math.Abs(before[i].Temperature-after[i].Temperature)<.0001),material+" stable/barrier temperature "+kind);
            }
        }
        File.WriteAllText(Path.Combine(dir,"mix.json"),JsonSerializer.Serialize(mixMetrics,new JsonSerializerOptions{WriteIndented=true}));
        r.Materials.Upload(r.Context,physicalTable);
        var drains = new List<object>();
        var drainByCase = new Dictionary<(SimulationMode,int,float),double>();
        foreach (var mode in new[] { SimulationMode.Sandbox, SimulationMode.Simulation }) foreach(int fps in new[]{30,60,100}) {
            var efflux = new List<double>();
            foreach (float temperature in new[]{-20f,20f,100f}) {
                uint oil = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
                var before = new GridCell[n];
                for(int y=85;y<=150;y++) foreach(int x in new[]{120,180}) before[y*w+x]=Cell(fixture);
                for(int x=120;x<=180;x++) if(x<146||x>149) before[150*w+x]=Cell(fixture);
                for(int x=90;x<=210;x++) before[215*w+x]=Cell(fixture);
                for(int y=100;y<150;y++) for(int x=121;x<180;x++) before[y*w+x]=Cell(oil,temperature);
                settings.Mode=mode;settings.Paused=false; Start(before);
                for(int f=0;f<fps*2;f++) coordinator.DispatchFrame(settings,[],1f/fps);
                var after=Read();
                // Walls conduct in normal fixtures. Account for all bodies in Q audit.
                Balance(before,after,$"drain {mode} {fps} {temperature}");
                double outMass=Enumerable.Range(0,n).Where(i=>i/w>150&&after[i].IsActive!=0&&after[i].MaterialIndex==oil).Sum(i=>(double)after[i].Mass);
                efflux.Add(outMass);drains.Add(new{mode=mode.ToString(),fps,temperature,outMass});
                drainByCase[(mode,fps,temperature)]=outMass;
                Console.WriteLine($"PHYXEL_LT_DRAIN mode={mode} fps={fps} T={temperature} mass={outMass:F4}");
                File.WriteAllBytes(Path.Combine(dir,$"drain-{mode}-{fps}-{temperature}.grid"),MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
            }
            if(!baseline)Check(efflux[2]>efflux[0]*1.3&&efflux[1]>efflux[0]&&efflux[2]>efflux[1],$"temperature response {mode} {fps}");
        }
        if(!baseline)foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
            foreach(float temperature in new[]{-20f,20f,100f})foreach(int fps in new[]{30,100})
                Check(Math.Abs(drainByCase[(mode,fps,temperature)]/drainByCase[(mode,60,temperature)]-1)<=.25,$"LT01 FPS {mode} {fps} {temperature}");
        File.WriteAllText(Path.Combine(dir,"drains.json"),JsonSerializer.Serialize(drains,new JsonSerializerOptions{WriteIndented=true}));
        settings.Paused=true; var pauseBefore=Read();coordinator.DispatchFrame(settings,[],1);var pauseAfter=Read();
        Check(MemoryMarshal.AsBytes(pauseBefore.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(pauseAfter.AsSpan())),"pause");
        var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(pauseAfter.AsSpan()).ToArray());
        string savePath=Path.Combine(dir,"roundtrip.json");
        Task.Run(()=>serializer.SaveAsync(savePath,settings,registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(savePath,registry)).GetAwaiter().GetResult()!;
        Check(loaded.World!.Grid.AsSpan().SequenceEqual(snapshot.Grid),"writer14 roundtrip");
        Console.WriteLine($"PHYXEL_LT_ENERGY maximumRelativeResidual={maximumEnergyResidual:E8}");
        Console.WriteLine($"PHYXEL_LIQUID_TEMPERATURE_{(failures==0?"SUCCESS":"FAILED")} checks={checks} failures={failures} baseline={baseline}");
        if(failures!=0)throw new InvalidOperationException("Liquid temperature acceptance failed.");
    }
}
