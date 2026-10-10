using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class CoalGpuRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string directory=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/coal-gpu";
        Directory.CreateDirectory(directory);
        int checks=0;
        void Check(bool pass,string name){checks++;if(!pass)throw new InvalidOperationException(name);}
        foreach(var size in new[]{(131,129),(480,270),(1920,1080)})
        {
            settings.Width=size.Item1;settings.Height=size.Item2;settings.Paused=true;
            var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,
                MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal)}],0);
            var c=r.Context;int n=r.Width*r.Height;
            foreach(string layout in new[]{"open","maze","isolated","boundary"})
            {
                var random=new Random(417);var faces=new Vector4[n];var initial=new Vector2[n];
                bool Open(int x,int y)=>x>=0&&y>=0&&x<r.Width&&y<r.Height&&
                    (layout!="maze"||x%17!=0||y%19==0);
                for(int y=0;y<r.Height;y++)for(int x=0;x<r.Width;x++)
                {
                    int i=y*r.Width+x;uint mask=Open(x,y)?16u:0;
                    if(layout!="isolated"&&Open(x,y)){
                        if(Open(x+1,y))mask|=1;if(Open(x-1,y))mask|=2;
                        if(Open(x,y+1))mask|=4;if(Open(x,y-1))mask|=8;
                    }
                    if(layout=="boundary"&&(x==0||y==0||x==r.Width-1))mask|=32;
                    faces[i]=new(0,0,mask,0);
                    initial[i]=new((float)(random.NextDouble()*20-10),(float)(random.NextDouble()*2-1));
                }
                c.UpdateSubresource(faces,r.OxidizerCarrierFaces.Buffer);
                var constants=new OxidizerConstants{Width=(uint)r.Width,Height=(uint)r.Height};
                c.UpdateSubresource(ref constants,r.OxidizerConstants);
                foreach(int iterations in new[]{4,64,1024})
                {
                    byte[] Execute(bool fused){
                        c.UpdateSubresource(initial,r.OxidizerCarrierPotential.ReadBuffer);
                        c.ComputeShader.SetConstantBuffer(0,r.OxidizerConstants);
                        c.ComputeShader.SetShaderResource(8,r.OxidizerCarrierFaces.View);
                        c.ComputeShader.Set(fused?r.OxidizerCarrierJacobiFourShader:r.OxidizerCarrierJacobiShader);
                        for(int iteration=0;iteration<iterations;iteration+=fused?4:1){
                            c.ComputeShader.SetShaderResource(7,r.OxidizerCarrierPotential.ReadView);
                            c.ComputeShader.SetUnorderedAccessView(4,r.OxidizerCarrierPotential.WriteUnorderedView);
                            c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
                            c.ComputeShader.SetShaderResource(7,null);c.ComputeShader.SetUnorderedAccessView(4,null);
                            r.OxidizerCarrierPotential.Swap();
                        }
                        c.ComputeShader.SetShaderResource(8,null);
                        return AirInventoryRegressionVerifier.Read(r,r.OxidizerCarrierPotential.ReadBuffer);
                    }
                    var old=Execute(false);var current=Execute(true);
                    Check(old.AsSpan().SequenceEqual(current),$"Oxygen projection {size}/{layout}/{iterations}");
                    if (iterations <= 64)
                    {
                        byte[] ExecuteSor(bool fused)
                        {
                            c.UpdateSubresource(initial,r.OxidizerCarrierPotential.ReadBuffer);
                            c.ComputeShader.SetConstantBuffer(0,r.OxidizerConstants);
                            c.ComputeShader.SetShaderResource(8,r.OxidizerCarrierFaces.View);
                            for (int iteration=0;iteration<iterations;iteration+=fused?2:1)
                            {
                                if (fused)
                                {
                                    c.ComputeShader.SetShaderResource(7,r.OxidizerCarrierPotential.ReadView);
                                    c.ComputeShader.SetUnorderedAccessView(4,r.OxidizerCarrierPotential.WriteUnorderedView);
                                    c.ComputeShader.Set(r.OxidizerCarrierSorTwoShader);
                                    c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
                                    c.ComputeShader.SetShaderResource(7,null);c.ComputeShader.SetUnorderedAccessView(4,null);
                                    r.OxidizerCarrierPotential.Swap();
                                }
                                else
                                {
                                    c.ComputeShader.SetUnorderedAccessView(4,r.OxidizerCarrierPotential.ReadUnorderedView);
                                    c.ComputeShader.Set(r.OxidizerCarrierSorRedShader);c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
                                    c.ComputeShader.Set(r.OxidizerCarrierSorBlackShader);c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
                                    c.ComputeShader.SetUnorderedAccessView(4,null);
                                }
                            }
                            c.ComputeShader.SetShaderResource(8,null);
                            return AirInventoryRegressionVerifier.Read(r,r.OxidizerCarrierPotential.ReadBuffer);
                        }
                        Check(ExecuteSor(false).AsSpan().SequenceEqual(ExecuteSor(true)),
                            $"Oxygen SOR tile/reference {size}/{layout}/{iterations}");
                    }
                }
                yield return r;
            }
            // Exercise the actual faces/projection/flux/transport/consumption path.
            foreach(bool open in new[]{false,true})foreach(bool filter in new[]{false,true})
            foreach(bool carrier in new[]{false,true})
            {
                var grid=new GridCell[n];var oxygen=new float[n];var demand=new float[n];var motion=new GasMotionState[n];var filters=new uint[n+1];
                var air = new AirCell[r.AirWidth * r.AirHeight];
                for (int i=0;i<air.Length;i++) air[i]=new(){VelocityX=2+.01f*(i%r.AirWidth),VelocityY=-3};
                for(int y=0;y<r.Height;y++)for(int x=0;x<r.Width;x++){
                    int i=y*r.Width+x;oxygen[i]=(x%17+1)/18f;demand[i]=(x%7)*.003f;
                    if(x%23==0&&y%19!=0)grid[i]=new(){IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),Mass=7.8f,Temperature=20};
                    else if((x+y)%3==0){grid[i]=new(){IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),Mass=.2f,Temperature=100};motion[i]=new(){VelocityX=7,VelocityY=-13};}
                    if(filter&&x==r.Width/2){filters[0]=1;filters[i+1]=FilterRules.Closed;}
                }
                byte[] Execute(bool reference){
                    Environment.SetEnvironmentVariable("PHYXEL_OXYGEN_REFERENCE",reference?"1":null);
                    Environment.SetEnvironmentVariable("PHYXEL_OXYGEN_GEOMETRY_REFERENCE",reference?"1":null);
                    c.UpdateSubresource(grid,r.Grid.ReadBuffer);c.UpdateSubresource(motion,r.GasMotion.Buffer);
                    c.UpdateSubresource(air,r.Air.Buffer);
                    c.UpdateSubresource(filters,r.Filters.Buffer);
                    foreach(var buffer in r.Oxidizer.Buffers)c.UpdateSubresource(oxygen,buffer);
                    foreach(var buffer in r.OxidizerCarrierPotential.Buffers)c.UpdateSubresource(new Vector2[n],buffer);
                    c.UpdateSubresource(demand,r.OxidizerDemand.Buffer);r.OxidizerCarrierWarm=false;
                    for(int tick=0;tick<2;tick++){
                        SimulationDispatchCoordinator.DispatchOxidizer(r,1f/60,open,false,carrier);
                        SimulationDispatchCoordinator.DispatchOxidizer(r,1f/60,open,true,false);
                    }
                    return new[]{r.Oxidizer.ReadBuffer,r.OxidizerCarrierPotential.ReadBuffer,r.OxidizerFlux.Buffer,r.OxidizerAvailable.Buffer}
                        .SelectMany(buffer=>AirInventoryRegressionVerifier.Read(r,buffer)).ToArray();
                }
                var old=Execute(true);var current=Execute(false);
                Check(old.AsSpan().SequenceEqual(current),$"Oxygen complete {size}/open{open}/filter{filter}/air{carrier}");
            }
            c.UpdateSubresource(new uint[n+1],r.Filters.Buffer);
        }
        Environment.SetEnvironmentVariable("PHYXEL_OXYGEN_REFERENCE",null);
        Environment.SetEnvironmentVariable("PHYXEL_OXYGEN_GEOMETRY_REFERENCE",null);
        settings.Width=480;settings.Height=300;settings.RenderWithoutEffects=false;
        settings.Paused=true;
        var visual=coordinator.DispatchFrame(settings,[new(){X=170,Y=130,Radius=12,Density=1,
            MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke)},new(){X=230,Y=130,Radius=8,Density=1,
            MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire)}],0);
        settings.Paused=false;
        foreach(int fps in new[]{30,60,100}){
            for(int frame=0;frame<10;frame++)visual=coordinator.DispatchFrame(settings,[],1f/fps);
            string first=Path.Combine(directory,$"visual-{fps}.png"),repeat=Path.Combine(directory,$"repeat-{fps}.png");
            SimulationScreenshotWriter.Save(visual,first);
            var grid=AirInventoryRegressionVerifier.Read(visual,visual.Grid.ReadBuffer);
            coordinator.RenderDiagnosticSnapshot(visual,settings);SimulationScreenshotWriter.Save(visual,repeat);
            Check(File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(repeat)),$"Display phase stable {fps}");
            Check(grid.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(visual,visual.Grid.ReadBuffer)),"Render changed grid");
            Environment.SetEnvironmentVariable("PHYXEL_GLOW_REFERENCE","1");
            coordinator.RenderDiagnosticSnapshot(visual,settings);
            string old=Path.Combine(directory,$"old-{fps}.png");SimulationScreenshotWriter.Save(visual,old);
            Check(!File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(old)),"Old display did not reproduce flicker");
            Environment.SetEnvironmentVariable("PHYXEL_GLOW_REFERENCE",null);
        }
        settings.Paused=true;
        visual=coordinator.DispatchFrame(settings,[new(){X=170,Y=130,Radius=60,Density=1,
            MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Eraser)}],0);
        var display=AirInventoryRegressionVerifier.Read(visual,visual.FireGlowPresentation.Buffer);
        Check(display.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(visual,visual.FireGlow.Buffer)),"Paused display rebuild");
        Console.WriteLine($"PHYXEL_COAL_GPU_SUCCESS checks={checks}");
        yield return visual;
    }
}
