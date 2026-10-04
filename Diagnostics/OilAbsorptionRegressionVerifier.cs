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
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

internal static class OilAbsorptionRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Path.GetFullPath(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/oil-absorption");
        Directory.CreateDirectory(dir);
        int checks=0;
        void Check(bool ok,string name) { checks++; if(!ok) throw new InvalidDataException(name); }
        var settings=new SimulationSettings { Paused=true, AirSimulation=false, OpenBoundaries=false };
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal), oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),
            water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water), fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var r=coordinator.DispatchFrame(settings,[new() { X=80,Y=80,EndX=80,EndY=80,Radius=1,Density=1,
            MaterialIndex=(ushort)coal,Mode=BrushCommandMode.Material }],0);
        int w=r.Width,n=w*r.Height,p=80*w+80;
        var table=registry.CreateGpuTable();
        var serializer=new SimulationStateSerializer();
        GridCell Cell(uint id,float t=20,float mass=1)=>new(){IsActive=1,MaterialIndex=id,Temperature=t,Mass=mass};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Upload(GridCell[] grid)
        {
            r.Context.ClearUnorderedAccessView(r.ContactSummary.UnorderedView,new RawInt4());
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));
        }
        double Mass(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
        double Energy(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        void Balance(GridCell[] a,GridCell[] b,string label)
        {
            Check(Math.Abs(Mass(a)-Mass(b))<=.0001*Math.Max(1,Mass(a)),label+" mass");
            Check(Math.Abs(Energy(a)-Energy(b))<=.0001*Math.Max(1,Math.Abs(Energy(a))),label+" energy");
        }
        void Contact(uint tick,ComputeShader? shader=null)
        {
            var constants=new ContactTransitionConstants{Width=(uint)w,Height=(uint)r.Height,DeltaTime=.05f,TickIndex=tick};
            r.Context.UpdateSubresource(ref constants,r.ContactTransitionConstants);
            r.Context.ComputeShader.Set(shader??r.MoistureShader);r.Context.ComputeShader.SetConstantBuffer(0,r.ContactTransitionConstants);
            r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CellMaterials.UnorderedView,r.GasMotion.UnorderedView,r.ContactSummary.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            r.Context.ComputeShader.SetShaderResource(0,null);for(int i=0;i<4;i++)r.Context.ComputeShader.SetUnorderedAccessView(i,null);
            r.Context.ComputeShader.Set(null);
        }
        GridCell[] Pair(uint liquid,float t=20,float amount=1)
        { var grid=new GridCell[n];grid[p]=Cell(coal);grid[p+1]=Cell(liquid,t,amount);return grid; }

        // Same cold scene on the actual original contact handler, compiled
        // against the current shared layout; it must have zero oil uptake.
        string baseline=Path.GetFullPath("artifacts/oil-absorption-20261003/baseline/ContactTransitions.hlsl");
        if(File.Exists(baseline))
        {
        string shaderText=File.ReadAllText(baseline)
            .Replace("#include \"PhysicsShared.hlsli\"",File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Content/Shaders/PhysicsShared.hlsli")))
            .Replace("#include \"PhaseEnthalpy.hlsli\"",File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Content/Shaders/PhaseEnthalpy.hlsli")));
        using(var bytecode=ShaderBytecode.Compile(shaderText,"CSMoisture","cs_5_0"))
        using(var shader=new ComputeShader(r.Device,bytecode))
        {
            var before=Pair(oil);Upload(before);for(uint i=0;i<20;i++)Contact(i,shader);
            var after=Read();Check(after[p].FuelMass==0 && after[p+1].Mass==1,"Baseline unexpectedly absorbs oil");
            Console.WriteLine("PHYXEL_OIL_ABSORPTION_BASELINE oil=0");
        }
        }
        else Console.WriteLine("PHYXEL_OIL_ABSORPTION_BASELINE_NOT_RUN original shader snapshot missing");
        foreach(int face in new[]{1,-1,w,-w})
        {
            var before=new GridCell[n];before[p]=Cell(coal);before[p+face]=Cell(oil,70);
            Upload(before);for(uint i=0;i<20;i++)Contact(i);
            var after=Read();Balance(before,after,"one face "+face);
            Check(Math.Abs(after[p].FuelMass-.025f)<.0001,"Oil rate "+face);
            Check(after[p].MoistureMass==0 && after[p].MaterialIndex==coal && after[p].Mass==1,"Oil became water/dry mass");
        }
        var waterPair=Pair(water);Upload(waterPair);for(uint i=0;i<20;i++)Contact(i);
        Check(Math.Abs(Read()[p].MoistureMass-.35f)<.0001,"Water speed changed");
        foreach(bool waterFirst in new[]{false,true})
        {
            var before=Pair(waterFirst?water:oil);Upload(before);for(uint i=0;i<120;i++)Contact(i);
            var partial=Read();partial[p+1]=Cell(waterFirst?oil:water);Upload(partial);
            for(uint i=0;i<120;i++)Contact(i);
            var after=Read();Balance(partial,after,"shared pores "+waterFirst);
            Check(after[p].MoistureMass/.35f+after[p].FuelMass/.25f<=1.0001,"Pores doubly filled");
            Check(waterFirst ? after[p].FuelMass==0 : after[p].FuelMass>0 && after[p].MoistureMass>0,"Sequential liquid contract");
        }
        foreach(bool reverse in new[]{false,true})
        {
            var before=new GridCell[n];int donor=reverse?p+1:p,receiver=reverse?p:p+1;
            before[donor]=Cell(coal,70,2);before[donor].FuelMass=.4f;
            before[receiver]=Cell(table[coal].MoistureWetMaterialIndex,20);before[receiver].MoistureMass=.175f;
            Upload(before);for(uint i=0;i<240;i++)Contact(i);
            var after=Read();Balance(before,after,"oil wick "+reverse);
            Check(after[receiver].FuelMass>0 && after[receiver].FuelMass<=.25f*(1-after[receiver].MoistureMass/.35f)+.0001,"Wick ignored remaining pores");
            Check(after[receiver].MoistureMass/.35f+after[receiver].FuelMass/.25f<=1.0001,"Wick overfilled pores");
        }
        var chain=new GridCell[n];for(int i=0;i<5;i++)chain[p+i]=Cell(coal);chain[p].FuelMass=.25f;
        Upload(chain);for(uint i=0;i<160;i++)Contact(i);
        var propagated=Read();Balance(chain,propagated,"chain");Check(propagated[p+2].FuelMass>1e-5,"Oil wick front stopped");

        var drying=new GridCell[n];drying[p]=Cell(table[coal].MoistureWetMaterialIndex,100);
        drying[p].FuelMass=.1f;drying[p].MoistureMass=.07f;drying[p].MoistureEnergy=.07f*table[water].TransitionAboveLatentHeat;
        Upload(drying);for(uint i=0;i<80;i++)Contact(i);
        var dried=Read();Balance(drying,dried,"mixed drying");
        Check(dried[p].MoistureMass==0 && dried[p].MaterialIndex==coal && dried[p].FuelMass==.1f,"Water drying erased oil");

        var probeConstants=new TemperatureProbeConstants{X=80,Y=80,Width=(uint)w,Height=(uint)r.Height};
        r.Context.UpdateSubresource(ref probeConstants,r.TemperatureProbeConstants);
        r.Context.ComputeShader.Set(r.TemperatureProbeShader);r.Context.ComputeShader.SetConstantBuffer(0,r.TemperatureProbeConstants);
        r.Context.ComputeShader.SetShaderResources(0,r.Grid.ReadView,r.Materials.View);
        r.Context.ComputeShader.SetUnorderedAccessView(0,r.TemperatureProbeResult.UnorderedView);r.Context.Dispatch(1,1,1);
        for(int i=0;i<2;i++)r.Context.ComputeShader.SetShaderResource(i,null);r.Context.ComputeShader.SetUnorderedAccessView(0,null);r.Context.ComputeShader.Set(null);
        var probe=MemoryMarshal.Cast<byte,TemperatureProbeResult>(AirInventoryRegressionVerifier.Read(r,r.TemperatureProbeResult.Buffer))[0];
        Check(Math.Abs(probe.FuelFraction-.1f/1.1f)<1e-6 && BitConverter.UInt32BitsToSingle(probe.Reserved)==0,"Probe mixed oil with water");

        GridCell[] React(GridCell[] grid,bool finite,float oxygen,float dt=1f/60)
        {
            Upload(grid);var ctx=r.Context;
            ctx.UpdateSubresource(Enumerable.Repeat(oxygen,n).ToArray(),r.OxidizerAvailable.Buffer);
            ctx.ClearUnorderedAccessView(r.CombustionSummary.UnorderedView,new RawInt4());
            ctx.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
            ctx.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
            ctx.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
            ctx.ClearUnorderedAccessView(r.ReactionPending.UnorderedView,new RawInt4());
            var c=new CombustionConstants{Width=(uint)w,Height=(uint)r.Height,DeltaTime=dt,TickIndex=1,
                MaterialCount=(uint)registry.Count,FiniteOxidizer=finite?1u:0u,Reserved1=1};
            ctx.UpdateSubresource(ref c,r.CombustionConstants);ctx.ComputeShader.Set(r.CombustionShader);
            ctx.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            ctx.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
            ctx.Dispatch((w+15)/16,(r.Height+15)/16,1);
            for(int i=0;i<3;i++)ctx.ComputeShader.SetShaderResource(i,null);for(int i=0;i<6;i++)ctx.ComputeShader.SetUnorderedAccessView(i,null);
            ctx.ComputeShader.Set(null);return Read();
        }
        foreach(bool finite in new[]{false,true}) foreach(string cover in new[]{"air","water","solid","wet","inert"})
        {
            var before=new GridCell[n];before[p]=Cell(coal,300);before[p].FuelMass=.2f;
            if(cover=="wet"){before[p].MaterialIndex=table[coal].MoistureWetMaterialIndex;before[p].MoistureMass=.02f;before[p].Temperature=100;}
            foreach(int d in new[]{-1,1,-w,w})if(cover=="water" || cover=="solid")before[p+d]=Cell(cover=="water"?water:fixture);
            var after=React(before,finite,cover=="inert"?0:1);
            double burned=before[p].FuelMass-after[p].FuelMass;
            bool burns=cover=="air" || (cover=="inert" && !finite);
            Check(burns ? burned>0 : burned==0,"Stored oil exposure/oxygen "+cover+" "+finite);
            Check(after[p].Mass==1 && after[p].MaterialIndex==before[p].MaterialIndex,"Oil consumed dry support");
            if(burns)
            {
                double expected=Energy(before)+burned*(table[oil].HeatPerMass-table[oil].HeatCapacity*before[p].Temperature);
                Check(Math.Abs(Energy(after)-expected)<=.0001*Math.Max(1,Math.Abs(expected)),"Oil reaction heat ledger");
                var demands=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.OxidizerDemand.Buffer));
                Check(Math.Abs(demands[p]-(finite?burned*registry[oil].Combustion!.OxidizerPerMass:0))<.00001,"Stored oil O2 demand");
            }
        }
        var ending=new GridCell[n];ending[p]=Cell(coal,300);ending[p].FuelMass=.00001f;
        var ended=React(ending,false,1);Check(ended[p].FuelMass==0 && ended[p].Mass==1 && ended[p].MaterialIndex==coal,"Last oil destroyed support");
        var capped=new GridCell[n];capped[p]=Cell(coal,1099);capped[p].FuelMass=.2f;
        var capAfter=React(capped,false,1);double capBurn=capped[p].FuelMass-capAfter[p].FuelMass;
        Check(capAfter[p].Temperature<=1100.01f && capBurn>0 && capBurn<.003,"Hot oil cap did not limit reaction");
        Check(Math.Abs(Energy(capAfter)-Energy(capped)-capBurn*(table[oil].HeatPerMass-table[oil].HeatCapacity*1099))<.02,"Cap discarded heat");

        // Five seconds without an external cooler reach the thermal limit.
        // Oil consumption must stop there while preserving the dry support.
        foreach(bool finite in new[]{false,true})
        {
            var initial=new GridCell[n];initial[p]=Cell(coal,1000);initial[p].FuelMass=.25f;
            var burning=initial;
            for(int tick=0;tick<300;tick++) burning=React(burning,finite,1);
            Check(burning[p].FuelMass>0 && burning[p].FuelMass<.25f && burning[p].Mass==1 &&
                burning[p].MaterialIndex==coal && burning[p].Temperature<=1100.01f,
                "Five-second oil cap/support "+finite);
        }

        var saved=new GridCell[n];saved[p]=Cell(table[coal].MoistureWetMaterialIndex,100);saved[p].MoistureMass=.07f;
        saved[p].MoistureEnergy=15;saved[p].FuelMass=.1f;
        string path=Path.Combine(dir,"partial.json");
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)coal,new(w,r.Height,MemoryMarshal.AsBytes(saved.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        Check(loaded.World!.Grid.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(saved.AsSpan())),"Fuel/water reload changed bytes");
        Upload(saved);settings.Paused=true;coordinator.RestoreWorldActivity(r,true,true,false);
        coordinator.DispatchFrame(settings,[],1);Check(Read()[p].FuelMass==.1f,"Pause consumed oil");
        foreach(int version in new[]{12,13})
        {
            byte[] bytes=WorldCellCodecRegressionVerifier.RepackWorldPrefix(File.ReadAllBytes(Path.ChangeExtension(path,".world")),n,48);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4,4),version);
            string old=Path.Combine(dir,"legacy-"+version+".json");
            var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;json["Version"]=version;
            File.WriteAllText(old,json.ToJsonString());File.WriteAllBytes(Path.ChangeExtension(old,".world"),bytes);
            var oldLoaded=Task.Run(()=>serializer.LoadAsync(old,registry)).GetAwaiter().GetResult()!;
            var c=MemoryMarshal.Cast<byte,GridCell>(oldLoaded.World!.Grid)[p];
            Check(c.FuelMass==0 && c.MoistureMass==.07f && c.MoistureEnergy==15 && c.Temperature==100,"Old world oil migration "+version);
        }
        foreach(float bad in new[]{-.01f,float.NaN,float.PositiveInfinity,.3f})
        {
            var invalid=(GridCell[])saved.Clone();invalid[p].FuelMass=bad;bool rejected=false;
            try{Task.Run(()=>serializer.SaveAsync(Path.Combine(dir,"invalid.json"),settings,(ushort)coal,
                new(w,r.Height,MemoryMarshal.AsBytes(invalid.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult();}
            catch(InvalidDataException){rejected=true;}Check(rejected,"Bad fuel accepted "+bad);
        }
        foreach(uint invalidMaterial in new[]{oil,water,registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand)})
        {
            var invalid=new GridCell[n];invalid[p]=Cell(invalidMaterial);invalid[p].FuelMass=.1f;bool rejected=false;
            try{Task.Run(()=>serializer.SaveAsync(Path.Combine(dir,"invalid-carrier.json"),settings,(ushort)invalidMaterial,
                new(w,r.Height,MemoryMarshal.AsBytes(invalid.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult();}
            catch(InvalidDataException){rejected=true;}Check(rejected,"Unsupported carrier accepted oil");
        }
        var moving=new GridCell[n];moving[p]=Cell(coal);moving[p].FuelMass=.1f;Upload(moving);
        settings.Paused=false;coordinator.RestoreWorldActivity(r,true,true,false);for(int i=0;i<60;i++)coordinator.DispatchFrame(settings,[],1f/60);
        var moved=Read();var grain=moved.Single(c=>c.IsActive!=0 && c.MaterialIndex==coal);
        Balance(moving,moved,"movement");Check(grain.FuelMass==.1f && Array.FindIndex(moved,c=>c.IsActive!=0&&c.MaterialIndex==coal)>p,"Moving grain lost oil");

        foreach(SimulationMode mode in Enum.GetValues<SimulationMode>()) foreach(uint liquid in new[]{oil,water})
        {
            var immersed=new GridCell[n];
            for(int y=60;y<=110;y++)for(int x=60;x<=100;x++)immersed[y*w+x]=Cell(x==60||x==100||y==110?fixture:liquid);
            immersed[p]=Cell(coal);immersed[p].FuelMass=.25f;Upload(immersed);
            settings.Mode=mode;settings.Paused=false;coordinator.RestoreWorldActivity(r,true,true,false);
            for(int f=0;f<30;f++)coordinator.DispatchFrame(settings,[],1f/60);
            var after=Read();int at=Array.FindIndex(after,c=>c.IsActive!=0&&c.MaterialIndex==coal);
            Check(at>=0 && (liquid==oil?at/w>80:at/w<80),"Oil saturated density in liquid "+liquid+" "+mode);
            Check(after[at].FuelMass==.25f && after[at].MoistureMass==0,"Saturated grain lost oil or overfilled with water");
            Balance(immersed,after,"saturated buoyancy");
            Console.WriteLine($"PHYXEL_OIL_ABSORPTION_BUOYANCY mode={mode} liquid={liquid} y={at/w}");
        }

        double? reference=null;
        foreach(SimulationMode mode in Enum.GetValues<SimulationMode>()) foreach(int fps in new[]{30,60,100})
        {
            var cage=new GridCell[n];for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=3;dx++)cage[p+dy*w+dx]=Cell(fixture);
            cage[p]=Cell(coal);cage[p+1]=Cell(oil);
            Upload(cage);settings.Mode=mode;settings.Paused=false;coordinator.RestoreWorldActivity(r,true,true,false);
            for(int f=0;f<2*fps;f++)coordinator.DispatchFrame(settings,[],1f/fps);
            var after=Read();Balance(cage,after,"clock "+mode+" "+fps);
            Check(Math.Abs(after[p].FuelMass-.05f)<.0001,"Production oil rate "+mode+" "+fps);
            reference??=after[p].FuelMass;Check(Math.Abs(after[p].FuelMass-reference.Value)<.0001,"FPS-dependent oil uptake");
            Console.WriteLine($"PHYXEL_OIL_ABSORPTION_CLOCK mode={mode} fps={fps} fuel={after[p].FuelMass}");
        }
        // Invalid configurations fail before a material reaches the GPU.
        string cores=Path.Combine(AppContext.BaseDirectory,"Materials/core");
        foreach(string invalid in new[]{"missing","water","gas","zero","unknown-field","partner"})
        {
            string coreDir=Path.Combine(dir,"invalid-core-"+invalid);Directory.CreateDirectory(coreDir);
            foreach(string file in Directory.GetFiles(cores,"*.json"))File.Copy(file,Path.Combine(coreDir,Path.GetFileName(file)),true);
            foreach(string file in new[]{"coal.json","wet_charcoal.json"})
            {
                string target=Path.Combine(coreDir,file);var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(target))!;
                var definition=json["fuelAbsorption"]!.AsObject();
                if(invalid=="missing")definition["liquid"]="test:absent";
                if(invalid=="water")definition["liquid"]=CoreMaterialIds.Water;
                if(invalid=="gas")definition["liquid"]=CoreMaterialIds.Fire;
                if(invalid=="zero")definition["capacity"]=0;
                if(invalid=="unknown-field")definition["unused"]=1;
                if(invalid=="partner" && file=="coal.json")definition["absorptionRate"]=.03;
                File.WriteAllText(target,json.ToJsonString());
            }
            bool rejected=false;try{_ = new MaterialRegistry(coreDir,Path.Combine(dir,"empty-external"));}
            catch(InvalidDataException){rejected=true;}Check(rejected,"Invalid absorption material reached GPU: "+invalid);
        }
        File.WriteAllText(Path.Combine(dir,"result.txt"),$"checks={checks}\nGrid52 Material224 writer14\n");
        Console.WriteLine($"PHYXEL_OIL_ABSORPTION_SUCCESS checks={checks}");
    }
}
