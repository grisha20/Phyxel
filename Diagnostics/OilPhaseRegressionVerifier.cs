using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

internal static class OilPhaseRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/oil-phases";
        Directory.CreateDirectory(dir);
        var settings=new SimulationSettings {Paused=true,AirSimulation=false,OpenBoundaries=false};
        uint oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),solid=registry.GetRequiredRuntimeIndex(CoreMaterialIds.FrozenOil);
        uint vapour=registry.GetRequiredRuntimeIndex(CoreMaterialIds.OilVapour),fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        uint fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var table=registry.CreateGpuTable();
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=(ushort)oil}],0);
        int w=r.Width,n=w*r.Height,at=100*w+140,checks=0; bool passed=true; double maxError=0;
        var data=new System.Collections.Generic.List<object>();
        GridCell Cell(uint id,float t=20,float mass=1)=>new(){IsActive=1,MaterialIndex=id,Mass=mass,Temperature=t};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Upload(GridCell[] grid){r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);}
        void Check(bool ok,string label){checks++;if(!ok){passed=false;Console.WriteLine("PHYXEL_OP_FAILED "+label);}}
        void Phase(uint tick=0){var c=new PhaseTransitionConstants{Width=(uint)w,Height=(uint)r.Height,
            MaterialCount=(uint)table.Length,TickIndex=tick,TickCount=1};var ctx=r.Context;
            ctx.UpdateSubresource(ref c,r.PhaseConstants);ctx.ComputeShader.Set(r.PhaseTransitionShader);
            ctx.ComputeShader.SetConstantBuffer(0,r.PhaseConstants);ctx.ComputeShader.SetShaderResource(0,r.Materials.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.PhaseSummary.UnorderedView);
            ctx.Dispatch((w+15)/16,(r.Height+15)/16,1);ctx.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<6;i++)ctx.ComputeShader.SetUnorderedAccessView(i,null);ctx.ComputeShader.Set(null);}
        double E(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        void Balance(GridCell[] before,GridCell[] after,string label){
            double error=Math.Abs(E(after)-E(before))/Math.Max(1,Math.Abs(E(before)));maxError=Math.Max(maxError,error);
            Check(error<=1e-5,label+" energy "+error);
            Check(Math.Abs(after.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass)-before.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass))<1e-5,label+" mass");}
        Check(Marshal.SizeOf<MaterialProperties>()==244&&Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.ContactIgnitionTemperature)).ToInt32()==240,"ABI244");
        Check(table[oil].IgnitionTemperature==205&&table[oil].ContactIgnitionTemperature==135,"Ignition thresholds");
        var emissions=registry.CreateEmissionGpuTable();
        Check(Math.Abs(emissions[oil].SmokeRate/table[oil].BurnRate-emissions[vapour].SmokeRate/table[vapour].BurnRate)<1e-6&&
            Math.Abs(emissions[oil].GasRate/table[oil].BurnRate-emissions[vapour].GasRate/table[vapour].BurnRate)<1e-6&&
            Math.Abs(emissions[oil].FlameRate/table[oil].BurnRate-emissions[vapour].FlameRate/table[vapour].BurnRate)<1e-4,
            "Phase-independent product yield per burned fuel");
        Check(registry[CoreMaterialIds.FrozenOil].Hidden&&registry[CoreMaterialIds.OilVapour].Hidden,"Hidden phase palette");
        Check(table[solid].MaximumLifetime==0&&table[vapour].MaximumLifetime==0,"No phase lifetime decay");
        string schemaDir=Path.Combine(dir,"invalid-schema");Directory.CreateDirectory(schemaDir);
        string original=File.ReadAllText(registry[CoreMaterialIds.Oil].SourcePath);
        foreach(var bad in new System.Text.Json.Nodes.JsonNode?[]{
            System.Text.Json.Nodes.JsonValue.Create(-273.15f),System.Text.Json.Nodes.JsonValue.Create(206),
            System.Text.Json.Nodes.JsonValue.Create("135"),System.Text.Json.Nodes.JsonValue.Create(true),null}){
            var doc=System.Text.Json.Nodes.JsonNode.Parse(original)!;doc["combustion"]!["contactIgnitionTemperature"]=bad?.DeepClone();
            File.WriteAllText(Path.Combine(schemaDir,"oil.json"),doc.ToJsonString());
            bool rejected=false;try{MaterialFileLoader.LoadCore(schemaDir,MaterialRegistry.MaximumMaterials);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Invalid flash schema "+bad);
        }
        // Independently prescribed absolute enthalpies; no SetSpecificEnergy
        // formula is used to manufacture the expected phase/progress.
        foreach(bool gpu in new[]{false,true}){
            var c=Cell(oil);double largest=0;
            for(int cycle=0;cycle<10;cycle++)foreach(var step in new (float h,uint id,float t,float p)[]{
                (h:-64f,id:oil,t:18f,p:-100f),(-220f,solid,(-220f+224.45f)/1.7f,0f),
                (-93f,solid,18.5f,100f),(37f,oil,18.5f,0f),
                (674f,oil,287f,100f),(844f,vapour,287f,0f),
                (741f,vapour,285f,100f),(570f,oil,285f,0f),(40f,oil,20f,0f)}){
                float old=PhaseEnthalpy.SpecificEnergy(c,table);
                c.Temperature+=(step.h-old)/table[c.MaterialIndex].HeatCapacity;
                var input=new GridCell[n];input[at]=c;
                if(gpu){Upload(input);Phase();c=Read()[at];}else PhaseTransitionRuntime.TryApply(ref c,table,out _);
                double error=Math.Abs(PhaseEnthalpy.SpecificEnergy(c,table)-step.h)/Math.Max(1,Math.Abs(step.h));largest=Math.Max(largest,error);
                Check(c.MaterialIndex==step.id&&Math.Abs(c.Temperature-step.t)<.003&&Math.Abs(c.PhaseProgress-step.p)<.003,"Cycle "+gpu+" "+cycle+" h="+step.h);
                Check(error<=1e-5,"Cycle energy "+gpu);
            }
            Console.WriteLine($"PHYXEL_OP_CYCLE gpu={gpu} cycles=10 relativeError={largest:E8}");
        }
        // Isolated finite hot/cold reservoir: actual 20Hz thermal frames,
        // no flame/air transport, surface closed by zero-k fixture walls.
        var originalFixture=table[fixture];table[fixture].ThermalConductivity=0;r.Materials.Upload(r.Context,table);
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100})
        foreach(bool hot in new[]{false,true}){
            var g=new GridCell[n];for(int y=-1;y<=1;y++)for(int x=-1;x<=2;x++)if(y!=0||x<0||x>1)g[at+y*w+x]=Cell(fixture);
            g[at]=Cell(oil,hot?287:18);g[at+1]=Cell(metal,hot?340:-35,7.8f);
            Upload(g);coordinator.RestoreWorldActivity(r,true,false,false);settings.Mode=mode;settings.Paused=false;
            for(int f=0;f<fps*5;f++)coordinator.DispatchFrame(settings,[],1f/fps);
            var after=Read();Balance(g,after,$"pocket-{mode}-{fps}-{hot}");
            Check(after[at].MaterialIndex==oil&&Math.Abs(after[at].Temperature-(hot?287:18))<.005&&
                (hot?after[at].PhaseProgress>1:after[at].PhaseProgress< -1),"Partial plateau "+mode+" "+fps+" "+hot);
            data.Add(new{scenario="pocket",mode=mode.ToString(),fps,hot,temperature=after[at].Temperature,progress=after[at].PhaseProgress});
        }
        table[fixture]=originalFixture;r.Materials.Upload(r.Context,table);settings.Paused=true;
        // Fully paid freeze in an open basin stops the low-temperature flow.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            var g=new GridCell[n];for(int y=130;y<150;y++)for(int x=130;x<150;x++)g[y*w+x]=Cell(oil,-150);
            Upload(g);coordinator.RestoreWorldActivity(r,true,false,false);settings.Mode=mode;settings.Paused=false;
            for(int f=0;f<fps*2;f++)coordinator.DispatchFrame(settings,[],1f/fps);
            var frozen=Read();Check(frozen.Count(c=>c.IsActive!=0&&c.MaterialIndex==solid)==400,"Freeze basin "+mode+fps);
            Balance(g,frozen,"Freeze basin");
            for(int f=0;f<fps;f++)coordinator.DispatchFrame(settings,[],1f/fps);
            Check(MemoryMarshal.AsBytes(frozen.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"Frozen stopped "+mode+fps);
        }
        settings.Paused=true;
        // Transport alone: disable only this fuel's burn target and wall/gas
        // conduction in a diagnostic table, leaving its production motion,
        // gas/phase flags and packet enthalpy intact. Do not infer combustion
        // balance from this isolated wall test.
        var originalVapour=table[vapour];var originalMetal=table[metal];
        table[vapour].BurnedIntoMaterialIndex=uint.MaxValue;table[vapour].ThermalConductivity=0;
        table[metal].ThermalConductivity=0;r.Materials.Upload(r.Context,table);
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            var g=new GridCell[n];
            for(int y=75;y<=140;y++)foreach(int x in new[]{100,160})g[y*w+x]=Cell(metal,300,7.8f);
            for(int x=100;x<=160;x++)foreach(int y in new[]{75,140})g[y*w+x]=Cell(metal,300,7.8f);
            for(int x=125;x<133;x++){g[100*w+x]=Cell(vapour,300);g[100*w+x].VelocityY=-2;}
            Upload(g);coordinator.RestoreWorldActivity(r,true,true,false);settings.Mode=mode;settings.Paused=false;
            for(int f=0;f<fps*3;f++){
                coordinator.DispatchFrame(settings,[],1f/fps);
                if((f+1)%fps==0){var after=Read();Balance(g,after,"Vapour transport");
                    Check(after.Where((c,i)=>c.IsActive!=0&&c.MaterialIndex==vapour&&
                        (i%w<=100||i%w>=160||i/w<=75||i/w>=140)).Count()==0,"Vapour crossed wall "+mode+fps);
                    Check(after.Count(c=>c.IsActive!=0&&c.MaterialIndex==metal)==g.Count(c=>c.IsActive!=0&&c.MaterialIndex==metal),"Metal wall changed");
                }
            }
        }
        table[vapour]=originalVapour;table[metal]=originalMetal;r.Materials.Upload(r.Context,table);settings.Paused=true;
        // One direct reaction tick separates temperature, flame and oxygen
        // from motion/heat transfer. Emission requests are not resolved here.
        (double loss,float temp,double demand,uint marker) React(uint id,float t,bool flame,bool finite,float oxygen,bool submerged=false,bool latched=false){
            var g=new GridCell[n];var supply=new float[n];g[at]=Cell(id,t);
            if(latched)g[at].BodyId=CombustionRuntime.FuelBurningMarker;
            foreach(int k in new[]{at-1,at+1,at-w,at+w}){supply[k]=oxygen;if(submerged)g[k]=Cell(registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water));}
            if(flame){g[at-1]=Cell(fire,700);g[at-1].Lifetime=2;}
            Upload(g);var ctx=r.Context;ctx.UpdateSubresource(supply,r.OxidizerAvailable.Buffer);
            ctx.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            ctx.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());ctx.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
            var c=new CombustionConstants{Width=(uint)w,Height=(uint)r.Height,MaterialCount=(uint)table.Length,
                DeltaTime=.05f,TickIndex=1,FiniteOxidizer=finite?1u:0u};
            // Fixed seed is scanned deterministically, keeping every tick
            // isolated: the contract requires contact to permit, not guarantee
            // a reaction on every probabilistic spread attempt.
            double loss=0;float temp=t;double demand=0;uint marker=0;
            for(uint tick=1;tick<=100;tick++){
                Upload(g);ctx.UpdateSubresource(supply,r.OxidizerAvailable.Buffer);c.TickIndex=tick;
                ctx.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
                ctx.UpdateSubresource(ref c,r.CombustionConstants);ctx.ComputeShader.Set(r.CombustionShader);ctx.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
                ctx.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
                ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                    r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView);
                ctx.Dispatch((w+15)/16,(r.Height+15)/16,1);for(int i=0;i<3;i++)ctx.ComputeShader.SetShaderResource(i,null);
                for(int i=0;i<6;i++)ctx.ComputeShader.SetUnorderedAccessView(i,null);ctx.ComputeShader.Set(null);
                var after=Read()[at];loss=1-after.Mass;temp=after.Temperature;marker=after.BodyId;
                demand=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.OxidizerDemand.Buffer))[at];
                if(loss>0)break;
            }
            data.Add(new{scenario="reaction",id,t,flame,finite,oxygen,submerged,latched,loss,temp,demand,marker});return(loss,temp,demand,marker);
        }
        foreach(bool finite in new[]{false,true}){
            Check(React(oil,134,true,finite,1).loss==0,"Below flash blocked");
            Check(React(oil,150,false,finite,1).loss==0,"Between flash and auto not automatic");
            var contact=React(oil,150,true,finite,1);Check(contact.loss>0&&contact.temp<205,"Contact paid heat without temperature teleport");
            Check((contact.marker&CombustionRuntime.FuelBurningMarker)!=0,"Contact retains ignition separately from latent heat");
            Check(React(oil,150,false,finite,1,latched:true).loss>0,"Warm ignited oil continues without contact");
            var cooled=React(oil,134,false,finite,1,latched:true);
            Check(cooled.loss==0&&(cooled.marker&CombustionRuntime.FuelBurningMarker)==0,"Cooling extinguishes latch");
            var covered=React(oil,150,false,finite,1,true,true);
            Check(covered.loss==0&&(covered.marker&CombustionRuntime.FuelBurningMarker)==0,"Cover extinguishes latch");
            Check(React(oil,206,false,finite,1).loss>0,"Automatic ignition");
            Check(React(oil,300,false,finite,1,true).loss==0,"Submerged blocked");
            Check(React(vapour,300,false,finite,1).loss>0,"Vapour combustible");
        }
        Check(React(oil,300,true,true,0).loss==0&&React(vapour,300,true,true,0).loss==0,"Inert Simulation blocked");
        var inert=React(oil,150,false,true,0,latched:true);
        Check(inert.loss==0&&(inert.marker&CombustionRuntime.FuelBurningMarker)==0,"Oxygen exhaustion extinguishes latch");
        var cpuFuel=Cell(oil,150);cpuFuel.BodyId=CombustionRuntime.FuelBurningMarker;
        Check(CombustionRuntime.TryApply(ref cpuFuel,table,.05f,out _,out var cpuBurn)&&cpuBurn>0&&cpuFuel.Temperature<205,"CPU warm latch pays heat");
        cpuFuel.Temperature=134;
        Check(!CombustionRuntime.TryApply(ref cpuFuel,table,.05f,out _,out _)&&cpuFuel.BodyId==0,"CPU cooling clears latch");
        var save=new GridCell[n];save[at]=Cell(oil,18);save[at].PhaseProgress=-100;
        save[at+2]=Cell(vapour,285);save[at+2].PhaseProgress=100;save[at+4]=Cell(solid,18.5f);save[at+4].PhaseProgress=100;
        save[at+6]=Cell(oil,150);save[at+6].BodyId=CombustionRuntime.FuelBurningMarker;
        Upload(save);settings.Paused=true;var paused=Read();for(int f=0;f<60;f++)coordinator.DispatchFrame(settings,[],1f/60);
        Check(MemoryMarshal.AsBytes(paused.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"Pause unchanged");
        var serializer=new SimulationStateSerializer();string path=Path.Combine(dir,"partial-oil.json");
        var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(save.AsSpan()).ToArray());
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)oil,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult();
        Check(loaded?.World is {} world&&snapshot.Grid.AsSpan().SequenceEqual(world.Grid),"Writer14 phase progress exact");
        if(loaded?.World is not {} reloaded)throw new InvalidDataException("Saved oil phases missing");
        Upload(save);Phase();var memory=Read();Upload(MemoryMarshal.Cast<byte,GridCell>(reloaded.Grid).ToArray());Phase();
        Check(MemoryMarshal.AsBytes(memory.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"Reload continuation");
        var cold=new GridCell[n];cold[at]=Cell(vapour,20);Upload(cold);Phase();var condensed=Read();Balance(cold,condensed,"Cold condensate");
        Check(condensed[at].MaterialIndex==oil,"Condensate returns oil");
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{passed,checks,maxEnergyResidual=maxError,data},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_OIL_PHASES_RESULT passed={passed} checks={checks} energyResidual={maxError:E8}");if(!passed)Environment.ExitCode=1;
    }
}
