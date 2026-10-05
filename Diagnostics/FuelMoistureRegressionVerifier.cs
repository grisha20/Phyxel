using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Mathematics.Interop;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

internal static class FuelMoistureRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string directory=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/fuel-moisture";
        Directory.CreateDirectory(directory);
        var settings=new SimulationSettings { Paused=true, AirSimulation=false, OpenBoundaries=false };
        var r=coordinator.DispatchFrame(settings,[new() { X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal) }],0);
        int w=r.Width,n=w*r.Height,origin=80*w+80,checks=0;
        bool passed=true;
        void Check(bool ok,string message) { checks++; if(!ok) { passed=false; Console.WriteLine("PHYXEL_FUEL_MOISTURE_CHECK_FAILED "+message); } }
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water), steam=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var table=registry.CreateGpuTable();
        string? legacyPath=Environment.GetEnvironmentVariable("PHYXEL_MOISTURE_LEGACY_SHADER");
        using var legacyBytecode=legacyPath is null ? null : ShaderBytecode.Compile(
            File.ReadAllText(legacyPath).Replace("#include \"PhysicsShared.hlsli\"",File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory,"Content","Shaders","PhysicsShared.hlsli"))),"CSMain","cs_5_0");
        using var legacyShader=legacyBytecode is null ? null : new ComputeShader(r.Device,legacyBytecode);
        GridCell Cell(uint id,float temperature=20,float mass=1) => new() { IsActive=1,MaterialIndex=id,Mass=mass,Temperature=temperature };
        GridCell[] Read() => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Mass(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
        double Energy(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        void Balance(GridCell[] before,GridCell[] after,string label) {
            Check(Math.Abs(Mass(after)-Mass(before))<.0001*Math.Max(1,Mass(before)),label+" mass");
            Check(Math.Abs(Energy(after)-Energy(before))<.0001*Math.Max(1,Math.Abs(Energy(before))),label+" energy");
        }
        void Upload(GridCell[] grid) {
            r.Context.ClearUnorderedAccessView(r.ContactSummary.UnorderedView,new RawInt4());
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer); r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);
        }
        void Contact(uint tick) {
            var constants=new ContactTransitionConstants { Width=(uint)w,Height=(uint)r.Height,DeltaTime=.05f,TickIndex=tick };
            r.Context.UpdateSubresource(ref constants,r.ContactTransitionConstants);
            r.Context.ComputeShader.Set(legacyShader??r.MoistureShader); r.Context.ComputeShader.SetConstantBuffer(0,r.ContactTransitionConstants);
            r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CellMaterials.UnorderedView,r.GasMotion.UnorderedView,r.ContactSummary.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            r.Context.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<4;i++) r.Context.ComputeShader.SetUnorderedAccessView(i,null);
            r.Context.ComputeShader.Set(null);
        }
        void React(bool finite) {
            r.Context.UpdateSubresource(Enumerable.Repeat(1f,n).ToArray(),r.OxidizerAvailable.Buffer);
            r.Context.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            r.Context.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
            r.Context.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
            r.Context.ClearUnorderedAccessView(r.ReactionPending.UnorderedView,new RawInt4());
            var constants=new CombustionConstants { Width=(uint)w,Height=(uint)r.Height,MaterialCount=(uint)registry.Count,
                DeltaTime=1f/60,TickIndex=1,FiniteOxidizer=finite?1u:0u,Reserved1=1 };
            r.Context.UpdateSubresource(ref constants,r.CombustionConstants);
            r.Context.ComputeShader.Set(r.CombustionShader); r.Context.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            r.Context.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<3;i++) r.Context.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<6;i++) r.Context.ComputeShader.SetUnorderedAccessView(i,null);
            r.Context.ComputeShader.Set(null);
        }

        // A surface water packet must travel through touching absorbent
        // grains without moving their dry mass or inventing heat/water.
        // Unequal capacities/masses and reversed orientation use the same
        // saturation equilibrium, rather than blindly copying water amounts.
        foreach(bool reverse in new[] {false,true}) {
            uint charcoal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
            uint powder=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
            var pair=new GridCell[n];
            int donor=reverse?origin+1:origin, recipient=reverse?origin:origin+1;
            pair[donor]=Cell(table[charcoal].MoistureWetMaterialIndex,20,2);
            pair[donor].MoistureMass=.7f; pair[recipient]=Cell(powder);
            Upload(pair); for(uint tick=0;tick<80;tick++) { Contact(tick); if(tick%16==15) yield return r; }
            var equalized=Read(); Balance(pair,equalized,"unequal wick "+reverse);
            Check(Math.Abs(equalized[donor].MoistureMass/.7f-equalized[recipient].MoistureMass/.3f)<.001,
                "unequal wick did not equalize saturation "+reverse);
            Check(equalized[recipient].MoistureMass<=.3f && equalized[donor].Mass==2 && equalized[recipient].Mass==1,
                "unequal wick capacity/dry mass "+reverse);
        }
        uint testCoal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        foreach(uint blocker in new[] {0u,fixture,registry.GetRequiredRuntimeIndex(CoreMaterialIds.Stone)}) {
            var blocked=new GridCell[n]; blocked[origin]=Cell(table[testCoal].MoistureWetMaterialIndex);
            blocked[origin].MoistureMass=.35f; blocked[origin+2]=Cell(testCoal);
            if(blocker!=0) blocked[origin+1]=Cell(blocker);
            Upload(blocked); for(uint tick=0;tick<160;tick++) { Contact(tick); if(tick%16==15) yield return r; }
            Check(Read()[origin+2].MoistureMass==0,"wick crossed barrier "+blocker);
            Balance(blocked,Read(),"wick barrier "+blocker);
        }
        var diagonal=new GridCell[n]; diagonal[origin]=Cell(table[testCoal].MoistureWetMaterialIndex);
        diagonal[origin].MoistureMass=.35f; diagonal[origin+w+1]=Cell(testCoal);
        Upload(diagonal); for(uint tick=0;tick<80;tick++) { Contact(tick); if(tick%16==15) yield return r; }
        Check(Read()[origin+w+1].MoistureMass==0,"wick crossed diagonal");
        Balance(diagonal,Read(),"wick diagonal");

        foreach(string id in new[] {CoreMaterialIds.Coal,CoreMaterialIds.Gunpowder,CoreMaterialIds.Wood}) {
            uint dry=registry.GetRequiredRuntimeIndex(id), wet=table[dry].MoistureWetMaterialIndex;
            var column=new GridCell[n];
            for(int depth=0;depth<12;depth++) column[origin+depth*w]=Cell(dry);
            column[origin-w]=Cell(water,20,2);
            Upload(column);
            for(uint tick=0;tick<160;tick++) {
                Contact(tick); if(tick%16==15) yield return r;
                if(tick==3) Check(Read()[origin+2*w].MoistureMass==0,id+" wick jumped two grains in .2s");
            }
            var soaked=Read();
            Check(soaked[origin+3*w].MoistureMass>.001f,id+" water stopped at surface");
            Check(Enumerable.Range(0,12).All(depth=>soaked[origin+depth*w].Mass==1),id+" wick moved dry fuel");
            Balance(column,soaked,id+" wick column");
            File.WriteAllBytes(Path.Combine(directory,id.Replace(':','-')+"-wick-column.grid"),MemoryMarshal.AsBytes(soaked.AsSpan()).ToArray());
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_WICK id={id} depth3={soaked[origin+3*w].MoistureMass:F8} totalWater={soaked.Sum(c=>(double)c.MoistureMass):F8}");
            // Partially paid boiling/excess heat must not be copied or lost.
            foreach(float stored in new[] {0f,225.6f,800f}) {
                var pair=new GridCell[n]; pair[origin]=Cell(wet,stored==0?20:100); pair[origin].MoistureMass=.2f; pair[origin].MoistureEnergy=stored;
                pair[origin+1]=Cell(dry,700); pair[origin+1].Lifetime=1;
                Upload(pair); Contact(0); var transferred=Read();
                Check(transferred[origin+1].MoistureMass>0 && transferred[origin+1].Lifetime==0,id+" wick hot receiver/latch");
                Check(transferred[origin+1].Temperature<=100.002f,id+" wick hot receiver temperature");
                Balance(pair,transferred,id+" wick heat "+stored);
                foreach(bool finite in new[] {false,true}) {
                    Upload(transferred); React(finite);
                    Check(Read()[origin+1].Mass==1,id+" wicked fuel burned "+finite);
                }
            }
        }

        foreach(string id in new[] {CoreMaterialIds.Coal,CoreMaterialIds.Gunpowder,CoreMaterialIds.Wood}) {
            uint fuelId=registry.GetRequiredRuntimeIndex(id), wetId=table[fuelId].MoistureWetMaterialIndex;
            // Every face; also wrong liquid, vapour, ice and a diagonal water cell.
            foreach((uint neighbour,int offset,bool allowed) in new[] {
                (water,-1,true),(water,1,true),(water,-w,true),(water,w,true),
                (registry.GetRequiredRuntimeIndex("core:molten_metal"),1,false),
                (steam,1,false),(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice),1,false),(water,w+1,false) }) {
                var grid=new GridCell[n]; grid[origin]=Cell(fuelId); grid[origin].Lifetime=id==CoreMaterialIds.Coal?1:0;
                grid[origin+offset]=Cell(neighbour,20,.4f);
                Upload(grid); for(uint tick=1;tick<=80;tick++) { Contact(tick); if(tick%16==15) yield return r; } var after=Read();
                Check(allowed ? after[origin].MoistureMass>.1 && after[origin].MaterialIndex==wetId : after[origin].MoistureMass==0,
                    id+" selector "+neighbour+" "+offset);
                if(allowed) Check(after[origin].Lifetime==0 && after[origin+offset].Mass<.4f,id+" conserved uptake/latch");
                Balance(grid,after,id+" uptake");
            }
            // Four grains compete for a single small water packet, carrying boiling progress.
            var shared=new GridCell[n]; shared[origin]=Cell(water,100,.08f); shared[origin].Lifetime=100;
            foreach(int offset in new[] {-1,1,-w,w}) shared[origin+offset]=Cell(fuelId,20);
            Upload(shared); for(uint tick=1;tick<=40;tick++) { Contact(tick); if(tick%16==15) yield return r; } var sharedAfter=Read();
            Check(Math.Abs(sharedAfter.Sum(c=>(double)c.MoistureMass)+sharedAfter.Where(c=>c.IsActive!=0&&c.MaterialIndex==water).Sum(c=>(double)c.Mass)-.08)<1e-6,
                id+" shared donor spent twice"); Balance(shared,sharedAfter,id+" shared donor");

            // Wet self-oxidizing fuel must also stop, even with hot fire and a saved latch.
            foreach(bool finite in new[] {false,true}) {
                var grid=new GridCell[n]; grid[origin]=Cell(fuelId,800); grid[origin].MoistureMass=.1f;
                grid[origin].MoistureEnergy=.1f*table[water].TransitionAboveLatentHeat; grid[origin].Lifetime=1;
                grid[origin+1]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),1000);
                grid[origin+1].Lifetime=1;
                Upload(grid); React(finite); var after=Read();
                Check(after[origin].Mass==1&&after[origin].Lifetime==0&&after[origin].MoistureMass==.1f,id+" wet burn "+finite);
                Check(MemoryMarshal.Cast<byte,System.Numerics.Vector4>(AirInventoryRegressionVerifier.Read(r,r.ReactionPending.Buffer)).ToArray()
                    .All(v=>v==default),id+" wet pressure source");
                grid[origin].MoistureMass=0; grid[origin].MoistureEnergy=0;
                Upload(grid); React(finite); Check(Read()[origin].Mass<1,id+" dry control did not burn");
            }

            // Genuine GPU conduction into wet fuel, rather than setting temperature past latent storage.
            var heat=new GridCell[n]; heat[origin]=Cell(wetId,99); heat[origin].MoistureMass=.1f;
            heat[origin+1]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),600,7.8f);
            Upload(heat); bool plateau=false;
            for(uint tick=0;tick<10;tick++) {
                coordinator.DispatchThermalDiffusion(r,false,tick,false);
                var intermediate=Read()[origin];
                if(intermediate.MoistureEnergy>0 && intermediate.MoistureEnergy<intermediate.MoistureMass*table[water].TransitionAboveLatentHeat)
                {
                    plateau=true;
                    Check(Math.Abs(intermediate.Temperature-100)<.002,id+" latent plateau lost");
                }
            }
            var warmed=Read(); Balance(heat,warmed,id+" wet conduction");
            Check(warmed[origin].Temperature<=100.002f,id+" wet fuel superheated");
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_HEAT id={id} T={warmed[origin].Temperature:F8} latent={warmed[origin].MoistureEnergy:F8}");
            Check(plateau,id+" no intermediate latent plateau");

            // Below boiling: even a long wait does not create steam.
            var cold=new GridCell[n]; cold[origin]=Cell(wetId,90); cold[origin].MoistureMass=.1f;
            Upload(cold); for(uint tick=1;tick<=80;tick++) { Contact(tick); if(tick%16==15) yield return r; } var coldAfter=Read();
            Check(coldAfter[origin].MoistureMass==.1f&&coldAfter.Count(c=>c.IsActive!=0&&c.MaterialIndex==steam)==0,id+" cold evaporation");
            Check(BitConverter.ToUInt32(AirInventoryRegressionVerifier.Read(r,r.ContactSummary.Buffer))==0,id+" cold fuel falsely wakes gas");
            Balance(cold,coldAfter,id+" cold");

            // Sealed grain keeps fully paid water; releasing the opening dries it without new heat.
            var sealedGrid=new GridCell[n]; sealedGrid[origin]=Cell(wetId,100); sealedGrid[origin].MoistureMass=.1f;
            sealedGrid[origin].MoistureEnergy=.1f*table[water].TransitionAboveLatentHeat;
            foreach(int offset in new[] {-1,1,-w,w}) sealedGrid[origin+offset]=Cell(fixture,100);
            Upload(sealedGrid); for(uint tick=1;tick<=80;tick++) { Contact(tick); if(tick%16==15) yield return r; } var closed=Read();
            Check(closed[origin].MoistureMass==.1f && closed[origin].MoistureEnergy==sealedGrid[origin].MoistureEnergy,id+" sealed loss");
            Balance(sealedGrid,closed,id+" sealed");
            // Collector removes emitted steam after measuring it, keeping the face open.
            closed[origin+1]=default; double beforeMass=Mass(closed),beforeEnergy=Energy(closed),collectedMass=0,collectedEnergy=0;
            Upload(closed);
            for(uint tick=81;tick<=160;tick++) {
                Contact(tick); if(tick%16==15) yield return r; closed=Read(); var emitted=closed[origin+1];
                if(emitted.IsActive!=0) { collectedMass+=emitted.Mass; collectedEnergy+=emitted.Mass*PhaseEnthalpy.SpecificEnergy(emitted,table); closed[origin+1]=default; Upload(closed); }
            }
            Check(closed[origin].MoistureMass==0&&closed[origin].MaterialIndex==fuelId,id+" no dry return");
            Check(Math.Abs(Mass(closed)+collectedMass-beforeMass)<1e-5 && Math.Abs(Energy(closed)+collectedEnergy-beforeEnergy)<.01,id+" drying ledger");
            Check(Math.Abs(collectedMass-.1)<1e-6,id+" water not returned as steam");
            double dryingError=Energy(closed)+collectedEnergy-beforeEnergy;
            closed[origin].Temperature=800; Upload(closed); React(false); Check(Read()[origin].Mass<1,id+" dry return unburnable");
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_CYCLE id={id} steam={collectedMass:F8} energyError={dryingError:F8}");

            // Only 25% of water has paid latent heat: no unearned evaporation.
            var partial=new GridCell[n]; partial[origin]=Cell(wetId,100); partial[origin].MoistureMass=.1f;
            partial[origin].MoistureEnergy=.025f*table[water].TransitionAboveLatentHeat;
            foreach(int offset in new[] {-1,-w,w}) partial[origin+offset]=Cell(fixture,100);
            Upload(partial); Contact(0); var partialAfter=Read(); Balance(partial,partialAfter,id+" partial latent");
            Check((BitConverter.ToUInt32(AirInventoryRegressionVerifier.Read(r,r.ContactSummary.Buffer)) & (uint)PhaseTransitionSummaryFlags.TargetGas)!=0,
                id+" drying vapour not published");
            var phaseConstants=new PhaseTransitionConstants { Width=(uint)w,Height=(uint)r.Height,
                MaterialCount=(uint)registry.Count,TickCount=1 };
            r.Context.ClearUnorderedAccessView(r.PhaseSummary.UnorderedView,new RawInt4());
            r.Context.UpdateSubresource(ref phaseConstants,r.PhaseConstants);
            r.Context.ComputeShader.Set(r.PhaseTransitionShader); r.Context.ComputeShader.SetConstantBuffer(0,r.PhaseConstants);
            r.Context.ComputeShader.SetShaderResources(0,r.Materials.View,r.ContactSummary.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.PhaseSummary.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<2;i++) { r.Context.ComputeShader.SetShaderResource(i,null); r.Context.ComputeShader.SetUnorderedAccessView(i,null); }
            r.Context.ComputeShader.Set(null);
            Check((BitConverter.ToUInt32(AirInventoryRegressionVerifier.Read(r,r.PhaseSummary.Buffer)) & (uint)PhaseTransitionSummaryFlags.TargetGas)!=0,
                id+" drying vapour absent from asynchronous phase summary");
            Check(partialAfter[origin].MoistureMass>=.075f-1e-6 && partialAfter[origin+1].Mass>0 &&
                partialAfter[origin+1].Mass<=.025f+1e-6,id+" unpaid steam");

            // Existing vapour must accept paid evaporation without losing its
            // prior mass or energy. These disjoint pairs have a single writer.
            var vapourOutlet=new GridCell[n]; vapourOutlet[origin]=Cell(wetId,100);
            vapourOutlet[origin].MoistureMass=.1f;
            vapourOutlet[origin].MoistureEnergy=.025f*table[water].TransitionAboveLatentHeat;
            vapourOutlet[origin+1]=Cell(steam,150,.3f);
            foreach(int offset in new[] {-1,-w,w}) vapourOutlet[origin+offset]=Cell(fixture,100);
            Upload(vapourOutlet); Contact(0); var outletAfter=Read();
            Check(outletAfter[origin].MoistureMass<.1f && outletAfter[origin+1].Mass>.3f,id+" vapour cork");
            Check(outletAfter[origin+1].MaterialIndex==steam,id+" vapour species changed");
            Balance(vapourOutlet,outletAfter,id+" vapour merge");
        }

        // A prior frame's wet grain in the ping-pong destination must not
        // survive an empty source after the thermal bulk clear.
        var emptySource=new GridCell[n]; Upload(emptySource);
        var staleDestination=new GridCell[n]; staleDestination[origin]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),100);
        staleDestination[origin].MoistureMass=.2f; staleDestination[origin].MoistureEnergy=30;
        r.Context.UpdateSubresource(staleDestination,r.Grid.WriteBuffer);
        coordinator.DispatchThermalDiffusion(r,false,1,false);
        Check(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer).All(v=>v==0),"thermal clear retained stale wet destination");

        // Air geometry's compact snapshot must be authoritative even when its
        // old cache is deliberately wrong. Include wet fuel and thin walls.
        foreach(bool sandbox in new[] {false,true}) {
            var geometry=new GridCell[n];
            for(int y=40;y<100;y++) geometry[y*w+60]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal));
            geometry[origin]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal)); geometry[origin].MoistureMass=.1f;
            geometry[origin+2]=Cell(water); geometry[origin+3]=Cell(steam,200);
            Upload(geometry); r.Context.ClearUnorderedAccessView(r.CellMaterials.UnorderedView,new RawInt4(-1,-1,-1,-1));
            coordinator.DispatchAirSimulation(r,1,sandbox,false);
            var map=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.CellMaterials.Buffer)).ToArray();
            Check(map.Where((v,i)=>v!=(geometry[i].IsActive!=0?geometry[i].MaterialIndex:0)).Any()==false,
                "compact air geometry stale, sandbox="+sandbox);
        }

        // Save, paused continuation and old 40-byte cell migration.
        var save=new GridCell[n]; uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        save[origin]=Cell(table[coal].MoistureWetMaterialIndex,100); save[origin].MoistureMass=.17f; save[origin].MoistureEnergy=45;
        save[origin+3]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder),40); save[origin+3].MoistureMass=.21f;
        var serializer=new SimulationStateSerializer(); string path=Path.Combine(directory,"moisture.json");
        var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(save.AsSpan()).ToArray());
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)coal,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        Check(snapshot.Grid.AsSpan().SequenceEqual(loaded.World!.Grid),"save/load moisture changed");
        Upload(save);
        var probeConstants=new TemperatureProbeConstants { X=(uint)(origin%w+3),Y=(uint)(origin/w),Width=(uint)w,Height=(uint)r.Height };
        r.Context.UpdateSubresource(ref probeConstants,r.TemperatureProbeConstants);
        r.Context.ComputeShader.Set(r.TemperatureProbeShader); r.Context.ComputeShader.SetConstantBuffer(0,r.TemperatureProbeConstants);
        r.Context.ComputeShader.SetShaderResources(0,r.Grid.ReadView,r.Materials.View);
        r.Context.ComputeShader.SetUnorderedAccessView(0,r.TemperatureProbeResult.UnorderedView); r.Context.Dispatch(1,1,1);
        for(int i=0;i<2;i++) r.Context.ComputeShader.SetShaderResource(i,null);
        r.Context.ComputeShader.SetUnorderedAccessView(0,null); r.Context.ComputeShader.Set(null);
        var probe=MemoryMarshal.Cast<byte,TemperatureProbeResult>(AirInventoryRegressionVerifier.Read(r,r.TemperatureProbeResult.Buffer))[0];
        Check(Math.Abs(BitConverter.UInt32BitsToSingle(probe.Reserved)-.21f/1.21f)<1e-6 &&
            Phyxel.UI.UiStatusBar.FormatTemperatureProbe(registry,probe).Contains("влага"),"Moisture cursor probe/HUD lost wet powder");
        GridCell[] Continue(GridCell[] grid) { Upload(grid); for(uint tick=1;tick<=20;tick++) Contact(tick); return Read(); }
        var memory=Continue(save); var reload=Continue(MemoryMarshal.Cast<byte,GridCell>(loaded.World.Grid).ToArray());
        Check(MemoryMarshal.AsBytes(memory.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(reload.AsSpan())),"reload continuation differs");
        // Motion must carry water and paid latent heat with the same grain.
        var moving=new GridCell[n]; moving[origin]=Cell(table[coal].MoistureWetMaterialIndex,100);
        moving[origin].MoistureMass=.17f; moving[origin].MoistureEnergy=45;
        serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(moving.AsSpan()).ToArray()));
        coordinator.RestoreWorldActivity(r,true,true,false); settings.Paused=false;
        // One frame moves granular matter, before the first .05-s moisture tick.
        coordinator.DispatchFrame(settings,[],1f/60);
        var transported=Read(); int destination=Array.FindIndex(transported,c=>c.IsActive!=0&&c.MoistureMass>0);
        Check(destination>origin && transported[destination].Mass==1 && transported[destination].MoistureMass==.17f &&
            transported[destination].MoistureEnergy==45,
            "Falling grain lost or duplicated moisture");
        Balance(moving,transported,"wet fall"); settings.Paused=true;
        // Repeated wet/dry cycles keep the same fuel mass, water ledger and reactivity.
        var cyclic=new GridCell[n]; cyclic[origin]=Cell(coal);
        for(int cycle=0;cycle<3;cycle++) {
            cyclic[origin+1]=Cell(water,20,.07f); Upload(cyclic);
            for(uint tick=1;tick<=20;tick++) { Contact(tick); if(tick%16==15) yield return r; } cyclic=Read();
            Check(Math.Abs(cyclic[origin].MoistureMass-.07f)<1e-6 && cyclic[origin+1].IsActive==0,"Repeated uptake "+cycle);
            // Explicit external heat: bring the stored water to fully paid boiling.
            cyclic[origin].Temperature=100; cyclic[origin].MoistureEnergy=cyclic[origin].MoistureMass*table[water].TransitionAboveLatentHeat;
            double cycleMass=Mass(cyclic),cycleEnergy=Energy(cyclic),steamMass=0,steamEnergy=0; Upload(cyclic);
            for(uint tick=21;tick<=60;tick++) {
                Contact(tick); if(tick%16==15) yield return r; cyclic=Read();
                for(int i=0;i<n;i++) if(cyclic[i].IsActive!=0&&cyclic[i].MaterialIndex==steam) {
                    steamMass+=cyclic[i].Mass; steamEnergy+=cyclic[i].Mass*PhaseEnthalpy.SpecificEnergy(cyclic[i],table); cyclic[i]=default;
                }
                Upload(cyclic);
            }
            Check(cyclic[origin].MaterialIndex==coal && cyclic[origin].MoistureMass==0 && cyclic[origin].Mass==1,"Repeated dry return "+cycle);
            Check(Math.Abs(Mass(cyclic)+steamMass-cycleMass)<1e-5&&Math.Abs(Energy(cyclic)+steamEnergy-cycleEnergy)<.01,"Repeated cycle ledger "+cycle);
            cyclic[origin].Temperature=20;
        }
        var legacyCell=Cell(coal,42); byte[] old=MemoryMarshal.AsBytes(new[] {legacyCell}.AsSpan())[..40].ToArray();
        foreach(int version in new[] {6,7,8,9,10,11}) {
            var migrated=WorldCellCodec.Decode(new RawWorldFile(version,1,1,40,old));
            var c=MemoryMarshal.Cast<byte,GridCell>(migrated.Grid)[0];
            Check(c.Temperature==42&&c.Mass==1&&c.MoistureMass==0&&c.MoistureEnergy==0,"legacy invented water "+version);
        }

        // The actual scheduler, with fuel/water held in two-cell insulated pockets.
        // A paused temperature/fire tool still respects wet boiling storage.
        foreach(string wetToolId in new[] {CoreMaterialIds.Coal,CoreMaterialIds.Gunpowder,CoreMaterialIds.Wood}) {
            uint dryToolId=registry.GetRequiredRuntimeIndex(wetToolId);
            var toolGrid=new GridCell[n]; toolGrid[origin]=Cell(table[dryToolId].MoistureWetMaterialIndex,80);
            toolGrid[origin].MoistureMass=.1f;
            settings.Paused=true;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(toolGrid.AsSpan()).ToArray()));
            coordinator.DispatchFrame(settings,[new() { X=80,Y=80,EndX=80,EndY=80,Radius=1,Density=1,
                Mode=BrushCommandMode.SetTemperature,MaterialIndex=(ushort)dryToolId,TargetTemperature=326.5f }],0);
            var toolAfter=Read();
            Check(toolAfter[origin].Temperature==100 && toolAfter[origin].MoistureMass==.1f,"wet temperature tool "+wetToolId);
            Check(Math.Abs(Energy(toolAfter)-(Energy(toolGrid)+PhaseEnthalpy.EffectiveCapacity(toolGrid[origin],table)*(326.5-80)))<.001,
                "wet temperature tool ledger "+wetToolId);
            coordinator.DispatchFrame(settings,[new() { X=80,Y=80,EndX=80,EndY=80,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=(ushort)registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire) }],0);
            Check(Read()[origin].Temperature<=100.002f,"wet flame brush "+wetToolId);
        }
        // A compact heap must not be treated as an empty path for falling
        // water merely because a free charcoal grain is buoyant.
        foreach(var mode in new[] {SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(int fps in new[] {30,60,100}) {
            var heap=new GridCell[n];
            for(int dy=0;dy<16;dy++) for(int dx=-12;dx<=12;dx++) heap[origin+dy*w+dx]=Cell(coal);
            for(int dy=-2;dy<=16;dy++) foreach(int dx in new[] {-13,13}) heap[origin+dy*w+dx]=Cell(fixture);
            for(int dx=-13;dx<=13;dx++) heap[origin+16*w+dx]=Cell(fixture);
            for(int dx=-4;dx<=4;dx++) heap[origin-w+dx]=Cell(water);
            settings.Paused=false; settings.AirSimulation=false; settings.Mode=mode;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(heap.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            for(int frame=0;frame<fps;frame++) { coordinator.DispatchFrame(settings,[],1f/fps); if(frame%8==7) yield return r; }
            var poured=Read();
            int innerWater=Enumerable.Range(0,n).Count(i=>i/w>=origin/w+2 && i/w<origin/w+16 &&
                Math.Abs(i%w-origin%w)<=10 && poured[i].IsActive!=0 && poured[i].MaterialIndex==water);
            Check(innerWater==0,"water burrowed compact heap "+mode+" "+fps);
            Check(poured.Any(c=>c.MoistureMass>0),"heap surface not wetted "+mode+" "+fps);
            Balance(heap,poured,"cold pour "+mode+" "+fps);
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_POUR mode={mode} fps={fps} innerWater={innerWater}");
        }
        settings.Paused=true;
        // Free light grains retain buoyancy; the packing rule must not turn
        // every powder into an impermeable solid or invert heavy powders.
        foreach(string grainId in new[] {CoreMaterialIds.Coal,CoreMaterialIds.Sand}) {
            uint grain=registry.GetRequiredRuntimeIndex(grainId);
            var immersed=new GridCell[n];
            immersed[origin]=Cell(water); immersed[origin+w]=Cell(grain);
            for(int dy=-1;dy<=3;dy++) for(int dx=-1;dx<=1;dx++)
                if(dx!=0 || dy==-1 || dy==3) immersed[origin+dy*w+dx]=Cell(fixture);
            immersed[origin+2*w]=Cell(water);
            settings.Paused=false;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(immersed.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            coordinator.DispatchFrame(settings,[],1f/60);
            var floated=Read(); int grainPosition=Array.FindIndex(floated,c=>c.IsActive!=0 && c.MaterialIndex==grain);
            Check(grainId==CoreMaterialIds.Coal ? grainPosition<origin+w : grainPosition>origin+w,"free grain buoyancy "+grainId);
            Balance(immersed,floated,"free grain "+grainId);
        }
        settings.Paused=true;
        // Actual liquid density controls buoyancy. Changing the wet ID on
        // first contact must not make a barely wet charcoal grain sink.
        foreach(float liquidDensity in new[] {.6f,1f,1.6f,1.15f})
        foreach(var sample in new[] {(CoreMaterialIds.Coal,0f),(CoreMaterialIds.Coal,.25f),
            (CoreMaterialIds.Coal,.9f),(CoreMaterialIds.Coal,1f),("core:stone_coal",0f)}) {
            uint dry=registry.GetRequiredRuntimeIndex(sample.Item1);
            uint grain=sample.Item2>0?table[dry].MoistureWetMaterialIndex:dry;
            var immersed=new GridCell[n]; immersed[origin]=Cell(water); immersed[origin+2*w]=Cell(water);
            immersed[origin+w]=Cell(grain); immersed[origin+w].MoistureMass=table[dry].MoistureCapacity*sample.Item2;
            for(int dy=-1;dy<=3;dy++) for(int dx=-1;dx<=1;dx++)
                if(dx!=0 || dy==-1 || dy==3) immersed[origin+dy*w+dx]=Cell(fixture);
            var custom=(MaterialProperties[])table.Clone(); custom[water].Density=liquidDensity;
            r.Materials.Upload(r.Context,custom);
            settings.Paused=false; settings.AirSimulation=false;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(immersed.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            coordinator.DispatchFrame(settings,[],1f/60);
            var after=Read(); int position=Array.FindIndex(after,c=>c.IsActive!=0 && c.MaterialIndex==grain);
            float density=sample.Item2>0?table[dry].Density+(table[grain].Density-table[dry].Density)*sample.Item2:table[dry].Density;
            int expected=Math.Sign(density-liquidDensity), actual=Math.Sign(position-(origin+w));
            Check(expected==actual,$"liquid buoyancy {sample.Item1} saturation={sample.Item2} liquid={liquidDensity} pos={actual} expected={expected}");
            Check(after[position].MoistureMass==immersed[origin+w].MoistureMass,"buoyancy lost moisture");
            Balance(immersed,after,"liquid buoyancy");
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_BUOYANCY material={sample.Item1} saturation={sample.Item2} density={density} liquid={liquidDensity} direction={actual}");
        }
        r.Materials.Upload(r.Context,table); settings.Paused=true;
        // Old hot wet v12 cells and a new reservoir larger than latent water
        // must round-trip without discarding the stored heat.
        var reservoir=new GridCell[n]; reservoir[origin]=Cell(table[coal].MoistureWetMaterialIndex,326.5f);
        reservoir[origin].MoistureMass=.35f; reservoir[origin].MoistureEnergy=.35f*table[water].TransitionAboveLatentHeat;
        string reservoirPath=Path.Combine(directory,"hot-wet-v12.json");
        Task.Run(()=>serializer.SaveAsync(reservoirPath,settings,(ushort)coal,new(w,r.Height,
            MemoryMarshal.AsBytes(reservoir.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult();
        var v12Json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(reservoirPath))!;
        Check(v12Json["Version"]!.GetValue<int>()==17,"reservoir writer version");
        v12Json["Version"]=12; File.WriteAllText(reservoirPath,v12Json.ToJsonString());
        string v12WorldPath=Path.ChangeExtension(reservoirPath,".world"); var v12Bytes=File.ReadAllBytes(v12WorldPath);
        v12Bytes=WorldCellCodecRegressionVerifier.RepackWorldPrefix(v12Bytes,n,48);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(v12Bytes.AsSpan(4,4),12);
        File.WriteAllBytes(v12WorldPath,v12Bytes);
        var migratedHot=Task.Run(()=>serializer.LoadAsync(reservoirPath,registry)).GetAwaiter().GetResult()!;
        var normalizedHot=MemoryMarshal.Cast<byte,GridCell>(migratedHot.World!.Grid).ToArray();
        Check(Math.Abs(normalizedHot[origin].Temperature-100)<.002,"v12 hot wet migration temperature");
        Check(normalizedHot[origin].MoistureEnergy>.35f*table[water].TransitionAboveLatentHeat,"excess wet heat disappeared");
        Balance(reservoir,normalizedHot,"v12 hot wet migration");
        Task.Run(()=>serializer.SaveAsync(reservoirPath,settings,(ushort)coal,new(w,r.Height,
            MemoryMarshal.AsBytes(normalizedHot.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult();
        var loadedReservoir=Task.Run(()=>serializer.LoadAsync(reservoirPath,registry)).GetAwaiter().GetResult()!;
        Check(loadedReservoir.World!.Grid.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(normalizedHot.AsSpan())),"reservoir round-trip");
        foreach(int offset in new[] {-1,-w,w}) normalizedHot[origin+offset]=Cell(fixture,100);
        Upload(normalizedHot);
        double retainedMass=Mass(normalizedHot),retainedEnergy=Energy(normalizedHot),releasedMass=0,releasedEnergy=0;
        for(uint tick=0;tick<100;tick++) {
            Contact(tick); if(tick%16==15) yield return r; normalizedHot=Read(); var output=normalizedHot[origin+1];
            if(output.IsActive!=0) {
                releasedMass+=output.Mass; releasedEnergy+=output.Mass*PhaseEnthalpy.SpecificEnergy(output,table);
                normalizedHot[origin+1]=default; Upload(normalizedHot);
            }
        }
        Check(normalizedHot[origin].MoistureMass==0 && normalizedHot[origin].Temperature>100,"reservoir dry heat did not return");
        Check(Math.Abs(Mass(normalizedHot)+releasedMass-retainedMass)<.0001 &&
            Math.Abs(Energy(normalizedHot)+releasedEnergy-retainedEnergy)<.01,"reservoir full drying ledger");

        // Observable bowl/pour, through the real brush, air and thermal clocks.
        // Save raw checkpoints for inspection, without touching the user's save.
        foreach(var mode in new[] {SimulationMode.Sandbox,SimulationMode.Simulation}) {
            var bowl=new GridCell[n];
            for(int x=110;x<=210;x++) bowl[151*w+x]=Cell(fixture);
            for(int y=90;y<=150;y++) foreach(int x in new[] {110,210}) bowl[y*w+x]=Cell(fixture);
            for(int y=115;y<=150;y++) for(int x=160-(y-115);x<=160+(y-115);x++) {
                bowl[y*w+x]=Cell(coal,600); bowl[y*w+x].Lifetime=1;
            }
            settings.Paused=false; settings.AirSimulation=true; settings.Mode=mode;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(bowl.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,true,true);
            bool wetted=false; int initialInterior=0,finalInterior=0;
            for(int frame=0;frame<900;frame++) {
                BrushDrawCommand[] brush=frame<120 ? [new() { X=160,Y=98,EndX=160,EndY=98,Radius=6,Density=1,
                    Mode=BrushCommandMode.Material,MaterialIndex=(ushort)water }] : [];
                coordinator.DispatchFrame(settings,brush,1f/60); if(frame%8==7) yield return r;
                if((frame+1)%60!=0) continue;
                var poured=Read(); wetted|=poured.Any(c=>c.MoistureMass>0);
                int interior=Enumerable.Range(0,n).Count(i=>i/w>=120 && i/w<=147 &&
                    Math.Abs(i%w-160)<=i/w-119 && poured[i].MoistureMass>.001f);
                if(frame==119) initialInterior=interior;
                if(frame==899) finalInterior=interior;
                Check(poured.Where(c=>c.MoistureMass>0).All(c=>c.Temperature<=100.01),"hot pour wet temperature "+mode+" "+frame);
                File.WriteAllBytes(Path.Combine(directory,$"pour-{mode}-{(frame+1)/60}s.grid"),MemoryMarshal.AsBytes(poured.AsSpan()).ToArray());
                Console.WriteLine($"PHYXEL_FUEL_MOISTURE_HOT_POUR mode={mode} seconds={(frame+1)/60} wet={poured.Count(c=>c.MoistureMass>0)} interior={interior} water={poured.Count(c=>c.IsActive!=0&&c.MaterialIndex==water)}");
            }
            Check(wetted,"hot pour did not wet surface "+mode);
            Check(finalInterior>initialInterior,"hot pour wet front did not grow inward "+mode);
        }
        File.WriteAllText(Path.Combine(directory,"pour-layout.txt"),$"{w} {r.Height} 56 {coal} {table[coal].MoistureWetMaterialIndex} {water} {steam} {fixture}");
        settings.Paused=true;
        var pocket=new GridCell[n];
        for(int k=0;k<2;k++) {
            int o=origin+k*8;
            for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=2;dx++)
                if(dy!=0||dx<0||dx>1) pocket[o+dy*w+dx]=Cell(fixture);
            pocket[o]=Cell(k==0?coal:registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder)); pocket[o+1]=Cell(water,20,.6f);
        }
        double[]? reference=null;
        foreach(bool air in new[] {false,true}) foreach(var mode in new[] {SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(int fps in new[] {30,60,100}) {
            settings.AirSimulation=air; settings.Mode=mode; settings.Paused=false;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(pocket.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            for(int frame=0;frame<fps*2;frame++) { coordinator.DispatchFrame(settings,[],1f/fps); if(frame%8==7) yield return r; }
            var after=Read(); double[] values=[after[origin].MoistureMass,after[origin+8].MoistureMass,Mass(after),Energy(after)];
            reference??=values;
            Check(values.Zip(reference,(a,b)=>Math.Abs(a-b)<.0001*Math.Max(1,Math.Abs(b))).All(v=>v),"frame cadence "+air+" "+mode+" "+fps);
            Check(values[0]>.3&&values[1]>.25,"actual scheduler missed moisture");
            settings.Paused=true; byte[] before=MemoryMarshal.AsBytes(after.AsSpan()).ToArray();
            for(int frame=0;frame<3;frame++) { coordinator.DispatchFrame(settings,[],1f/fps); if(frame%8==7) yield return r; }
            Check(before.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"pause altered moisture "+fps);
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_CLOCK mode={mode} air={air} fps={fps} coal={values[0]:F8} powder={values[1]:F8}");
        }
        // A narrow closed column keeps grains stationary while exercising
        // the production clocks, diffusion, save/load and pause together.
        var wickPocket=new GridCell[n];
        for(int dy=-2;dy<=12;dy++) foreach(int dx in new[] {-1,1}) wickPocket[origin+dy*w+dx]=Cell(fixture);
        wickPocket[origin-2*w]=Cell(fixture); wickPocket[origin+12*w]=Cell(fixture);
        wickPocket[origin-w]=Cell(water,20,2);
        for(int depth=0;depth<12;depth++) wickPocket[origin+depth*w]=Cell(coal);
        double[]? wickReference=null;
        foreach(var mode in new[] {SimulationMode.Sandbox,SimulationMode.Simulation}) foreach(int fps in new[] {30,60,100}) {
            settings.Paused=false; settings.AirSimulation=false; settings.Mode=mode;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(wickPocket.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            for(int frame=0;frame<fps*8;frame++) { coordinator.DispatchFrame(settings,[],1f/fps); if(frame%8==7) yield return r; }
            var after=Read();
            double[] values=Enumerable.Range(0,12).Select(depth=>(double)after[origin+depth*w].MoistureMass).Concat(new[] {Mass(after),Energy(after)}).ToArray();
            wickReference??=values;
            Check(values.Zip(wickReference,(a,b)=>Math.Abs(a-b)<.0001*Math.Max(1,Math.Abs(b))).All(v=>v),"wick frame cadence "+mode+" "+fps);
            Check(after[origin+3*w].MoistureMass>.001f,"production wick front stopped "+mode+" "+fps);
            Balance(wickPocket,after,"production wick "+mode+" "+fps);
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_WICK_CLOCK mode={mode} fps={fps} depth3={values[3]:F8} water={Mass(after):F8} energy={Energy(after):F8}");
        }
        Upload(wickPocket); for(uint tick=0;tick<80;tick++) { Contact(tick); if(tick%16==15) yield return r; }
        var half=Read(); settings.Paused=true;
        string wickSave=Path.Combine(directory,"wick-half.json");
        Task.Run(()=>serializer.SaveAsync(wickSave,settings,(ushort)coal,new(w,r.Height,
            MemoryMarshal.AsBytes(half.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult();
        var wickLoaded=Task.Run(()=>serializer.LoadAsync(wickSave,registry)).GetAwaiter().GetResult()!;
        for(int frame=0;frame<3;frame++) { coordinator.DispatchFrame(settings,[],1f/60); if(frame%8==7) yield return r; }
        Check(MemoryMarshal.AsBytes(half.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"wick pause changed front");
        for(uint tick=80;tick<160;tick++) { Contact(tick); if(tick%16==15) yield return r; } var continued=Read();
        Upload(MemoryMarshal.Cast<byte,GridCell>(wickLoaded.World!.Grid).ToArray());
        for(uint tick=80;tick<160;tick++) { Contact(tick); if(tick%16==15) yield return r; }
        Check(MemoryMarshal.AsBytes(continued.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"wick reload continuation changed front");
        if(legacyShader is null) foreach(float scale in new[] {.25f,.35f,.50f,.75f,.85f,1f}) {
            settings.ApplyScale(scale); settings.Paused=true;
            var resized=coordinator.DispatchFrame(settings,[new() { X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=(ushort)coal }],0);
            Check(resized.Grid.ReadBuffer.Description.SizeInBytes==resized.Width*resized.Height*Marshal.SizeOf<GridCell>(),"grid allocation stride "+scale);
            coordinator.ClearCurrentWorld(settings);
            var cleared=coordinator.DispatchFrame(settings,[],0);
            Check(MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(cleared,cleared.Grid.ReadBuffer)).ToArray()
                .All(c=>c.IsActive==0&&c.MoistureMass==0&&c.MoistureEnergy==0),"clear moisture "+scale);
            var restarted=coordinator.DispatchFrame(settings,[new() { X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=(ushort)coal }],0);
            Check(MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(restarted,restarted.Grid.ReadBuffer)).ToArray()
                .All(c=>c.MoistureMass==0&&c.MoistureEnergy==0),"restart restored cleared moisture "+scale);
            Console.WriteLine($"PHYXEL_FUEL_MOISTURE_ALLOCATION scale={scale} width={resized.Width} height={resized.Height} stride=56");
        }
        Console.WriteLine($"PHYXEL_FUEL_MOISTURE_RESULT passed={passed} checks={checks}");
        if(!passed) Environment.ExitCode=1;
    }
}
