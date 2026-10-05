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
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class LiquidLayersRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings, Action<string> status)
    {
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/liquid-layers";
        Directory.CreateDirectory(dir);
        bool baseline = Environment.GetEnvironmentVariable("PHYXEL_LL_BASELINE") == "1";
        bool matrix = Environment.GetEnvironmentVariable("PHYXEL_LL_MATRIX") == "1";
        string selected = Environment.GetEnvironmentVariable("PHYXEL_LL_CASES") ?? "bowl,layers,partition";
        var physical = registry.CreateGpuTable(); var isolated = physical.ToArray();
        for (int i = 0; i < isolated.Length; i++) isolated[i].ThermalConductivity = 0;
        uint oil = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint fixture = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        settings.Paused = true; settings.AirSimulation = false; settings.OpenBoundaries = false;
        var r = coordinator.DispatchFrame(settings, [new() { X=20, Y=20, EndX=20, EndY=20,
            Radius=1, Density=1, Mode=BrushCommandMode.Material, MaterialIndex=oil }], 0);
        yield return r;
        int w = r.Width, h = r.Height, failures = 0;
        var results = new List<object>(); var serializer = new SimulationStateSerializer();
        GridCell Cell(uint id, float t) => new() { IsActive=1, MaterialIndex=id, Mass=id==fixture?physical[id].Density:1,
            Temperature=t, RestFrames=id==fixture?2u:0u };
        GridCell[] Read() => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Mass(GridCell[] a, uint id) => a.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass);
        double Energy(GridCell[] a) => a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        // Verify creation through the real brush kernel, not only JSON fields.
        if (!baseline) {
            var expected30 = new HashSet<string>(new[]{"core:co2","core:coal","core:fixture","core:gunpowder",
                "core:metal","core:oil","core:sand","core:stone_coal","core:stone","core:water","core:wet_charcoal","core:wood"});
            foreach (string id in expected30) {
                uint runtimeId=registry.GetRequiredRuntimeIndex(id);
                coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=2,Density=1,
                    Mode=BrushCommandMode.Erase}],0);
                coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
                    Mode=BrushCommandMode.Material,MaterialIndex=runtimeId}],0);
                var created=Read().Where(c=>c.IsActive!=0&&c.MaterialIndex==runtimeId).ToArray();
                if(created.Length==0||created.Any(c=>c.Temperature!=30))throw new InvalidOperationException("Initial brush temperature "+id);
                yield return r;
            }
            var special = new Dictionary<string,float> { [CoreMaterialIds.Ice]=-5,[CoreMaterialIds.FrozenOil]=10,
                [CoreMaterialIds.Fire]=420,[CoreMaterialIds.Smoke]=340,[CoreMaterialIds.Steam]=122,
                ["core:molten_metal"]=1050,[CoreMaterialIds.OilVapour]=300,[CoreMaterialIds.Heater]=20,[CoreMaterialIds.Cooler]=20 };
            foreach(var pair in special) {
                uint runtimeId=registry.GetRequiredRuntimeIndex(pair.Key);
                coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=2,Density=1,Mode=BrushCommandMode.Erase}],0);
                coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
                    Mode=BrushCommandMode.Material,MaterialIndex=runtimeId}],0);
                var created=Read().Where(c=>c.IsActive!=0&&c.MaterialIndex==runtimeId).ToArray();
                if(created.Length==0||created.Any(c=>c.Temperature!=pair.Value))throw new InvalidOperationException("Special brush temperature "+pair.Key);
                yield return r;
            }
            Console.WriteLine("PHYXEL_LL_BRUSH ordinary=12 temperature=30 special=9 passed=True");
        }
        int Range(GridCell[] a, bool boundary) {
            var tops = new List<int>();
            for (int x=140; x<=320; x++) {
                for (int y=60; y<235; y++) if (a[y*w+x].IsActive!=0 &&
                    (boundary ? a[y*w+x].MaterialIndex==water : a[y*w+x].MaterialIndex==oil)) { tops.Add(y); break; }
            }
            return tops.Count==181?tops.Max()-tops.Min():h;
        }
        foreach (var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach (bool hydraulic in new[]{false,true})
        foreach (int fps in matrix?new[]{30,60,100}:new[]{60})
        foreach (string scene in selected.Split(','))
        foreach (float temperature in scene=="bowl"?new[]{20f,30f,40f}:scene=="layers"?new[]{20f,30f}:new[]{30f}) {
            var g = new GridCell[w*h];
            for (int x=80; x<=380; x++) {
                int bottom=235-(int)Math.Round(.006*(x-230)*(x-230));
                for(int y=bottom;y<=bottom+3;y++)g[y*w+x]=Cell(fixture,temperature);
                int top=scene=="bowl"?115+(int)Math.Round(.0014*(x-230)*(x-230)):115;
                int boundary=185-(int)Math.Round(.0025*(x-230)*(x-230));
                for(int y=top;y<bottom;y++)g[y*w+x]=Cell(scene!="bowl"&&y>=boundary?water:oil,temperature);
            }
            if(scene=="partition")for(int y=60;y<239;y++)g[y*w+230]=Cell(fixture,temperature);
            settings.Mode=mode;settings.HydraulicPressure=hydraulic;settings.Paused=true;
            serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,hydraulic);r.Materials.Upload(r.Context,isolated);settings.Paused=false;
            string label=$"{mode}-{hydraulic}-{fps}-{scene}-{temperature}";status(label);
            double LeftMass(GridCell[] a,uint id)=>Enumerable.Range(0,a.Length).Where(i=>i%w<230&&a[i].IsActive!=0&&a[i].MaterialIndex==id).Sum(i=>(double)a[i].Mass);
            var rows=new List<object>();
            for(int f=0;f<fps*30;f++) {
                coordinator.DispatchFrame(settings,[],1f/fps);
                coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
                if((f+1)%(fps*5)==0){var a=Read();rows.Add(new{seconds=(f+1)/fps,surface=Range(a,false),boundary=scene=="bowl"?0:Range(a,true)});}
                // dt and all production dispatches stay unchanged. A bounded
                // batch keeps long matrices visible without rendering every tick.
                if((f+1)%16==0)yield return r;
            }
            var end=Read();int surface=Range(end,false),interfaceRange=scene=="bowl"?0:Range(end,true);
            double oilError=Math.Abs(Mass(g,oil)-Mass(end,oil)),waterError=Math.Abs(Mass(g,water)-Mass(end,water));
            double qError=Math.Abs(Energy(g)-Energy(end))/Math.Max(1,Math.Abs(Energy(g)));
            int walls=Enumerable.Range(0,g.Length).Count(i=>g[i].IsActive!=0&&g[i].MaterialIndex==fixture&&
                (end[i].IsActive==0||end[i].MaterialIndex!=fixture));
            double partitionError=scene=="partition"?Math.Max(Math.Abs(LeftMass(g,oil)-LeftMass(end,oil)),Math.Abs(LeftMass(g,water)-LeftMass(end,water))):0;
            bool pass=(scene=="partition"||surface<=2&&interfaceRange<=2)&&oilError<1e-4&&waterError<1e-4&&qError<1e-5&&walls==0&&partitionError<1e-4;
            if(!pass)failures++;
            results.Add(new{label,surface,interfaceRange,oilError,waterError,qError,walls,partitionError,pass,rows});
            File.WriteAllBytes(Path.Combine(dir,label+".grid"),MemoryMarshal.AsBytes(end.AsSpan()).ToArray());
            SimulationScreenshotWriter.Save(r,Path.Combine(dir,label+".png"));
            Console.WriteLine($"PHYXEL_LL_CASE {label} surface={surface} interface={interfaceRange} pass={pass}");
        }
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{failures,results},new JsonSerializerOptions{WriteIndented=true}));
        if(failures>0&&!baseline)throw new InvalidOperationException("Liquid layer acceptance failed");
    }
}
