using System;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

internal static class FurnaceCombustionRegressionVerifier
{
    public static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var resources = coordinator.DispatchFrame(new SimulationSettings { Paused = true },
            [new() { X=40, EndX=40, Y=40, EndY=40, Radius=1, Density=1,
                Mode=BrushCommandMode.Material, MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }], 0);
        int w=resources.Width, h=resources.Height, n=w*h, index=80*w+80;
        var context=resources.Context;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        (double Burn, float Gas, float Rise) Reaction(string id, float oxygen, bool oneFace, bool finite,
            float mass = 0, bool noFace = false, bool latched = false)
        {
            var material=registry[id].Properties;
            var grid=new GridCell[n];
            grid[index]=new() { IsActive=1, MaterialIndex=registry.GetRequiredRuntimeIndex(id),
                Mass=mass>0?mass:material.Density, Temperature=material.IgnitionTemperature+10,
                Lifetime=latched?1:0 };
            var available=new float[n];
            foreach (int neighbor in new[] { index-1, index+1, index-w, index+w })
            {
                if (noFace || (oneFace && neighbor!=index+1))
                    grid[neighbor]=new() { IsActive=1, MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal), Mass=7.8f, Temperature=20 };
                else available[neighbor]=oxygen;
            }
            context.UpdateSubresource(grid,resources.Grid.ReadBuffer);
            context.UpdateSubresource(available,resources.OxidizerAvailable.Buffer);
            context.ClearUnorderedAccessView(resources.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            context.ClearUnorderedAccessView(resources.EmissionRequests.UnorderedView,new RawInt4());
            context.ClearUnorderedAccessView(resources.OxidizerDemand.UnorderedView,new RawInt4());
            var constants=new CombustionConstants { Width=(uint)w, Height=(uint)h,
                MaterialCount=(uint)registry.Materials.Count, DeltaTime=1f/60, TickIndex=1, FiniteOxidizer=finite?1u:0u };
            context.UpdateSubresource(ref constants,resources.CombustionConstants);
            context.ComputeShader.Set(resources.CombustionShader);
            context.ComputeShader.SetConstantBuffer(0,resources.CombustionConstants);
            context.ComputeShader.SetShaderResources(0,resources.Materials.View,resources.Emissions.View,resources.OxidizerAvailable.View);
            context.ComputeShader.SetUnorderedAccessViews(0,resources.Grid.ReadUnorderedView,resources.CombustionSummary.UnorderedView,
                resources.EmissionClaims.UnorderedView,resources.EmissionRequests.UnorderedView,resources.OxidizerDemand.UnorderedView);
            context.Dispatch((w+15)/16,(h+15)/16,1);
            for(int i=0;i<3;i++) context.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<5;i++) context.ComputeShader.SetUnorderedAccessView(i,null);
            var after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(resources,resources.Grid.ReadBuffer));
            var requests=MemoryMarshal.Cast<byte,EmissionRequest>(AirInventoryRegressionVerifier.Read(resources,resources.EmissionRequests.Buffer));
            var demand=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(resources,resources.OxidizerDemand.Buffer));
            double burn=(mass>0?mass:material.Density)-after[index].Mass;
            Check(Math.Abs(demand[index]-(finite?burn*registry.CreateEmissionGpuTable()[registry.GetRequiredRuntimeIndex(id)].OxidizerPerMass:0))<2e-6,"Demand disagrees with actual fuel loss");
            return (burn,requests[n*2+index].Mass,after[index].Temperature-material.IgnitionTemperature-10);
        }
        foreach (string id in new[] { CoreMaterialIds.Coal, CoreMaterialIds.StoneCoal, CoreMaterialIds.Wood })
        {
            var full=Reaction(id,1,false,true);
            var surface=Reaction(id,1,true,true);
            var weak=Reaction(id,.25f,false,true);
            var empty=Reaction(id,0,false,true);
            var sandbox=Reaction(id,0,false,false);
            Check(Math.Abs(full.Burn-surface.Burn)<2e-7,"A fresh-air surface burns slower because of walls");
            Check(Math.Abs(weak.Burn/full.Burn-.25)<.003,"Quarter oxygen did not reduce the actual reaction");
            Check(Math.Abs(weak.Gas/full.Gas-.25)<.001,"Starved fuel emitted full-rate CO2");
            Check(empty.Burn==0 && empty.Gas==0,"Empty oxygen produced reaction products");
            float sandboxGas=registry.CreateEmissionGpuTable()[registry.GetRequiredRuntimeIndex(id)].GasRate/60;
            Check(Math.Abs(sandbox.Burn-full.Burn)<2e-7 && Math.Abs(sandbox.Gas-sandboxGas)<1e-7,"Sandbox changed its full-rate reaction");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_FURNACE_REACTION fuel={id} full={full.Burn:F8} surface={surface.Burn:F8} weak={weak.Burn:F8} gasRatio={weak.Gas/full.Gas:F4} sandbox=PASS"));
        }
        // Test the actual Mass1 emitted by the brush, rather than Density.2.
        var settings = new SimulationSettings { Paused = true };
        coordinator.ClearCurrentWorld(settings);
        resources = coordinator.DispatchFrame(settings,
            [new() { X=80,EndX=80,Y=80,EndY=80,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal) }],0);
        var painted = MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(resources,resources.Grid.ReadBuffer));
        Check(painted[index].Mass == 1, "Coal test must exercise actual brush Mass1");
        var calibrated = Reaction(CoreMaterialIds.Coal,1,false,true,mass:painted[index].Mass);
        Check(Math.Abs(calibrated.Burn-.06/60)<2e-7 && Math.Abs(calibrated.Rise-6)<.001,
            "Coal calibration changed the previous 360/s full-rate heating power");
        Check(Math.Abs(calibrated.Burn*registry.CreateEmissionGpuTable()[registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal)].OxidizerPerMass-.24/60)<2e-7,
            "Coal calibration changed full-rate oxidizer consumption");
        foreach(string id in new[]{CoreMaterialIds.Coal,CoreMaterialIds.StoneCoal})
        {
            var enclosed = Reaction(id,0,false,false,mass:1,noFace:true);
            var retained = Reaction(id,0,false,false,mass:1,noFace:true,latched:true);
            var starved = Reaction(id,0,false,true,mass:1,noFace:true,latched:true);
            Check(enclosed.Burn==0 && retained.Burn>0 && starved.Burn==0,
                "Coal surface ignition/latch/finite oxygen contract failed");
        }
        VerifyCoalLifetime(coordinator,registry,resources,index);
        // A single gas pair pass must preserve mass, heat and momentum while
        // clearing the redundant cell; nominal painted packets stay separate.
        foreach (bool finite in new[] { true, false }) foreach (bool nominal in new[] { false, true })
        {
            var grid=new GridCell[n]; var motion=new GasMotionState[n];
            float density=registry[CoreMaterialIds.Co2].Properties.Density;
            float mass=nominal?density:density*.05f;
            grid[index]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Co2),Mass=mass,Temperature=100 };
            grid[index+1]=grid[index]; grid[index+1].Temperature=500;
            motion[index]=new() { VelocityX=1 }; motion[index+1]=new() { VelocityX=3 };
            context.UpdateSubresource(grid,resources.Grid.ReadBuffer);
            context.UpdateSubresource(motion,resources.GasMotion.Buffer);
            var frame=new SimulationFrameConstants { Width=(uint)w,Height=(uint)h,SimulationPhase=82,
                DispatchExtentX=(uint)((w+1)/2),DispatchExtentY=(uint)h,SolidPass=finite?1u:0u };
            context.UpdateSubresource(ref frame,resources.FrameConstants);
            context.ComputeShader.Set(resources.CellularAutomataShader);
            context.ComputeShader.SetConstantBuffer(0,resources.FrameConstants);
            // This isolated pair dispatch bypasses the tick's tile classifier.
            context.ClearUnorderedAccessView(resources.GasActiveTiles.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4(1,1,1,1));
            context.ComputeShader.SetShaderResources(0,resources.Materials.View,resources.Air.View,resources.GasActiveTiles.View);
            context.ComputeShader.SetUnorderedAccessView(0,resources.Grid.ReadUnorderedView);
            context.ComputeShader.SetUnorderedAccessView(3,resources.CellMaterials.UnorderedView);
            context.ComputeShader.SetUnorderedAccessView(6,resources.GasMotion.UnorderedView);
            context.Dispatch(((w+1)/2+15)/16,(h+15)/16,1);
            for(int i=0;i<3;i++)context.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<12;i++)context.ComputeShader.SetUnorderedAccessView(i,null);
            var after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(resources,resources.Grid.ReadBuffer));
            var velocity=MemoryMarshal.Cast<byte,GasMotionState>(AirInventoryRegressionVerifier.Read(resources,resources.GasMotion.Buffer));
            double total=after[index].Mass+after[index+1].Mass;
            double heat=after[index].Mass*after[index].Temperature+after[index+1].Mass*after[index+1].Temperature;
            double momentum=after[index].Mass*velocity[index].VelocityX+after[index+1].Mass*velocity[index+1].VelocityX;
            Check(Math.Abs(total-2*mass)<1e-6 && Math.Abs(heat-600*mass)<1e-4 && Math.Abs(momentum-4*mass)<1e-6,"Packet consolidation lost mass, heat or momentum");
            bool merged=after[index].IsActive==0 || after[index+1].IsActive==0;
            Check(merged==(finite&&!nominal),"Packet consolidation affected Sandbox or nominal gas");
        }
        foreach (uint marker in new uint[] { 0, 0x40000000u, 0x80000000u })
        {
            var grid=new GridCell[n];
            grid[index]=new() { IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
                Mass=1,Temperature=420,Lifetime=.5f,BodyId=marker };
            var stock=new float[n]; stock[index]=.5f;
            context.UpdateSubresource(grid,resources.Grid.ReadBuffer);
            context.UpdateSubresource(stock,resources.Oxidizer.ReadBuffer);
            context.UpdateSubresource(stock,resources.OxidizerAvailable.Buffer);
            context.ClearUnorderedAccessView(resources.OxidizerDemand.UnorderedView,new RawInt4());
            SimulationDispatchCoordinator.DispatchOxidizer(resources,1f/60,false,true);
            var oxygen=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(resources,resources.Oxidizer.ReadBuffer));
            Check(Math.Abs(oxygen[index]-(marker==0?.4975f:.5f))<1e-7,"Fuel-created flame spent oxygen twice");
            context.ClearUnorderedAccessView(resources.OxidizerAvailable.UnorderedView,new RawInt4());
            var constants=new CombustionConstants { Width=(uint)w,Height=(uint)h,MaterialCount=(uint)registry.Materials.Count,
                DeltaTime=1f/60,FiniteOxidizer=1 };
            context.UpdateSubresource(ref constants,resources.CombustionConstants);
            context.ComputeShader.Set(resources.TransientLifecycleShader);
            context.ComputeShader.SetConstantBuffer(0,resources.CombustionConstants);
            context.ComputeShader.SetShaderResources(0,resources.Materials.View,resources.OxidizerAvailable.View);
            context.ComputeShader.SetUnorderedAccessViews(0,resources.Grid.ReadUnorderedView,resources.CombustionSummary.UnorderedView);
            context.Dispatch((w+15)/16,(h+15)/16,1);
            for(int i=0;i<2;i++) { context.ComputeShader.SetShaderResource(i,null); context.ComputeShader.SetUnorderedAccessView(i,null); }
            var after=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(resources,resources.Grid.ReadBuffer));
            Check((after[index].MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire))==(marker!=0),
                "Fuel heat tracer was quenched a second time or painted fire ignored oxygen");
        }
        Console.WriteLine("PHYXEL_FURNACE_COMBUSTION_SUCCESS");
    }

    private static void VerifyCoalLifetime(SimulationDispatchCoordinator coordinator, MaterialRegistry registry,
        GpuSimulationResources r, int index)
    {
        int w=r.Width,h=r.Height,n=w*h;var context=r.Context;
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        var grid=new GridCell[n];
        grid[index]=new(){IsActive=1,MaterialIndex=coal,Mass=1,Temperature=410};
        context.UpdateSubresource(grid,r.Grid.ReadBuffer);
        void Step(int count)
        {
            var constants=new CombustionConstants{Width=(uint)w,Height=(uint)h,
                MaterialCount=(uint)registry.Materials.Count,DeltaTime=1f/60,FiniteOxidizer=0};
            context.UpdateSubresource(ref constants,r.CombustionConstants);
            context.ComputeShader.Set(r.CombustionShader);context.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            context.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView);
            for(int i=0;i<count;i++)context.Dispatch((w+15)/16,(h+15)/16,1);
            for(int i=0;i<3;i++)context.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<5;i++)context.ComputeShader.SetUnorderedAccessView(i,null);
        }
        byte[] Read()=>AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        Step(500);byte[] half=Read();var partial=MemoryMarshal.Cast<byte,GridCell>(half)[index];
        Check(partial.MaterialIndex==coal && Math.Abs(partial.Mass-.5)<.0001 && partial.Lifetime==1,
            "Brush coal did not consume half its mass at 8.33s");
        var serializer=new Phyxel.Serialization.SimulationStateSerializer();
        string dir=System.IO.Path.Combine(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/coal-unit","resume");
        System.IO.Directory.CreateDirectory(dir);string path=System.IO.Path.Combine(dir,"scene.json");
        var settings=new SimulationSettings{Paused=true};
        var world=new Phyxel.Serialization.SimulationWorldSnapshot(w,h,half);
        System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)coal,world,registry)).GetAwaiter().GetResult();
        var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!.World!;
        Check(half.AsSpan().SequenceEqual(loaded.Grid),"Partial coal mass/latch changed across save-load");
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        {
            serializer.ApplyWorldSnapshot(r,loaded);coordinator.RestoreWorldActivity(r,true,false,false,true);
            settings.Mode=mode;coordinator.DispatchFrame(settings,[],1);
            Check(half.AsSpan().SequenceEqual(Read()),"Paused coal consumed mass during mode switch");
        }
        serializer.ApplyWorldSnapshot(r,loaded);Step(499);
        var before=MemoryMarshal.Cast<byte,GridCell>(Read())[index];
        Check(before.MaterialIndex==coal && before.Mass>0,"Coal burned out before its calibrated lifetime");
        Step(2);var after=MemoryMarshal.Cast<byte,GridCell>(Read())[index];
        Check(after.MaterialIndex==fire,"Coal did not become a flame at 16.7s after save-load");
        Console.WriteLine("PHYXEL_COAL_LIFETIME brushMass=1 heatingPower=360 oxidizerRate=.24 burnoutSeconds=16.7 closedCore=PASS pauseModesSaveLoad=PASS");
    }
}
