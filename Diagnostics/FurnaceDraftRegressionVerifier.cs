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

// Saved user world, all auxiliary fields included; batched fixed-time steps
// return to Update/Draw regularly. This is measurement, not a real-time video.
internal static class FurnaceDraftRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings, Action<int> frameRate)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/furnace-draft";
        Directory.CreateDirectory(dir);
        if (Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BULK_TRACE") == "1")
        {
            BulkHeatRegressionVerifier.Run(coordinator, registry);
            Console.WriteLine("PHYXEL_DRAFT_COMPLETE");
            yield break;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_DRAFT_SURFACE_TRACE") == "1")
        {
            FurnaceSurfaceHeatVerifier.Run(coordinator, registry);
            Console.WriteLine("PHYXEL_DRAFT_COMPLETE");
            yield break;
        }
        string path=Environment.GetEnvironmentVariable("PHYXEL_DRAFT_SCENE")??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Phyxel","Начало паровая печь.json");
        if(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_EMISSION_TRACE")=="1")
        {
            // The isolated production probes do not require the user's files.
            settings.Width=672;settings.Height=394;settings.Paused=true;
            var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,Radius=1,Density=1,
                MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal)}],0);
            TraceEmissionLifetime(r,coordinator,registry,dir);
            Console.WriteLine("PHYXEL_DRAFT_COMPLETE");yield return r;yield break;
        }
        var serializer=new SimulationStateSerializer();
        var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        var world=loaded.World!;
        if(world.Width!=672||world.Height!=394)throw new InvalidDataException("Measurement regions require user's 672x394 furnace.");
        if(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_HEAT_TRACE")=="1")
        {
            SimulationStateSerializer.Apply(loaded.State,settings);settings.Width=world.Width;settings.Height=world.Height;settings.Paused=true;
            var traceResources=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,Radius=1,Density=1,
                MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal)}],0);
            TraceExteriorHeat(traceResources,coordinator,registry,serializer,world,dir);
            Console.WriteLine("PHYXEL_DRAFT_COMPLETE");
            yield return traceResources;
            yield break;
        }
        var rates=Environment.GetEnvironmentVariable("PHYXEL_DRAFT_MATRIX")=="1"?new[]{30,60,100}:new[]{60};
        var modes=Environment.GetEnvironmentVariable("PHYXEL_DRAFT_MATRIX")=="1"?
            new[]{SimulationMode.Simulation,SimulationMode.Sandbox}:new[]{SimulationMode.Simulation};
        int seconds=int.Parse(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_SECONDS")??"60");
        var rows=new List<object>();
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
            smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),co2=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Co2),
            metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),molten=registry.GetRequiredRuntimeIndex("core:molten_metal"),
            water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        bool powder=Environment.GetEnvironmentVariable("PHYXEL_DRAFT_POWDER")=="1";
        if(powder)
        {
            // A bounded, explicitly edited copy of the saved geometry. This
            // reconstructs the powder trigger; the original files stay intact.
            var cells=MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray();
            uint id=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
            for(int y=300;y<320;y++)for(int x=230;x<250;x++)
                if(cells[y*world.Width+x].IsActive==0)cells[y*world.Width+x]=new(){IsActive=1,MaterialIndex=id,Mass=1,Temperature=251};
            world=world with{Grid=MemoryMarshal.AsBytes(cells.AsSpan()).ToArray()};
        }
        int failures=0;
        foreach(var mode in modes)foreach(int fps in rates)
        {
            SimulationStateSerializer.Apply(loaded.State,settings);
            settings.Width=world.Width;settings.Height=world.Height;settings.Paused=true;settings.Mode=mode;
            var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,Radius=1,Density=1,MaterialIndex=coal}],0);
            if(rows.Count==0)
            {
                if(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_OXYGEN_TRACE")=="1")
                {
                    TraceOxygenGeometry(r,registry,world);
                    Console.WriteLine("PHYXEL_DRAFT_COMPLETE");
                    yield return r;
                    yield break;
                }
                if(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_OLD_SHADER") is {Length:>0} oldPath)
                {
                    using var oldShader=new SharpDX.Direct3D11.ComputeShader(r.Device,File.ReadAllBytes(oldPath));
                    VerifySparseRoof(r,registry,oldShader,true);
                }
                VerifySparseRoof(r,registry,r.CellularAutomataShader!,false);
                VerifyVolumeLoss(r,registry);
            }
            serializer.ApplyWorldSnapshot(r,world);coordinator.RestoreWorldActivity(r,true,true,false,true);
            settings.Paused=false;frameRate(100);
            yield return r;
            double initialMass=MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray().Where(c=>c.IsActive!=0&&c.MaterialIndex==coal).Sum(c=>(double)c.Mass);
            double initialWaterMass=MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray().Where(c=>c.IsActive!=0&&c.MaterialIndex==water).Sum(c=>(double)c.Mass);
            bool thermalBounded=true,acceptance=false;
            for(int frame=0;frame<=fps*seconds;frame++)
            {
                if(frame>0)coordinator.DispatchFrame(settings,[],1f/fps);
                if(frame%fps==0)
                {
                    var grid=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
                    var heat=MemoryMarshal.Cast<byte,System.Numerics.Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer)).ToArray();
                    var air=MemoryMarshal.Cast<byte,AirCell>(AirInventoryRegressionVerifier.Read(r,r.Air.Buffer)).ToArray();
                    double chimney=0, pipe=0, roofT=0,outerT=0,escaped=0;int cn=0,pn=0,rn=0,on=0;
                    int burning=0,flames=0,boilingCells=0;double mass=0,waterMass=0,waterHeat=0,waterLatent=0;
                    float minWater=5000,maxWater=-273.15f;
                    for(int y=0;y<r.Height;y++)for(int x=0;x<r.Width;x++)
                    {
                        var c=grid[y*r.Width+x];if(c.IsActive==0)continue;
                        if(c.MaterialIndex==coal){mass+=c.Mass;if(c.Lifetime>0)burning++;}
                        if(c.MaterialIndex==fire)flames++;
                        if(c.MaterialIndex==water)
                        {
                            waterMass+=c.Mass;waterHeat+=c.Mass*c.Temperature;
                            waterLatent+=c.Mass*Math.Max(0,c.Lifetime);
                            minWater=Math.Min(minWater,c.Temperature);maxWater=Math.Max(maxWater,c.Temperature);
                            if(c.Temperature>=99.99f&&c.Lifetime>.01f)boilingCells++;
                        }
                        if((c.MaterialIndex==smoke||c.MaterialIndex==co2)&&x<190&&y<45)escaped+=c.Mass;
                        if(c.MaterialIndex==metal||c.MaterialIndex==molten)
                        {
                            if(x>=220&&x<300&&y>=250&&y<274){roofT+=c.Temperature;rn++;}
                            if(x>=220&&x<300&&y>=365){outerT+=c.Temperature;on++;}
                        }
                    }
                    for(int y=20;y<65;y++)for(int x=39;x<46;x++)
                        if(air[y*r.AirWidth+x].Blocked<.5){chimney-=air[y*r.AirWidth+x].VelocityY;cn++;}
                    for(int y=80;y<87;y++)for(int x=125;x<131;x++)
                        if(air[y*r.AirWidth+x].Blocked<.5){pipe+=air[y*r.AirWidth+x].VelocityX;pn++;}
                    var row=new{mode=mode.ToString(),fps,seconds=frame/fps,mass,burning,flames,escaped,
                        chimney=chimney/Math.Max(1,cn),pipe=pipe/Math.Max(1,pn),roofT=roofT/Math.Max(1,rn),outerT=outerT/Math.Max(1,on),
                        waterMass,waterMean=waterHeat/Math.Max(.00001,waterMass),minWater,maxWater,waterLatent,boilingCells,
                        maxAirK=heat.Where(s=>s.Y>0).Max(s=>(double)s.X/s.Y),
                        minAirK=heat.Where(s=>s.Y>0).Min(s=>(double)s.X/s.Y)};
                    thermalBounded &= heat.All(s=>float.IsFinite(s.X)&&float.IsFinite(s.Y)&&s.X>=0&&s.Y>=0&&
                        (s.Y==0?s.X==0:float.IsFinite(s.X/s.Y)&&s.X/s.Y<=5273.3f));
                    rows.Add(row);Console.WriteLine("PHYXEL_DRAFT "+JsonSerializer.Serialize(row));
                    if(frame==fps*seconds)
                    {
                        // Original user state is used for draft acceptance.
                        // Powder variant stresses numerical heat, not this
                        // different reaction's steady chimney target.
                        acceptance=thermalBounded&&(powder||seconds<40||
                            (row.chimney>=1&&row.pipe<0&&row.roofT>110&&mass<initialMass&&escaped>0));
                        if(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BOILER")=="1")
                            acceptance &= seconds>=120&&row.waterMean>=95&&row.waterLatent>=2256&&row.waterMass<initialWaterMass;
                        if(!acceptance)failures++;
                        Console.WriteLine("PHYXEL_DRAFT_ACCEPTANCE "+JsonSerializer.Serialize(new{mode=mode.ToString(),fps,powder,acceptance,thermalBounded}));
                    }
                    if(frame% (fps*10)==0)
                    {
                        SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{mode}-{fps}-{frame/fps}.png"));
                        // The displayed field uses the real composition pass.
                        settings.ShowAirField=true;coordinator.DispatchFrame(settings,[],0);
                        SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{mode}-{fps}-{frame/fps}-air.png"));
                        settings.ShowAirField=false;
                        settings.RenderWithoutEffects=true;coordinator.DispatchFrame(settings,[],0);
                        SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{mode}-{fps}-{frame/fps}-plain.png"));
                        settings.RenderWithoutEffects=false;
                    }
                }
                if(frame%8==0)yield return r;
            }
            Console.WriteLine("PHYXEL_DRAFT_TIMING " + JsonSerializer.Serialize(new
                { mode=mode.ToString(), fps, thermal=coordinator.ThermalGpuTiming }));
            serializer.BeginWorldCapture(r);
            SimulationWorldSnapshot? snapshot;
            while(!serializer.TryCompleteWorldCapture(r,out snapshot))yield return r;
            var saving=serializer.SaveAsync(Path.Combine(dir,$"continued-{mode}-{fps}.json"),settings,(ushort)coal,snapshot!,registry);
            while(!saving.IsCompleted)yield return r;
            saving.GetAwaiter().GetResult();
            var reloading=serializer.LoadAsync(Path.Combine(dir,$"continued-{mode}-{fps}.json"),registry);
            while(!reloading.IsCompleted)yield return r;
            var roundTrip=reloading.GetAwaiter().GetResult()!.World!;
            // The existing codec canonically clears inactive cells and an
            // unused retained-liquid ID. Compare all meaningful live bytes.
            var expectedGrid=(byte[])snapshot!.Grid.Clone();
            var expectedCells=MemoryMarshal.Cast<byte,GridCell>(expectedGrid);
            for(int i=0;i<expectedCells.Length;i++)
                if(expectedCells[i].IsActive==0)expectedCells[i]=default;
                else if(expectedCells[i].FuelMass==0)expectedCells[i].RetainedLiquidMaterialIndex=0;
            foreach(var pair in new[]{("grid",snapshot!.Grid,roundTrip.Grid),
                ("heat",snapshot.AirThermal!,roundTrip.AirThermal!),
                ("air",snapshot.Air!,roundTrip.Air!),
                ("motion",snapshot.GasMotion!,roundTrip.GasMotion!),
                ("oxygen",snapshot.Oxidizer!,roundTrip.Oxidizer!),
                ("pending",snapshot.ReactionPending!,roundTrip.ReactionPending!),
                ("pulse",snapshot.ReactionPulse!,roundTrip.ReactionPulse!),
                ("filters",snapshot.Filters!,roundTrip.Filters!)})
            {
                int differences=pair.Item2.Zip(pair.Item3).Count(v=>v.First!=v.Second);
                Console.WriteLine($"PHYXEL_DRAFT_SAVE field={pair.Item1} differingBytes={differences}");
            }
            if(!roundTrip.Grid.AsSpan().SequenceEqual(expectedGrid)||
                !roundTrip.AirThermal!.AsSpan().SequenceEqual(snapshot.AirThermal)||
                !roundTrip.Air!.AsSpan().SequenceEqual(snapshot.Air)||
                !roundTrip.GasMotion!.AsSpan().SequenceEqual(snapshot.GasMotion)||
                !roundTrip.Oxidizer!.AsSpan().SequenceEqual(snapshot.Oxidizer)||
                !roundTrip.ReactionPending!.AsSpan().SequenceEqual(snapshot.ReactionPending)||
                !roundTrip.ReactionPulse!.AsSpan().SequenceEqual(snapshot.ReactionPulse)||
                !roundTrip.Filters!.AsSpan().SequenceEqual(snapshot.Filters))
                throw new InvalidDataException("Furnace save changed its complete state.");
            settings.Paused=true;
            coordinator.DispatchFrame(settings,[],1f/fps);
            if(!AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer).AsSpan().SequenceEqual(snapshot.AirThermal))throw new InvalidDataException("Pause changed carrier heat.");
        }
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        if(failures>0&&Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE")!="1")
            throw new InvalidDataException($"Furnace draft acceptance failed: {failures}.");
        Console.WriteLine("PHYXEL_DRAFT_COMPLETE");
    }

    private static void TraceEmissionLifetime(GpuSimulationResources r,SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,string dir)
    {
        // Call the production reaction/resolve/lifecycle sequence. Older
        // shader-only probes cleared requests themselves and hid this bug.
        int w=r.Width,n=w*r.Height,i=80*w+80;
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),
            fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
        var emissions=registry.CreateEmissionGpuTable();var testEmissions=(MaterialEmissionProperties[])emissions.Clone();
        testEmissions[coal].FlameRate=60; // deterministic births for a moving surface
        r.Emissions.Upload(r.Context,testEmissions);
        var rows=new List<object>();int failures=0;
        void ResetRequests()=>r.Context.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4());
        EmissionRequest[] Step(bool blocker=false,bool absorbed=false,bool cold=false)
        {
            var grid=new GridCell[n];
            grid[i]=new(){IsActive=1,MaterialIndex=coal,Mass=10,Temperature=cold?30:600,Lifetime=cold?0:1,
                FuelMass=absorbed?.2f:0,RetainedLiquidMaterialIndex=absorbed?oil:0};
            if(blocker)grid[i-w-1]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=30};
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            coordinator.DispatchCombustion(r,1f/60,false,false,false,false);
            return MemoryMarshal.Cast<byte,EmissionRequest>(AirInventoryRegressionVerifier.Read(r,r.EmissionRequests.Buffer)).ToArray();
        }
        void Check(string test,bool pass,object evidence)
        {if(!pass)failures++;var row=new{test,pass,evidence};rows.Add(row);Console.WriteLine("PHYXEL_DRAFT_EMISSIONS "+JsonSerializer.Serialize(row));}
        try
        {
            ResetRequests();var masses=new List<float>();
            for(int tick=0;tick<12;tick++)masses.Add(Step()[2*n+i].Mass);
            float expected=emissions[coal].GasRate/60;
            Check("repeated-gas-budget",masses.All(m=>Math.Abs(m-expected)<1e-7),new{expected,masses});
            ResetRequests();uint first=Step()[i].DestinationIndex,second=Step(blocker:true)[i].DestinationIndex;
            var after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
            var actualEmission=MemoryMarshal.Cast<byte,MaterialEmissionProperties>(AirInventoryRegressionVerifier.Read(r,r.Emissions.Buffer)).ToArray()[coal];
            Check("redirect-after-obstacle",first==(uint)(i-w-1)&&second==(uint)(i-w)&&after[i-w].MaterialIndex==fire,
                new{first,second,expected=(uint)(i-w),actualMaterial=after[i-w].MaterialIndex,fire,
                    flameRate=actualEmission.FlameRate,flameInto=actualEmission.FlameIntoMaterialIndex});
            ResetRequests();var combined=new[]{Step(absorbed:true)[n+i].Mass,Step(absorbed:true)[n+i].Mass};
            float combinedExpected=(emissions[coal].SmokeRate+emissions[oil].SmokeRate)/60;
            Check("host-and-pore-same-tick",combined.All(m=>Math.Abs(m-combinedExpected)<1e-7),new{combinedExpected,combined});
            var cold=Step(cold:true);
            Check("inactive-source-no-requests",new[]{cold[i],cold[n+i],cold[2*n+i]}.All(q=>q.Mass==0),new{masses=new[]{cold[i].Mass,cold[n+i].Mass,cold[2*n+i].Mass}});
            File.WriteAllText(Path.Combine(dir,"emission-lifetime.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
            if(failures>0&&Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE")!="1")throw new InvalidDataException($"Emission lifetime failures: {failures}.");
        }
        finally{r.Emissions.Upload(r.Context,emissions);}
    }

    private static void TraceExteriorHeat(GpuSimulationResources r,SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationStateSerializer serializer,SimulationWorldSnapshot world,string dir)
    {
        var start=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Phyxel","Начало паровая печь.json"),registry)).GetAwaiter().GetResult()!.World!;
        if(start.Width!=world.Width||start.Height!=world.Height)throw new InvalidDataException("Heat trace geometry mismatch.");
        var original=MemoryMarshal.Cast<byte,GridCell>(start.Grid).ToArray();
        var before=MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray();var table=registry.CreateGpuTable();
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),molten=registry.GetRequiredRuntimeIndex("core:molten_metal");
        int w=world.Width,h=world.Height;
        bool IsMetal(GridCell c)=>c.IsActive!=0&&(c.MaterialIndex==metal||c.MaterialIndex==molten);
        var outer=new bool[before.Length];var inner=new bool[before.Length];var below=new bool[before.Length];
        for(int x=150;x<510;x++)
        {
            int top=-1,bottom=-1;
            for(int y=349;y<h;y++)if(original[y*w+x].IsActive!=0&&original[y*w+x].MaterialIndex==metal)
            {if(top<0)top=y;bottom=y;}
            if(top<0)continue;
            for(int y=top;y<=bottom;y++)if(IsMetal(before[y*w+x]))
            {inner[y*w+x]=y<=top+1;outer[y*w+x]=y>=bottom-1;}
            for(int y=bottom+1;y<h;y++)below[y*w+x]=true;
        }
        double Energy(GridCell c)=>c.IsActive!=0?c.Mass*(double)PhaseEnthalpy.SpecificEnergy(c,table):0;
        double BodyEnergy(GridCell[] cells)=>cells.Sum(Energy);
        double AirEnergy()=>MemoryMarshal.Cast<byte,System.Numerics.Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer)).ToArray().Sum(v=>(double)v.X);
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        object Region(GridCell[] after,bool[] mask)
        {
            var ids=Enumerable.Range(0,mask.Length).Where(i=>mask[i]).ToArray();
            var changes=ids.Select(i=>Energy(after[i])-Energy(before[i])).ToArray();
            return new{cells=ids.Length,initialT=ids.Average(i=>(double)before[i].Temperature),
                finalT=ids.Average(i=>(double)after[i].Temperature),netQ=changes.Sum(),
                receivedQ=changes.Where(q=>q>0).Sum(),releasedQ=-changes.Where(q=>q<0).Sum()};
        }
        var reports=new List<object>();
        int moltenCount=before.Count(c=>c.IsActive!=0&&c.MaterialIndex==molten);
        int displaced=Enumerable.Range(0,before.Length).Count(i=>before[i].IsActive!=0&&before[i].MaterialIndex==molten&&original[i].IsActive==0);
        var belowMolten=Enumerable.Range(0,before.Length).Where(i=>below[i]&&before[i].IsActive!=0&&before[i].MaterialIndex==molten).ToArray();
        var profiles=new[]{200,300,450}.Select(x=>new{x,cells=Enumerable.Range(349,h-349)
            .Where(y=>IsMetal(before[y*w+x])).Select(y=>new{y,temperature=before[y*w+x].Temperature,
                molten=before[y*w+x].MaterialIndex==molten,phaseProgress=before[y*w+x].PhaseProgress}).ToArray()}).ToArray();
        reports.Add(new{test="saved-state",moltenCount,displacedIntoFormerlyEmpty=displaced,
            moltenBelowOriginalFloor=new{cells=belowMolten.Length,
                maxTemperature=belowMolten.Length>0?belowMolten.Max(i=>before[i].Temperature):0},profiles,
            outer=Region(before,outer),inner=Region(before,inner)});
        foreach(string pass in new[]{"contact","air-0","air-1","air-2","air-3"})
        {
            serializer.ApplyWorldSnapshot(r,world);coordinator.RestoreWorldActivity(r,true,true,false,true);
            // Isolate exchange: no motion, combustion, advection, reservoir,
            // or phase conversion. Each probe starts from the same bad save.
            r.Context.ClearUnorderedAccessView(r.AirFlowLinks.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4());
            r.Context.ClearUnorderedAccessView(r.ThermalEnergyLedger!.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4());
            double e0=BodyEnergy(before)+AirEnergy();
            if(pass=="contact")coordinator.DispatchThermalDiffusion(r,false,0,false);
            else SimulationDispatchCoordinator.DispatchAirHeat(r,false,uint.Parse(pass.AsSpan(4)));
            var after=Read();
            double externalQ=MemoryMarshal.Cast<byte,ThermalEnergyLedgerCell>(AirInventoryRegressionVerifier.Read(r,r.ThermalEnergyLedger.Buffer))
                .ToArray().Sum(v=>(double)v.DeviceHeat+v.AmbientHeat);
            double error=BodyEnergy(after)+AirEnergy()-e0-externalQ;
            var row=new{test=pass,outer=Region(after,outer),inner=Region(after,inner),externalQ,energyError=error,relativeError=Math.Abs(error)/Math.Max(1,Math.Abs(e0))};
            reports.Add(row);Console.WriteLine("PHYXEL_DRAFT_HEAT "+JsonSerializer.Serialize(row));
            if(row.relativeError>1e-5)throw new InvalidDataException("Saved furnace heat trace lost energy.");
        }
        File.WriteAllText(Path.Combine(dir,"heat-trace.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void VerifyVolumeLoss(GpuSimulationResources r,MaterialRegistry registry)
    {
        int w=r.Width,h=r.Height,airCount=r.AirWidth*r.AirHeight,node=25*r.AirWidth+25;
        uint smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        foreach(int packets in new[]{0,1,16})foreach(float wind in new[]{0f,1f})
        {
            var cells=new GridCell[w*h];var map=new uint[w*h];var motion=new GasMotionState[w*h];
            for(int k=0;k<packets;k++)
            {
                int i=(100+k/4)*w+100+k%4;
                cells[i]=new(){IsActive=1,MaterialIndex=smoke,Mass=1,Temperature=20};
                map[i]=smoke;motion[i]=new(){VelocityX=wind};
            }
            var air=new AirCell[airCount];for(int i=0;i<airCount;i++)air[i].VelocityX=wind;
            var ctx=r.Context;
            ctx.UpdateSubresource(cells,r.Grid.ReadBuffer);ctx.UpdateSubresource(map,r.CellMaterials.Buffer);
            ctx.UpdateSubresource(motion,r.GasMotion.Buffer);ctx.UpdateSubresource(air,r.Air.Buffer);
            ctx.ClearUnorderedAccessView(r.AirThermal.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4());
            ctx.ClearUnorderedAccessView(r.ReactionPulse.ReadUnorderedView,new SharpDX.Mathematics.Interop.RawInt4());
            var constants=new AirSimulationConstants{AirWidth=(uint)r.AirWidth,AirHeight=(uint)r.AirHeight,
                AirGridWidth=(uint)w,AirGridHeight=(uint)h,AirAmbientTemperature=20};
            ctx.UpdateSubresource(ref constants,r.AirConstants);
            ctx.ComputeShader.Set(r.AirInjectShader);ctx.ComputeShader.SetConstantBuffer(0,r.AirConstants);
            ctx.ComputeShader.SetShaderResources(0,r.Materials.View,r.Grid.ReadView,r.GasMotion.View,
                r.AirThermal.View,r.ReactionPulse.ReadView,r.CellMaterials.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Air.UnorderedView,r.AirScratch.UnorderedView,
                r.GasAirImpulse.UnorderedView,r.AirFlowLinks.UnorderedView,r.AirProjectionA.UnorderedView,r.AirProjectionB.UnorderedView);
            ctx.Dispatch((r.AirWidth+7)/8,(r.AirHeight+7)/8,1);
            for(int i=0;i<6;i++){ctx.ComputeShader.SetUnorderedAccessView(i,null);ctx.ComputeShader.SetShaderResource(i,null);}
            var result=MemoryMarshal.Cast<byte,AirCell>(AirInventoryRegressionVerifier.Read(r,r.Air.Buffer))[node];
            double expected=wind*Math.Pow(registry.CreateGpuTable()[smoke].MotionAirLoss,packets/16.0);
            bool pass=Math.Abs(result.VelocityX-expected)<1e-6&&Math.Abs(result.VelocityY)<1e-6;
            Console.WriteLine($"PHYXEL_DRAFT_VOLUME packets={packets} wind={wind} actual={result.VelocityX:R} expected={expected:R} pass={pass}");
            if(!pass)throw new InvalidDataException("Air volume loss.");
        }
    }

    private static void TraceOxygenGeometry(GpuSimulationResources r,MaterialRegistry registry,SimulationWorldSnapshot world)
    {
        int w=r.Width,h=r.Height;
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        var cells=MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray();
        // No reaction, emission, cooling or moving footprint. Fresh ambient
        // stock tests whether the saved carrier alone evacuates the fuel face.
        for(int i=0;i<cells.Length;i++)
            if(cells[i].IsActive!=0 && registry.Materials[(int)cells[i].MaterialIndex].Properties.SimulationKind==(uint)MaterialSimulationKind.Gas)cells[i]=default;
        bool Space(int i)=>cells[i].IsActive==0;
        var surface=Enumerable.Range(w,w*(h-2)).Where(i=>i%w>0&&i%w<w-1&&cells[i].IsActive!=0&&cells[i].MaterialIndex==coal&&
            (Space(i-1)||Space(i+1)||Space(i-w)||Space(i+w))).ToArray();
        foreach(bool flow in new[]{false,true})
        {
            r.OxidizerCarrierWarm=false;
            r.Context.UpdateSubresource(cells,r.Grid.ReadBuffer);
            var stock=Enumerable.Range(0,w*h).Select(i=>Space(i)?1f:0f).ToArray();
            r.Context.UpdateSubresource(stock,r.Oxidizer.ReadBuffer);
            r.Context.UpdateSubresource(flow?MemoryMarshal.Cast<byte,AirCell>(world.Air!).ToArray():new AirCell[r.AirWidth*r.AirHeight],r.Air.Buffer);
            var timer=System.Diagnostics.Stopwatch.StartNew();
            const int ticks=2400;
            for(int tick=0;tick<ticks;tick++)SimulationDispatchCoordinator.DispatchOxidizer(r,1f/60,false,false,true);
            var after=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.Oxidizer.ReadBuffer)).ToArray();
            timer.Stop();
            var exposure=surface.Select(i=>new[]{i-1,i+1,i-w,i+w}.Where(Space).Average(j=>(double)after[j])).Order().ToArray();
            Console.WriteLine("PHYXEL_DRAFT_OXYGEN "+JsonSerializer.Serialize(new{flow,initial=stock.Sum(x=>(double)x),final=after.Sum(x=>(double)x),
                ticks,faces=surface.Length,starved=exposure.Count(x=>x<=.2),min=exposure.First(),median=exposure[exposure.Length/2],max=exposure.Last(),elapsedMs=timer.Elapsed.TotalMilliseconds}));
            if(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE")!="1"&&
                (exposure.Any(x=>x<=.2)||Math.Abs(after.Sum(x=>(double)x)-stock.Sum(x=>(double)x))>stock.Sum(x=>(double)x)*1e-5))
                throw new InvalidDataException("Carrier alone starved fresh fuel faces or lost oxygen.");
        }
    }

    private static void VerifySparseRoof(GpuSimulationResources r,MaterialRegistry registry,
        SharpDX.Direct3D11.ComputeShader shader,bool old)
    {
        int w=r.Width,h=r.Height,index=100*w+100;
        uint fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),
            smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        foreach(string caseName in new[]{"sparse","dense","closed","liquid","pocket"})
        {
            var cells=new GridCell[w*h];var map=new uint[w*h];
            for(int x=80;x<=160;x++)cells[90*w+x]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=20};
            if(caseName=="closed")for(int y=91;y<=110;y++)
                cells[y*w+80]=cells[y*w+160]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=20};
            if(caseName=="dense")for(int y=91;y<100;y++)cells[y*w+100]=new(){IsActive=1,MaterialIndex=smoke,Mass=1,Temperature=600};
            if(caseName=="liquid")cells[95*w+100]=new(){IsActive=1,MaterialIndex=water,Mass=1,Temperature=20};
            int sourceIndex=caseName=="pocket"?100*w+130:index;
            if(caseName=="pocket")
            {
                // Right edge looks open at roof height, but it is capped two
                // pixels higher. The actual open riser is on the left.
                for(int x=161;x<=164;x++)cells[88*w+x]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=20};
                for(int y=89;y<=110;y++)cells[y*w+164]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=20};
            }
            cells[sourceIndex]=new(){IsActive=1,MaterialIndex=fire,Mass=1,Temperature=600,Lifetime=2};
            for(int i=0;i<map.Length;i++)map[i]=cells[i].MaterialIndex;
            var ctx=r.Context;
            ctx.UpdateSubresource(cells,r.Grid.ReadBuffer);ctx.UpdateSubresource(map,r.CellMaterials.Buffer);
            ctx.UpdateSubresource(new AirCell[r.AirWidth*r.AirHeight],r.Air.Buffer);
            ctx.UpdateSubresource(new GasMotionState[w*h],r.GasMotion.Buffer);
            ctx.ClearUnorderedAccessView(r.GasActiveTiles.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4(1,1,1,1));
            var constants=new SimulationFrameConstants{Width=(uint)w,Height=(uint)h,SimulationPhase=89,
                DispatchExtentX=(uint)w,DispatchExtentY=(uint)h,SolidPass=1};
            ctx.UpdateSubresource(ref constants,r.FrameConstants);
            ctx.ComputeShader.Set(shader);ctx.ComputeShader.SetConstantBuffer(0,r.FrameConstants);
            ctx.ComputeShader.SetShaderResources(0,r.Materials.View,r.Air.View,r.GasActiveTiles.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.BodyFlags.UnorderedView,
                r.PathBlockerMasks.UnorderedView,r.CellMaterials.UnorderedView);
            ctx.ComputeShader.SetUnorderedAccessView(6,r.GasMotion.UnorderedView);
            ctx.ComputeShader.SetUnorderedAccessView(7,r.GasObstacleBypassStatistics.UnorderedView);
            ctx.ComputeShader.SetUnorderedAccessView(8,r.GasLateralTransferStatistics.UnorderedView);
            ctx.ComputeShader.SetUnorderedAccessView(9,r.GasAirImpulse.UnorderedView);
            ctx.ComputeShader.SetUnorderedAccessView(10,r.GasVerticalMotionStatistics.UnorderedView);
            ctx.ComputeShader.SetUnorderedAccessView(11,r.GasVerticalBlockFrameMarkers.UnorderedView);
            ctx.Dispatch((w+15)/16,(h+15)/16,1);
            for(int i=0;i<12;i++)ctx.ComputeShader.SetUnorderedAccessView(i,null);
            for(int i=0;i<3;i++)ctx.ComputeShader.SetShaderResource(i,null);
            var motion=MemoryMarshal.Cast<byte,GasMotionState>(AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer))[sourceIndex];
            bool open=caseName=="dense"||caseName=="pocket"||caseName=="sparse";
            bool pass=open?motion.VelocityX<-3.5f:Math.Abs(motion.VelocityX)<.001;
            Console.WriteLine($"PHYXEL_DRAFT_ROOF old={old} case={caseName} vx={motion.VelocityX:R} pass={pass}");
            if(!pass&&Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE")!="1")throw new InvalidDataException("Roof jet: "+caseName);
        }
    }
}
