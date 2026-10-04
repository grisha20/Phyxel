using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class SubmergedHeapRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/submerged-heaps";
        Directory.CreateDirectory(dir);
        var settings=new SimulationSettings {Paused=true,AirSimulation=false,OpenBoundaries=false};
        uint sand=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand),coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        uint stone=registry.GetRequiredRuntimeIndex("core:stone_coal"),water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        var table=registry.CreateGpuTable();
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=(ushort)sand}],0);
        int w=r.Width,n=w*r.Height,checks=0; bool passed=true;
        var serializer=new SimulationStateSerializer();
        GridCell Cell(uint id)=>new(){IsActive=1,MaterialIndex=id,Mass=1,Temperature=20};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Mass(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass);
        double Energy(GridCell[] grid)=>grid.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        void Check(bool ok,string label){checks++;if(!ok){passed=false;Console.WriteLine("PHYXEL_SUBMERGED_CHECK_FAILED "+label);}}
        void Balance(GridCell[] before,GridCell[] after,string label){
            Check(Math.Abs(Mass(after)-Mass(before))<.0001*Math.Max(1,Mass(before)),label+" mass");
            Check(Math.Abs(Energy(after)-Energy(before))<.0001*Math.Max(1,Math.Abs(Energy(before))),label+" energy");
        }
        GridCell[] Basin(){
            var grid=new GridCell[n];
            for(int y=75;y<=210;y++)foreach(int x in new[]{100,220})grid[y*w+x]=Cell(fixture);
            for(int x=100;x<=220;x++)grid[210*w+x]=Cell(fixture);
            for(int y=130;y<210;y++)for(int x=101;x<220;x++)grid[y*w+x]=Cell(water);
            return grid;
        }
        void Start(GridCell[] grid,SimulationMode mode,bool contacts=true){
            settings.Mode=mode;settings.Paused=false;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,contacts,false,true);
        }
        void Step(int frames,int fps){for(int frame=0;frame<frames;frame++)coordinator.DispatchFrame(settings,[],1f/fps);}
        void Capture(GridCell[] grid,string name)=>File.WriteAllBytes(Path.Combine(dir,name+".grid"),MemoryMarshal.AsBytes(grid.AsSpan()).ToArray());
        (int width,int height,int jump) Shape(GridCell[] grid,uint id){
            int[] top=new int[119];Array.Fill(top,210);
            for(int y=75;y<210;y++)for(int x=101;x<220;x++)if(grid[y*w+x].IsActive!=0&&grid[y*w+x].MaterialIndex==id)top[x-101]=Math.Min(top[x-101],y);
            int jump=0;for(int x=1;x<top.Length;x++)jump=Math.Max(jump,Math.Abs(top[x]-top[x-1]));
            return(top.Count(y=>y<210),210-top.Min(),jump);
        }
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100})
        foreach(uint grain in new[]{sand,stone}){
            var grid=Basin();
            for(int y=134;y<210;y++)for(int x=158;x<=162;x++){grid[y*w+x]=Cell(grain);grid[y*w+x].RestFrames=30;}
            Start(grid,mode);Step(fps*10,fps);var after=Read();var shape=Shape(after,grain);
            Check(shape.jump<=3,"column cliff "+grain+" "+mode+" "+fps+" jump="+shape.jump);
            Check(shape.width>5,"column did not spread "+grain+" "+mode+" "+fps);
            Balance(grid,after,"column "+grain+" "+mode+" "+fps);Capture(after,$"column-{grain}-{mode}-{fps}");
            Console.WriteLine($"PHYXEL_SUBMERGED_COLUMN material={grain} mode={mode} fps={fps} width={shape.width} height={shape.height} jump={shape.jump}");
        }
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation}){
            var grid=Basin();Start(grid,mode);
            for(int frame=0;frame<600;frame++){
                BrushDrawCommand[] brush=frame<90?[new(){X=160,Y=105,EndX=160,EndY=105,Radius=3,Density=.82f,
                    Mode=BrushCommandMode.Material,MaterialIndex=(ushort)sand}]:[];
                coordinator.DispatchFrame(settings,brush,1f/60);
            }
            var after=Read();var shape=Shape(after,sand);Check(shape.jump<=3,"actual sand brush cliff "+mode+" jump="+shape.jump);
            Capture(after,"brush-"+mode);Console.WriteLine($"PHYXEL_SUBMERGED_BRUSH mode={mode} width={shape.width} height={shape.height} jump={shape.jump}");
        }
        // Charcoal initially rests on the water; stone coal is deposited onto
        // that heap, with a connected water path below and on both sides.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            var grid=Basin();
            for(int y=111;y<135;y++)for(int x=160-(y-110);x<=160+(y-110);x++)grid[y*w+x]=Cell(coal);
            for(int y=98;y<111;y++)for(int x=160-(y-97);x<=160+(y-97);x++)grid[y*w+x]=Cell(stone);
            int totalStone=grid.Count(c=>c.IsActive!=0&&c.MaterialIndex==stone);
            Start(grid,mode);Capture(grid,$"mixed-initial-{mode}-{fps}");Step(fps*20,fps);var after=Read();
            int sunkStone=Enumerable.Range(0,n).Count(i=>i/w>=150&&after[i].IsActive!=0&&after[i].MaterialIndex==stone);
            int[] surfaces=Enumerable.Range(0,119).Select(dx=>Enumerable.Range(75,135)
                .Where(y=>after[y*w+101+dx].IsActive!=0&&after[y*w+101+dx].MaterialIndex==water)
                .DefaultIfEmpty(210).Min()).ToArray();
            int left=surfaces.Take(20).Min(),right=surfaces.TakeLast(20).Min();
            int inner=surfaces.Skip(30).Take(60).Min();
            Check(sunkStone>=totalStone/2,"stone coating suspended "+mode+" "+fps+" sunk="+sunkStone);
            Check(Math.Max(left,right)-Math.Min(left,right)<=3 && Math.Min(left,right)-inner<=3,
                "mixed water level "+mode+" "+fps+" levels="+left+"/"+inner+"/"+right);
            Check(after.Where(c=>c.MoistureMass>0).All(c=>c.Temperature<=100.01),"mixed wet temperature");
            Balance(grid,after,"mixed "+mode+" "+fps);Capture(after,$"mixed-{mode}-{fps}");
            Console.WriteLine($"PHYXEL_SUBMERGED_MIXED mode={mode} fps={fps} stoneSunk={sunkStone}/{totalStone} waterLevels={left}/{inner}/{right}");
        }
        // Mechanical loading must work before any absorption. A narrow feed
        // prevents the coating escaping sideways before reaching the water.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100})
        foreach(bool loaded in new[]{false,true}){
            var load=Basin();int mouth=129*w+160;load[mouth]=Cell(coal);
            for(int y=123;y<=129;y++)foreach(int x in new[]{159,161})load[y*w+x]=Cell(fixture);
            if(loaded)for(int y=125;y<129;y++)load[y*w+160]=Cell(stone);
            Start(load,mode,false);Step((int)Math.Ceiling(fps/30f),fps);var pressed=Read();
            int grain=Array.FindIndex(pressed,c=>c.IsActive!=0&&c.MaterialIndex==coal);
            Check(loaded?grain>mouth:grain==mouth,"dry floating load displacement loaded="+loaded+" position="+grain/w);
            Check(pressed.All(c=>c.MoistureMass==0),"load test accidentally absorbed water");
            Balance(load,pressed,"dry floating load "+loaded);
            Capture(pressed,$"dry-load-{loaded}-{mode}-{fps}");
            Console.WriteLine($"PHYXEL_SUBMERGED_LOAD loaded={loaded} mode={mode} fps={fps} charcoalY={grain/w} initialY=129 moisture={pressed[grain].MoistureMass}");
        }
        // A dense dry heap must not density-sort just because the grains differ.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation}){
            Start(Basin(),mode);
            for(int frame=0;frame<1440;frame++){
                uint material=frame<60?coal:stone;
                bool pouring=frame<60 || (frame>=180 && frame<240);
                BrushDrawCommand[] brush=pouring?[new(){X=160,Y=100,EndX=160,EndY=100,Radius=2,Density=.82f,
                    Mode=BrushCommandMode.Material,MaterialIndex=(ushort)material}]:[];
                coordinator.DispatchFrame(settings,brush,1f/60);
                if(frame==179)Capture(Read(),"sequential-charcoal-"+mode);
            }
            var after=Read();int count=after.Count(c=>c.IsActive!=0&&c.MaterialIndex==stone);
            int sunk=Enumerable.Range(0,n).Count(i=>i/w>=150&&after[i].IsActive!=0&&after[i].MaterialIndex==stone);
            int[] surface=Enumerable.Range(101,119).Select(x=>Enumerable.Range(75,135)
                .Where(y=>after[y*w+x].IsActive!=0&&after[y*w+x].MaterialIndex==water).DefaultIfEmpty(210).Min()).ToArray();
            int left=surface.Take(20).Min(),right=surface.TakeLast(20).Min(),inner=surface.Skip(30).Take(60).Min();
            Check(count>0 && sunk>=count/2,"sequential stone did not sink "+mode);
            Check(Math.Abs(left-right)<=3 && Math.Min(left,right)-inner<=3,"sequential water mound "+mode);
            Capture(after,"sequential-final-"+mode);
            Console.WriteLine($"PHYXEL_SUBMERGED_SEQUENTIAL mode={mode} stoneSunk={sunk}/{count} waterLevels={left}/{inner}/{right}");
        }
        var dry=new GridCell[n];for(int y=179;y<210;y++)for(int x=140;x<=180;x++)dry[y*w+x]=Cell(coal);
        for(int x=139;x<=181;x++)dry[210*w+x]=Cell(fixture);
        for(int y=170;y<210;y++)foreach(int x in new[]{139,181})dry[y*w+x]=Cell(fixture);
        for(int y=175;y<179;y++)for(int x=150;x<=170;x++)dry[y*w+x]=Cell(stone);
        Start(dry,SimulationMode.Sandbox);Step(300,60);var dryAfter=Read();
        Check(!Enumerable.Range(0,n).Any(i=>i/w>=180&&dryAfter[i].IsActive!=0&&dryAfter[i].MaterialIndex==stone),"dry powders density-sorted");
        Balance(dry,dryAfter,"dry mixed");
        File.WriteAllText(Path.Combine(dir,"layout.txt"),$"{w} {r.Height} 52 {sand} {coal} {table[coal].MoistureWetMaterialIndex} {stone} {water} {fixture}");
        Console.WriteLine($"PHYXEL_SUBMERGED_RESULT passed={passed} checks={checks}");if(!passed)Environment.ExitCode=1;
    }
}
