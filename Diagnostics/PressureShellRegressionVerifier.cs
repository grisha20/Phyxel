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

internal static class PressureShellRegressionVerifier
{
    internal static SimulationWorldSnapshot Read(GpuSimulationResources r) => new(r.Width, r.Height,
        AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer),
        AirInventoryRegressionVerifier.Read(r, r.Air.Buffer),
        AirInventoryRegressionVerifier.Read(r, r.GasMotion.Buffer),
        AirInventoryRegressionVerifier.Read(r, r.Oxidizer.ReadBuffer),
        AirInventoryRegressionVerifier.Read(r, r.AirThermal.Buffer),
        AirInventoryRegressionVerifier.Read(r, r.ReactionPending.Buffer),
        AirInventoryRegressionVerifier.Read(r, r.ReactionPulse.ReadBuffer),
        r.FilterCount == 0 ? null : MemoryMarshal.AsBytes(r.FilterMap.AsSpan()).ToArray());

    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/pressure-shell";
        Directory.CreateDirectory(dir);
        bool baseline = Environment.GetEnvironmentVariable("PHYXEL_SHELL_BASELINE") == "1";
        bool matrix = Environment.GetEnvironmentVariable("PHYXEL_SHELL_MATRIX") == "1";
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phyxel");
        var serializer = new SimulationStateSerializer();
        var rows = new List<object>();
        int failures = 0;
        if (Environment.GetEnvironmentVariable("PHYXEL_SHELL_EDIT_PROBES") == "1")
        {
            foreach (var resource in PressureEditRegressionVerifier.Run(coordinator, registry, settings, dir))
                yield return resource;
            yield break;
        }
        if (!baseline && Environment.GetEnvironmentVariable("PHYXEL_SHELL_SKIP_PROBES") != "1")
        {
            foreach(var resource in VerifyMechanisms(coordinator,registry,settings,dir)) yield return resource;
            if(Environment.GetEnvironmentVariable("PHYXEL_SHELL_PROBES_ONLY") == "1") yield break;
        }
        foreach (string name in (Environment.GetEnvironmentVariable("PHYXEL_SHELL_SCENES") ?? "Питарда,Новая печь").Split(','))
        foreach (var mode in name == "Новая печь" && Environment.GetEnvironmentVariable("PHYXEL_SHELL_VARIANT")!="powder" ? new[] { SimulationMode.Simulation } :
            new[] { SimulationMode.Sandbox, SimulationMode.Simulation })
        foreach (int fps in (name == "Новая печь" && Environment.GetEnvironmentVariable("PHYXEL_SHELL_VARIANT")!="powder") || !matrix ? new[] { 60 } : new[] { 30, 60, 100 })
        {
            if (Environment.GetEnvironmentVariable("PHYXEL_SHELL_MODE") is { Length: > 0 } requestedMode &&
                mode.ToString() != requestedMode) continue;
            var loaded = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(Path.Combine(root, name + ".json"), registry))
                .GetAwaiter().GetResult() ?? throw new InvalidDataException("Missing saved scene: " + name);
            var world = loaded.World ?? throw new InvalidDataException("Missing world");
            // Reproduce reported edits in memory; never overwrite a user's scene.
            string variant = Environment.GetEnvironmentVariable("PHYXEL_SHELL_VARIANT") ?? "original";
            var initialCells = MemoryMarshal.Cast<byte, GridCell>(world.Grid).ToArray();
            if (name == "Питарда" && variant == "vent")
                for (int y=90;y<125;y++) for (int x=204;x<=244;x++)
                    if(initialCells[y*world.Width+x].IsActive!=0 &&
                        registry[initialCells[y*world.Width+x].MaterialIndex].Properties.SimulationKind==2)
                        initialCells[y*world.Width+x]=default;
            if (name == "Новая печь" && variant == "powder")
                for(int y=300;y<320;y++) for(int x=230;x<250;x++)
                    if(initialCells[y*world.Width+x].IsActive==0)
                        initialCells[y*world.Width+x]=new(){IsActive=1,
                            MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder),Mass=1,Temperature=251};
            world=world with {Grid=MemoryMarshal.AsBytes(initialCells.AsSpan()).ToArray()};
            SimulationStateSerializer.Apply(loaded.State, settings);
            settings.Width = world.Width; settings.Height = world.Height; settings.Mode = mode; settings.Paused = true;
            settings.AirSimulation = true; settings.RenderWithoutEffects = true;
            coordinator.ClearCurrentWorld(settings);
            // Identical render seeds in independently replayed scenes. Diagnostic only.
            typeof(SimulationDispatchCoordinator).GetField("frameIndex",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(coordinator,0u);
            typeof(SimulationDispatchCoordinator).GetField("lastObservedStatisticsFrame",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(coordinator,0u);
            // Air's heat-face phase also belongs to the replay epoch. A prior
            // FPS matrix must not choose a different phase for this scene.
            typeof(SimulationDispatchCoordinator).GetField("airTickIndex",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(coordinator,0UL);
            var r = coordinator.DispatchFrame(settings, [new() { X = 1, Y = 1, Radius = 0, Density = 1,
                MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }], 0);
            serializer.ApplyWorldSnapshot(r, world);
            if (!baseline && name == "Питарда" && !r.PressureMechanicsPotential)
                throw new InvalidDataException("Unexpected saved-world pressure activity: " + name);
            r.Materials.Upload(r.Context, registry.CreateGpuTable());
            coordinator.RestoreWorldActivity(r, true, true, settings.HydraulicPressure, true);
            using var fence = new SharpDX.Direct3D11.Query(r.Device,new SharpDX.Direct3D11.QueryDescription
                { Type=SharpDX.Direct3D11.QueryType.Event,Flags=SharpDX.Direct3D11.QueryFlags.None });
            File.WriteAllBytes(Path.Combine(dir,"materials.bin"),MemoryMarshal.AsBytes(registry.CreateGpuTable().AsSpan()).ToArray());
            settings.Paused = false;
            bool ventAcceptance=Environment.GetEnvironmentVariable("PHYXEL_VENT_ACCEPTANCE")=="1";
            int peakFragments=0;
            int final = (int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_SHELL_SECONDS"),out int seconds) ? seconds : name == "Новая печь" ? 30 : 5) * fps;
            string prefix = (name == "Новая печь" ? "furnace" : "shell") + "-" + mode + "-" + fps;
            for (int frame = 0; frame <= final; frame++)
            {
                if (frame > 0) r = coordinator.DispatchFrame(settings, [], 1f / fps);
                // An event fences every GPU command. Mapping an unrelated
                // statistics staging buffer alone does not fence the grid.
                r.Context.End(fence);r.Context.Flush();
                while(!r.Context.IsDataAvailable(fence,SharpDX.Direct3D11.AsynchronousFlags.None))
                    System.Threading.Thread.Yield();
                var stats = AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer);
                coordinator.ObserveStatistics(MemoryMarshal.Read<SimulationStatistics>(stats));
                if(ventAcceptance)
                {
                    var observed=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
                    int count=0;foreach(var c in observed)
                        if(c.IsActive!=0 && registry[c.MaterialIndex].Properties.SimulationKind==2 && (c.BodyId&0x40000000u)!=0)count++;
                    peakFragments=Math.Max(peakFragments,count);
                }
                if (frame == 0 || (Environment.GetEnvironmentVariable("PHYXEL_SHELL_FINE_SAMPLES") == "1" && frame <= 6) ||
                    frame == fps / 2 || frame == fps || frame == 2 * fps || frame == final)
                {
                    var snapshot = Read(r);
                    var cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid).ToArray();
                    uint powder = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
                    var grains = cells.Where(c => c.IsActive != 0 && c.MaterialIndex == powder).ToArray();
                    var air = MemoryMarshal.Cast<byte, AirCell>(snapshot.Air!).ToArray();
                    var pulse = MemoryMarshal.Cast<byte, System.Numerics.Vector4>(snapshot.ReactionPulse!).ToArray();
                    int fragments = cells.Count(c => c.IsActive != 0 && registry[c.MaterialIndex].Properties.SimulationKind == 2 && (c.BodyId & 0x40000000u) != 0);
                    int coolFragments = cells.Count(c => c.IsActive != 0 && registry[c.MaterialIndex].Properties.SimulationKind == 2 &&
                        (c.BodyId & 0x40000000u) != 0 && c.Temperature < registry[c.MaterialIndex].Properties.TransitionAboveTemperature);
                    int outside = cells.Select((c,i)=>(c,i)).Count(v=>v.c.IsActive!=0 &&
                        registry[v.c.MaterialIndex].Properties.SimulationKind==2 && (v.c.BodyId&0x40000000u)!=0 &&
                        (v.i%r.Width<174 || v.i%r.Width>274 || v.i/r.Width<95 || v.i/r.Width>195));
                    double lowerPowder = cells.Select((c,i)=>(c,i)).Where(v=>v.c.IsActive!=0 && v.c.MaterialIndex==powder &&
                        v.i/r.Width>=145).Sum(v=>(double)v.c.Mass);
                    var row = new { scene = name, variant, mode = mode.ToString(), fps, frame,
                        powder = grains.Sum(c => (double)c.Mass), lowerPowder, fragments, coolFragments, outside,
                        carrierMax=air.Max(a=>a.Pressure), waveMax=pulse.Max(a=>a.X),
                        wallMaxTemperature=cells.Where(c=>c.IsActive!=0 && registry[c.MaterialIndex].Properties.SimulationKind==2)
                            .Select(c=>c.Temperature).DefaultIfEmpty().Max(),
                        solidMass = cells.Where(c => c.IsActive != 0 && registry[c.MaterialIndex].Properties.SimulationKind == 2)
                            .Sum(c => (double)c.Mass), ticks = coordinator.ThermalTicks };
                    rows.Add(row); Console.WriteLine("PHYXEL_SHELL_SAMPLE " + JsonSerializer.Serialize(row));
                    string stamp = prefix + "-" + frame;
                    File.WriteAllBytes(Path.Combine(dir, stamp + "-grid.bin"), snapshot.Grid);
                    File.WriteAllBytes(Path.Combine(dir, stamp + "-air.bin"), snapshot.Air!);
                    File.WriteAllBytes(Path.Combine(dir, stamp + "-motion.bin"), snapshot.GasMotion!);
                    File.WriteAllBytes(Path.Combine(dir, stamp + "-pulse.bin"), snapshot.ReactionPulse!);
                    SimulationScreenshotWriter.Save(r, Path.Combine(dir, stamp + ".png"));
                    if(!baseline && !ventAcceptance && name=="Питарда" && frame==fps && (coolFragments==0 || outside==0 || lowerPowder>=1650))
                    {failures++;Console.WriteLine("PHYXEL_SHELL_SCENE_FAILED cold/outward/lower-front " + prefix);}
                    if(!baseline && name=="Питарда" && frame==2*fps && grains.Sum(c=>(double)c.Mass)>65.12)
                    {failures++;Console.WriteLine("PHYXEL_SHELL_SCENE_FAILED burnout " + prefix);}
                    if (!baseline && name == "Новая печь" && frame == final &&
                        Environment.GetEnvironmentVariable("PHYXEL_SHELL_COMPARE_BASELINE") is { Length: > 0 } before)
                    {
                        foreach (var (field, bytes) in new[] { ("grid", snapshot.Grid), ("air", snapshot.Air!), ("motion", snapshot.GasMotion!) })
                        {
                            bool equal = File.ReadAllBytes(Path.Combine(before, stamp + "-" + field + ".bin")).AsSpan().SequenceEqual(bytes);
                            Console.WriteLine($"PHYXEL_SHELL_FURNACE {field} equal={equal}");
                            if (!equal) failures++;
                        }
                    }
                }
                if (frame % fps == 0) yield return r;
            }
            if(ventAcceptance)
            {
                bool open=name=="Питарда" && variant=="vent" || name=="Новая печь";
                bool pass=open?peakFragments==0:peakFragments>0;
                Console.WriteLine("PHYXEL_VENT_ACCEPTANCE "+JsonSerializer.Serialize(new{scene=name,variant,mode=mode.ToString(),fps,peakFragments,pass}));
                if(!pass)failures++;
            }
            if(name=="Питарда")
            {
                settings.Paused=true;settings.RenderWithoutEffects=false;
                r=coordinator.DispatchFrame(settings,[],0);
                SimulationScreenshotWriter.Save(r,Path.Combine(dir,prefix+"-effects.png"));
            }
        }
        File.WriteAllText(Path.Combine(dir, "measurements.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(dir, "runtime-palette.txt"), registry.Materials.Select(m => $"{m.RuntimeIndex}:{m.Id}"));
        if (failures > 0) Environment.ExitCode = 1;
        Console.WriteLine($"PHYXEL_SHELL_COMPLETE baseline={baseline} failures={failures}");
    }

    private static IEnumerable<GpuSimulationResources> VerifyMechanisms(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings,string dir)
    {
        settings.Width=64;settings.Height=64;settings.Paused=true;settings.OpenBoundaries=false;
        coordinator.ClearCurrentWorld(settings);
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,
            MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal)}],0);
        var table=registry.CreateGpuTable();r.Materials.Upload(r.Context,table);
        uint powder=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        int checks=0;
        void Check(bool ok,string message)
        {checks++;Console.WriteLine($"PHYXEL_SHELL_CHECK pass={ok} {message}");if(!ok)throw new InvalidDataException(message);}
        GridCell[] Cells()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Upload(GridCell[] cells)
        {
            r.Context.UpdateSubresource(cells,r.Grid.ReadBuffer);r.Context.UpdateSubresource(cells,r.Grid.WriteBuffer);
        }
        GridCell Grain(float t)=>new(){MaterialIndex=powder,IsActive=1,Mass=1,Temperature=t};
        var constants=new SimulationFrameConstants{Width=64,Height=64,Gravity=0,DeltaTime=1f/60};
        var grid=new GridCell[4096];
        for(int y=24;y<=40;y++)for(int x=24;x<=40;x++)grid[y*64+x]=Grain(30);
        grid[32*64+32]=Grain(1500);Upload(grid);
        double energy=grid.Sum(c=>(double)c.Mass*c.Temperature*table[c.MaterialIndex].HeatCapacity);
        for(int tick=0;tick<12;tick++)SimulationDispatchCoordinator.DispatchPowderFront(r,constants);
        var after=Cells();
        double error=after.Sum(c=>(double)c.Mass*c.Temperature*table[c.MaterialIndex].HeatCapacity)-energy;
        Check(Math.Abs(error)<energy*.0001,"PF01 front energy error="+error);
        Check(after[32*64+31].Temperature==after[32*64+33].Temperature &&
            Math.Abs(after[31*64+32].Temperature-after[33*64+32].Temperature)<.001 &&
            Math.Abs(after[32*64+31].Temperature-after[31*64+32].Temperature)<.001,"PF01 four directions symmetric");
        Check(after[32*64+31].Temperature>30,"PF01 adjacent grain receives heat");
        foreach(bool wet in new[]{false,true})
        {
            grid=new GridCell[4096];grid[32*64+32]=Grain(wet?1500:30);
            grid[32*64+33]=Grain(30);if(wet)grid[32*64+33].MoistureMass=.1f;
            Upload(grid);SimulationDispatchCoordinator.DispatchPowderFront(r,constants);
            Check(Cells()[32*64+33].Temperature==30,"PF02 "+(wet?"wet":"cold")+" no chain");
        }
        grid=new GridCell[4096];grid[32*64+31]=Grain(1500);grid[32*64+33]=Grain(30);
        grid[32*64+32]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=30};
        Upload(grid);SimulationDispatchCoordinator.DispatchPowderFront(r,constants);
        Check(Cells()[32*64+33].Temperature==30,"PF02 metal barrier blocks front");
        // A thin plate has a strong wave on its left face. Uniform pressure,
        // ordinary carrier pressure and fixed support must not break it.
        GridCell[] Plate(uint material)
        {
            var g=new GridCell[4096];for(int y=16;y<48;y++)g[y*64+32]=new(){
                MaterialIndex=material,IsActive=1,Mass=table[material].Density,Temperature=30,BodyId=1,Lifetime=7};return g;
        }
        void Wave(float left,float right,float carrier=0)
        {
            var wave=new System.Numerics.Vector4[r.AirWidth*r.AirHeight];var air=new AirCell[wave.Length];
            for(int y=0;y<r.AirHeight;y++)for(int x=0;x<r.AirWidth;x++)
            {wave[y*r.AirWidth+x].X=x<8?left:right;air[y*r.AirWidth+x].Pressure=wave[y*r.AirWidth+x].X+(x<8?carrier:0);}
            r.Context.UpdateSubresource(wave,r.ReactionPulse.ReadBuffer);r.Context.UpdateSubresource(air,r.Air.Buffer);
        }
        int Count(GridCell[] cells)=>cells.Count(c=>c.IsActive!=0 && table[c.MaterialIndex].SimulationKind==2 && (c.BodyId&0x40000000u)!=0);
        foreach(var load in new[]{(0f,0f,5f),(5f,0f,0f),(10f,0f,0f),(100f,100f,0f)})
        {
            var unchanged=Plate(metal);Upload(unchanged);Wave(load.Item1,load.Item2,load.Item3);
            SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
            Check(Count(Cells())==0,"PF04 normal/subthreshold/uniform pressure "+load);
            Check(MemoryMarshal.AsBytes(unchanged.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Cells().AsSpan())),"PF07 no-op grid "+load);
        }
        Upload(Plate(metal));Wave(18,0);
        var poreAir=MemoryMarshal.Cast<byte,AirCell>(AirInventoryRegressionVerifier.Read(r,r.Air.Buffer)).ToArray();
        for(int i=0;i<poreAir.Length;i++){poreAir[i].Blocked=1;poreAir[i].Pressure=0;}
        r.Context.UpdateSubresource(poreAir,r.Air.Buffer);
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,true,true);
        Check(Count(Cells())>0,"PV01 pore pressure works without a carrier overlay");
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        Upload(Plate(fixture));Wave(100,0);SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())==0,"PF04 fixed support");
        var strength=table[metal].PressureStrength;table[metal].PressureStrength=0;r.Materials.Upload(r.Context,table);
        Upload(Plate(metal));Wave(100,0);SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())==0,"PF04 zero strength disables fracture");
        table[metal].PressureStrength=strength;r.Materials.Upload(r.Context,table);
        for(int y=0;y<64;y++)r.FilterMap[y*64+31]=1u<<19;
        r.FilterCount=64;r.UploadFilters();Upload(Plate(metal));Wave(100,0);
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())==0,"PF04 closed filter blocks pressure probe");
        Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
        uint steel=registry.GetRequiredRuntimeIndex("core:steel");
        uint copper=registry.GetRequiredRuntimeIndex("core:copper");
        uint castIron=registry.GetRequiredRuntimeIndex("core:cast_iron");
        uint stone=registry.GetRequiredRuntimeIndex("core:stone");
        foreach(uint material in new[]{metal,steel,copper,castIron,stone})
        {
            var plate=Plate(material);Upload(plate);Wave(100,0);
            SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
            var broken=Cells();
            Check(Count(broken)>0 && broken.Where(c=>c.IsActive!=0).All(c=>c.Pressure==0),"PB01 immediate loose fragments "+registry[material].Id);
            Check(broken.Where(c=>c.IsActive!=0).All(c=>c.MaterialIndex==material && c.Temperature==30 && c.Lifetime==7) &&
                Math.Abs(broken.Sum(c=>(double)c.Mass)-plate.Sum(c=>(double)c.Mass))<.0001,"PB01 no removed mass/ID/heat "+registry[material].Id);
        }
        foreach(uint material in new[]{metal,steel})
        {
            Upload(Plate(material));Wave(18,0);SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
            Check((Count(Cells())>0)==(material==metal),"PB01 stronger steel "+registry[material].Id);
        }
        Upload(Plate(stone));Wave(19,0);SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())==0,"PB02 stone without melting transition retains strength");
        var thick=new GridCell[4096];var thin=Plate(metal);
        // A full-height wall isolates thickness from exposed top/bottom corners.
        for(int y=0;y<64;y++)for(int x=32;x<36;x++)thick[y*64+x]=thin[32*64+32];
        Upload(thick);Wave(25,0);SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())==0,"PB02 thick plate holds same load");
        Upload(thin);Wave(25,0);SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())>0,"PB02 thin plate bursts");
        var hot=Plate(steel);for(int i=0;i<hot.Length;i++)if(hot[i].IsActive!=0)hot[i].Temperature=1400;
        Upload(hot);Wave(18,0);SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())>0,"PB02 hot plate weaker than cold steel");
        Upload(Plate(metal));Wave(0,0,100);var pressured=Read(r);
        var serializerProbe=new SimulationStateSerializer();serializerProbe.ApplyWorldSnapshot(r,pressured);
        Check(r.PressureMechanicsPotential,"PB02 saved carrier pressure activates mechanics without powder/wave");
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        Check(Count(Cells())>0,"PB02 carrier pressure alone bursts plate");
        // Uniformly pressurized raster ring, not a chemistry-dependent shape.
        var ring=new GridCell[4096];
        for(int y=0;y<64;y++)for(int x=0;x<64;x++)
        {
            int radius=(x-32)*(x-32)+(y-32)*(y-32);
            if(radius>=144 && radius<=196)ring[y*64+x]=new(){MaterialIndex=metal,IsActive=1,Mass=7.8f,Temperature=30,Lifetime=7};
        }
        Upload(ring);var uniform=new System.Numerics.Vector4[r.AirWidth*r.AirHeight];
        for(int y=0;y<r.AirHeight;y++)for(int x=0;x<r.AirWidth;x++)
            if(Math.Pow(x*4+2-32,2)+Math.Pow(y*4+2-32,2)<144)uniform[y*r.AirWidth+x].X=200;
        r.Context.UpdateSubresource(uniform,r.ReactionPulse.ReadBuffer);
        r.Context.UpdateSubresource(uniform.Select(v=>new AirCell{Pressure=v.X}).ToArray(),r.Air.Buffer);
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);
        var sectors=new List<double>[8];for(int i=0;i<8;i++)sectors[i]=[];
        foreach(var pair in Cells().Select((c,i)=>(c,i)).Where(v=>(v.c.BodyId&0x40000000u)!=0))
        {
            double x=pair.i%64-32,y=pair.i/64-32;
            int sector=(int)Math.Floor((Math.Atan2(y,x)+Math.PI)*4/Math.PI)%8;
            sectors[sector].Add((x*pair.c.VelocityX+y*pair.c.VelocityY)/Math.Sqrt(x*x+y*y));
        }
        Check(sectors.All(v=>v.Count>0 && v.Average()>0),"PB03 eight sectors eject outward");
        double ratio=sectors.Max(v=>v.Average())/sectors.Min(v=>v.Average());
        Check(ratio<=2,"PB03 sector speed ratio="+ratio);
        Check(Math.Abs(Cells().Sum(c=>(double)c.Mass)-ring.Sum(c=>(double)c.Mass))<.0001,"PB03 ring mass conserved");
        // A gas packet is displaced, never treated as a disposable empty cell.
        uint smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        var swap=new GridCell[4096];swap[32*64+20]=new(){MaterialIndex=metal,IsActive=1,Mass=7.8f,Temperature=600,Lifetime=11,
            BodyId=0x40000000u,VelocityX=120};
        swap[32*64+22]=new(){MaterialIndex=smoke,IsActive=1,Mass=.03f,Temperature=300,Lifetime=5};
        var motion=new GasMotionState[4096];motion[32*64+22]=new(){VelocityX=3.5f,VelocityY=-2,OffsetX=.125f,OffsetY=-.125f};
        Upload(swap);Wave(0,0);r.Context.UpdateSubresource(motion,r.GasMotion.Buffer);
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);
        var swapped=Cells();var swappedMotion=MemoryMarshal.Cast<byte,GasMotionState>(Read(r).GasMotion!).ToArray();
        Check(swapped[32*64+22].MaterialIndex==metal && swapped[32*64+20].MaterialIndex==smoke &&
            MemoryMarshal.AsBytes(swapped.AsSpan(32*64+20,1)).SequenceEqual(MemoryMarshal.AsBytes(swap.AsSpan(32*64+22,1))),"PB04 full gas packet swapped");
        Check(MemoryMarshal.AsBytes(swappedMotion.AsSpan(32*64+20,1)).SequenceEqual(MemoryMarshal.AsBytes(motion.AsSpan(32*64+22,1))) &&
            swappedMotion[32*64+22].VelocityX==0,"PB04 gas motion travels with packet");
        swap[32*64+24]=swap[32*64+20];swap[32*64+24].VelocityX=-120;
        Upload(swap);r.Context.UpdateSubresource(motion,r.GasMotion.Buffer);
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);
        Check(Cells().Count(c=>c.IsActive!=0 && c.MaterialIndex==metal)==2 && Cells().Count(c=>c.IsActive!=0 && c.MaterialIndex==smoke)==1 &&
            Math.Abs(Cells().Sum(c=>(double)c.Mass)-swap.Sum(c=>(double)c.Mass))<.0001,"PB04 competing fragments preserve gas and mass");
        var heap=new GridCell[4096];heap[32*64+32]=new(){MaterialIndex=metal,IsActive=1,Mass=7.8f,
            Temperature=600,Lifetime=11,BodyId=0x40000000u};
        heap[33*64+32]=new(){MaterialIndex=fixture,IsActive=1,Mass=1,Temperature=30};
        Upload(heap);Wave(0,0);constants.Gravity=980;
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);
        var settled=Cells();
        Check(settled.Select((c,i)=>(c,i)).Any(v=>v.c.MaterialIndex==metal && v.c.IsActive!=0 &&
            v.i/64==33 && Math.Abs(v.i%64-32)==1) && settled.Count(c=>c.IsActive!=0)==2 &&
            Math.Abs(settled.Sum(c=>(double)c.Mass)-8.8)<.0001,"PB04 loose grain rolls around support without losing packets");
        constants.Gravity=0;
        // Retire old PM damage without moving the intact plate.
        var legacy=Plate(steel);for(int i=0;i<legacy.Length;i++)if(legacy[i].IsActive!=0){legacy[i].Pressure=-.5f;legacy[i].VelocityX=60;}
        Upload(legacy);Wave(0,0);var legacySave=Read(r);serializerProbe.ApplyWorldSnapshot(r,legacySave);
        Check(r.PressureMechanicsPotential,"PB04 PM damage save remains readable");
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);
        Check(Cells().Where(c=>c.IsActive!=0).All(c=>c.Pressure==0 && c.VelocityX==0) && Count(Cells())==0 &&
            Cells().Count(c=>c.IsActive!=0)==32,"PB04 PM damage retired without fragmenting/deforming");
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(bool gravity in new[]{false,true})
        {
            var plate=Plate(steel);for(int i=0;i<plate.Length;i++)plate[i].Lifetime=0;
            Upload(plate);Wave(100,0);var livePlate=Read(r);
            settings.Mode=mode;settings.SolidGravity=gravity;settings.Gravity=0;settings.AirSimulation=true;settings.Paused=false;
            serializerProbe.ApplyWorldSnapshot(r,livePlate);coordinator.RestoreWorldActivity(r,true,false,false,true);
            for(int frame=0;frame<3;frame++)r=coordinator.DispatchFrame(settings,[],1f/60);
            var liveCells=Cells();
            Check(liveCells.Count(c=>c.IsActive!=0)==32 && Math.Abs(liveCells.Sum(c=>(double)c.Mass)-plate.Sum(c=>(double)c.Mass))<.0001 &&
                liveCells.Where(c=>c.IsActive!=0).All(c=>c.MaterialIndex==steel && float.IsFinite(c.Temperature)),
                "PB04 production packets mode="+mode+" gravity="+gravity);
            Check(Count(liveCells)>0,"PB04 production direct rupture mode="+mode+" gravity="+gravity);
        }
        settings.SolidGravity=false;settings.AirSimulation=false;settings.Paused=true;
        grid=Plate(metal);Upload(grid);Wave(100,0);
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,true);after=Cells();
        Check(Count(after)>0,"PF03 cold metal detaches");
        Check(after.Where(c=>c.IsActive!=0).All(c=>c.MaterialIndex==metal && c.Temperature==30 && c.Lifetime==7),"PF05 material/temperature/enthalpy retained");
        double mass=grid.Sum(c=>(double)c.Mass);
        for(uint tick=1;tick<=90;tick++){constants.DebugReserved2=tick;SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);}
        after=Cells();Check(Math.Abs(after.Sum(c=>(double)c.Mass)-mass)<.0001,"PF05 fragment mass conserved");
        Check(after.Select((c,i)=>(c,i)).Any(p=>p.c.IsActive!=0 && p.i%64>35),"PF03 outward movement");
        // Replay identical fixed steps across render groupings. Physics sees
        // sixty ticks regardless of how frames are grouped.
        byte[]? reference=null;
        foreach(bool finite in new[]{false,true})foreach(int fps in new[]{30,60,100})
        {
            var sample=new GridCell[4096];sample[32*64+16]=new(){MaterialIndex=metal,IsActive=1,Mass=7.8f,
                Temperature=600,Lifetime=11,BodyId=0x40000000u,VelocityX=180};Upload(sample);
            for(int y=0;y<64;y++)r.FilterMap[y*64+40]=1u<<19;
            r.FilterCount=64;r.UploadFilters();
            double accumulated=0;uint tick=0;
            for(int frame=0;frame<fps;frame++)
            {
                accumulated+=1d/fps;
                while(accumulated+1e-6>=SimulationDispatchCoordinator.FixedAirStep)
                {accumulated-=SimulationDispatchCoordinator.FixedAirStep;constants.DebugReserved2=++tick;
                    SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,finite,false);}
            }
            var result=Cells();var bytes=MemoryMarshal.AsBytes(result.AsSpan()).ToArray();reference??=bytes;
            Check(tick==60 && reference.AsSpan().SequenceEqual(bytes),"PB04 fixed fast fragment "+fps+" FPS finite="+finite);
            Check(result.Where(c=>c.IsActive!=0).Sum(c=>c.Mass)==7.8f &&
                !result.Select((c,i)=>(c,i)).Any(v=>v.c.IsActive!=0&&v.i%64>=40),"PB04 fast filter collision/mass "+fps+" finite="+finite);
            Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
        }
        Upload(after);
        // Serialize the actual fragment state and compare the next physical tick.
        var serializer=new SimulationStateSerializer();var snapshot=Read(r);
        string path=Path.Combine(dir,"fragment-roundtrip.json");
        System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)metal,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        Check(loaded.World!.Grid.AsSpan().SequenceEqual(snapshot.Grid) && loaded.World.GasMotion!.AsSpan().SequenceEqual(snapshot.GasMotion),"PB04 save/load fragment and gas motion bytes");
        constants.DebugReserved2=91;SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);var next=Cells();var nextMotion=Read(r).GasMotion!;
        serializer.ApplyWorldSnapshot(r,loaded.World);
        Check(r.PressureMechanicsPotential,"PF05 reload enables fragment mechanics without powder");
        SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);
        Check(MemoryMarshal.AsBytes(Cells().AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(next.AsSpan())) && nextMotion.AsSpan().SequenceEqual(Read(r).GasMotion),"PB04 identical grid/motion continuation");
        settings.AirSimulation=false;coordinator.RestoreWorldActivity(r,true,false,false,true);settings.Paused=true;
        var paused=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);coordinator.DispatchFrame(settings,[],.2f);
        Check(paused.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)),"PF05 pause unchanged");
        // Actual production frame path, with a saved fragment's own clock.
        serializer.ApplyWorldSnapshot(r,snapshot);settings.Gravity=0;settings.Paused=false;
        coordinator.RestoreWorldActivity(r,true,false,false,true);
        r=coordinator.DispatchFrame(settings,[],1f/60);
        var liveSave=Read(r);
        r=coordinator.DispatchFrame(settings,[],1f/60);var liveNext=Cells();
        serializer.ApplyWorldSnapshot(r,liveSave);coordinator.RestoreWorldActivity(r,true,false,false,true);
        r=coordinator.DispatchFrame(settings,[],1f/60);
        Check(MemoryMarshal.AsBytes(liveNext.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Cells().AsSpan())),"PF05 production continuation with restored clock");
        foreach(bool fragment in new[]{true,false})
        {
        grid=new GridCell[4096];grid[32*64+32]=new(){MaterialIndex=metal,IsActive=1,Mass=7.8f,
            Pressure=-.5f,BodyId=fragment?0x40000040u:1u,Temperature=5000};Upload(grid);
        var phase=new PhaseTransitionConstants{Width=64,Height=64,MaterialCount=(uint)table.Length,TickCount=1};
        r.Context.UpdateSubresource(ref phase,r.PhaseConstants);
        r.Context.ComputeShader.SetConstantBuffer(0,r.PhaseConstants);r.Context.ComputeShader.SetShaderResources(0,r.Materials.View,r.ContactSummary.View);
        r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.PhaseSummary.UnorderedView);
        r.Context.ComputeShader.Set(r.PhaseTransitionShader);r.Context.Dispatch(4,4,1);
        r.Context.ComputeShader.SetUnorderedAccessViews(0,new SharpDX.Direct3D11.UnorderedAccessView[2]);
        r.Context.ComputeShader.SetShaderResources(0,new SharpDX.Direct3D11.ShaderResourceView[2]);r.Context.ComputeShader.Set(null);
        var melted=Cells()[32*64+32];
        Check(melted.MaterialIndex==registry.GetRequiredRuntimeIndex("core:molten_metal") && melted.BodyId==0 && melted.Mass==7.8f && melted.Pressure==0,
            "PB04 legacy damage cleared on melting fragment="+fragment);
        }
        Console.WriteLine($"PHYXEL_SHELL_PROBES_PASS checks={checks}");yield return r;
    }
}
