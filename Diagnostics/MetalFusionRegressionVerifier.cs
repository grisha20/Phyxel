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

internal static class MetalFusionRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        const string moltenId="core:molten_metal";
        var settings=new SimulationSettings { Paused=true, AirSimulation=false, OpenBoundaries=false };
        var r=coordinator.DispatchFrame(settings,[new() { X=40,Y=40,EndX=40,EndY=40,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }],0);
        var table=registry.CreateGpuTable();
        int w=r.Width,n=w*r.Height,origin=80*w+80;
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal), molten=registry.GetRequiredRuntimeIndex(moltenId);
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        string directory=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/metal-fusion";
        Directory.CreateDirectory(directory);
        bool passed=true; int checks=0;
        void Check(bool ok,string message) { checks++; if(!ok) { passed=false; Console.WriteLine("PHYXEL_METAL_CHECK_FAILED "+message); } }
        GridCell Cell(string id,float t,float mass=1,float progress=0) => new() {
            IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(id),Temperature=t,Mass=mass,Lifetime=progress };
        GridCell[] Read() => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        // Independent metal energy contract; this same observer detects the
        // old unflagged JSON's energy creation, not just implementation parity.
        double Specific(GridCell c) => c.MaterialIndex==metal ? .13*c.Temperature+c.Lifetime :
            c.MaterialIndex==molten ? .50*c.Temperature+c.Lifetime : PhaseEnthalpy.SpecificEnergy(c,table);
        double Energy(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>c.Mass*Specific(c));
        double Mass(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass);
        void Upload(GridCell[] grid) {
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);
        }
        void Phase(uint tick=0) {
            var c=new PhaseTransitionConstants { Width=(uint)w,Height=(uint)r.Height,MaterialCount=(uint)table.Length,TickIndex=tick,TickCount=1 };
            r.Context.UpdateSubresource(ref c,r.PhaseConstants);
            r.Context.ComputeShader.Set(r.PhaseTransitionShader);
            r.Context.ComputeShader.SetConstantBuffer(0,r.PhaseConstants);
            r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.PhaseSummary.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            r.Context.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<3;i++) r.Context.ComputeShader.SetUnorderedAccessView(i,null);
            r.Context.ComputeShader.Set(null);
        }
        void Balance(GridCell[] before,GridCell[] after,string label,double tolerance=.01) {
            double error=Energy(after)-Energy(before);
            Check(Math.Abs(error)<=tolerance,label+" energy "+error);
            Check(Math.Abs(Mass(after)-Mass(before))<=Math.Max(1e-5,Mass(before)*1e-5),label+" mass changed");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_METAL_BALANCE label={label} energyError={error:E6} mass={Mass(after):F6}"));
        }
        foreach(var test in new[] {
            ("partial-melt",CoreMaterialIds.Metal,1010f,0f,metal,1000f,1.3f),
            ("partial-freeze",moltenId,940f,0f,molten,950f,-5f),
            ("paid-melt",CoreMaterialIds.Metal,1000f,370f,molten,1000f,0f),
            ("paid-freeze",moltenId,950f,-351.5f,metal,950f,0f),
            ("excess-melt",CoreMaterialIds.Metal,5000f,0f,molten,1300f,0f),
            ("excess-freeze",moltenId,100f,0f,metal,50f/.13f,0f) })
        {
            var input=new GridCell[n]; input[origin]=Cell(test.Item2,test.Item3,7.8f,test.Item4);
            input[origin].BodyId=123;
            Upload(input); Phase(); var result=Read(); var c=result[origin];
            Check(c.MaterialIndex==test.Item5 && Math.Abs(c.Temperature-test.Item6)<.003 &&
                Math.Abs(c.Lifetime-test.Item7)<.003,test.Item1+" wrong phase/plateau/progress");
            if(c.MaterialIndex!=input[origin].MaterialIndex)
                Check(c.BodyId==0 && c.RestFrames==0,test.Item1+" retained stale body/rest state");
            Balance(input,result,test.Item1);
        }

        foreach(bool gpu in new[] { false,true }) {
            var c=Cell(CoreMaterialIds.Metal,20); double maxError=0;
            for(int i=0;i<20;i++) foreach(var step in new[] {
                (230f,metal), (2.6f,metal), (650f,molten), (375f,molten), (525f,molten), (2.6f,metal) }) {
                // Q is independently specified as absolute h, including a
                // reversal before conversion. Do not use SetSpecificEnergy.
                c.Temperature=(step.Item1-c.Lifetime)/(c.MaterialIndex==metal?.13f:.5f);
                if(gpu) { var input=new GridCell[n]; input[origin]=c; Upload(input); Phase(); c=Read()[origin]; }
                else PhaseTransitionRuntime.TryApply(ref c,table,out _);
                double error=Math.Abs(Specific(c)-step.Item1); maxError=Math.Max(maxError,error);
                Check(error<.003 && c.MaterialIndex==step.Item2,"Cycle phase/energy failed "+gpu);
            }
            Check(c.MaterialIndex==metal && Math.Abs(c.Temperature-20)<.003,"Cycle did not return to cold metal");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_METAL_CYCLE gpu={gpu} cycles=20 maxEnergyError={maxError:E6}"));
        }

        foreach(var test in new[] { ("hot-contact",CoreMaterialIds.Metal,1000f,1100f,7.8f,3f),
            ("cold-contact",moltenId,950f,900f,1f,3f) }) {
            var grid=new GridCell[n]; grid[origin]=Cell(test.Item2,test.Item3,test.Item5);
            grid[origin+1]=Cell(CoreMaterialIds.Stone,test.Item4,test.Item6); Upload(grid);
            for(uint i=0;i<400;i++) { coordinator.DispatchThermalDiffusion(r,false,i,false); Phase(i); }
            var after=Read(); var c=after[origin]; Balance(grid,after,test.Item1);
            bool hot=test.Item1=="hot-contact";
            Check(c.MaterialIndex==(hot?metal:molten) && Math.Abs(c.Temperature-(hot?1000:950))<.003 &&
                (hot?c.Lifetime>20:c.Lifetime< -20),test.Item1+" missing finite-reservoir plateau");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_METAL_CONTACT label={test.Item1} temperature={c.Temperature:F6} progress={c.Lifetime:F6}"));
        }

        var mix=new GridCell[n]; mix[origin]=Cell(moltenId,950,.5f,-200);
        mix[origin+w]=Cell(moltenId,1050,.5f); Upload(mix);
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
        var mixed=Read(); Balance(mix,mixed,"molten-liquid-transfer");
        Check(mixed[origin].IsActive==0 && Math.Abs(mixed[origin+w].Mass-1)<1e-6 &&
            Math.Abs(mixed[origin+w].Temperature-950)<.003 && Math.Abs(mixed[origin+w].Lifetime+75)<.003,
            "Liquid merge lost solidification energy");

        var save=new GridCell[n]; save[origin]=Cell(CoreMaterialIds.Metal,1000,7.8f,100);
        save[origin+1]=Cell(moltenId,950,1,-100);
        var serializer=new SimulationStateSerializer();
        string path=Path.Combine(directory,"partial-metal.json");
        var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(save.AsSpan()).ToArray());
        LoadedSimulationScene? loaded=null;
        try {
            Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)metal,snapshot,registry)).GetAwaiter().GetResult();
            loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult();
        } catch(InvalidDataException exception) {
            Console.WriteLine("PHYXEL_METAL_SAVE_REJECTED "+exception.Message);
        }
        Check(loaded?.World is { } loadedWorld && snapshot.Grid.AsSpan().SequenceEqual(loadedWorld.Grid),
            "Partial metal progress not saved exactly");
        GridCell[] Continue(GridCell[] input) {
            Upload(input);
            for(uint i=0;i<100;i++) { coordinator.DispatchThermalDiffusion(r,false,i,false); Phase(i); }
            return Read();
        }
        var memory=Continue((GridCell[])save.Clone());
        var reload=loaded?.World is { } world ? Continue(MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray()) : [];
        Check(reload.Length==memory.Length && MemoryMarshal.AsBytes(memory.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(reload.AsSpan())),
            "Reload changed subsequent metal contact evolution");

        // Production contact coefficients: heat must pass through the metal
        // separating molten stock from water, paying water's boiling progress.
        // Finite stock chosen to partially boil, so there is no steam ambient
        // heat sink in this isolated energy ledger.
        var wet=new GridCell[n]; wet[origin]=Cell(moltenId,1000,1);
        wet[origin+1]=Cell(CoreMaterialIds.Metal,200,7.8f);
        wet[origin+2]=Cell(CoreMaterialIds.Water,20); Upload(wet);
        for(uint i=0;i<800;i++) { coordinator.DispatchThermalDiffusion(r,false,i,false); Phase(i); }
        var wetAfter=Read(); Balance(wet,wetAfter,"molten-metal-water",Math.Abs(Energy(wet))*.00005);
        var water=wetAfter[origin+2];
        Check(water.MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water) &&
            Math.Abs(water.Temperature-100)<.003 && water.Lifetime>100,"Molten heat failed to reach water through metal");
        Check(wetAfter[origin+1].MaterialIndex==metal && Math.Abs(water.Mass-1)<1e-6,
            "Wet separator or water mass changed unexpectedly");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_METAL_WATER temperature={water.Temperature:F6} boilProgress={water.Lifetime:F6}"));

        // Actual full-frame clocks, insulated finite-reservoir pocket.
        var pocket=new GridCell[n];
        for(int y=-1;y<=1;y++) for(int x=-1;x<=2;x++) if(y!=0 || x<0 || x>1)
            pocket[origin+y*w+x]=Cell(CoreMaterialIds.Fixture,20,1);
        pocket[origin]=Cell(CoreMaterialIds.Metal,1000,7.8f);
        pocket[origin+1]=Cell(CoreMaterialIds.Stone,1100,3);
        var originalFixture=table[fixture]; table[fixture].ThermalConductivity=0; r.Materials.Upload(r.Context,table);
        float? referenceProgress=null;
        foreach(var mode in new[] { SimulationMode.Sandbox,SimulationMode.Simulation }) foreach(int fps in new[] {30,60,100}) {
            Upload(pocket); coordinator.RestoreWorldActivity(r,true,false,false);
            settings.Paused=false; settings.Mode=mode;
            for(int i=0;i<fps*3;i++) coordinator.DispatchFrame(settings,[],1f/fps);
            var after=Read(); Balance(pocket,after,$"cadence-{mode}-{fps}");
            float p=after[origin].Lifetime;
            Check(after[origin].MaterialIndex==metal && Math.Abs(after[origin].Temperature-1000)<.003 && p>20,
                "Frame schedule did not melt partially");
            referenceProgress ??=p;
            Check(Math.Abs(p-referenceProgress.Value)<1,"FPS/mode changed finite heat uptake by >1");
            settings.Paused=true; var frozen=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);
            coordinator.DispatchFrame(settings,[],1);
            Check(frozen.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)),"Paused metal changed grid");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_METAL_CADENCE mode={mode} fps={fps} progress={p:F6} ticks={coordinator.ThermalTicks}"));
        }

        // Melted mass must fall and later solidify as a movable body. Same
        // geometric chamber at each cadence; no external heat or wall losses.
        foreach(var mode in new[] { SimulationMode.Sandbox,SimulationMode.Simulation }) foreach(int fps in new[] {30,60,100}) {
            var chamber=new GridCell[n];
            for(int y=70;y<=105;y++) for(int x=70;x<=90;x++) if(y==70||y==105||x==70||x==90)
                chamber[y*w+x]=Cell(CoreMaterialIds.Fixture,20);
            for(int x=77;x<=82;x++) chamber[80*w+x]=Cell(CoreMaterialIds.Metal,1000,.8f,370);
            Upload(chamber); Phase(); var melted=Read(); Balance(chamber,melted,"body-melt");
            Check(melted.Count(c=>c.IsActive!=0&&c.MaterialIndex==molten)==6,"Metal body did not become molten");
            coordinator.RestoreWorldActivity(r,true,false,false); settings.Mode=mode; settings.Paused=false;
            for(int i=0;i<fps*2;i++) coordinator.DispatchFrame(settings,[],1f/fps);
            var settled=Read(); Balance(chamber,settled,$"flow-{mode}-{fps}",Math.Max(.01,Math.Abs(Energy(chamber))*.00005));
            double familyMass=0,weightedY=0;
            for(int i=0;i<n;i++) if(settled[i].IsActive!=0 && settled[i].MaterialIndex==molten) {
                familyMass+=settled[i].Mass; weightedY+=settled[i].Mass*(i/w);
                settled[i].Temperature=950; settled[i].Lifetime=-351.5f;
            }
            Check(Math.Abs(familyMass-4.8)<.00005 && weightedY/familyMass>100,"Molten body failed to flow down/conserve mass");
            var cooled=(GridCell[])settled.Clone(); Upload(cooled); Phase(); var solid=Read(); Balance(cooled,solid,"body-freeze");
            Check(solid.Where(c=>c.IsActive!=0&&c.MaterialIndex!=fixture).All(c=>c.MaterialIndex==metal&&c.BodyId==0&&c.RestFrames==0),
                "Solidification failed to restore movable-solid normalization");
            settings.SolidGravity=true; coordinator.RestoreWorldActivity(r,true,false,false);
            for(int i=0;i<fps;i++) coordinator.DispatchFrame(settings,[],1f/fps);
            var body=Read(); Balance(solid,body,"solid-body",Math.Max(.01,Math.Abs(Energy(solid))*.00005));
            Check(body.Any(c=>c.IsActive!=0&&c.MaterialIndex==metal&&c.BodyId!=0),"Solidified metal not recognized as a body");
            settings.SolidGravity=false;
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_METAL_FLOW mode={mode} fps={fps} mass={familyMass:F6} averageY={weightedY/familyMass:F6}"));
        }
        // A still-solid, partially melting body carries latent progress while
        // the actual solid solver moves it, independently of liquid transfer.
        var partialBody=new GridCell[n];
        for(int x=70;x<=90;x++) partialBody[105*w+x]=Cell(CoreMaterialIds.Fixture,20);
        partialBody[80*w+80]=Cell(CoreMaterialIds.Metal,1000,7.8f,100);
        Upload(partialBody); coordinator.RestoreWorldActivity(r,true,false,false);
        settings.SolidGravity=true; settings.Paused=false;
        for(int i=0;i<120;i++) coordinator.DispatchFrame(settings,[],1f/60);
        var movedPartial=Read(); Balance(partialBody,movedPartial,"partial-solid-body");
        var partialCells=movedPartial.Select((c,i)=>(c,i)).Where(v=>v.c.IsActive!=0&&v.c.MaterialIndex==metal).ToArray();
        Check(partialCells.Length==1 && partialCells[0].i/w>100 &&
            Math.Abs(partialCells[0].c.Lifetime-100)<.003 && Math.Abs(partialCells[0].c.Temperature-1000)<.003,
            "Moving partial solid lost latent progress");
        table[fixture]=originalFixture; r.Materials.Upload(r.Context,table);
        Console.WriteLine($"PHYXEL_METAL_FUSION_RESULT passed={passed} checks={checks}");
        if(!passed) throw new InvalidOperationException("Metal fusion acceptance failed.");
    }
}
