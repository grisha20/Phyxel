using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
namespace Phyxel.Diagnostics;

internal static class AbsorptionModelRegressionVerifier
{
    internal static int Run()
    {
        try{Task.Run(RunAsync).GetAwaiter().GetResult();Console.WriteLine("PHYXEL_ABSORPTION_MODEL_SUCCESS");return 0;}
        catch(Exception e){Console.WriteLine("PHYXEL_ABSORPTION_MODEL_FAILED "+e);return 1;}
    }
    private static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"phyxel-absorption-"+Guid.NewGuid().ToString("N"));
        string external=Path.Combine(root,"external");Directory.CreateDirectory(external);
        string core=Path.Combine(AppContext.BaseDirectory,"Materials/core");
        if(!Directory.Exists(core)) core=Path.GetFullPath("Materials/core");
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);}
        string Liquid(string id)=>$$$"""{"schema":1,"id":"{{{id}}}","name":"Test liquid","kind":"liquid","color":"#8090A0","physics":{"density":1.2},"thermal":{"initialTemperature":30,"heatCapacity":2,"conductivity":0},"ui":{"hidden":false,"order":500}}""";
        await File.WriteAllTextAsync(Path.Combine(external,"liquid.json"),Liquid("test:liquid"));
        var registry=new MaterialRegistry(core,external);
        uint sand=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand),species=registry.GetRequiredRuntimeIndex("test:liquid");
        var table=registry.CreateGpuTable();var cell=new GridCell{IsActive=1,MaterialIndex=sand,Mass=1,Temperature=70,MoistureMass=.06f,FuelMass=.12f,RetainedLiquidMaterialIndex=species};
        Check(Math.Abs(PhaseEnthalpy.EffectiveCapacity(cell,table)-(table[sand].HeatCapacity+.06f*table[registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water)].HeatCapacity+.24f))<1e-5,"Retained species heat capacity ignored");
        float energy=PhaseEnthalpy.SpecificEnergy(cell,table);PhaseEnthalpy.SetSpecificEnergy(ref cell,energy,table);
        Check(Math.Abs(cell.Temperature-70)<1e-4,"Retained mixture energy roundtrip");
        var serializer=new SimulationStateSerializer();string path=Path.Combine(root,"mixed.json");
        await serializer.SaveAsync(path,new SimulationSettings(),(ushort)sand,new(1,1,MemoryMarshal.AsBytes(new[]{cell}.AsSpan()).ToArray()),registry);
        // Insert a species that sorts before the saved one. Runtime indices change.
        await File.WriteAllTextAsync(Path.Combine(external,"earlier.json"),Liquid("aaa:liquid"));
        var reordered=new MaterialRegistry(core,external);var loaded=await serializer.LoadAsync(path,reordered);
        var restored=MemoryMarshal.Cast<byte,GridCell>(loaded!.World!.Grid)[0];
        Check(restored.RetainedLiquidMaterialIndex==reordered.GetRequiredRuntimeIndex("test:liquid") && restored.FuelMass==cell.FuelMass && restored.MoistureMass==cell.MoistureMass && restored.Temperature==cell.Temperature,"Species palette remap/reload lost mixture");
        // Old extension spelling remains valid, with its restricted carrier semantics.
        string legacyCore=Path.Combine(root,"legacy-core");Directory.CreateDirectory(legacyCore);
        foreach(string file in Directory.GetFiles(core,"*.json")){
            var node=JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            if(node["liquidAbsorption"] is {} absorption){
                var old=absorption.DeepClone();old.AsObject().Remove("allLiquids");
                node.AsObject().Remove("liquidAbsorption");node["fuelAbsorption"]=old;
            }
            await File.WriteAllTextAsync(Path.Combine(legacyCore,Path.GetFileName(file)),node.ToJsonString());
        }
        var restricted=new MaterialRegistry(legacyCore,external);
        Check((restricted[CoreMaterialIds.Sand].Properties.Flags & (uint)MaterialFlags.UniversalPores)==0 &&
            restricted[CoreMaterialIds.Sand].Properties.FuelCapacity==table[sand].FuelCapacity,"Legacy extension semantics changed");
        bool forbidden=false;
        try{await serializer.LoadAsync(path,restricted);}catch(InvalidDataException){forbidden=true;}
        Check(forbidden,"Restricted host loaded a different carrier");
        var invalidCell=cell;invalidCell.RetainedLiquidMaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        forbidden=false;
        try{await serializer.SaveAsync(Path.Combine(root,"invalid.json"),new SimulationSettings(),(ushort)sand,
            new(1,1,MemoryMarshal.AsBytes(new[]{invalidCell}.AsSpan()).ToArray()),registry);}catch(InvalidDataException){forbidden=true;}
        Check(forbidden,"Primary water was accepted in the extra stock without its latent ledger");
        // Historical v14: strip only the appended ID, preserving the old prefix.
        var legacyCell=cell;legacyCell.MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);legacyCell.MoistureMass=0;legacyCell.RetainedLiquidMaterialIndex=0;
        string legacy=Path.Combine(root,"legacy.json");await serializer.SaveAsync(legacy,new SimulationSettings(),(ushort)sand,new(1,1,MemoryMarshal.AsBytes(new[]{legacyCell}.AsSpan()).ToArray()),registry);
        var metadata=JsonNode.Parse(await File.ReadAllTextAsync(legacy))!;metadata["Version"]=14;await File.WriteAllTextAsync(legacy,metadata.ToJsonString());
        byte[] bytes=await File.ReadAllBytesAsync(Path.ChangeExtension(legacy,".world"));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4,4),14);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16,4),52);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20,4),52);
        // The header is 28 bytes; following section lengths/data remain unchanged.
        byte[] prefix=[..bytes[..(28+52)],..bytes[(28+56)..]];await File.WriteAllBytesAsync(Path.ChangeExtension(legacy,".world"),prefix);
        loaded=await serializer.LoadAsync(legacy,reordered);restored=MemoryMarshal.Cast<byte,GridCell>(loaded!.World!.Grid)[0];
        Check(restored.FuelMass==legacyCell.FuelMass&&restored.RetainedLiquidMaterialIndex==reordered.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),"v14 oil migration failed");
        // Missing retained species must be rejected rather than disappearing or becoming oil.
        File.Delete(Path.Combine(external,"liquid.json"));bool rejected=false;
        try{await serializer.LoadAsync(path,new MaterialRegistry(core,external));}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Missing retained species silently replaced");
        SelectiveBarrierPolicy.GasOnly.Validate(registry);SelectiveBarrierPolicy.WaterOnly.Validate(registry);
        foreach(var m in registry.Materials)
        {
            Check(SelectiveBarrierPolicy.GasOnly.Allows(m)==(m.Properties.SimulationKind==(uint)MaterialSimulationKind.Gas),"Gas barrier species");
            Check(SelectiveBarrierPolicy.WaterOnly.Allows(m)==(m.Id==CoreMaterialIds.Water),"Water barrier species");
        }
        Check(SelectiveBarrierPolicy.GasOnly.AllowsAmbientAir&&!SelectiveBarrierPolicy.WaterOnly.AllowsAmbientAir,"Air is a separate permeability channel");
        Check(!SelectiveBarrierPolicy.WaterOnly.Allows(registry[CoreMaterialIds.Fire]),"Water barrier passed fire");
        var gasWithoutFlame=SelectiveBarrierPolicy.GasOnly with {BlockFlame=true};gasWithoutFlame.Validate(registry);
        Check(!gasWithoutFlame.Allows(registry[CoreMaterialIds.Fire])&&gasWithoutFlame.Allows(registry[CoreMaterialIds.Co2]),"Flame exclusion changed other gas permeability");
        var noncanonical=SelectiveBarrierPolicy.WaterOnly with {AllowedIds=new System.Collections.Generic.HashSet<string>{"CORE:WATER"}};
        forbidden=false;try{noncanonical.Validate(registry);}catch(ArgumentException){forbidden=true;}
        Check(forbidden,"A validated policy could disagree with its exact ID match");
        Console.WriteLine("PHYXEL_ABSORPTION_MODEL remap=True legacyV14=True missingSpeciesRejected=True barrierPolicies=True");
        // Temp files stay recoverable for diagnosis; no generated files in the checkout.
    }
}
