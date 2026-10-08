using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class PressureReliabilityRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/pressure-reliability";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_RELIABILITY_BASELINE")=="1";
        var settings=new SimulationSettings{Width=256,Height=192,Paused=true,PressureDestruction=true,SolidGravity=false};
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=metal}],0);
        int w=r.Width,n=w*r.Height;
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        bool Fragment(GridCell c)=>c.IsActive!=0&&(c.BodyId&0x40000000u)!=0;
        var rows=new List<object>();
        foreach(var mode in new[]{SimulationMode.Simulation,SimulationMode.Sandbox})
        foreach(int fps in new[]{30,60,100})
        foreach(string variant in new[]{"weak","vent","equal","off","hot","cold"})
        {
            settings.Mode=mode;settings.Paused=true;settings.PressureDestruction=variant!="off";coordinator.ClearCurrentWorld(settings);
            r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=metal}],0);
            var initial=new GridCell[n];
            bool Weak(int x,int y)=>x>=90&&x<102&&y>=40&&y<45;
            bool hot=variant is "hot" or "cold";
            // The two inner notch shoulders are one cell thick on a diagonal.
            // Baseline tracing identified (89,44)/(102,44), not a thick-wall cascade.
            bool Vulnerable(int x,int y)=>Weak(x,y)||(!hot&&y==44&&(x==89||x==102));
            for(int y=40;y<120;y++)for(int x=40;x<152;x++)
            {
                bool wall=x<45||x>=147||y<45||y>=115;
                if(hot && y<45)wall=y>=42||x<45||x>=147;
                if(!hot&&Weak(x,y)&&y<44)wall=false;
                if(variant=="vent"&&Weak(x,y))wall=false;
                if(wall)initial[y*w+x]=new(){IsActive=1,MaterialIndex=metal,Mass=1,Temperature=variant=="hot"&&Weak(x,y)?800:30,RestFrames=2};
            }
            // Identify origins across the isolated mechanical transport only.
            // Clear these diagnostic tags before any ordinary lifetime processing.
            var tagged=(GridCell[])initial.Clone();
            for(int i=0;i<n;i++)if(tagged[i].IsActive!=0)tagged[i].Lifetime=i+1;
            r.Context.UpdateSubresource(tagged,r.Grid.ReadBuffer);
            var air=new AirCell[r.AirWidth*r.AirHeight];
            float pressure=hot?28:24;
            for(int y=0;y<r.AirHeight;y++)for(int x=0;x<r.AirWidth;x++)
            {
                bool interior=x*4+2>=45&&x*4+2<147&&y*4+2>=45&&y*4+2<115;
                air[y*r.AirWidth+x].Pressure=interior||variant=="equal"?pressure:0;
            }
            r.Context.UpdateSubresource(air,r.Air.Buffer);
            coordinator.RestoreWorldActivity(r,true,true,false,true);
            var constants=new SimulationFrameConstants{Width=(uint)w,Height=(uint)r.Height,Gravity=0};
            SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,mode==SimulationMode.Simulation,settings.PressureDestruction);
            var first=Read();int weakFirst=0,strongFirst=0;double outward=0;
            for(int i=0;i<n;i++)if(Fragment(first[i]))
            {
                int origin=(int)first[i].Lifetime-1;
                if(Vulnerable(origin%w,origin/w)){weakFirst++;outward-=first[i].VelocityY;}
                else strongFirst++;
            }
            for(int i=0;i<n;i++)if(first[i].IsActive!=0)first[i].Lifetime=0;
            r.Context.UpdateSubresource(first,r.Grid.ReadBuffer);
            settings.Paused=false;
            int maxFragments=first.Count(Fragment);int maxStrong=strongFirst;
            for(int frame=1;frame<=5*fps;frame++)
            {
                r=coordinator.DispatchFrame(settings,[],1f/fps);
                if(frame%6==0)
                {
                    var cells=Read();maxFragments=Math.Max(maxFragments,cells.Count(Fragment));
                    int changed=0;
                    for(int i=0;i<n;i++)if(initial[i].IsActive!=0&&!Vulnerable(i%w,i/w)&&Fragment(cells[i]))changed++;
                    maxStrong=Math.Max(maxStrong,changed);
                }
                if(frame==fps||frame==5*fps)SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{mode}-{fps}-{variant}-{frame}.png"));
                if(frame%4==0)yield return r;
            }
            var end=Read();int strongInitial=0,strongIntact=0;
            for(int i=0;i<n;i++)if(initial[i].IsActive!=0&&!Vulnerable(i%w,i/w))
            {strongInitial++;if(end[i].IsActive!=0&&end[i].MaterialIndex==metal&&!Fragment(end[i]))strongIntact++;}
            bool positive=variant is "weak" or "hot";
            bool pass=positive?weakFirst>0&&strongFirst==0&&outward>0&&strongIntact>=strongInitial*.95&&maxStrong<=strongInitial*.01:maxFragments==0;
            var row=new{mode=mode.ToString(),fps,variant,pressure,weakFirst,strongFirst,outward,maxFragments,maxStrong,strongInitial,strongIntact,pass};
            rows.Add(row);Console.WriteLine("PHYXEL_RELIABILITY "+JsonSerializer.Serialize(row));
            if(!baseline&&!pass)throw new InvalidOperationException("RS weak-spot/vent failed: "+variant);
        }
        File.WriteAllText(Path.Combine(dir,"results.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PHYXEL_RELIABILITY_COMPLETE");
    }
}
