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
internal static class FilterModelRegressionVerifier
{
    internal static int Run(){try{RunAsync().GetAwaiter().GetResult();return 0;}catch(Exception e){Console.WriteLine("PHYXEL_FILTER_MODEL_FAILED "+e);return 1;}}
    private static async Task RunAsync()
    {
        string dir=Path.Combine(Path.GetTempPath(),"phyxel-filters-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string external=Path.Combine(dir,"external");Directory.CreateDirectory(external);
        string core=Path.GetFullPath("Materials/core");int checks=0;
        void Check(bool ok,string name){checks++;if(!ok)throw new InvalidOperationException(name);}
        string Liquid(string id)=>$$$"""{"schema":1,"id":"{{{id}}}","name":"Filter test","kind":"liquid","color":"#8090A0","physics":{"density":1.2},"thermal":{"conductivity":0},"ui":{"order":500}}""";
        await File.WriteAllTextAsync(Path.Combine(external,"test.json"),Liquid("test:filter_liquid"));
        var registry=new MaterialRegistry(core,external);
        ushort selected=registry.GetRequiredRuntimeIndex("test:filter_liquid");
        foreach(var preset in Enum.GetValues<FilterSelection>())foreach(var material in registry.Materials){
            uint rule=FilterRules.Select(preset,registry,selected);var kind=(MaterialSimulationKind)material.Properties.SimulationKind;
            bool expected=preset switch{
                FilterSelection.Steam=>material.Id==CoreMaterialIds.Steam,FilterSelection.Water=>material.Id==CoreMaterialIds.Water,
                FilterSelection.Oil=>material.Id==CoreMaterialIds.Oil,FilterSelection.Gases=>kind==MaterialSimulationKind.Gas,
                FilterSelection.Liquids=>kind==MaterialSimulationKind.Liquid,FilterSelection.Powders=>kind==MaterialSimulationKind.Granular,
                _=>material.Id=="test:filter_liquid"};
            Check(FilterRules.Allows(rule,material.RuntimeIndex,kind)==expected,"preset "+preset+"/"+material.Id);
        }
        Check(FilterRules.Select(FilterSelection.SelectedMaterial,registry,registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal))==FilterRules.Closed,"immobile selection");
        Check(!FilterRules.AirAllows(FilterRules.Closed|FilterRules.AmbientAir),"closed air");
        foreach(uint rule in new[]{1u<<31,FilterRules.AmbientAir,511u}){
            bool invalid=false;try{FilterRules.Validate(rule,registry.Count);}catch(InvalidDataException){invalid=true;}Check(invalid,"bad rule "+rule);
        }
        uint[] rules=[(uint)selected+1,FilterRules.Gas|FilterRules.AmbientAir,FilterRules.Closed,0];
        var world=new SimulationWorldSnapshot(2,2,new byte[4*Marshal.SizeOf<GridCell>()],Filters:MemoryMarshal.AsBytes(rules.AsSpan()).ToArray());
        var serializer=new SimulationStateSerializer();string path=Path.Combine(dir,"filters.json");
        await serializer.SaveAsync(path,new(){FilterSelection=FilterSelection.SelectedMaterial},selected,world,registry);
        await File.WriteAllTextAsync(Path.Combine(external,"earlier.json"),Liquid("aaa:earlier"));
        var changed=new MaterialRegistry(core,external);var loaded=await serializer.LoadAsync(path,changed);
        uint[] restored=MemoryMarshal.Cast<byte,uint>(loaded!.World!.Filters!).ToArray();
        Check(selected!=changed.GetRequiredRuntimeIndex("test:filter_liquid"),"runtime order changed");
        Check(restored[0]==(uint)changed.GetRequiredRuntimeIndex("test:filter_liquid")+1&&restored.Skip(1).SequenceEqual(rules.Skip(1)),"palette mapping");
        Check(loaded.State.FilterSelection==FilterSelection.SelectedMaterial,"selected preset saved");
        var expanded=CanvasWorldExpansion.Expand(loaded.World,new(6,6));
        var expandedRules=MemoryMarshal.Cast<byte,uint>(expanded.Filters!).ToArray();
        Check(expandedRules[4*6]==restored[0]&&expandedRules[5*6]==restored[2]&&expandedRules.ToArray().Count(x=>x!=0)==3,"expand map");
        File.Delete(Path.Combine(external,"test.json"));var absent=new MaterialRegistry(core,external);loaded=await serializer.LoadAsync(path,absent);
        Check(MemoryMarshal.Cast<byte,uint>(loaded!.World!.Filters!)[0]==FilterRules.Closed,"unknown species seals filter");
        // A genuine v15 file has the same cell/field prefix and no overlay tail.
        string old=Path.Combine(dir,"v15.json");await serializer.SaveAsync(old,new(),0,new(2,2,new byte[4*56]),registry);
        var json=JsonNode.Parse(await File.ReadAllTextAsync(old))!;json["Version"]=15;await File.WriteAllTextAsync(old,json.ToJsonString());
        byte[] bytes=await File.ReadAllBytesAsync(Path.ChangeExtension(old,".world"));BitConverter.GetBytes(15).CopyTo(bytes,4);await File.WriteAllBytesAsync(Path.ChangeExtension(old,".world"),bytes);
        loaded=await serializer.LoadAsync(old,registry);Check(loaded!.World!.Filters is null,"v15 remains readable without overlay");
        byte[] full=await File.ReadAllBytesAsync(Path.ChangeExtension(path,".world"));await File.WriteAllBytesAsync(Path.ChangeExtension(path,".world"),full[..^1]);
        bool rejected=false;try{await serializer.LoadAsync(path,registry);}catch(Exception e)when(e is InvalidDataException or EndOfStreamException){rejected=true;}Check(rejected,"truncated map rejected");
        Console.WriteLine($"PHYXEL_FILTER_MODEL_SUCCESS checks={checks}");
    }
}
