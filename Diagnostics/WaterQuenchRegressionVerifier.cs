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
        foreach(float paid in new[]{0f,1128f})
        {
            var submerged=new GridCell[n];int site=80*w+80;
            submerged[site]=Cell(water,20);submerged[site+w]=Cell(water,100,1,paid);
            submerged[site+2*w]=Cell(iron,220,7.2f);
            Upload(submerged);Evaporate(1);var nucleated=Read();Balanced(submerged,nucleated,"submerged bubble "+paid);
            double vapour=nucleated.Where(c=>c.IsActive!=0 && c.MaterialIndex==steam).Sum(c=>(double)c.Mass);
            Console.WriteLine($"PHYXEL_QR_NUCLEATION paid={paid} vapour={vapour} Qerror={Q(nucleated)-Q(submerged)}");
            Check(paid==0?vapour==0:vapour>=.2,"submerged paid bubble missing");
        }
        var submergedFiltered=new GridCell[n];int blockedBubble=80*w+80;
        submergedFiltered[blockedBubble]=Cell(water,20);submergedFiltered[blockedBubble+w]=Cell(water,100,1,1128);
        submergedFiltered[blockedBubble+2*w]=Cell(iron,220,7.2f);
        r.FilterMap[blockedBubble+w]=FilterRules.Closed;r.FilterCount=1;r.UploadFilters();
        Upload(submergedFiltered);Evaporate(1);
        Check(Read().AsSpan().SequenceEqual(submergedFiltered),"filter allowed submerged nucleation");
        Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
        var coldLiquid=new GridCell[n];coldLiquid[80*w+80]=Cell(water,20);coldLiquid[81*w+80]=Cell(water,20);
        Upload(coldLiquid);Evaporate(1);
        Check(Read().AsSpan().SequenceEqual(coldLiquid),"cold liquid nucleated a bubble");
        foreach(float wallTemperature in new[]{20f,101f,199f})
        {
            var bulk=new GridCell[n];int site=80*w+80;
            bulk[site]=Cell(water,20);bulk[site+w]=Cell(water,100,1,1128);
            bulk[site+2*w]=Cell(iron,wallTemperature,7.2f);
            Upload(bulk);Evaporate(1);
            Check(Read().AsSpan().SequenceEqual(bulk),"bulk latent heat renucleated away from hot wall");
        }
        var unsupportedBubble=new GridCell[n];unsupportedBubble[80*w+80]=Cell(water,20);
        unsupportedBubble[81*w+80]=Cell(water,100,1,1128);
        Upload(unsupportedBubble);Evaporate(1);
        Check(Read().AsSpan().SequenceEqual(unsupportedBubble),"unsupported interior nucleation");
        unsupportedBubble[81*w+80].BodyId=0x40000000u;
        Upload(unsupportedBubble);Evaporate(1);var filmBubble=Read();
        Balanced(unsupportedBubble,filmBubble,"detached paid film");
        Check(filmBubble[81*w+80].MaterialIndex==steam,"detached hot film cannot release paid vapour");
        var overfill=new GridCell[n];
        for(int y=78;y<=80;y++)overfill[y*w+80]=Cell(water,20,y==80?1.75f:1);
        Upload(overfill);coordinator.DispatchThermalDiffusion(r,false,0,true);var relieved=Read();
        Balanced(overfill,relieved,"displaced water volume");
        Check(relieved[77*w+80].MaterialIndex==water && Math.Abs(relieved[77*w+80].Mass-.75)<.00001,
            "displaced liquid did not reach free volume");
        Check(relieved.Where(c=>c.IsActive!=0).All(c=>c.Mass<=1.00001),"overfilled liquid persisted on clear route");
        var obstructed=new GridCell[n];uint smoke=registry.GetRequiredRuntimeIndex("core:smoke");
        obstructed[78*w+80]=Cell(water,20);obstructed[79*w+80]=Cell(smoke,20,.25f);
        obstructed[80*w+80]=Cell(water,20,1.75f);
        var motion=new float[n*4];int gasMotion=(79*w+80)*4;
        motion[gasMotion]=3;motion[gasMotion+1]=-2;motion[gasMotion+2]=.25f;motion[gasMotion+3]=.5f;
        Upload(obstructed);r.Context.UpdateSubresource(motion,r.GasMotion.Buffer);
        coordinator.DispatchThermalDiffusion(r,false,0,true);var pushed=Read();Balanced(obstructed,pushed,"bubble displacement volume");
        var pushedMotion=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer)).ToArray();
        Check(pushed[77*w+80].MaterialIndex==water && pushed[78*w+80].MaterialIndex==smoke &&
            Math.Abs(pushed[79*w+80].Mass-.75)<.00001 && pushed[80*w+80].Mass==1,"bubble prevented water expansion");
        Check(pushedMotion.AsSpan((78*w+80)*4,4).SequenceEqual(motion.AsSpan(gasMotion,4)),"displaced bubble lost motion");
        var coldBath=new GridCell[n];int bubble=80*w+80;
        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)coldBath[bubble+dy*w+dx]=Cell(water,20);
        coldBath[bubble]=Cell(steam,100,.25f);
        Upload(coldBath);for(uint tick=0;tick<20;tick++)coordinator.DispatchThermalDiffusion(r,false,tick,true);
        var condensed=Read();Balanced(coldBath,condensed,"subcooled bubble energy");
        Check(condensed[bubble].Lifetime>1000,"cold water did not absorb condensation heat");
        Console.WriteLine($"PHYXEL_QR_CONDENSATION progress={condensed[bubble].Lifetime} waterC={condensed.Where(c=>c.IsActive!=0 && c.MaterialIndex==water).Average(c=>(double)c.Temperature)} Qerror={Q(condensed)-Q(coldBath)}");
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
        for(uint i=0;i<1200;i++)
        {
            coordinator.DispatchThermalDiffusion(r,false,i,true);
            if(i is 99 or 199)
            {
                var early=Read();double mean=early.Where(c=>c.IsActive!=0 && c.MaterialIndex==iron).Average(c=>(double)c.Temperature);
                Balanced(plate,early,"early wet wall "+i);
                Console.WriteLine($"PHYXEL_QR_EARLY seconds={(i+1)*.05} wall={mean} Qerror={Q(early)-initial}");
                Check(mean<(i==99?400:200),"early cooling too slow "+i);
            }
        }
        var cooled=Read();Balanced(plate,cooled,"filled plate60s");
        double wall=cooled.Where(c=>c.IsActive!=0 && c.MaterialIndex==iron).Average(c=>(double)c.Temperature);
        double surface=Enumerable.Range(80,64).Average(x=>(double)cooled[88*w+x].Temperature);
        Check(wall<200,"thin full plate did not cool below200C");
        Check(cooled[87*w+100].IsActive!=0,"deep pool lifted");
        Console.WriteLine($"PHYXEL_QH_POOL seconds=60 wall={wall} surface={surface} Qerror={Q(cooled)-initial}");
        // A flying paid film must receive a reciprocal heat budget even above
        // the old one-cell gap; a closed overlay or solid breaks that path.
        foreach(int barrier in new[]{-1,85,87,90})
        {
            bool blocked=barrier>=0;
            var filmGap=new GridCell[n];int drop=85*w+80,hot=90*w+80;
            filmGap[drop]=Cell(water,100,1,10);filmGap[drop].BodyId=0x40000000u;
            filmGap[hot]=Cell(iron,1000,7.2f);
            if(blocked){r.FilterMap[barrier*w+80]=FilterRules.Closed;r.FilterCount=1;r.UploadFilters();}
            Upload(filmGap);coordinator.DispatchThermalDiffusion(r,false,0,false);var heatedFilm=Read();
            Balanced(filmGap,heatedFilm,"flying film heat "+barrier);
            Console.WriteLine($"PHYXEL_SE_FILM barrier={barrier} paid={heatedFilm[drop].Lifetime} wall={heatedFilm[hot].Temperature} Qerror={Q(heatedFilm)-Q(filmGap)}");
            Check(blocked?Math.Abs(heatedFilm[drop].Lifetime-10)<.0001:heatedFilm[drop].Lifetime>10,
                "flying film heat source/path");
            Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
        }
        // A small film-supported column must make a finite flight and return.
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
                var coldWall=(GridCell[])a.Clone();
                for(int x=80;x<85;x++)coldWall[90*w+x].Temperature=200;
                Upload(coldWall);int minimumBottom=100;
                for(uint tick=1;tick<=24;tick++)
                {
                    coordinator.DispatchThermalDiffusion(r,false,tick,true);var moving=Read();
                    int lowest=Enumerable.Range(0,r.Height).Where(y=>moving[y*w+82].IsActive!=0 && moving[y*w+82].MaterialIndex==water).Max();
                    minimumBottom=Math.Min(minimumBottom,lowest);
                }
                var released=Read();Balanced(coldWall,released,"flight and landing");
                Check(minimumBottom<=84,"drop did not rise beyond old gap");
                Check(released[89*w+82].MaterialIndex==water &&
                    (released[89*w+82].BodyId & 0x40000000u)==0,"drop did not land on cold wall");
                Console.WriteLine($"PHYXEL_QR_FLIGHT minimumBottom={minimumBottom} landing=89 Qerror={Q(released)-Q(coldWall)}");
            }
        }
        var blockedDrop=new GridCell[n];
        var mergedFlight=new GridCell[n];
        for(int y=70;y<86;y++){mergedFlight[y*w+82]=Cell(water,100);mergedFlight[y*w+82].BodyId=0x40000000u;mergedFlight[y*w+82].VelocityY=-10;}
        Upload(mergedFlight);coordinator.DispatchThermalDiffusion(r,false,0,true);var mergedMoved=Read();Balanced(mergedFlight,mergedMoved,"merged flying columns");
        for(uint tick=1;tick<=4;tick++)coordinator.DispatchThermalDiffusion(r,false,tick,true);
        Check(Read()[86*w+82].MaterialIndex==water,"merged flight froze above twelve cells");
        var landedPool=new GridCell[n];landedPool[80*w+82]=Cell(water,100);landedPool[80*w+82].BodyId=0x40000000u;
        landedPool[81*w+82]=Cell(water,100);
        Upload(landedPool);coordinator.DispatchThermalDiffusion(r,false,0,true);
        Check((Read()[80*w+82].BodyId & 0x40000000u)==0,"flight tag persisted after joining settled water");
        var shallowPool=new GridCell[n];
        for(int x=80;x<112;x++){shallowPool[90*w+x]=Cell(iron,1000,7.2f);for(int y=87;y<90;y++)shallowPool[y*w+x]=Cell(water,100,1,10);}
        Upload(shallowPool);coordinator.DispatchThermalDiffusion(r,false,0,true);var stayed=Read();
        Balanced(shallowPool,stayed,"wide shallow pool");
        Check(stayed[89*w+96].MaterialIndex==water && (stayed[89*w+96].BodyId & 0x40000000u)==0,
            "wide shallow pool launched as a drop");
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
        Upload(filmSnapshot!);coordinator.DispatchThermalDiffusion(r,false,1,true);var flightNext=Read();
        serializer.ApplyWorldSnapshot(r,loaded);coordinator.DispatchThermalDiffusion(r,false,1,true);
        Check(Read().AsSpan().SequenceEqual(flightNext),"flight reload continuation");
        serializer.ApplyWorldSnapshot(r,loaded);coordinator.RestoreWorldActivity(r,true,true,false);
        coordinator.DispatchFrame(settings,[],.05f);
        Check(Read().AsSpan().SequenceEqual(filmSnapshot),"paused film moved");
        SteamPlumeRegressionVerifier.Run(coordinator,registry,r);
        WaterContactRegressionVerifier.Run(coordinator,registry);
        BulkHeatRegressionVerifier.Run(coordinator,registry);
        Console.WriteLine($"PHYXEL_QH_SUCCESS checks={checks}");
    }
}
