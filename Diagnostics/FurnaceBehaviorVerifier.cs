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
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

// Actual reaction/phase/transport observations supplement the isolated heat budget audit.
// No FIRE brush is applied to a sample, and disappearing/moving packets are not called reactions.
internal static class FurnaceBehaviorVerifier
{
    static GridCell[] Read(GpuSimulationResources r)=>MemoryMarshal.Cast<byte,GridCell>(
        AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
    static bool Reacting(GridCell cell,MaterialDefinition definition)
    {
        if(definition.Combustion==null)return false;
        var m=definition.Properties;
        if((m.Flags&(uint)(MaterialFlags.ProgressiveIgnition|MaterialFlags.PersistentCoalIgnition))!=0)
            return cell.Lifetime>0;
        return (cell.BodyId&CombustionRuntime.FuelBurningMarker)!=0||
            m.SimulationKind==(uint)MaterialSimulationKind.Solid&&cell.Mass<m.Density-.0001f;
    }
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings,string dir,string mode)
    {
        if(mode=="contact")
        {
            foreach(var item in Contact(coordinator,registry,settings,dir))yield return item;
            yield break;
        }
        if(mode=="chamber")
        {
            foreach(var item in Chamber(coordinator,registry,settings,dir))yield return item;
            yield break;
        }
        var r=FurnaceSensorRegressionVerifier.LoadFixture(coordinator,registry,settings);
        settings.Paused=false;
        string scene=Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SCENE")!;
        string world=Path.ChangeExtension(scene,".world");
        string jsonHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scene)));
        string worldHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(world)));
        for(int frame=0;frame<60*60;frame++)
        {coordinator.DispatchFrame(settings,[],1f/60);if(frame%6==0)yield return r;}
        var serializer=new SimulationStateSerializer();
        serializer.BeginWorldCapture(r);SimulationWorldSnapshot? snapshot;
        while(!serializer.TryCompleteWorldCapture(r,out snapshot))System.Threading.Thread.Yield();
        File.WriteAllBytes(Path.Combine(dir,"warm-grid.bin"),snapshot!.Grid);
        SimulationScreenshotWriter.Save(r,Path.Combine(dir,"warm.png"));
        var rows=new List<object>();
        string? only=Environment.GetEnvironmentVariable("PHYXEL_FH_ONLY");
        foreach(var definition in registry.Materials)
        {
            if(only!=null&&definition.Id!=only)continue;
            if(definition.Properties.SimulationKind is (uint)MaterialSimulationKind.None or (uint)MaterialSimulationKind.Tool)
            {rows.Add(new{id=definition.Id,status="N/A",reason="not a particle"});continue;}
            settings.Paused=true;coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,
                MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal)}],0);
            serializer.ApplyWorldSnapshot(r,snapshot);
            coordinator.RestoreWorldActivity(r,true,true,settings.HydraulicPressure,true);
            // The only edit to the diagnostic copy is the sample; all furnace fields are restored.
            uint id=definition.RuntimeIndex;
            var sample=new List<int>();var grid=Read(r);var table=registry.CreateGpuTable();
            bool cord=definition.Id==CoreMaterialIds.Fuse;
            for(int y=345;y<=(cord?345:347);y++)for(int x=445;x<=(cord?476:447);x++)
            {
                int i=y*r.Width+x;
                if(grid[i].IsActive!=0&&table[grid[i].MaterialIndex].SimulationKind!=(uint)MaterialSimulationKind.Gas)
                    throw new InvalidOperationException("Furnace sample intersects a wall");
                float mass=definition.Properties.SimulationKind==(uint)MaterialSimulationKind.Solid?definition.Properties.Density:1;
                grid[i]=new(){IsActive=1,MaterialIndex=id,Temperature=definition.Properties.InitialTemperature,
                    Mass=mass,RestFrames=2,Lifetime=definition.Properties.MaximumLifetime>0?definition.Properties.MaximumLifetime:0,
                    DeviceTargetTemperature=definition.Properties.ThermalDeviceTargetTemperature,
                    DeviceMaximumPower=definition.Properties.ThermalDeviceMaximumPower};
                sample.Add(i);
            }
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,settings.HydraulicPressure,true);
            settings.Paused=false;
            var observations=new List<object>();double firstReaction=-1,firstChange=-1;
            for(int frame=0;frame<=60*15;frame++)
            {
                if(frame>0)coordinator.DispatchFrame(settings,[],1f/60);
                if(frame%6==0)
                {
                    var after=Read(r);var cells=sample.Select(i=>after[i]).ToArray();
                    var original=cells.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).ToArray();
                    double seconds=frame/60.0;
                    // Fixed solid progressive reaction is unambiguous; granular/liquid packet
                    // identity is not tracked, so their mass leaving the patch is only transport.
                    if(firstReaction<0&&original.Any(c=>Reacting(c,definition)))firstReaction=seconds;
                    if(firstChange<0&&cells.Any(c=>c.IsActive!=0&&c.MaterialIndex!=id))firstChange=seconds;
                    if(after.Any(c=>c.IsActive!=0&&(!float.IsFinite(c.Temperature)||!float.IsFinite(c.Mass)||c.Mass<=0)))
                        throw new InvalidOperationException("Invalid state in furnace "+definition.Id);
                    var histogram=cells.Where(c=>c.IsActive!=0).GroupBy(c=>c.MaterialIndex)
                        .ToDictionary(g=>registry[(ushort)g.Key].Id,g=>g.Count());
                    observations.Add(new{seconds,remaining=original.Length,mass=original.Sum(c=>(double)c.Mass),
                        minTemperature=original.Length==0?(float?)null:original.Min(c=>c.Temperature),
                        maxTemperature=original.Length==0?(float?)null:original.Max(c=>c.Temperature),
                        meanTemperature=original.Length==0?(double?)null:original.Average(c=>(double)c.Temperature),
                        reacting=original.Count(c=>Reacting(c,definition)),
                        phaseProgress=definition.PhaseTransitions==null?0:original.Sum(c=>(double)c.PhaseProgress),histogram});
                    if(frame is 0 or 60 or 180 or 900)
                        SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{definition.Id[5..]}-{frame/60}.png"));
                }
                if(frame%6==0)yield return r;
            }
            var row=new{id=definition.Id,firstReaction,firstChange,observations};rows.Add(row);
            File.WriteAllText(Path.Combine(dir,definition.Id[5..]+".json"),JsonSerializer.Serialize(row,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PHYXEL_FH_BEHAVIOR_ROW {definition.Id} firstReaction={firstReaction} firstPatchChange={firstChange}");
        }
        if(jsonHash!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scene)))||worldHash!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(world))))
            throw new InvalidOperationException("User save changed");
        File.WriteAllText(Path.Combine(dir,"behavior.json"),JsonSerializer.Serialize(new{jsonHash,worldHash,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_FH_BEHAVIOR_COMPLETE");
    }

    static IEnumerable<GpuSimulationResources> Chamber(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings,string dir)
    {
        settings.Width=96;settings.Height=96;settings.Paused=true;settings.AirSimulation=true;
        settings.OpenBoundaries=false;settings.Mode=SimulationMode.Simulation;
        settings.SolidGravity=false;settings.HydraulicPressure=false;settings.PressureDestruction=false;
        uint heater=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Heater),fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var table=registry.CreateGpuTable();var rows=new List<object>();
        string? only=Environment.GetEnvironmentVariable("PHYXEL_FH_ONLY");
        foreach(var definition in registry.Materials)
        {
            if(only!=null&&definition.Id!=only)continue;
            if(definition.Properties.SimulationKind is (uint)MaterialSimulationKind.None or (uint)MaterialSimulationKind.Tool)
            {rows.Add(new{id=definition.Id,status="N/A"});continue;}
            settings.Paused=true;coordinator.ClearCurrentWorld(settings);
            var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=fixture}],0);
            GridCell Cell(uint id,float temperature,float mass)=>new(){IsActive=1,MaterialIndex=id,Temperature=temperature,Mass=mass,RestFrames=2};
            var grid=new GridCell[96*96];
            for(int x=30;x<=65;x++)
            {
                grid[80*96+x]=Cell(heater,2000,table[heater].Density);
                grid[80*96+x].DeviceTargetTemperature=2000;
                grid[80*96+x].DeviceMaximumPower=table[heater].ThermalDeviceMaximumPower;
            }
            for(int y=28;y<80;y++)foreach(int x in new[]{29,66})grid[y*96+x]=Cell(fixture,30,table[fixture].Density);
            uint id=definition.RuntimeIndex;var m=definition.Properties;
            double initialSampleMass=0;
            for(int y=75;y<=77;y++)for(int x=47;x<=49;x++)
            {
                float mass=m.SimulationKind==(uint)MaterialSimulationKind.Solid?m.Density:1;
                var cell=Cell(id,m.InitialTemperature,mass);
                if(definition.Id==CoreMaterialIds.WetCharcoal)cell.MoistureMass=mass*.35f;
                if(definition.Lifecycle!=null)cell.Lifetime=m.MaximumLifetime;
                if(definition.ThermalRegulator!=null)
                {cell.DeviceTargetTemperature=m.ThermalDeviceTargetTemperature;cell.DeviceMaximumPower=m.ThermalDeviceMaximumPower;}
                grid[y*96+x]=cell;
                initialSampleMass+=mass;
            }
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            settings.Paused=false;
            var trace=new List<object>();var seen=new HashSet<uint>();
            double peak=m.InitialTemperature,minimumMass=initialSampleMass;
            for(int frame=0;frame<=60*30;frame++)
            {
                if(frame>0)coordinator.DispatchFrame(settings,[],1f/60);
                if(frame%6==0)
                {
                    var after=Read(r);
                    if(after.Any(c=>c.IsActive!=0&&(!float.IsFinite(c.Temperature)||!float.IsFinite(c.Mass)||c.Mass<=0)))
                        throw new InvalidOperationException("Chamber non-finite state "+definition.Id);
                    var original=after.Where(c=>c.IsActive!=0&&c.MaterialIndex==id &&
                        // Heater/fixture samples share the chamber material: observe their original patch only.
                        (id!=heater&&id!=fixture)).ToArray();
                    if(id==heater||id==fixture)original=Enumerable.Range(75,3).SelectMany(y=>Enumerable.Range(47,3).Select(x=>after[y*96+x]))
                        .Where(c=>c.IsActive!=0&&c.MaterialIndex==id).ToArray();
                    var histogram=after.Where(c=>c.IsActive!=0&&c.MaterialIndex!=heater&&c.MaterialIndex!=fixture)
                        .GroupBy(c=>c.MaterialIndex).ToDictionary(g=>registry[(ushort)g.Key].Id,g=>g.Sum(c=>(double)c.Mass));
                    foreach(var cell in after)if(cell.IsActive!=0)seen.Add(cell.MaterialIndex);
                    if(original.Length>0)peak=Math.Max(peak,original.Max(c=>c.Temperature));
                    minimumMass=Math.Min(minimumMass,original.Sum(c=>(double)c.Mass));
                    trace.Add(new{seconds=frame/60.0,cells=original.Length,mass=original.Sum(c=>(double)c.Mass),
                        moisture=original.Sum(c=>(double)c.MoistureMass),
                        minTemperature=original.Length==0?(float?)null:original.Min(c=>c.Temperature),
                        maxTemperature=original.Length==0?(float?)null:original.Max(c=>c.Temperature),
                        phaseProgress=definition.PhaseTransitions==null?0:original.Sum(c=>(double)c.PhaseProgress),histogram});
                    if(frame is 0 or 180 or 600 or 1800)SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{definition.Id[5..]}-{frame/60}.png"));
                }
                if(frame%6==0)yield return r;
            }
            if(peak<=m.InitialTemperature+.1)throw new InvalidOperationException("No observable heating "+definition.Id);
            if(definition.Combustion!=null&&minimumMass>=initialSampleMass-.001)
                throw new InvalidOperationException("No observable fuel consumption "+definition.Id);
            if(definition.PhaseTransitions?.Above is {Temperature:<2000} hot &&
                !seen.Contains(registry.GetRequiredRuntimeIndex(hot.IntoId)))
                throw new InvalidOperationException("Supported hot phase was not observed "+definition.Id);
            if(definition.Lifecycle!=null&&minimumMass>=initialSampleMass-.001)
                throw new InvalidOperationException("Lifecycle did not advance "+definition.Id);
            File.WriteAllText(Path.Combine(dir,definition.Id[5..]+".json"),JsonSerializer.Serialize(trace,new JsonSerializerOptions{WriteIndented=true}));
            rows.Add(new{id=definition.Id,observations=trace});Console.WriteLine("PHYXEL_FH_CHAMBER_ROW "+definition.Id);
        }
        File.WriteAllText(Path.Combine(dir,"chamber.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_FH_BEHAVIOR_COMPLETE");
    }

    static IEnumerable<GpuSimulationResources> Contact(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings,string dir)
    {
        settings.Width=96;settings.Height=96;settings.Paused=true;settings.AirSimulation=false;
        uint fuse=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fuse),fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=fuse}],0);
        var table=registry.CreateGpuTable();for(int i=0;i<table.Length;i++)table[i].AmbientCoolingRate=0;
        r.Materials.Upload(r.Context,table);
        // The opt-in must change the exposed interface, not conduction inside a cord.
        int center=48*r.Width+48;
        GridCell Cell(uint id,float t)=>new(){IsActive=1,MaterialIndex=id,Mass=1,Temperature=t,Lifetime=id==fire?2:0};
        var pair=new GridCell[r.Width*r.Height];pair[center]=Cell(fuse,30);pair[center+1]=Cell(fuse,800);
        uint flags=table[fuse].Flags;table[fuse].Flags&=~(uint)MaterialFlags.ExternalSurfaceHeat;
        r.Materials.Upload(r.Context,table);r.Context.UpdateSubresource(pair,r.Grid.ReadBuffer);
        coordinator.DispatchThermalDiffusion(r,false,0,false,surfaceRadiation:false);var legacyInternal=Read(r);
        table[fuse].Flags=flags;r.Materials.Upload(r.Context,table);r.Context.UpdateSubresource(pair,r.Grid.ReadBuffer);
        coordinator.DispatchThermalDiffusion(r,false,0,false,surfaceRadiation:false);var currentInternal=Read(r);
        if(!MemoryMarshal.AsBytes(legacyInternal.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(currentInternal.AsSpan())))
            throw new InvalidOperationException("External heat flag changed internal conduction");
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);table[fixture].ThermalConductivity=0;
        r.Materials.Upload(r.Context,table);
        var corner=new GridCell[pair.Length];corner[center]=Cell(fuse,30);corner[center+r.Width+1]=Cell(fire,1100);
        corner[center+1]=Cell(fixture,30);corner[center+r.Width]=Cell(fixture,30);
        r.Context.UpdateSubresource(corner,r.Grid.ReadBuffer);
        for(uint tick=0;tick<20;tick++)coordinator.DispatchThermalDiffusion(r,false,tick,false);
        if(Math.Abs(Read(r)[center].Temperature-30)>.0001)throw new InvalidOperationException("Closed corner leaked external heat");
        var insulating=new GridCell[pair.Length];insulating[center]=Cell(fuse,30);insulating[center-1]=Cell(fire,1100);
        table[fuse].ThermalConductivity=0;r.Materials.Upload(r.Context,table);r.Context.UpdateSubresource(insulating,r.Grid.ReadBuffer);
        for(uint tick=0;tick<20;tick++)coordinator.DispatchThermalDiffusion(r,false,tick,false);
        if(Math.Abs(Read(r)[center].Temperature-30)>.0001)throw new InvalidOperationException("k0 surface leaked heat");
        table=registry.CreateGpuTable();for(int i=0;i<table.Length;i++)table[i].AmbientCoolingRate=0;
        r.Materials.Upload(r.Context,table);
        Console.WriteLine("PHYXEL_FH_CONTACT_CONTROLS internal-exact/closed-corner/k0=PASS");
        var rows=new List<object>();
        foreach(float sourceTemperature in new[]{1100f,80f})
        {
            var grid=new GridCell[r.Width*r.Height];int at=48*r.Width+48;
            grid[at]=new(){IsActive=1,MaterialIndex=fuse,Mass=1,Temperature=30,RestFrames=2};
            grid[at-1]=new(){IsActive=1,MaterialIndex=fire,Mass=1,Temperature=sourceTemperature,Lifetime=2};
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            double ignition=-1,maxHeatBudgetError=0;var trace=new List<object>();
            for(uint tick=1;tick<=180;tick++)
            {
                if(tick%3==0)
                {
                    double Energy(GridCell[] cells)=>cells.Where(cell=>cell.IsActive!=0).Sum(cell=>
                        (double)cell.Mass*PhaseEnthalpy.SpecificEnergy(cell,table));
                    double before=Energy(Read(r));
                    coordinator.DispatchThermalDiffusion(r,false,tick/3,false);
                    if(ignition<0)maxHeatBudgetError=Math.Max(maxHeatBudgetError,Math.Abs(Energy(Read(r))-before));
                }
                // Raw reaction kernel: finite FIRE packet retains contact geometry, but neither
                // source temperature nor receiver temperature is restored between heat steps.
                var c=r.Context;
                c.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
                c.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
                c.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
                var constants=new CombustionConstants{Width=(uint)r.Width,Height=(uint)r.Height,
                    MaterialCount=(uint)table.Length,DeltaTime=1f/60,TickIndex=tick,FiniteOxidizer=0};
                c.UpdateSubresource(ref constants,r.CombustionConstants);c.ComputeShader.Set(r.CombustionShader);
                c.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
                c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
                c.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                    r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
                c.Dispatch(6,6,1);for(int i=0;i<3;i++)c.ComputeShader.SetShaderResource(i,null);
                for(int i=0;i<6;i++)c.ComputeShader.SetUnorderedAccessView(i,null);
                var after=Read(r);var receiver=after[at];
                if(ignition<0&&receiver.Mass<.999)ignition=tick/60.0;
                if(tick%3==0)trace.Add(new{seconds=tick/60.0,receiver.Temperature,receiver.Mass,receiver.Lifetime,
                    sourceTemperature=after[at-1].Temperature});
                if(tick%6==0)yield return r;
            }
            if(maxHeatBudgetError>.01)throw new InvalidOperationException("Contact heat budget error");
            if((table[fuse].Flags&(uint)MaterialFlags.ExternalSurfaceHeat)!=0&&
                (sourceTemperature==1100&&(ignition<0||ignition>.5)||sourceTemperature==80&&ignition>=0))
                throw new InvalidOperationException("Cold fuse ignition contract failed");
            var row=new{sourceTemperature,ignition,maxHeatBudgetError,trace};rows.Add(row);
            Console.WriteLine("PHYXEL_FH_CONTACT "+JsonSerializer.Serialize(new{sourceTemperature,ignition}));
        }
        File.WriteAllText(Path.Combine(dir,"contact.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_FH_BEHAVIOR_COMPLETE");
    }
}
