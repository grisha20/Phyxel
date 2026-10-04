using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class WettingContactRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string directory=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/wetting-contact";
        Directory.CreateDirectory(directory);
        bool passed=true; int checks=0;
        void Check(bool ok,string label) { checks++; if(!ok) { passed=false; Console.WriteLine("PHYXEL_WETTING_CHECK_FAILED "+label); } }
        VerifyLoading(directory,Check);
        var settings=new SimulationSettings { Paused=true,AirSimulation=false,OpenBoundaries=false };
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal), wet=registry.GetRequiredRuntimeIndex(CoreMaterialIds.WetCharcoal);
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water), molten=registry.GetRequiredRuntimeIndex("core:molten_metal");
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var r=coordinator.DispatchFrame(settings,[new() { X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=(ushort)coal }],0);
        int w=r.Width,n=w*r.Height;
        var table=registry.CreateGpuTable();
        GridCell Cell(uint material,float temperature=20,float mass=1) => new() {
            IsActive=1,MaterialIndex=material,Temperature=temperature,Mass=mass };
        GridCell Fuel() { var cell=Cell(coal,20,.6f); cell.Lifetime=1; cell.Pressure=3; cell.RestFrames=7; return cell; }
        GridCell[] Read() => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Upload(GridCell[] grid) {
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer); r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);
        }
        bool Equal(GridCell a,GridCell b) => MemoryMarshal.AsBytes(new[] {a}.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(new[] {b}.AsSpan()));
        void Contact(uint tick,float dt=.05f) {
            var constants=new ContactTransitionConstants { Width=(uint)w,Height=(uint)r.Height,DeltaTime=dt,TickIndex=tick };
            r.Context.UpdateSubresource(ref constants,r.ContactTransitionConstants);
            r.Context.ComputeShader.Set(r.ContactTransitionShader); r.Context.ComputeShader.SetConstantBuffer(0,r.ContactTransitionConstants);
            r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CellMaterials.UnorderedView,r.GasMotion.UnorderedView,r.ContactSummary.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            r.Context.ComputeShader.Set(r.MoistureShader);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);
            r.Context.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<4;i++) r.Context.ComputeShader.SetUnorderedAccessView(i,null);
            r.Context.ComputeShader.Set(null);
        }
        double Energy(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        double Mass(GridCell[] grid) => grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass);

        // Real contact shader, stationary separated populations. It must
        // distinguish identity from simulation kind without altering neighbours.
        var cases=new[] { "water-left","water-right","water-up","water-down","molten","steam","ice","metal","empty","diagonal","mixed" };
        foreach(string label in cases) {
            var before=new GridCell[n]; var positions=new List<int>();
            for(int i=0;i<64;i++) {
                int index=(30+6*(i/8))*w+30+6*(i%8); positions.Add(index); before[index]=Fuel();
                int offset=label switch { "water-left" => -1,"water-up" => -w,"water-down" => w,"diagonal" => w+1,_=>1 };
                uint neighbour=label switch { "molten"=>molten,"steam"=>registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
                    "ice"=>registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice),"metal"=>registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),_=>water };
                if(label!="empty") before[index+offset]=Cell(neighbour,label=="molten"?1050:20,.4f);
                if(label=="mixed") before[index-1]=Cell(molten,1050,.4f);
            }
            Upload(before); for(uint tick=0;tick<1000;tick++) Contact(tick);
            var after=Read(); int count=positions.Count(i=>after[i].MaterialIndex==wet);
            bool allowed=label.StartsWith("water-",StringComparison.Ordinal)||label=="mixed";
            Check(allowed?count==64:count==0,label+" unexpected wet count="+count);
            foreach(int index in positions) {
                var c=after[index]; Check(c.Mass==before[index].Mass&&c.Temperature==before[index].Temperature,label+" changed fuel mass/T");
                Check(allowed?c.Lifetime==0&&c.Pressure==0&&c.RestFrames==0:Equal(c,before[index]),label+" wrong ignition/contact reset");
            }
            Check(Enumerable.Range(0,n).Where(i=>before[i].IsActive!=0&&before[i].MaterialIndex!=coal).All(i=>
                allowed && before[i].MaterialIndex==water ? after[i].Temperature==before[i].Temperature && after[i].Mass<=before[i].Mass : Equal(before[i],after[i])),
                label+" changed nonabsorbed neighbour or water temperature");
            Check(Math.Abs(Energy(after)-Energy(before))<.0001*Math.Max(1,Math.Abs(Energy(before)))&&Math.Abs(Mass(after)-Mass(before))<1e-5,label+" lost mass/heat");
            Console.WriteLine($"PHYXEL_WETTING_POPULATION case={label} wet={count}/64 energyError={Energy(after)-Energy(before):E6}");
        }
        // Edges exercise the guard for each outside neighbour.
        foreach(int index in new[] { 0,w-1,(r.Height-1)*w,n-1 }) {
            var edge=new GridCell[n]; edge[index]=Fuel(); edge[index%w==0?index+1:index-1]=Cell(water);
            Upload(edge); for(uint tick=0;tick<1000;tick++) Contact(tick);
            var after=Read(); Check(after[index].MaterialIndex==wet&&after[index].Mass==.6f,"Boundary contact failed at "+index);
        }
        // The selector itself is generic: retarget a diagnostic material table
        // to molten, then wildcard, using the same loaded shader.
        var coalProperties=table[coal]; var wetProperties=table[wet];
        table[coal].MoistureCapacity=0; table[wet].MoistureCapacity=0;
        foreach(bool wildcard in new[] {false,true}) {
            table[coal].ContactLiquidRequiredMaterialIndex=wildcard?uint.MaxValue:molten;
            table[coal].ContactLiquidRatePerSecond=100; r.Materials.Upload(r.Context,table);
            foreach(uint neighbour in new[] {water,molten}) {
                int origin=100*w+100; var pair=new GridCell[n]; pair[origin]=Fuel(); pair[origin+1]=Cell(neighbour,20);
                Upload(pair); Contact(10,1); var after=Read();
                Check((after[origin].MaterialIndex==wet)==(wildcard||neighbour==molten),"Generic selector/wildcard failed "+wildcard+" "+neighbour);
            }
        }
        table[coal]=coalProperties; table[wet]=wetProperties; r.Materials.Upload(r.Context,table);

        // Continue identical contact ticks after codec round-trip, preserving
        // coal's ignition latch and the already transitioned hidden material.
        var save=new GridCell[n];
        for(int i=0;i<64;i++) { int index=(100+4*(i/8))*w+100+4*(i%8); save[index]=Fuel(); save[index+(i%2==0?1:-w)]=Cell(water,20,.4f); }
        Upload(save); for(uint tick=0;tick<2;tick++) Contact(tick); var partial=Read();
        Check(partial.Any(c=>c.IsActive!=0&&c.MaterialIndex==wet)&&partial.Any(c=>c.IsActive!=0&&c.MaterialIndex==coal),"Save fixture not partially transitioned");
        var serializer=new SimulationStateSerializer();
        var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(partial.AsSpan()).ToArray());
        string savePath=Path.Combine(directory,"partial-wetting.json");
        Task.Run(()=>serializer.SaveAsync(savePath,settings,(ushort)coal,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(savePath,registry)).GetAwaiter().GetResult()
            ?? throw new InvalidDataException("Wetting scene was not loaded.");
        Check(loaded.World is not null&&snapshot.Grid.AsSpan().SequenceEqual(loaded.World.Grid),"Save changed wetting/latch fields");
        GridCell[] Continue(GridCell[] grid) { Upload(grid); for(uint tick=2;tick<220;tick++) Contact(tick); return Read(); }
        var memory=Continue(partial); var reload=Continue(MemoryMarshal.Cast<byte,GridCell>(loaded.World!.Grid).ToArray());
        Check(MemoryMarshal.AsBytes(memory.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(reload.AsSpan())),"Reload changed wetting continuation");

        // Actual frame clocks: insulated two-cell pockets prevent transport
        // changing contact duration. No reaction: temperature stays20C.
        var pocket=new GridCell[n]; var origins=new List<int>();
        for(int i=0;i<64;i++) {
            int x=30+6*(i%8),y=100+5*(i/8),origin=y*w+x; origins.Add(origin);
            for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=2;dx++)
                if(dy!=0||dx<0||dx>1) pocket[(y+dy)*w+x+dx]=Cell(fixture);
            pocket[origin]=Cell(coal,20,.6f); pocket[origin+1]=Cell(water,20,.4f);
        }
        int? referenceCount=null;
        foreach(bool air in new[] {false,true})
        foreach(var mode in new[] {SimulationMode.Sandbox,SimulationMode.Simulation}) foreach(int fps in new[] {30,60,100}) {
            Upload(pocket); coordinator.RestoreWorldActivity(r,true,true,false); settings.Mode=mode; settings.Paused=false;
            settings.AirSimulation=air;
            for(int i=0;i<fps*10;i++) coordinator.DispatchFrame(settings,[],1f/fps);
            var after=Read(); int count=after.Count(c=>c.IsActive!=0&&c.MaterialIndex==wet);
            referenceCount ??=count;
            Check(count>=50&&Math.Abs(count-referenceCount.Value)<=1,"Wetting cadence drift "+mode+" "+fps+" count="+count);
            Check(Math.Abs(Energy(after)-Energy(pocket))<1e-5&&Math.Abs(Mass(after)-Mass(pocket))<1e-5,"Frame contact lost mass/heat");
            settings.Paused=true; var paused=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);
            coordinator.DispatchFrame(settings,[],1);
            Check(paused.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)),"Paused wetting changed cells");
            Console.WriteLine($"PHYXEL_WETTING_CADENCE mode={mode} air={air} fps={fps} ticks={coordinator.ThermalTicks} wet={count}/64");
        }
        Console.WriteLine($"PHYXEL_WETTING_RESULT passed={passed} checks={checks}");
        if(!passed) throw new InvalidOperationException("Wetting contact acceptance failed.");
    }

    private static void VerifyLoading(string directory,Action<bool,string> check)
    {
        string external=Path.Combine(directory,"loading-external"); Directory.CreateDirectory(external);
        string Json(string id,string kind,JsonObject? rule=null) {
            var document=new JsonObject { ["schema"]=1,["id"]=id,["kind"]=kind,
                ["name"]=new JsonObject {["en"]=id},["color"]="#777777",
                ["physics"]=new JsonObject {["density"]=1,["friction"]=.5,["flowRate"]=.3} };
            if(rule is not null) document["contactTransitions"]=new JsonObject {["liquid"]=rule};
            return document.ToJsonString();
        }
        JsonObject Rule(JsonNode? with,bool explicitWith=true) {
            var rule=new JsonObject {["into"]="test:wet",["ratePerSecond"]=.35};
            if(explicitWith) rule["with"]=with;
            return rule;
        }
        File.WriteAllText(Path.Combine(external,"wet.json"),Json("test:wet","granular"));
        File.WriteAllText(Path.Combine(external,"liquid.json"),Json("test:liquid","liquid"));
        File.WriteAllText(Path.Combine(external,"selected.json"),Json("test:selected","granular",Rule(JsonValue.Create(" TEST:LIQUID "))));
        File.WriteAllText(Path.Combine(external,"legacy.json"),Json("test:legacy","granular",Rule(null,false)));
        var invalid=new Dictionary<string,JsonObject> {
            ["null"]=Rule(null),["empty"]=Rule(JsonValue.Create(" ")),["number"]=Rule(JsonValue.Create(5)),
            ["array"]=Rule(new JsonArray("core:water")),["bad-id"]=Rule(JsonValue.Create("water")),
            ["missing"]=Rule(JsonValue.Create("test:missing")),["solid"]=Rule(JsonValue.Create(CoreMaterialIds.Metal)),
            ["gas"]=Rule(JsonValue.Create(CoreMaterialIds.Steam)),["granular"]=Rule(JsonValue.Create(CoreMaterialIds.Coal)) };
        foreach(var entry in invalid) File.WriteAllText(Path.Combine(external,entry.Key+".json"),Json("test:bad_"+entry.Key.Replace('-','_'),"granular",entry.Value));
        var originalError=Console.Error; using var errors=new StringWriter(); MaterialRegistry loaded;
        try { Console.SetError(errors); loaded=new MaterialRegistry(external); } finally { Console.SetError(originalError); }
        check(loaded["test:selected"].LiquidContactTransition?.WithId=="test:liquid"&&
            loaded["test:selected"].Properties.ContactLiquidRequiredMaterialIndex==loaded["test:liquid"].RuntimeIndex,
            "External selector normalization/resolution failed");
        check(loaded["test:legacy"].Properties.ContactLiquidRequiredMaterialIndex==uint.MaxValue,"Legacy ANY-liquid loading changed");
        foreach(var entry in invalid) check(!loaded.Materials.Any(d=>d.Id=="test:bad_"+entry.Key.Replace('-','_')),
            "Invalid selector accepted: "+entry.Key);
        File.WriteAllText(Path.Combine(directory,"loading-errors.log"),errors.ToString());
        string coreSource=Path.GetDirectoryName(loaded[CoreMaterialIds.Coal].SourcePath)!;
        foreach(string withId in new[] {CoreMaterialIds.Metal,"core:not_present","test:liquid"}) {
            string core=Path.Combine(directory,"invalid-core-"+withId.Split(':')[1]); Directory.CreateDirectory(core);
            foreach(string file in Directory.GetFiles(coreSource,"*.json")) File.Copy(file,Path.Combine(core,Path.GetFileName(file)),true);
            string path=Path.Combine(core,"coal.json"); var json=JsonNode.Parse(File.ReadAllText(path))!;
            json["contactTransitions"]!["liquid"]!["with"]=withId; File.WriteAllText(path,json.ToJsonString());
            bool rejected=false;
            try { _=new MaterialRegistry(core,external); }
            catch(InvalidDataException exception) { rejected=exception.Message.Contains("Invalid core contact transition",StringComparison.Ordinal); }
            check(rejected,"Core selector did not fail closed: "+withId);
        }
        check(Marshal.SizeOf<MaterialProperties>()==224&&Marshal.OffsetOf<MaterialProperties>(nameof(MaterialProperties.ThermalDeviceMaximumPower)).ToInt32()==172,
            "Contact selector changed material ABI");
        Console.WriteLine("PHYXEL_WETTING_LOADING invalidSelectors=9 invalidCore=3 legacy=True customLiquid=True materialBytes=224");
    }
}
