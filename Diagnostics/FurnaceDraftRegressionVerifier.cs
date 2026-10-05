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
        string path=Environment.GetEnvironmentVariable("PHYXEL_DRAFT_SCENE")??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Phyxel","Начало паровая печь.json");
        var serializer=new SimulationStateSerializer();
        var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        var world=loaded.World!;
        if(world.Width!=672||world.Height!=394)throw new InvalidDataException("Measurement regions require user's 672x394 furnace.");
        var rates=Environment.GetEnvironmentVariable("PHYXEL_DRAFT_MATRIX")=="1"?new[]{30,60,100}:new[]{60};
        var modes=Environment.GetEnvironmentVariable("PHYXEL_DRAFT_MATRIX")=="1"?
            new[]{SimulationMode.Simulation,SimulationMode.Sandbox}:new[]{SimulationMode.Simulation};
        int seconds=int.Parse(Environment.GetEnvironmentVariable("PHYXEL_DRAFT_SECONDS")??"60");
        var rows=new List<object>();
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
            smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),co2=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Co2),
            metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),molten=registry.GetRequiredRuntimeIndex("core:molten_metal");
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
                    int burning=0,flames=0;double mass=0;
                    for(int y=0;y<r.Height;y++)for(int x=0;x<r.Width;x++)
                    {
                        var c=grid[y*r.Width+x];if(c.IsActive==0)continue;
                        if(c.MaterialIndex==coal){mass+=c.Mass;if(c.Lifetime>0)burning++;}
                        if(c.MaterialIndex==fire)flames++;
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
                    }
                }
                if(frame%8==0)yield return r;
            }
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

    private static void VerifySparseRoof(GpuSimulationResources r,MaterialRegistry registry,
        SharpDX.Direct3D11.ComputeShader shader,bool old)
    {
        int w=r.Width,h=r.Height,index=100*w+100;
        uint fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),
            smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        foreach(string caseName in new[]{"sparse","dense","closed","liquid"})
        {
            var cells=new GridCell[w*h];var map=new uint[w*h];
            for(int x=80;x<=160;x++)cells[90*w+x]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=20};
            if(caseName=="closed")for(int y=91;y<=110;y++)
                cells[y*w+80]=cells[y*w+160]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=20};
            if(caseName=="dense")for(int y=91;y<100;y++)cells[y*w+100]=new(){IsActive=1,MaterialIndex=smoke,Mass=1,Temperature=600};
            if(caseName=="liquid")cells[95*w+100]=new(){IsActive=1,MaterialIndex=water,Mass=1,Temperature=20};
            cells[index]=new(){IsActive=1,MaterialIndex=fire,Mass=1,Temperature=600,Lifetime=2};
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
            var motion=MemoryMarshal.Cast<byte,GasMotionState>(AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer))[index];
            bool open=caseName=="dense"||caseName=="sparse"&&!old;
            bool pass=open?motion.VelocityX<-3.5f:Math.Abs(motion.VelocityX)<.001;
            Console.WriteLine($"PHYXEL_DRAFT_ROOF old={old} case={caseName} vx={motion.VelocityX:R} pass={pass}");
            if(!pass&&Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE")!="1")throw new InvalidDataException("Roof jet: "+caseName);
        }
    }
}
