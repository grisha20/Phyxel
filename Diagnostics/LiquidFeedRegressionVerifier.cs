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

internal static class LiquidFeedRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/liquid-feed";
        Directory.CreateDirectory(dir);
        var settings=new SimulationSettings {Paused=true,AirSimulation=false,OpenBoundaries=false};
        uint coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),wet=registry.GetRequiredRuntimeIndex(CoreMaterialIds.WetCharcoal);
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
        uint frozenOil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.FrozenOil),oilVapour=registry.GetRequiredRuntimeIndex(CoreMaterialIds.OilVapour);
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture),fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
        var table=registry.CreateGpuTable();var serializer=new SimulationStateSerializer();
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=(ushort)coal}],0);
        int w=r.Width,n=w*r.Height,checks=0;bool passed=true;
        GridCell Cell(uint id,float t=20)=>new(){IsActive=1,MaterialIndex=id,Mass=1,Temperature=t};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        bool IsCoal(GridCell c)=>c.IsActive!=0&&(c.MaterialIndex==coal||c.MaterialIndex==wet);
        double CoalMass(GridCell[] g)=>g.Where(IsCoal).Sum(c=>(double)c.Mass);
        double TotalMass(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
        double Energy(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
        double Fuel(GridCell[] g,uint id)=>g.Where(c=>c.IsActive!=0).Sum(c=>
            (c.MaterialIndex==id||(id==oil&&(c.MaterialIndex==frozenOil||c.MaterialIndex==oilVapour))?
                (double)c.Mass:0)+(id==oil?c.FuelMass:0));
        int InTank(GridCell[] g)=>Enumerable.Range(0,n).Count(i=>i/w<124&&IsCoal(g[i]));
        void Check(bool ok,string label){checks++;if(!ok){passed=false;Console.WriteLine("PHYXEL_LIQUID_FEED_CHECK_FAILED "+label);}}
        void Start(GridCell[] grid,SimulationMode mode,bool contacts){
            settings.Mode=mode;settings.Paused=false;
            serializer.ApplyWorldSnapshot(r,new(w,r.Height,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,contacts,false,true);
        }
        void Step(int frames,int fps){for(int f=0;f<frames;f++)coordinator.DispatchFrame(settings,[],1f/fps);}
        void Capture(GridCell[] g,string name)=>File.WriteAllBytes(Path.Combine(dir,name+".grid"),MemoryMarshal.AsBytes(g.AsSpan()).ToArray());
        GridCell[] Scene(uint liquid,int hole,bool hot=false){
            var g=new GridCell[n];
            for(int y=70;y<124;y++)foreach(int x in new[]{105,175})g[y*w+x]=Cell(fixture);
            for(int y=120;y<124;y++)for(int x=105;x<=175;x++)if(Math.Abs(x-140)>hole/2)g[y*w+x]=Cell(fixture);
            for(int y=90;y<120;y++)for(int x=106;x<175;x++)g[y*w+x]=Cell(liquid);
            for(int y=130;y<170;y++)for(int x=140-(y-130);x<=140+(y-130);x++)g[y*w+x]=Cell(coal,hot?650:20);
            for(int x=95;x<=185;x++)g[170*w+x]=Cell(fixture);
            for(int y=124;y<=170;y++)foreach(int x in new[]{95,185})g[y*w+x]=Cell(fixture);
            return g;
        }
        foreach(uint liquid in new[]{water,oil})foreach(int hole in new[]{1,3})
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            var before=Scene(liquid,hole);Start(before,mode,false);Step(fps*10,fps);var after=Read();
            string label=$"{liquid}-{hole}-{mode}-{fps}";int tank=InTank(after);
            Check(tank==0,"coal lifted into tank "+label+" count="+tank);
            Check(Enumerable.Range(0,n).Any(i=>i/w>=124&&after[i].IsActive!=0&&after[i].MaterialIndex==liquid),"feed did not reach chamber "+label);
            Check(Math.Abs(CoalMass(after)-CoalMass(before))<.0001,"cold coal mass "+label);
            Check(Math.Abs(TotalMass(after)-TotalMass(before))<.0001*Math.Max(1,TotalMass(before)),"mass "+label);
            Check(Math.Abs(Energy(after)-Energy(before))<.0001*Math.Max(1,Energy(before)),"energy "+label);
            Capture(after,"feed-"+label);Console.WriteLine($"PHYXEL_LIQUID_FEED_COLD liquid={liquid} hole={hole} mode={mode} fps={fps} coalInTank={tank}");
        }
        foreach(uint liquid in new[]{water,oil}){
            var before=new GridCell[n];before[100*w+140]=Cell(liquid);before[101*w+140]=Cell(coal);
            Start(before,SimulationMode.Sandbox,false);Step(1,60);var after=Read();
            int at=Array.FindIndex(after,IsCoal);Check(at>=0&&at/w>=101,"free falling drop lifted coal "+liquid);
        }
        // Repeat the actual water contact/wetting path, with both hydraulic
        // settings. The mechanical-only cases above cannot substitute for it.
        foreach(bool hydraulic in new[]{false,true})foreach(uint liquid in new[]{water,oil})
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            settings.HydraulicPressure=hydraulic;
            var before=Scene(liquid,1);Start(before,mode,true);Step(fps*10,fps);var after=Read();
            string label=$"{liquid}-{hydraulic}-{mode}-{fps}";
            Check(InTank(after)==0,"actual contact lifted coal "+label);
            Check(Enumerable.Range(0,n).Any(i=>i/w>=124&&after[i].IsActive!=0&&after[i].MaterialIndex==liquid),"actual feed blocked "+label);
            Check(Math.Abs(CoalMass(after)-CoalMass(before))<.0001,"actual coal mass "+label);
            Check(Math.Abs(TotalMass(after)-TotalMass(before))<.0001*Math.Max(1,TotalMass(before)),"actual mass "+label);
            Check(Math.Abs(Energy(after)-Energy(before))<.0001*Math.Max(1,Energy(before)),"actual energy "+label);
            Console.WriteLine($"PHYXEL_LIQUID_FEED_CONTACT liquid={liquid} hydraulic={hydraulic} mode={mode} fps={fps} coalInTank={InTank(after)}");
        }
        settings.HydraulicPressure=false;
        settings.AirSimulation=true;settings.OpenBoundaries=true;
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            var before=Scene(oil,1,true);Start(before,mode,true);
            double flameTime=0,peak=0;
            for(int f=0;f<fps*10;f++){
                coordinator.DispatchFrame(settings,[],1f/fps);
                if(f%(fps/10)==0){var sample=Read();int flames=sample.Count(c=>c.IsActive!=0&&c.MaterialIndex==fire&&c.Lifetime>0);flameTime+=flames*.1;peak=Math.Max(peak,flames);}
            }
            var after=Read();double burned=Fuel(before,oil)-Fuel(after,oil);
            Check(InTank(after)==0,"hot coal lifted into tank "+mode);
            Check(burned>=.1,"feed oil did not burn "+mode+" consumed="+burned);
            Check(after.Any(c=>c.IsActive!=0&&c.MaterialIndex==fire&&c.Lifetime>0),"feed lost flame "+mode);
            Capture(after,$"hot-feed-{mode}-{fps}");
            Console.WriteLine($"PHYXEL_LIQUID_FEED_HOT mode={mode} fps={fps} oilConsumed={burned:F6} flameTime={flameTime:F3} peak={peak:F0} coalInTank={InTank(after)}");
            var dry=Scene(oil,1,true);for(int i=0;i<n;i++)if(dry[i].MaterialIndex==oil)dry[i]=default;
            Start(dry,mode,true);double controlFlame=0,controlPeak=0;
            for(int f=0;f<fps*10;f++){
                coordinator.DispatchFrame(settings,[],1f/fps);
                if(f%(fps/10)==0){int flames=Read().Count(c=>c.IsActive!=0&&c.MaterialIndex==fire&&c.Lifetime>0);controlFlame+=flames*.1;controlPeak=Math.Max(controlPeak,flames);}
            }
            Check(flameTime>=controlFlame*1.1,"oil failed to add integrated flame "+mode+" "+fps);
            Check(peak>=controlPeak*1.1,"oil failed to increase peak flame "+mode+" "+fps);
            Console.WriteLine($"PHYXEL_LIQUID_FEED_CONTROL mode={mode} fps={fps} flameTime={controlFlame:F3} peak={controlPeak:F0}");
        }
        var demo=Scene(oil,1,true);settings.Mode=SimulationMode.Sandbox;settings.Paused=false;
        System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(Path.Combine(dir,"oil-feed-demo.json"),settings,(ushort)oil,
            new(w,r.Height,MemoryMarshal.AsBytes(demo.AsSpan()).ToArray()),registry)).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(dir,"layout.txt"),$"{w} {r.Height} 52 {coal} {wet} {water} {oil} {fixture} {fire}");
        Console.WriteLine($"PHYXEL_LIQUID_FEED_RESULT passed={passed} checks={checks}");if(!passed)Environment.ExitCode=1;
    }
}
