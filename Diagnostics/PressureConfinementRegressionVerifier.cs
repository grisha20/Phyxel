using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class PressureConfinementRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings,string dir)
    {
        const int size=256;
        settings.Width=size;settings.Height=size;settings.Paused=true;coordinator.ClearCurrentWorld(settings);
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        uint powder=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=fixture}],0);
        var table=registry.CreateGpuTable();r.Materials.Upload(r.Context,table);
        int checks=0,failures=0;
        void Check(bool pass,string name){checks++;if(!pass)failures++;Console.WriteLine($"PHYXEL_CONFINEMENT pass={pass} {name}");}
        GridCell Cell(uint material)=>new(){MaterialIndex=material,IsActive=1,Mass=table[material].Density,Temperature=30};
        foreach(string variant in new[]{"closed","vent","powder","closed-filter","air-filter","maze"})
        {
            var grid=new GridCell[size*size];var filters=new uint[grid.Length+1];
            for(int y=32;y<224;y++)for(int x=32;x<224;x++)
                if(x<36||x>=220||y<36||y>=220)grid[y*size+x]=Cell(fixture);
            if(variant!="closed" && variant!="maze")
                for(int y=32;y<36;y++)for(int x=120;x<140;x++)
                {
                    grid[y*size+x]=variant=="powder"?Cell(powder):default;
                    if(variant.EndsWith("filter"))
                    {filters[0]++;filters[y*size+x+1]=variant=="air-filter"?1u<<18:1u<<19;}
                }
            if(variant=="maze")
            {
                // Alternating long baffles form a path far longer than 32
                // roots. A top opening reaches its distant bottom pocket.
                for(int y=32;y<36;y++)for(int x=40;x<52;x++)grid[y*size+x]=default;
                int wall=0;
                for(int y=48;y<210;y+=16,wall++)for(int x=36;x<220;x++)
                    if(wall%2==0?x<204:x>=52)for(int dy=0;dy<4;dy++)grid[(y+dy)*size+x]=Cell(fixture);
            }
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            r.Context.UpdateSubresource(filters,r.Filters.Buffer);
            r.Context.UpdateSubresource(new AirCell[r.AirWidth*r.AirHeight],r.Air.Buffer);
            r.Context.UpdateSubresource(new System.Numerics.Vector4[r.AirWidth*r.AirHeight],r.ReactionPulse.ReadBuffer);
            SimulationDispatchCoordinator.DispatchPressureFragments(r,new(){Width=size,Height=size},true,true);
            uint[] roots=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer)).ToArray();
            uint[] links=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.PressureLinks.Buffer)).ToArray();
            // Independent CPU graph traversal validates GPU atomic union and
            // compression, including all pairwise component equivalences.
            var adjacent=Enumerable.Range(0,roots.Length).Select(_=>new List<int>()).ToArray();
            void Edge(int a,int b){adjacent[a].Add(b);adjacent[b].Add(a);}
            for(int i=0;i<links.Length;i++)
            {if((links[i]&1)!=0)Edge(i+1,i+2);if((links[i]&2)!=0)Edge(i+1,i+r.AirWidth+1);if((links[i]&4)!=0)Edge(i+1,0);}
            var expected=Enumerable.Repeat(-1,roots.Length).ToArray();
            for(int start=0;start<roots.Length;start++)if(expected[start]<0)
            {var queue=new Queue<int>();queue.Enqueue(start);expected[start]=start;
                while(queue.Count>0){int a=queue.Dequeue();foreach(int b in adjacent[a])if(expected[b]<0){expected[b]=start;queue.Enqueue(b);}}}
            Check(expected.Select((root,i)=>roots[i]==(uint)root).All(p=>p),"PC01 GPU roots match CPU BFS "+variant);
            int probe=(214/4)*r.AirWidth+64/4+1;
            bool open=variant is "vent" or "powder" or "air-filter" or "maze";
            Check((roots[probe]==0)==open,"PC01 expected geometry confinement "+variant+" root="+roots[probe]);
            File.WriteAllBytes(Path.Combine(dir,"confinement-"+variant+"-roots.bin"),MemoryMarshal.AsBytes(roots.AsSpan()).ToArray());
        }
        r.Context.UpdateSubresource(new uint[size*size+1],r.Filters.Buffer);
        if(failures>0)Environment.ExitCode=1;
        Console.WriteLine($"PHYXEL_CONFINEMENT_COMPLETE checks={checks} failures={failures}");yield return r;
    }
}
