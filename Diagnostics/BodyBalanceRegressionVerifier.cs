using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class BodyBalanceRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/body-balance";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_BODY_BALANCE_BASELINE")=="1";
        var settings=new SimulationSettings{Paused=true,AirSimulation=false,OpenBoundaries=false,SolidGravity=false};
        uint ice=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice),frozen=registry.GetRequiredRuntimeIndex(CoreMaterialIds.FrozenOil),
            metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),stone=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Stone),
            water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),
            fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=ice}],0);
        int w=r.Width,h=r.Height,n=w*h,checks=0,failures=0;
        var physical=registry.CreateGpuTable();var table=physical.ToArray();foreach(int i in Enumerable.Range(0,table.Length))table[i].ThermalConductivity=0;
        var serializer=new SimulationStateSerializer();var results=new List<object>();
        GridCell Cell(uint id)=>new(){IsActive=1,MaterialIndex=id,Mass=physical[id].SimulationKind==(uint)MaterialSimulationKind.Solid?physical[id].Density:1,
            Temperature=id==ice?-5:id==frozen?10:20};
        void Check(bool ok,string label){checks++;if(!ok){failures++;Console.WriteLine("PHYXEL_BB_FAIL "+label);}}
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Start(GridCell[] g){serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false);r.Materials.Upload(r.Context,table);settings.Paused=false;}
        void Advance(int fps,float seconds){for(int f=0;f<(int)Math.Round(fps*seconds);f++){
            coordinator.DispatchFrame(settings,[],1f/fps);coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);}}
        (double x,double y,double slope) Shape(GridCell[] g,uint id){double mass=0,x=0,y=0,xx=0,xy=0;
            for(int i=0;i<n;i++)if(g[i].IsActive!=0&&g[i].MaterialIndex==id){double m=g[i].Mass,px=i%w,py=i/w;mass+=m;x+=px*m;y+=py*m;xx+=px*px*m;xy+=px*py*m;}
            x/=mass;y/=mass;return(x,y,(xy/mass-x*y)/Math.Max(.001,xx/mass-x*x));}
        double Inertia(GridCell[] g,uint id){var shape=Shape(g,id);double sum=0,mass=0;
            for(int i=0;i<n;i++)if(g[i].IsActive!=0&&g[i].MaterialIndex==id){double dx=i%w-shape.x,dy=i/w-shape.y;sum+=(dx*dx+dy*dy)*g[i].Mass;mass+=g[i].Mass;}return sum/mass;}
        void Audit(GridCell[] a,GridCell[] b,string label){foreach(uint id in new[]{ice,frozen,metal,stone,water,oil,fixture}){
            var aa=a.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).ToArray();var bb=b.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).ToArray();
            Check(aa.Length==bb.Length,label+" count "+id);Check(Math.Abs(aa.Sum(c=>(double)c.Mass)-bb.Sum(c=>(double)c.Mass))<1e-5,label+" mass "+id);
            Check(Math.Abs(aa.Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical))-bb.Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical)))<.001,label+" heat "+id);}}
        GridCell[] Scene(uint body,uint liquid,bool support,bool balanced=false){var g=new GridCell[n];
            for(int x=80;x<380;x++)g[240*w+x]=Cell(fixture);
            for(int y=80;y<=240;y++){g[y*w+80]=Cell(fixture);g[y*w+379]=Cell(fixture);}
            if(liquid!=0)for(int y=140;y<240;y++)for(int x=81;x<379;x++)g[y*w+x]=Cell(liquid);
            for(int y=120;y<128;y++)for(int x=150;x<206;x++)g[y*w+x]=Cell(body);
            if(support)for(int y=128;y<240;y++)for(int x=balanced?166:150;x<(balanced?190:158);x++)g[y*w+x]=Cell(fixture);
            return g;}
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(uint body in new[]{ice,frozen,metal,stone}){
            settings.Mode=mode;settings.SolidGravity=false;var g=Scene(body,0,false);Start(g);Advance(60,2);var moved=Read();Audit(g,moved,"gravity switch");
            bool free=body==ice||body==frozen;Check(free?Shape(moved,body).y>Shape(g,body).y+15:Math.Abs(Shape(moved,body).y-Shape(g,body).y)<.01,"gravity switch "+body+"/"+mode);
        }
        foreach(uint body in new[]{ice,frozen})foreach(uint liquid in new[]{water,oil}){
            settings.SolidGravity=false;var g=Scene(body,liquid,false);
            // Place the piece fully below the initial waterline.
            for(int y=120;y<128;y++)for(int x=150;x<206;x++)g[y*w+x]=default;
            for(int y=160;y<168;y++)for(int x=150;x<206;x++)g[y*w+x]=Cell(body);
            Start(g);Advance(60,3);var moved=Read();Audit(g,moved,"buoyancy independent");
            Check(liquid==water?Shape(moved,body).y<150:Shape(moved,body).y>180,"buoyancy with building gravity off "+body+"/"+liquid);
        }
        foreach(uint body in new[]{ice,frozen,metal,stone})foreach(int fps in new[]{30,60,100}){
            settings.SolidGravity=body==metal||body==stone;var g=Scene(body,0,true);Start(g);Advance(fps,1);var moved=Read();Audit(g,moved,"overhang");
            var shape=Shape(moved,body);Check(shape.slope>.15,"right overhang tilts "+body+"/"+fps);
            Check(Math.Abs(Inertia(moved,body)/Inertia(g,body)-1)<.03,"rigid shape preserved "+body+"/"+fps);
            Check(moved.Count(c=>c.IsActive!=0&&c.MaterialIndex==fixture)==g.Count(c=>c.IsActive!=0&&c.MaterialIndex==fixture),"support preserved");
            results.Add(new{body,fps,x=shape.x,y=shape.y,slope=shape.slope});
            File.WriteAllBytes(Path.Combine(dir,$"overhang-{body}-{fps}.grid"),MemoryMarshal.AsBytes(moved.AsSpan()).ToArray());
            if(fps==60)File.WriteAllBytes(Path.Combine(dir,$"overhang-{body}-before.grid"),MemoryMarshal.AsBytes(g.AsSpan()).ToArray());
        }
        foreach(uint body in new[]{ice,metal}){
            settings.SolidGravity=body==metal;var g=Scene(body,0,true,true);Start(g);Advance(60,2);var settled=Read();Audit(g,settled,"wide support");
            Check(Math.Abs(Shape(settled,body).slope)<.01,"wide support stable "+body);
        }
        var weighted=Scene(ice,0,true,true);
        for(int y=120;y<128;y++)for(int x=150;x<158;x++)weighted[y*w+x].Mass*=50;
        settings.SolidGravity=false;Start(weighted);Advance(60,1);var weightedAfter=Read();Audit(weighted,weightedAfter,"actual mass center");
        Check(Shape(weightedAfter,ice).slope<-.15,"heavy left edge uses actual packet mass");
        var asym=Scene(ice,water,false);for(int y=120;y<128;y++)for(int x=150;x<206;x++)asym[y*w+x]=default;
        // A clearly tilted plank straddling the waterline; the earlier L
        // fixture was already within one angular raster step of equilibrium.
        for(int x=150;x<206;x++)for(int y=155-(x-150)/2;y<163-(x-150)/2;y++)asym[y*w+x]=Cell(ice);
        settings.SolidGravity=false;Start(asym);Advance(60,4);var floating=Read();Audit(asym,floating,"floating torque");
        Check(Math.Abs(Shape(floating,ice).slope-Shape(asym,ice).slope)>.1,"asymmetric floating piece rotates");
        Check(Math.Abs(Inertia(floating,ice)/Inertia(asym,ice)-1)<.03,"floating rigid shape preserved");
        File.WriteAllBytes(Path.Combine(dir,"floating-before.grid"),MemoryMarshal.AsBytes(asym.AsSpan()).ToArray());
        File.WriteAllBytes(Path.Combine(dir,"floating-after.grid"),MemoryMarshal.AsBytes(floating.AsSpan()).ToArray());
        settings.Paused=true;var paused=Read();coordinator.DispatchFrame(settings,[],1);Check(MemoryMarshal.AsBytes(paused.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"pause byte exact");
        // Repeated unrelated edits rebuild labels; canonical shape must survive.
        var edited=Scene(ice,0,true);Start(edited);
        for(int f=0;f<120;f++){
            coordinator.DispatchFrame(settings,[new(){X=300,Y=200,EndX=300,EndY=200,Radius=1,Density=1,
                Mode=BrushCommandMode.Material,MaterialIndex=fixture}],1f/60);
            coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
        }
        var editedAfter=Read();Check(Shape(editedAfter,ice).slope>.15,"unrelated topology preserves rotation");
        Check(Math.Abs(Inertia(editedAfter,ice)/Inertia(edited,ice)-1)<.03,"canonical shape survives relabeling");
        // A ceiling/wall may block a turn but cannot consume body cells.
        var blocked=Scene(ice,0,true);for(int y=119;y<220;y++)blocked[y*w+209]=Cell(fixture);
        Start(blocked);Advance(60,3);Audit(blocked,Read(),"rotation obstacle");
        var longBody=Scene(ice,0,true);Start(longBody);Advance(60,15);var longAfter=Read();Audit(longBody,longAfter,"long rotation");
        Check(Math.Abs(Inertia(longAfter,ice)/Inertia(longBody,ice)-1)<.03,"long rotation retains rigid shape");
        File.WriteAllBytes(Path.Combine(dir,"long-rotation.grid"),MemoryMarshal.AsBytes(longAfter.AsSpan()).ToArray());
        var snapshot=new SimulationWorldSnapshot(w,h,MemoryMarshal.AsBytes(longAfter.AsSpan()).ToArray());
        string savedPath=Path.Combine(dir,"rotated.json");System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(savedPath,settings,(ushort)ice,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(savedPath,registry)).GetAwaiter().GetResult()!;
        Check(loaded.World!.Grid.AsSpan().SequenceEqual(snapshot.Grid),"rotated save byte exact");
        Start(longAfter);Advance(60,2);var continued=Read();Start(MemoryMarshal.Cast<byte,GridCell>(loaded.World.Grid).ToArray());Advance(60,2);var resumed=Read();
        Check(Math.Abs(Shape(continued,ice).x-Shape(resumed,ice).x)<1&&Math.Abs(Shape(continued,ice).y-Shape(resumed,ice).y)<1,"rotated reload restarts same pose");
        uint smoke=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);var fog=Scene(ice,0,true);var gasMotion=new GasMotionState[n];
        for(int y=90;y<220;y++)for(int x=100;x<220;x++){
            int i=y*w+x;if(fog[i].IsActive!=0)continue;
            fog[i]=Cell(smoke);fog[i].Temperature=100+x*.1f+y*.001f;
            gasMotion[i]=new(){VelocityX=fog[i].Temperature,VelocityY=-.5f,OffsetX=.125f,OffsetY=-.125f};
        }
        serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(fog.AsSpan()).ToArray(),GasMotion:MemoryMarshal.AsBytes(gasMotion.AsSpan()).ToArray()));
        coordinator.RestoreWorldActivity(r,true,true,false);r.Materials.Upload(r.Context,table);
        for(int i=0;i<15;i++)coordinator.DispatchBodyOnlyAcceptanceStep(r,false,i==0);
        var fogAfter=Read();var motionAfter=MemoryMarshal.Cast<byte,GasMotionState>(AirInventoryRegressionVerifier.Read(r,r.GasMotion.Buffer)).ToArray();
        Check(fogAfter.Count(c=>c.IsActive!=0&&c.MaterialIndex==smoke)==fog.Count(c=>c.IsActive!=0&&c.MaterialIndex==smoke),"displaced gas count");
        Check(Enumerable.Range(0,n).Where(i=>fogAfter[i].IsActive!=0&&fogAfter[i].MaterialIndex==smoke).All(i=>
            motionAfter[i].VelocityX==fogAfter[i].Temperature&&motionAfter[i].VelocityY==-.5f&&motionAfter[i].OffsetX==.125f&&motionAfter[i].OffsetY==-.125f),"gas packet carries its motion through body rotation");
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{passed=failures==0,checks,failures,width=w,height=h,results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_BODY_BALANCE_RESULT passed={failures==0} checks={checks} failures={failures}");
        if(failures>0&&!baseline)throw new InvalidOperationException("Body balance acceptance failed.");
    }
}
