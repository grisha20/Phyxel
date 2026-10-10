using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

// Observation only: replay a user world and reset initial motion on a control copy.
// This is not a regression PASS or validation of physical flame attachment.
internal static class SavedFlameReplay
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string path = Environment.GetEnvironmentVariable("PHYXEL_FLAME_SCENE")
            ?? throw new InvalidOperationException("PHYXEL_FLAME_SCENE is required.");
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/saved-flame";
        Directory.CreateDirectory(dir);
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        string jsonHash = Hash(path), worldPath = Path.ChangeExtension(path, ".world"), worldHash = Hash(worldPath);
        var serializer = new SimulationStateSerializer();
        var loaded = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(path, registry)).GetAwaiter().GetResult()!;
        var source = loaded.World ?? throw new InvalidDataException("World is missing.");
        SimulationStateSerializer.Apply(loaded.State, settings);
        settings.Width = source.Width; settings.Height = source.Height; settings.Paused = true;
        var r = coordinator.DispatchFrame(settings, [new() { X = 20, Y = 20, Radius = 1, Density = 1,
            MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand) }], 0);
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        uint coal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        int fps=int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_FLAME_FPS"),out int f)?f:60;
        int seconds=int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_FLAME_SECONDS"),out int s)?s:10;
        string[] controls=(Environment.GetEnvironmentVariable("PHYXEL_FLAME_CONTROLS")??"saved,still,cold,mirror-cold").Split(',');
        string? selectedMode=Environment.GetEnvironmentVariable("PHYXEL_FLAME_MODE");
        var results = new List<object>();
        foreach (var mode in new[] { SimulationMode.Sandbox, SimulationMode.Simulation })
        foreach (string control in controls)
        {
            if(selectedMode is not null && selectedMode!=mode.ToString())continue;
            settings.Mode = mode; settings.Paused = true;
            byte[]? Mirror(byte[]? bytes)
            {
                if (bytes is null || bytes.Length == 0) return bytes;
                int count = source.Width * source.Height;
                if (bytes.Length % count != 0) throw new InvalidDataException("Expected fine-grid field.");
                int stride = bytes.Length / count;
                byte[] copy = new byte[bytes.Length];
                for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                    bytes.AsSpan((y * source.Width + x) * stride, stride).CopyTo(
                        copy.AsSpan((y * source.Width + source.Width - 1 - x) * stride, stride));
                return copy;
            }
            var world = control == "saved" ? source : source with { Air = null, GasMotion = null };
            if (control is "cold" or "mirror-cold" or "cold-hopper" or "fresh-mound")
                world = world with { AirThermal = null, Oxidizer = null, ReactionPending = null, ReactionPulse = null };
            if(control=="cold-hopper")
            {
                var coldGrid=MemoryMarshal.Cast<byte,GridCell>(source.Grid).ToArray();
                for(int i=0;i<coldGrid.Length;i++)
                {
                    // Control copy only: stored hopper stock starts cold while
                    // its original flame-heated steel wall and firebox remain.
                    if(i/source.Width<350 && coldGrid[i].MaterialIndex==coal)
                    {coldGrid[i].Temperature=30;coldGrid[i].Lifetime=0;}
                    if(i/source.Width<350 && coldGrid[i].MaterialIndex==fire)coldGrid[i]=default;
                }
                world=world with {Grid=MemoryMarshal.AsBytes(coldGrid.AsSpan()).ToArray()};
            }
            if(control=="fresh-mound")
            {
                var fresh=MemoryMarshal.Cast<byte,GridCell>(source.Grid).ToArray();
                for(int i=0;i<fresh.Length;i++)
                {
                    if(fresh[i].MaterialIndex==coal){fresh[i].Temperature=30;fresh[i].Lifetime=0;}
                    else if(fresh[i].IsActive!=0&&registry[fresh[i].MaterialIndex].Properties.SimulationKind==(uint)MaterialSimulationKind.Gas)fresh[i]=default;
                }
                world=world with {Grid=MemoryMarshal.AsBytes(fresh.AsSpan()).ToArray()};
            }
            if (control == "mirror-cold")
                world = world with { Grid = Mirror(source.Grid)!, Filters = Mirror(source.Filters) };
            serializer.ApplyWorldSnapshot(r, world);
            coordinator.RestoreWorldActivity(r, true, true, settings.HydraulicPressure, true);
            coordinator.RenderDiagnosticSnapshot(r,settings);
            settings.Paused = false;
            string label = $"{mode}-{control}";
            var rows = new List<object>();
            for (int frame = 0; frame <= seconds*fps; frame++)
            {
                if (frame > 0) coordinator.DispatchFrame(settings,
                    control=="fresh-mound"&&frame>=fps&&frame<fps*1.5
                        ? [new(){X=18,Y=143,EndX=18,EndY=143,Radius=4,Density=1,MaterialIndex=fire,Seed=73001}]
                        : [],1f/fps);
                if (frame % fps == 0)
                {
                    var grid = MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
                    var air = MemoryMarshal.Cast<byte, AirCell>(AirInventoryRegressionVerifier.Read(r, r.Air.Buffer)).ToArray();
                    var motion = MemoryMarshal.Cast<byte, GasMotionState>(AirInventoryRegressionVerifier.Read(r, r.GasMotion.Buffer)).ToArray();
                    var heat = MemoryMarshal.Cast<byte, System.Numerics.Vector2>(AirInventoryRegressionVerifier.Read(r, r.AirThermal.Buffer)).ToArray();
                    var oxygen = MemoryMarshal.Cast<byte, float>(AirInventoryRegressionVerifier.Read(r, r.Oxidizer.ReadBuffer)).ToArray();
                    object Section(string name, int x0, int y0, int x1, int y1)
                    {
                        double vx=0,vy=0,e=0,c=0,o=0; int count=0;
                        for(int y=y0/4;y<=y1/4;y++)for(int x=x0/4;x<=x1/4;x++)
                        {
                            int i=y*r.AirWidth+x;
                            if(air[i].Blocked>.5f)continue;
                            vx+=air[i].VelocityX;vy+=air[i].VelocityY;e+=heat[i].X;c+=heat[i].Y;count++;
                        }
                        int fineCount=0;
                        for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++){o+=oxygen[y*r.Width+x];fineCount++;}
                        return new{name,vx=vx/Math.Max(1,count),vy=vy/Math.Max(1,count),
                            temperature=c>0?(double?)(e/c-273.15):null,capacity=c,oxygen=o/Math.Max(1,fineCount)};
                    }
                    var indices = Enumerable.Range(0, grid.Length).Where(i => grid[i].IsActive != 0 && grid[i].MaterialIndex == fire).ToArray();
                    double Mean(Func<int, double> f) => indices.Length > 0 ? indices.Average(f) : 0;
                    var stocks=Enumerable.Range(0,grid.Length).Where(i=>grid[i].IsActive!=0&&grid[i].MaterialIndex==coal).ToArray();
                    object Stock(Func<int,bool> region){var cells=stocks.Where(region).ToArray();return new {
                        mass=cells.Sum(i=>(double)grid[i].Mass),count=cells.Length,
                        burning=cells.Count(i=>grid[i].Lifetime>0),temperature=cells.Length>0?cells.Average(i=>(double)grid[i].Temperature):0,
                        maxTemperature=cells.Length>0?cells.Max(i=>grid[i].Temperature):0};}
                    var row = new { seconds = frame / fps, fireCount = indices.Length,
                        fireX = Mean(i => i % r.Width), fireY = Mean(i => i / r.Width),
                        fireVx = Mean(i => motion[i].VelocityX), fireVy = Mean(i => motion[i].VelocityY),
                        fireAirVx = Mean(i => air[(i / r.Width / 4) * r.AirWidth + i % r.Width / 4].VelocityX),
                        fireAirVy = Mean(i => air[(i / r.Width / 4) * r.AirWidth + i % r.Width / 4].VelocityY),
                        hopper=Stock(i=>i/r.Width<350),firebox=Stock(i=>i/r.Width>=350&&i%r.Width<400),
                        exterior=Stock(i=>i%r.Width>=500),
                        // Observation coordinates belong only to the HC user fixture.
                        // Never used to control production physics or smaller fixtures.
                        sections=r.Width==968&&r.Height==564?new[]{
                            Section("inlet",170,500,200,510),Section("underGrate",200,520,340,540),
                            Section("firebox",280,430,355,475),Section("turn",278,375,307,405),
                            Section("horizontal",145,338,255,355),Section("chimney",102,100,124,300),
                            Section("mouth",100,35,126,58)}:Array.Empty<object>(),
                        oilStock = grid.Where(c => c.IsActive != 0).Sum(c => (double)c.FuelMass),
                        airMaxSpeed = air.Max(a => Math.Sqrt(a.VelocityX * a.VelocityX + a.VelocityY * a.VelocityY)) };
                    rows.Add(row);
                    Console.WriteLine("PHYXEL_FLAME_ROW " + label + " " + JsonSerializer.Serialize(row));
                    if (frame==0 || frame==3*fps || frame==10*fps || frame% (30*fps)==0 || frame==seconds*fps)
                    {
                        SimulationScreenshotWriter.Save(r, Path.Combine(dir, $"{label}-{frame / fps}.png"));
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / fps}-grid.bin"), MemoryMarshal.AsBytes(grid.AsSpan()).ToArray());
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / fps}-air.bin"), MemoryMarshal.AsBytes(air.AsSpan()).ToArray());
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / fps}-motion.bin"), MemoryMarshal.AsBytes(motion.AsSpan()).ToArray());
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / fps}-heat.bin"),AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer));
                        File.WriteAllBytes(Path.Combine(dir, $"{label}-{frame / fps}-oxygen.bin"),AirInventoryRegressionVerifier.Read(r,r.Oxidizer.ReadBuffer));
                    }
                }
                if (frame % 4 == 0) yield return r;
            }
            results.Add(new { label, rows });
        }
        if (jsonHash != Hash(path) || worldHash != Hash(worldPath))
            throw new InvalidOperationException("Source scene changed during observation.");
        File.WriteAllText(Path.Combine(dir, "measurements.json"), JsonSerializer.Serialize(
            new { jsonHash, worldHash, source.Width, source.Height, fps, seconds, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PHYXEL_FLAME_OBSERVATION_COMPLETE sourceUnchanged=true");
    }
}
