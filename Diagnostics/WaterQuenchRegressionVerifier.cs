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

internal static class WaterQuenchRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var settings=new SimulationSettings { Paused=true,AirSimulation=false,OpenBoundaries=false };
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,Radius=1,Density=1,
            MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water)}],0);
        int w=r.Width,n=w*r.Height,checks=0;
        var table=registry.CreateGpuTable();
        foreach(ref var m in table.AsSpan())m.AmbientCoolingRate=0;
        r.Materials.Upload(r.Context,table);
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),steam=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
            iron=registry.GetRequiredRuntimeIndex("core:cast_iron");
        GridCell Cell(uint id,float t,float mass=1,float latent=0)=>new(){IsActive=1,MaterialIndex=id,Temperature=t,Mass=mass,Lifetime=latent};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Upload(GridCell[] g){r.Context.UpdateSubresource(g,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(g.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);}
        double Q(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        double Mass(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass);
        void Check(bool ok,string label){checks++;if(!ok)throw new InvalidOperationException("QH "+label);}
        void Balanced(GridCell[] before,GridCell[] after,string label)
        {
            Check(Math.Abs(Mass(before)-Mass(after))<.0001,label+" mass");
            Check(Math.Abs(Q(before)-Q(after))<Math.Max(.01,Math.Abs(Q(before))*.00005),label+" energy");
            Check(after.Where(c=>c.IsActive!=0).All(c=>float.IsFinite(c.Temperature) && c.Mass>0),label+" finite");
        }
        foreach(float paid in new[]{.01f,2255.9f})
        {
            var g=new GridCell[n];int origin=80*w+80;g[origin]=Cell(water,100,1,paid);
            Upload(g);Evaporate(0);var a=Read();Balanced(g,a,"resolved tail "+paid);
            Check(paid<1?a[origin].Mass==1:a[origin].Mass>=.0004999,"unresolved liquid tail");
            Check(paid<1?a.AsSpan().SequenceEqual(g):a[origin+1].Mass>.99,"paid tail retained");
        }
        void Evaporate(uint tick)
        {
            var c=new ContactTransitionConstants{DeltaTime=.05f,Width=(uint)w,Height=(uint)r.Height,TickIndex=tick};
            var ctx=r.Context;ctx.UpdateSubresource(ref c,r.ContactTransitionConstants);
            ctx.ComputeShader.Set(r.MoistureShader);ctx.ComputeShader.SetConstantBuffer(0,r.ContactTransitionConstants);
            ctx.ComputeShader.SetShaderResource(0,r.Materials.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CellMaterials.UnorderedView,r.GasMotion.UnorderedView,r.ContactSummary.UnorderedView);
            ctx.Dispatch((w+15)/16,(r.Height+15)/16,1);
            ctx.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<4;i++)ctx.ComputeShader.SetUnorderedAccessView(i,null);
        }
        // Partial boiling must emit paid mass and preserve the other liquid.
        var superheated=new GridCell[n];superheated[80*w+80]=Cell(water,100.5f,1,2256);
        Upload(superheated);Evaporate(0);
        Check(Read().AsSpan().SequenceEqual(superheated),"fully paid superheat must remain for phase pass");
        foreach(float paid in new[]{0f,225.6f,1128f})
        {
            var g=new GridCell[n];int origin=80*w+80;g[origin]=Cell(water,100,1,paid);
            Upload(g);Evaporate(0);var a=Read();Balanced(g,a,"partial "+paid);
            double vapour=a.Where(c=>c.IsActive!=0 && c.MaterialIndex==steam).Sum(c=>(double)c.Mass);
            Check(Math.Abs(vapour-paid/2256)<.00001,"paid evaporation amount");
            Check(paid==0 ? a[origin].Mass==1 : a[origin].Mass<1 && Math.Abs(a[origin].Temperature-100)<.001,"liquid remainder");
            Console.WriteLine($"PHYXEL_QH_PARTIAL paid={paid} vapour={vapour} Qerror={Q(a)-Q(g)}");
        }
        var sealedWater=new GridCell[n];int o=80*w+80;
        sealedWater[o]=Cell(water,100,1,1000);
        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)if(dx!=0||dy!=0)sealedWater[o+dy*w+dx]=Cell(iron,100,7.2f);
        Upload(sealedWater);for(uint i=0;i<4;i++)Evaporate(i);
        Check(Read().AsSpan().SequenceEqual(sealedWater),"sealed paid stock retained");
        var filtered=new GridCell[n];filtered[o]=Cell(water,100,1,1000);
        r.FilterMap[o+1]=FilterRules.Closed;r.FilterCount=1;r.UploadFilters();
        Upload(filtered);Evaporate(0);
        Check(Read().AsSpan().SequenceEqual(filtered),"wall filter blocked partial vapour");
        Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
        // A broad pool on an eight-cell wall: no film lift of a deep column.
        var plate=new GridCell[n];
        for(int y=64;y<96;y++)for(int x=80;x<144;x++)
            plate[y*w+x]=y<88?Cell(water,20):Cell(iron,1000,7.2f);
        Upload(plate);double initial=Q(plate);
        for(uint i=0;i<1200;i++)coordinator.DispatchThermalDiffusion(r,false,i,true);
        var cooled=Read();Balanced(plate,cooled,"filled plate60s");
        double wall=cooled.Where(c=>c.IsActive!=0 && c.MaterialIndex==iron).Average(c=>(double)c.Temperature);
        double surface=Enumerable.Range(80,64).Average(x=>(double)cooled[88*w+x].Temperature);
        Check(wall<200,"thin full plate did not cool below200C");
        Check(cooled[87*w+100].IsActive!=0,"deep pool lifted");
        Console.WriteLine($"PHYXEL_QH_POOL seconds=60 wall={wall} surface={surface} Qerror={Q(cooled)-initial}");
        // Film lift of a small column, and a finite symmetric heat flux in gap.
        GridCell[]? filmSnapshot=null;
        foreach(float wallT in new[]{200f,1000f})
        {
            var g=new GridCell[n];
            for(int x=80;x<85;x++) {g[90*w+x]=Cell(iron,wallT,7.2f);for(int y=87;y<90;y++)g[y*w+x]=Cell(water,100,1,10);}
            Upload(g);coordinator.DispatchThermalDiffusion(r,false,0,true);var a=Read();Balanced(g,a,"film "+wallT);
            Check(wallT==1000?a[89*w+82].IsActive==0 && a[86*w+82].MaterialIndex==water:a[89*w+82].MaterialIndex==water,
                "hot/cold film selection");
            if(wallT==1000)
            {
                filmSnapshot=a;
                double before=Q(a);coordinator.DispatchThermalDiffusion(r,false,1,false);var b=Read();Balanced(a,b,"film gap");
                Check(b[88*w+82].Lifetime>a[88*w+82].Lifetime,"film gap no heat transfer");
                Console.WriteLine($"PHYXEL_QH_FILM wall={wallT} water={b[88*w+82].Temperature} Qerror={Q(b)-before}");
                for(int x=80;x<85;x++)b[90*w+x].Temperature=200;
                Upload(b);coordinator.DispatchThermalDiffusion(r,false,2,true);var released=Read();
                Balanced(b,released,"cooled film release");
                Check((released[88*w+82].BodyId & 0x40000000u)==0,"cooled film still held");
            }
        }
        var blockedDrop=new GridCell[n];
        for(int x=80;x<85;x++) {blockedDrop[90*w+x]=Cell(iron,1000,7.2f);for(int y=87;y<90;y++)blockedDrop[y*w+x]=Cell(water,100,1,10);}
        r.FilterMap[86*w+82]=FilterRules.Closed;r.FilterCount=1;r.UploadFilters();
        var filterProbe=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.Filters.Buffer)).ToArray();
        Check(filterProbe[0]==1 && filterProbe[86*w+82+1]==FilterRules.Closed,"filter fixture upload");
        Upload(blockedDrop);coordinator.DispatchThermalDiffusion(r,false,0,true);
        var blockedResult=Read();
        Console.WriteLine($"PHYXEL_QH_FILTER bottom={blockedResult[89*w+82].MaterialIndex} water={water} top={blockedResult[86*w+82].MaterialIndex} count={r.FilterCount}");
        Check(blockedResult[86*w+82].IsActive==0,"film crossed wall filter");
        Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
        // The shared interface must stay bounded for all current conductors.
        foreach(string id in new[]{CoreMaterialIds.Metal,"core:steel","core:cast_iron","core:copper"})
        {
            uint conductor=registry.GetRequiredRuntimeIndex(id);var g=new GridCell[n];
            for(int y=64;y<96;y++)for(int x=80;x<112;x++)
                g[y*w+x]=y<88?Cell(water,20):Cell(conductor,800,table[conductor].Density);
            Upload(g);for(uint i=0;i<120;i++)coordinator.DispatchThermalDiffusion(r,false,i,true);
            var a=Read();Balanced(g,a,id+" wet wall");
            Check(a.Where(c=>c.IsActive!=0).All(c=>c.Temperature>=19.999 && c.Temperature<=800.001),id+" wet wall range");
        }
        // Save the new fractional liquid and emitted vapour, then continue exactly.
        var gsave=new GridCell[n];gsave[o]=Cell(water,100,1,225.6f);Upload(gsave);Evaporate(0);
        var saved=Read();var serializer=new SimulationStateSerializer();
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!;Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"partial-boiling.json");
        var snapshot=new SimulationWorldSnapshot(r.Width,r.Height,MemoryMarshal.AsBytes(saved.AsSpan()).ToArray());
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)water,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!.World!;
        Check(loaded.Grid.AsSpan().SequenceEqual(snapshot.Grid),"fractional save/load");
        Upload(saved);coordinator.DispatchThermalDiffusion(r,false,1,false);var next=Read();
        serializer.ApplyWorldSnapshot(r,loaded);coordinator.DispatchThermalDiffusion(r,false,1,false);
        Check(Read().AsSpan().SequenceEqual(next),"reload continuation");
        snapshot=snapshot with {Grid=MemoryMarshal.AsBytes(filmSnapshot!.AsSpan()).ToArray()};
        path=Path.Combine(dir,"vapour-cushion.json");
        Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)water,snapshot,registry)).GetAwaiter().GetResult();
        loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!.World!;
        Check(loaded.Grid.AsSpan().SequenceEqual(snapshot.Grid),"film save/load exact");
        serializer.ApplyWorldSnapshot(r,loaded);coordinator.RestoreWorldActivity(r,true,true,false);
        coordinator.DispatchFrame(settings,[],.05f);
        Check(Read().AsSpan().SequenceEqual(filmSnapshot),"paused film moved");
        WaterContactRegressionVerifier.Run(coordinator,registry);
        BulkHeatRegressionVerifier.Run(coordinator,registry);
        Console.WriteLine($"PHYXEL_QH_SUCCESS checks={checks}");
    }
}
