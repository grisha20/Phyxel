using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class FragmentJetRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings,string dir)
    {
        const int size=128; settings.Width=size;settings.Height=size;settings.Paused=true;
        settings.OpenBoundaries=false;settings.SolidGravity=false;coordinator.ClearCurrentWorld(settings);
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=metal}],0);
        var table=registry.CreateGpuTable();r.Materials.Upload(r.Context,table);
        var constants=new SimulationFrameConstants{Width=size,Height=size,Gravity=0};
        int failures=0,checks=0;
        void Check(bool pass,string message){checks++;if(!pass)failures++;Console.WriteLine($"PHYXEL_FRAGMENT_JET pass={pass} {message}");}
        GridCell[] Cells()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        GridCell Grain(uint id,float vx=0)=>new(){IsActive=1,MaterialIndex=id,Mass=table[id].Density,
            BodyId=0x40000000u,Temperature=30,Lifetime=11,VelocityX=vx};
        void Upload(GridCell[] grid,float wind=0,bool split=false,float pressure=0,bool wave=false)
        {
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);r.Context.UpdateSubresource(grid,r.Grid.WriteBuffer);
            var air=new AirCell[r.AirWidth*r.AirHeight];var pulse=new System.Numerics.Vector4[air.Length];
            for(int i=0;i<air.Length;i++)
            {air[i].VelocityX=(!split||i%r.AirWidth<16)?wind:0;air[i].Pressure=i%r.AirWidth<16?pressure:0;
                if(wave)pulse[i].X=air[i].Pressure;}
            r.Context.UpdateSubresource(air,r.Air.Buffer);r.Context.UpdateSubresource(pulse,r.ReactionPulse.ReadBuffer);
            r.Context.UpdateSubresource(new GasMotionState[size*size],r.GasMotion.Buffer);
        }
        GridCell[] Single(uint id,float vx=0){var grid=new GridCell[size*size];grid[64*size+64]=Grain(id,vx);return grid;}
        void Tick(bool air)=>SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,true,air);
        var sample=Single(metal,3);Upload(sample);
        for(int tick=0;tick<60;tick++)Tick(false);
        var grain=Cells().Single(c=>c.IsActive!=0);
        Check(grain.VelocityX==3,"FJ01 zero rounded step does not damp free flight vx="+grain.VelocityX);
        Upload(Single(metal));constants.Gravity=980;
        for(int tick=0;tick<20;tick++)Tick(false);
        int drop=Array.FindIndex(Cells(),c=>c.IsActive!=0)/size-64;
        Check(drop>=0&&drop<=12,"FJ01 no automatic one-pixel fall in free air drop="+drop);
        constants.Gravity=0;
        float lightSpeed=0;
        foreach(float mass in new[]{1f,8f})
        {
            sample=Single(metal);sample[64*size+64].Mass=mass;Upload(sample,4);
            for(int tick=0;tick<30;tick++)Tick(true);
            grain=Cells().Single(c=>c.IsActive!=0);
            if(mass==1)lightSpeed=grain.VelocityX;
            else Check(lightSpeed>grain.VelocityX*2&&grain.Mass==mass,"FJ02 mass changes wind response");
        }
        foreach(string id in new[]{"core:metal","core:steel","core:cast_iron","core:copper","core:stone"})
        foreach(int sign in new[]{-1,1})
        {
            uint material=registry.GetRequiredRuntimeIndex(id);var grid=Single(material);Upload(grid,sign*4);
            for(int tick=0;tick<30;tick++)Tick(true);
            var cells=Cells();grain=cells.Single(c=>c.IsActive!=0);
            Check(sign*grain.VelocityX>20,"FJ02 directed wind "+id+" sign="+sign+" vx="+grain.VelocityX);
            Check(grain.Mass==table[material].Density&&grain.MaterialIndex==material&&grain.Temperature==30&&grain.Lifetime==11,
                "FJ02 preserved packet "+id+" sign="+sign);
        }
        sample=Single(metal);uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        for(int y=0;y<size;y++)sample[y*size+63]=new(){IsActive=1,MaterialIndex=fixture,Mass=10,Temperature=30};
        Upload(sample,-4,true);for(int tick=0;tick<30;tick++)Tick(true);
        grain=Cells().Single(c=>c.IsActive!=0&&c.MaterialIndex==metal);
        Check(grain.VelocityX==0,"FJ02 no wind through rigid wall");
        var plate=new GridCell[size*size];for(int y=0;y<size;y++){plate[y*size+64]=Grain(metal);plate[y*size+64].BodyId=1;}
        Upload(plate,pressure:100);var before=PressureShellRegressionVerifier.Read(r);Tick(true);
        var released=PressureShellRegressionVerifier.Read(r);
        var pulse=MemoryMarshal.Cast<byte,System.Numerics.Vector4>(released.ReactionPulse!).ToArray();
        double inventory=pulse.Sum(v=>(double)v.X);
        Check(inventory>0,"FJ02 carrier reservoir releases a finite wave stock="+inventory);
        Check(before.Air!.AsSpan().SequenceEqual(released.Air)&&pulse.All(v=>v.Y==0&&v.Z==0&&v.W==0),
            "FJ02 total pressure stays unchanged; no added velocity/expansion stock");
        Tick(true);Check(released.ReactionPulse!.AsSpan().SequenceEqual(PressureShellRegressionVerifier.Read(r).ReactionPulse),
            "FJ02 old fragments do not issue the stock twice");
        Upload(plate,pressure:100,wave:true);before=PressureShellRegressionVerifier.Read(r);Tick(true);
        Check(before.ReactionPulse!.AsSpan().SequenceEqual(PressureShellRegressionVerifier.Read(r).ReactionPulse),
            "FJ02 existing reaction wave is not converted again");
        Upload(plate,pressure:100);
        var opposing=new System.Numerics.Vector4[r.AirWidth*r.AirHeight];
        for(int i=0;i<opposing.Length;i++)if(i%r.AirWidth>=16)opposing[i].X=200;
        r.Context.UpdateSubresource(opposing,r.ReactionPulse.ReadBuffer);
        var opposedAir=new AirCell[opposing.Length];
        for(int i=0;i<opposing.Length;i++)opposedAir[i].Pressure=i%r.AirWidth<16?100:200;
        r.Context.UpdateSubresource(opposedAir,r.Air.Buffer);before=PressureShellRegressionVerifier.Read(r);Tick(true);
        Check(before.ReactionPulse!.AsSpan().SequenceEqual(PressureShellRegressionVerifier.Read(r).ReactionPulse),
            "FJ02 opposing carrier cannot launch a reverse pressure stock");
        byte[]? reference=null;
        foreach(int fps in new[]{30,60,100})
        {
            Upload(Single(metal),4);double clock=0;int ticks=0;
            for(int frame=0;frame<fps;frame++){clock+=1d/fps;while(clock+1e-6>=SimulationDispatchCoordinator.FixedAirStep)
                {clock-=SimulationDispatchCoordinator.FixedAirStep;ticks++;Tick(true);}}
            var bytes=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);reference??=bytes;
            Check(ticks==60&&reference.AsSpan().SequenceEqual(bytes),"FJ03 fixed wind clock FPS="+fps);
        }
        Upload(plate,pressure:100);Tick(true);var snapshot=PressureShellRegressionVerifier.Read(r);
        var serializer=new SimulationStateSerializer();string path=Path.Combine(dir,"jet-roundtrip.json");
        System.Threading.Tasks.Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)metal,snapshot,registry)).GetAwaiter().GetResult();
        var loaded=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
        Check(loaded.World!.Grid.AsSpan().SequenceEqual(snapshot.Grid)&&loaded.World.ReactionPulse!.AsSpan().SequenceEqual(snapshot.ReactionPulse),
            "FJ03 fragment/released stock save-load bytes");
        Tick(true);var next=PressureShellRegressionVerifier.Read(r);
        serializer.ApplyWorldSnapshot(r,loaded.World);Tick(true);var resumed=PressureShellRegressionVerifier.Read(r);
        Check(next.Grid.AsSpan().SequenceEqual(resumed.Grid)&&next.ReactionPulse!.AsSpan().SequenceEqual(resumed.ReactionPulse),
            "FJ03 identical next fragment tick after load");
        settings.Paused=true;var paused=PressureShellRegressionVerifier.Read(r);coordinator.DispatchFrame(settings,[],.2f);
        var still=PressureShellRegressionVerifier.Read(r);
        Check(paused.Grid.AsSpan().SequenceEqual(still.Grid)&&paused.ReactionPulse!.AsSpan().SequenceEqual(still.ReactionPulse),
            "FJ03 pause retains fragments and released pressure stock");
        if(failures>0)Environment.ExitCode=1;
        Console.WriteLine($"PHYXEL_FRAGMENT_JET_COMPLETE checks={checks} failures={failures}");yield return r;
    }
}
