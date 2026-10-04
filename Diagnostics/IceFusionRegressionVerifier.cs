using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class IceFusionRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var settings = new SimulationSettings { Paused=true, AirSimulation=false, OpenBoundaries=false };
        var r=coordinator.DispatchFrame(settings,[new() { X=40,Y=40,EndX=40,EndY=40,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice) }],0);
        var table=registry.CreateGpuTable(); int w=r.Width,n=w*r.Height,origin=80*w+80;
        uint ice=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice), water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        string directory=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/ice-fusion";
        Directory.CreateDirectory(directory);
        bool passed=true; int checks=0;
        void Check(bool ok,string message) { checks++; if(!ok) { passed=false; Console.WriteLine("PHYXEL_ICE_CHECK_FAILED "+message); } }
        GridCell Cell(string id,float t,float mass=1,float progress=0) => new() {
            IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(id),Temperature=t,Mass=mass,Lifetime=progress };
        GridCell[] Read() => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Energy(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        double Mass(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass);
        void Upload(GridCell[] grid) {
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);
        }
        void Phase(uint tick=0) {
            var c=new PhaseTransitionConstants { Width=(uint)w,Height=(uint)r.Height,MaterialCount=(uint)table.Length,
                TickIndex=tick,TickCount=1 };
            var context=r.Context; context.UpdateSubresource(ref c,r.PhaseConstants);
            context.ComputeShader.Set(r.PhaseTransitionShader); context.ComputeShader.SetConstantBuffer(0,r.PhaseConstants);
            context.ComputeShader.SetShaderResource(0,r.Materials.View);
            context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.PhaseSummary.UnorderedView);
            context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            context.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<3;i++) context.ComputeShader.SetUnorderedAccessView(i,null);
            context.ComputeShader.Set(null);
        }
        void Balance(GridCell[] before,GridCell[] after,string label,double tolerance=.01) {
            double error=Energy(after)-Energy(before);
            Check(Math.Abs(error)<=tolerance,label+" energy "+error);
            Check(Math.Abs(Mass(after)-Mass(before))<=Math.Max(1e-5,Mass(before)*1e-5),label+" mass changed");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_ICE_BALANCE label={label} energyError={error:E6} mass={Mass(after):F6}"));
        }

        // Independent amounts of supplied/withdrawn heat, including excess
        // beyond a phase boundary and reversal before it is reached.
        foreach(var test in new[] {
            ("partial-melt",CoreMaterialIds.Ice,10f,0f,ice,0f,21f),
            ("partial-freeze",CoreMaterialIds.Water,-10f,0f,water,0f,-41.8f),
            ("paid-melt",CoreMaterialIds.Ice,0f,333.4f,water,0f,0f),
            ("paid-freeze",CoreMaterialIds.Water,0f,-333.4f,ice,0f,0f),
            ("excess-melt",CoreMaterialIds.Ice,200f,0f,water,(420f-333.4f)/4.18f,0f),
            ("excess-freeze",CoreMaterialIds.Water,-100f,0f,ice,(-418f+333.4f)/2.1f,0f) })
        {
            var input=new GridCell[n]; input[origin]=Cell(test.Item2,test.Item3,1,test.Item4);
            Upload(input); Phase(); var result=Read(); var c=result[origin];
            Check(c.MaterialIndex==test.Item5 && Math.Abs(c.Temperature-test.Item6)<.001 &&
                Math.Abs(c.Lifetime-test.Item7)<.002,test.Item1+" wrong phase/plateau/progress");
            Balance(input,result,test.Item1);
        }

        var cycle=Cell(CoreMaterialIds.Ice,-5); float cold=PhaseEnthalpy.SpecificEnergy(cycle,table);
        for(int i=0;i<100;i++) {
            foreach(float e in new[] { -166.7f,cold,83.6f,2674f+41.6f,376.2f,-166.7f,cold }) {
                PhaseEnthalpy.SetSpecificEnergy(ref cycle,e,table);
                for(int j=0;j<2;j++) PhaseTransitionRuntime.TryApply(ref cycle,table,out _);
                Check(Math.Abs(PhaseEnthalpy.SpecificEnergy(cycle,table)-e)<.001,"CPU three-phase cycle energy");
            }
            Check(cycle.MaterialIndex==ice && Math.Abs(cycle.Temperature+5)<.001,"CPU cycle failed to return to ice");
        }
        uint steam=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        var gpuCycle=Cell(CoreMaterialIds.Ice,-5);
        double maxCycleError=0;
        for(int i=0;i<10;i++) {
            foreach(var state in new[] { (-166.7f,ice), (cold,ice), (83.6f,water),
                (2715.6f,steam), (376.2f,water), (-166.7f,water), (cold,ice) }) {
                // Independent absolute energy references (ice -333.4, liquid 0,
                // vapour 2466), avoiding the CPU canonicalization helper.
                float reference=gpuCycle.MaterialIndex==ice ? -333.4f : gpuCycle.MaterialIndex==steam ? 2466f : 0;
                float auxiliary=gpuCycle.MaterialIndex==steam ? -gpuCycle.Lifetime : gpuCycle.Lifetime;
                gpuCycle.Temperature=(state.Item1-reference-auxiliary)/table[gpuCycle.MaterialIndex].HeatCapacity;
                var input=new GridCell[n]; input[origin]=gpuCycle; Upload(input);
                Phase(); Phase(); gpuCycle=Read()[origin];
                double error=Math.Abs(PhaseEnthalpy.SpecificEnergy(gpuCycle,table)-state.Item1);
                maxCycleError=Math.Max(maxCycleError,error);
                Check(gpuCycle.MaterialIndex==state.Item2 && error<.001,"GPU three-phase cycle changed phase/energy");
            }
        }
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_ICE_CYCLE gpuCycles=10 cpuCycles=100 maxEnergyError={maxCycleError:E6}"));

        // Real GPU contact through hot/cold metal, with no external energy.
        foreach(var test in new[] { ("hot-contact",CoreMaterialIds.Ice,0f,200f),
            ("cold-contact",CoreMaterialIds.Water,0f,-200f) }) {
            var grid=new GridCell[n]; grid[origin]=Cell(test.Item2,test.Item3);
            grid[origin+1]=Cell(CoreMaterialIds.Metal,test.Item4,7.8f);
            Upload(grid);
            for(uint i=0;i<400;i++) { coordinator.DispatchThermalDiffusion(r,false,i,false); Phase(i); }
            var after=Read(); var c=after[origin]; Balance(grid,after,test.Item1);
            Check(c.Temperature==0 && (test.Item1=="hot-contact" ? c.MaterialIndex==ice&&c.Lifetime>20 :
                c.MaterialIndex==water&&c.Lifetime< -20),test.Item1+" no latent plateau/contact");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_ICE_CONTACT label={test.Item1} temperature={c.Temperature:F6} progress={c.Lifetime:F6}"));
        }

        // The actual liquid-transfer kernel must carry freezing energy too.
        var mix=new GridCell[n]; mix[origin]=Cell(CoreMaterialIds.Water,0,.5f,-200);
        mix[origin+w]=Cell(CoreMaterialIds.Water,20,.5f); Upload(mix);
        var frame=new SimulationFrameConstants { Width=(uint)w,Height=(uint)r.Height,
            DispatchExtentX=(uint)w,DispatchExtentY=(uint)((r.Height+1)/2),SimulationPhase=0 };
        r.Context.UpdateSubresource(ref frame,r.FrameConstants);
        r.Context.ComputeShader.Set(r.CellularAutomataShader); r.Context.ComputeShader.SetConstantBuffer(0,r.FrameConstants);
        r.Context.ComputeShader.SetShaderResources(0,r.Materials.View,r.Air.View);
        r.Context.ComputeShader.SetUnorderedAccessView(0,r.Grid.ReadUnorderedView);
        r.Context.ComputeShader.SetUnorderedAccessView(3,r.CellMaterials.UnorderedView);
        r.Context.Dispatch((w+15)/16,((r.Height+1)/2+15)/16,1);
        for(int i=0;i<2;i++) r.Context.ComputeShader.SetShaderResource(i,null);
        for(int i=0;i<12;i++) r.Context.ComputeShader.SetUnorderedAccessView(i,null);
        r.Context.ComputeShader.Set(null);
        var mixed=Read(); Balance(mix,mixed,"freezing-liquid-transfer");
        Check(mixed[origin].IsActive==0 && mixed[origin+w].Temperature==0 &&
            Math.Abs(mixed[origin+w].Lifetime+58.2f)<.001,"Liquid transfer did not canonicalize freezing mixture");

        // Partial melting/freezing round-trip, including refusal of negatives
        // on non-fusion matter and legacy negative auxiliary data.
        var save=new GridCell[n]; save[origin]=Cell(CoreMaterialIds.Ice,0,.92f,100);
        save[origin+1]=Cell(CoreMaterialIds.Water,0,1,-100);
        var serializer=new SimulationStateSerializer();
        var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(save.AsSpan()).ToArray());
        string path=Path.Combine(directory,"partial-fusion.json");
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)ice,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        Check(snapshot.Grid.AsSpan().SequenceEqual(loaded.World!.Grid),"Partial fusion save/load not exact");
        // Resume the same GPU contact from memory and from loaded bytes.
        GridCell[] Continuation(GridCell[] input) {
            input[origin+2]=Cell(CoreMaterialIds.Metal,150,7.8f);
            Upload(input);
            for(uint i=0;i<100;i++) { coordinator.DispatchThermalDiffusion(r,false,i,false); Phase(i); }
            return Read();
        }
        var memoryContinuation=Continuation((GridCell[])save.Clone());
        var reloadContinuation=Continuation(MemoryMarshal.Cast<byte,GridCell>(loaded.World.Grid).ToArray());
        Check(MemoryMarshal.AsBytes(memoryContinuation.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(reloadContinuation.AsSpan())),"Reload changed subsequent fusion evolution");
        // Independently specified heat reversal on actual GPU phase passes.
        foreach(var state in new[] { (CoreMaterialIds.Ice,100f,ice), (CoreMaterialIds.Water,-100f,water) }) {
            var reverse=new GridCell[n]; reverse[origin]=Cell(state.Item1,0,1,state.Item2);
            Upload(reverse); Phase(); var partial=Read();
            Check(partial[origin].MaterialIndex==state.Item3 && partial[origin].Lifetime==state.Item2,
                "Partial progress changed without heat");
            // Remove exactly the supplied latent Q (or return withdrawn Q).
            reverse[origin].Temperature=-state.Item2/table[state.Item3].HeatCapacity;
            Upload(reverse); Phase(); var reverted=Read()[origin];
            Check(reverted.MaterialIndex==state.Item3 && Math.Abs(reverted.Temperature)<.001 &&
                Math.Abs(reverted.Lifetime)<.001,"GPU partial transition cannot reverse");
        }
        var bad=(GridCell[])save.Clone(); bad[origin+1]=Cell(CoreMaterialIds.Smoke,20,1,-1);
        bool rejected=false;
        try { Task.Run(()=>serializer.SaveAsync(Path.Combine(directory,"invalid-negative.json"),settings,(ushort)ice,
            new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(bad.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult(); }
        catch(InvalidDataException) { rejected=true; }
        Check(rejected,"Negative smoke lifetime accepted");

        // Whole-frame scheduling, six real cadences/modes. A closed insulating
        // pocket contains water/ice; metal's finite reservoir is the only Q.
        var cadence=new GridCell[n];
        for(int y=-1;y<=1;y++) for(int x=-1;x<=2;x++)
            if(y!=0 || x<0 || x>1) cadence[origin+y*w+x]=Cell(CoreMaterialIds.Fixture,20,100);
        cadence[origin]=Cell(CoreMaterialIds.Ice,0);
        cadence[origin+1]=Cell(CoreMaterialIds.Metal,200,7.8f);
        var fixture=table[registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture)];
        uint fixtureIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        table[fixtureIndex].ThermalConductivity=0; r.Materials.Upload(r.Context,table);
        float? referenceProgress=null;
        foreach(var mode in new[] { SimulationMode.Sandbox,SimulationMode.Simulation }) foreach(int fps in new[] { 30,60,100 }) {
            Upload(cadence); coordinator.RestoreWorldActivity(r,true,false,false);
            settings.Paused=false; settings.Mode=mode;
            for(int i=0;i<fps*3;i++) coordinator.DispatchFrame(settings,[],1f/fps);
            var after=Read(); Balance(cadence,after,$"cadence-{mode}-{fps}",.03);
            float p=after[origin].Lifetime;
            Check(after[origin].MaterialIndex==ice && after[origin].Temperature==0 && p>20,"Frame schedule did not melt partially");
            referenceProgress ??=p;
            Check(Math.Abs(p-referenceProgress.Value)<1,"FPS/mode changed finite heat uptake by >1 energy unit");
            settings.Paused=true; var beforePause=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);
            coordinator.DispatchFrame(settings,[],1);
            Check(beforePause.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)),"Paused fusion changed grid");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_ICE_CADENCE mode={mode} fps={fps} progress={p:F6} material={after[origin].MaterialIndex} iceT={after[origin].Temperature:F6} metalT={after[origin+1].Temperature:F6} ticks={coordinator.ThermalTicks}"));
        }
        table[fixtureIndex]=fixture; r.Materials.Upload(r.Context,table);
        Console.WriteLine($"PHYXEL_ICE_FUSION_RESULT passed={passed} checks={checks}");
        if(!passed) throw new InvalidOperationException("Ice fusion acceptance failed.");
    }
}
