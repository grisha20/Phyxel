using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

internal static class ReactionPulseRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        var settings=new SimulationSettings { Paused=true,Mode=SimulationMode.Simulation,OpenBoundaries=false };
        var r=coordinator.DispatchFrame(settings,[new() { X=40,EndX=40,Y=40,EndY=40,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }],0);
        var ctx=r.Context;int n=r.Width*r.Height,ai=20*r.AirWidth+20,gi=80*r.Width+80;
        void Check(bool yes,string text) { if(!yes) throw new InvalidOperationException(text); }
        byte[] Read(SharpDX.Direct3D11.Buffer b)=>AirInventoryRegressionVerifier.Read(r,b);
        var grid=new GridCell[n];uint powder=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
        grid[gi]=new() { IsActive=1,MaterialIndex=powder,Mass=1,Temperature=300 };
        ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);
        ctx.ClearUnorderedAccessView(r.ReactionPending.UnorderedView,new RawInt4());
        double initialEnergy=registry[CoreMaterialIds.Gunpowder].Properties.HeatCapacity*(300+273.15);
        for(uint tick=0;tick<7;tick++)
        {
            var c=new CombustionConstants { DeltaTime=1f/60,Width=(uint)r.Width,Height=(uint)r.Height,
                MaterialCount=(uint)registry.Count,TickIndex=tick,FiniteOxidizer=1,Reserved1=1 };
            ctx.UpdateSubresource(ref c,r.CombustionConstants);
            ctx.ComputeShader.Set(r.CombustionShader);ctx.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            ctx.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
            ctx.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
            for(int k=0;k<3;k++) ctx.ComputeShader.SetShaderResource(k,null);
            for(int k=0;k<6;k++) ctx.ComputeShader.SetUnorderedAccessView(k,null);
            var cell=MemoryMarshal.Cast<byte,GridCell>(Read(r.Grid.ReadBuffer))[gi];
            var source=MemoryMarshal.Cast<byte,Vector4>(Read(r.ReactionPending.Buffer))[gi];
            double remaining=cell.MaterialIndex==powder?cell.Mass:0;
            double retained=remaining*registry[CoreMaterialIds.Gunpowder].Properties.HeatCapacity*(cell.Temperature+273.15);
            double reacted=1-remaining;
            Check(Math.Abs(source.X-reacted)<1e-5,"Reaction pressure did not match consumed fuel");
            Check(Math.Abs(retained+source.Y-initialEnergy-reacted*2400)<.01,"Reaction heat partition lost energy");
        }
        var pending=Read(r.ReactionPending.Buffer);
        var packet=MemoryMarshal.Cast<byte,Vector4>(pending)[gi];
        Check(Math.Abs(packet.X-1)<1e-5 && Math.Abs(packet.Z-.9)<1e-5,"One grain did not issue one complete packet");
        var reactionFlame=MemoryMarshal.Cast<byte,GridCell>(Read(r.Grid.ReadBuffer))[gi];
        Check(reactionFlame.MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire) &&
            reactionFlame.Lifetime>=3 && reactionFlame.Lifetime<=4.22501f && reactionFlame.BodyId==0x80000000u,
            "Powder burnout did not keep its own TPT-style flame duration/oxidizer marker");
        var serializer=new SimulationStateSerializer();
        SimulationWorldSnapshot Capture()=>new(r.Width,r.Height,Read(r.Grid.ReadBuffer),Read(r.Air.Buffer),Read(r.GasMotion.Buffer),
            Read(r.Oxidizer.ReadBuffer),Read(r.AirThermal.Buffer),Read(r.ReactionPending.Buffer),Read(r.ReactionPulse.ReadBuffer));
        var before=Capture();
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/reaction-pulse";Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"mid-reaction.json");
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)powder,before,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!.World!;
        Check(loaded.ReactionPending!.AsSpan().SequenceEqual(pending),"Save lost pending reaction");
        serializer.ApplyWorldSnapshot(r,loaded);coordinator.RestoreWorldActivity(r,true,false,false,true);
        coordinator.DispatchAirSimulation(r,1,false,false);
        var next=Capture();
        Check(MemoryMarshal.Cast<byte,Vector4>(next.ReactionPending!).ToArray().All(s=>s==Vector4.Zero),"Mapped source was not consumed");
        Check(MemoryMarshal.Cast<byte,Vector4>(next.ReactionPulse!).ToArray().Any(s=>Math.Abs(s.X)>.01),"Pressure source was erased by normal projection");
        string wavePath=Path.Combine(dir,"during-pulse.json");
        Task.Run(()=>serializer.SaveAsync(wavePath,settings,(ushort)powder,next,registry)).GetAwaiter().GetResult();
        var waveLoaded=Task.Run(()=>serializer.LoadAsync(wavePath,registry)).GetAwaiter().GetResult()!.World!;
        Check(next.ReactionPulse!.AsSpan().SequenceEqual(waveLoaded.ReactionPulse) && next.Air!.AsSpan().SequenceEqual(waveLoaded.Air) &&
            next.GasMotion!.AsSpan().SequenceEqual(waveLoaded.GasMotion),"Save lost the running pressure/volume wave");
        serializer.ApplyWorldSnapshot(r,waveLoaded);coordinator.RestoreWorldActivity(r,true,false,false,true);
        coordinator.DispatchAirSimulation(r,2,false,false);var waveNext=Capture();
        serializer.ApplyWorldSnapshot(r,waveLoaded);coordinator.RestoreWorldActivity(r,true,false,false,true);
        coordinator.DispatchAirSimulation(r,2,false,false);var waveRepeated=Capture();
        Check(waveNext.ReactionPulse!.AsSpan().SequenceEqual(waveRepeated.ReactionPulse),"Running wave did not continue identically after loading");
        double stock=MemoryMarshal.Cast<byte,Vector4>(waveNext.ReactionPulse!).ToArray().Sum(s=>(double)s.W);
        Check(Math.Abs(stock-4.86)<1e-5,"Expansion stock was duplicated or did not decay once");
        serializer.ApplyWorldSnapshot(r,loaded);coordinator.RestoreWorldActivity(r,true,false,false,true);
        coordinator.DispatchAirSimulation(r,1,false,false);
        var repeated=Capture();
        Check(next.ReactionPulse!.AsSpan().SequenceEqual(repeated.ReactionPulse) && next.ReactionPending!.AsSpan().SequenceEqual(repeated.ReactionPending) &&
            next.Air!.AsSpan().SequenceEqual(repeated.Air) && next.AirThermal!.AsSpan().SequenceEqual(repeated.AirThermal),"Reload duplicated or changed next pulse step");
        var paused=Capture();coordinator.DispatchFrame(settings,[],1);
        Check(paused.ReactionPulse!.AsSpan().SequenceEqual(Read(r.ReactionPulse.ReadBuffer)) && paused.ReactionPending!.AsSpan().SequenceEqual(Read(r.ReactionPending.Buffer)),"Pause advanced reaction");
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        {
            settings.Mode=mode;coordinator.DispatchFrame(settings,[],1);
            Check(paused.ReactionPulse!.AsSpan().SequenceEqual(Read(r.ReactionPulse.ReadBuffer)) && paused.ReactionPending!.AsSpan().SequenceEqual(Read(r.ReactionPending.Buffer)),
                "Paused mode switch discarded or minted a reaction source");
        }
        // Pending gas trapped behind a solid must neither be consumed nor
        // mapped through the solid. It is released only when that cell opens.
        grid=new GridCell[n];grid[gi]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),Mass=7.8f,Temperature=20 };
        serializer.ApplyWorldSnapshot(r,new(r.Width,r.Height,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray(),ReactionPending:pending));
        coordinator.RestoreWorldActivity(r,true,false,false,true);
        coordinator.DispatchAirSimulation(r,2,false,false);
        Check(Read(r.ReactionPending.Buffer).AsSpan().SequenceEqual(pending),"Blocked pending source leaked through metal");
        grid[gi]=default;ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);
        coordinator.DispatchAirSimulation(r,3,false,false);
        Check(MemoryMarshal.Cast<byte,Vector4>(Read(r.ReactionPending.Buffer)).ToArray().All(s=>s==Vector4.Zero),"Opening trapped source did not release it");
        uint fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
        foreach(uint marker in new[]{0u,0x40000000u,0x80000000u})
        foreach(float temperature in new[]{20f,100f,101f})
        {
            grid=new GridCell[n];grid[gi]=new() { IsActive=1,MaterialIndex=fire,Mass=.75f,Temperature=temperature,Lifetime=2,BodyId=marker };
            ctx.UpdateSubresource(grid,r.Grid.ReadBuffer);
            SimulationDispatchCoordinator.DispatchTransientLifecycle(r,new() { DeltaTime=1f/60,Width=(uint)r.Width,Height=(uint)r.Height,
                MaterialCount=(uint)registry.Count,FiniteOxidizer=0 });
            var cooled=MemoryMarshal.Cast<byte,GridCell>(Read(r.Grid.ReadBuffer))[gi];
            bool extinguished=temperature<=100;
            Check(cooled.MaterialIndex==(extinguished?smoke:fire),"Cooling threshold disagrees for a flame origin marker");
            Check(Math.Abs(cooled.Mass-.75)<1e-6,"Cooling deleted flame packet mass");
            double beforeHeat=.75*registry[(ushort)fire].Properties.HeatCapacity*(temperature-20);
            double afterHeat=cooled.Mass*registry[(ushort)cooled.MaterialIndex].Properties.HeatCapacity*(cooled.Temperature-20);
            Check(Math.Abs(beforeHeat-afterHeat)<.0001,"Cooling decay lost sensible heat");
            if(extinguished) Check(cooled.BodyId==0,"Smoke retained a flame origin marker");
        }
        VerifyFrameCadence(coordinator, registry, serializer);
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_REACTION_PULSE_SUCCESS pressure={packet.X:F6} heat={packet.Y:F6} capacity={packet.Z:F6} ledger=PASS saveLoad=PASS pause=PASS trapped=PASS cooling=PASS cadence=PASS"));
    }

    // Exercise DispatchFrame, rather than only the individual GPU kernels.
    // An airborne grain used to fall once per rendered frame while reacting
    // at 60 Hz, changing both the source location and wave at 30/60/100 FPS.
    private static void VerifyFrameCadence(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationStateSerializer serializer)
    {
        uint powder = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
        foreach (var mode in new[] { SimulationMode.Sandbox, SimulationMode.Simulation })
        foreach (bool hot in new[] { false, true })
        foreach (bool useAir in new[] { true, false })
        {
            int[]? referencePositions = null;
            Vector4[]? referenceWave = null;
            foreach (int fps in new[] { 60, 30, 100 })
            {
                var settings = new SimulationSettings { Paused = true, Mode = mode,
                    SolidGravity = false, OpenBoundaries = false, AirSimulation = useAir };
                coordinator.ClearCurrentWorld(settings);
                var r = coordinator.DispatchFrame(settings, [new() { X = 40, EndX = 40, Y = 40, EndY = 40,
                    Radius = 1, Density = 1, Mode = BrushCommandMode.Material, MaterialIndex = powder }], 0);
                var grid = new GridCell[r.Width * r.Height];
                for (int x = 80; x <= 200; x += 8)
                    grid[40 * r.Width + x] = new() { IsActive = 1, MaterialIndex = powder,
                        Mass = 1, Temperature = hot ? 300 : 20 };
                serializer.ApplyWorldSnapshot(r, new(r.Width, r.Height,
                    MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));
                coordinator.RestoreWorldActivity(r, true, false, false, true);
                settings.Paused = false;
                int frames = fps / (hot ? 5 : 10);
                for (int frame = 0; frame < frames; frame++) coordinator.DispatchFrame(settings, [], 1f / fps);
                var after = MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
                int[] positions = Enumerable.Range(0, after.Length)
                    .Where(i => after[i].IsActive != 0 && after[i].MaterialIndex == powder).ToArray();
                var wave = MemoryMarshal.Cast<byte, Vector4>(AirInventoryRegressionVerifier.Read(r, r.ReactionPulse.ReadBuffer)).ToArray();
                if (hot && useAir && wave.All(v => v == Vector4.Zero))
                    throw new InvalidOperationException("Hot frame probe produced no reaction wave");
                if (!useAir && wave.Any(v => v != Vector4.Zero))
                    throw new InvalidOperationException("Air-off frame probe produced a pressure wave");
                if (coordinator.GasMotionTicks != (hot ? 12UL : 6UL))
                    throw new InvalidOperationException("Powder frame clock lost a physical tick");
                if (hot && positions.Length != 0)
                    throw new InvalidOperationException("Hot airborne powder did not burn out");
                if (!hot && (positions.Length != 16 || positions.Any(i => i / r.Width <= 40) ||
                    positions.Sum(i => after[i].Mass) != 16))
                    throw new InvalidOperationException("Cold pressure powder froze or lost mass");
                if (referencePositions is not null && !positions.SequenceEqual(referencePositions))
                    throw new InvalidOperationException("Cold powder trajectory depends on rendered FPS");
                if (referenceWave is not null)
                {
                    double difference = wave.Select((v, i) => (double)Vector4.Distance(v, referenceWave[i])).Sum();
                    double stock = referenceWave.Sum(v => (double)v.Length());
                    if (difference > Math.Max(1e-5, stock * .001))
                        throw new InvalidOperationException($"Airborne powder wave depends on FPS: {mode} {fps} difference={difference} stock={stock}");
                }
                referencePositions ??= positions;
                referenceWave ??= wave;
                settings.Paused = true;
                var paused = AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer);
                coordinator.DispatchFrame(settings, [], 1);
                if (!paused.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)))
                    throw new InvalidOperationException("Paused powder moved");
                Console.WriteLine($"PHYXEL_POWDER_CADENCE mode={mode} hot={hot} air={useAir} fps={fps} ticks={coordinator.GasMotionTicks} mass=PASS trajectory=PASS wave=PASS pause=PASS");
            }
        }
    }
}
