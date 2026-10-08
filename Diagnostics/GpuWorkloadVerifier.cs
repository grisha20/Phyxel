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
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

// Compare equivalent calculations and time kernels on the actual adapter.
// No FPS estimate for another GPU; readbacks are diagnostic only.
internal static class GpuWorkloadVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/gpu-workload";
        Directory.CreateDirectory(dir);
        int checks=0;var rows=new List<object>();
        void Check(bool pass,string name){checks++;Console.WriteLine($"PHYXEL_WORKLOAD_CHECK pass={pass} {name}");
            if(!pass)throw new InvalidOperationException(name);}
        foreach(var size in new[]{(131,129),(480,270),(968,564),(1920,1080)})
        {
            settings.Width=size.Item1;settings.Height=size.Item2;settings.Paused=true;
            var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,
                MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal)}],0);
            int aw=r.AirWidth,ah=r.AirHeight,n=aw*ah,gx=(aw+7)/8,gy=(ah+7)/8;
            var c=r.Context;
            void Unbind(){for(int i=0;i<9;i++){c.ComputeShader.SetShaderResource(i,null);c.ComputeShader.SetUnorderedAccessView(i,null);}}
            foreach(string layout in new[]{"open","blocked","isolated","maze"})
            {
                var random=new Random(9181);var air=new AirCell[n];var links=new uint[n];var initial=new Vector2[n];
                for(int y=0;y<ah;y++)for(int x=0;x<aw;x++)
                {
                    int i=y*aw+x;
                    air[i].Blocked=layout=="blocked"&&random.Next(5)==0||layout=="maze"&&x%12==0&&y%14!=0?1:0;
                    initial[i]=new((float)(random.NextDouble()*20-10),(float)(random.NextDouble()*2-1));
                }
                for(int y=0;y<ah;y++)for(int x=0;x<aw;x++)
                {
                    int i=y*aw+x;if(air[i].Blocked>.5||layout=="isolated")continue;
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                        if((dx!=0||dy!=0)&&x+dx>=0&&x+dx<aw&&y+dy>=0&&y+dy<ah&&air[(y+dy)*aw+x+dx].Blocked<=.5)
                            links[i]|=1u<<((dy+1)*3+dx+1);
                }
                var ac=new AirSimulationConstants{AirWidth=(uint)aw,AirHeight=(uint)ah,AirGridWidth=(uint)r.Width,AirGridHeight=(uint)r.Height};
                c.UpdateSubresource(ref ac,r.AirConstants);c.ComputeShader.SetConstantBuffer(0,r.AirConstants);
                c.UpdateSubresource(air,r.Air.Buffer);c.UpdateSubresource(links,r.AirFlowLinks.Buffer);
                void Bind(){c.ComputeShader.SetUnorderedAccessViews(0,r.Air.UnorderedView,r.AirScratch.UnorderedView,
                    r.GasAirImpulse.UnorderedView,r.AirFlowLinks.UnorderedView,r.AirProjectionA.UnorderedView,r.AirProjectionB.UnorderedView);}
                void Solve(bool fused)
                {
                    for(int pass=0;pass<(fused?16:64);pass++)
                    {c.ComputeShader.Set(fused?r.AirJacobiFourABShader:r.AirJacobiABShader);c.Dispatch(gx,gy,1);
                     c.ComputeShader.Set(fused?r.AirJacobiFourBAShader:r.AirJacobiBAShader);c.Dispatch(gx,gy,1);}
                }
                c.UpdateSubresource(initial,r.AirProjectionA.Buffer);Bind();Solve(false);Unbind();
                byte[] expected=AirInventoryRegressionVerifier.Read(r,r.AirProjectionA.Buffer);
                var b=MemoryMarshal.Cast<byte,Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirProjectionB.Buffer)).ToArray();
                c.UpdateSubresource(initial,r.AirProjectionA.Buffer);Bind();Solve(true);Unbind();
                byte[] actual=AirInventoryRegressionVerifier.Read(r,r.AirProjectionA.Buffer);
                var b2=MemoryMarshal.Cast<byte,Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirProjectionB.Buffer)).ToArray();
                Check(expected.AsSpan().SequenceEqual(actual)&&b.Select(v=>v.Y).SequenceEqual(b2.Select(v=>v.Y)),
                    $"Jacobi128 byte exact {r.Width}x{r.Height} {layout}");
                Bind();using var oldTimer=new GpuStageTimer(r.Device);using var newTimer=new GpuStageTimer(r.Device);
                for(int repeat=0;repeat<24;repeat++)
                {
                    oldTimer.Begin(c);Solve(false);oldTimer.End(c);
                    newTimer.Begin(c);Solve(true);newTimer.End(c);
                    if(repeat%4==0){Unbind();AirInventoryRegressionVerifier.Read(r,r.AirProjectionA.Buffer);yield return r;Bind();}
                }
                Unbind();
                rows.Add(new{kind="Jacobi128",r.Width,r.Height,layout,baseline=oldTimer.Statistics,current=newTimer.Statistics});
                Console.WriteLine($"PHYXEL_WORKLOAD_TIME Jacobi128 {r.Width}x{r.Height} {layout} old={oldTimer.Statistics.AverageMilliseconds:F6} new={newTimer.Statistics.AverageMilliseconds:F6}");
            }
            checks+=AirSourceMappingVerifier.Run(r,registry);
            uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
            var grid=new GridCell[r.Width*r.Height];
            for(int y=r.Height/4;y<3*r.Height/4;y++)for(int x=r.Width/4;x<3*r.Width/4;x++)
                if(y<r.Height/4+4||y>=3*r.Height/4-4||x<r.Width/4+4||x>=3*r.Width/4-4)
                    grid[y*r.Width+x]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=30};
            c.UpdateSubresource(grid,r.Grid.ReadBuffer);
            c.UpdateSubresource(new uint[grid.Length+1],r.Filters.Buffer);
            var fc=new SimulationFrameConstants{Width=(uint)r.Width,Height=(uint)r.Height};c.UpdateSubresource(ref fc,r.PressureFrameConstants);
            void Graph()
            {
                c.ComputeShader.SetConstantBuffer(0,r.PressureFrameConstants);c.ComputeShader.SetShaderResources(0,r.Grid.ReadView,r.Materials.View);
                c.ComputeShader.SetShaderResource(15,r.Filters.View);
                c.ComputeShader.SetUnorderedAccessViews(0,r.PressureRoots.UnorderedView,r.PressureLinks.UnorderedView);
                c.ComputeShader.Set(r.PressureInitializeShader);c.Dispatch(gx,gy,1);
                for(int pass=0;pass<8;pass++)
                {c.ComputeShader.Set(r.PressureUnionShader);c.Dispatch(gx,gy,1);
                 c.ComputeShader.Set(r.PressureCompressShader);c.Dispatch((n+256)/256,1,1);}
                Unbind();
            }
            Graph();var roots=AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);
            r.PressureGraphValid=false;SimulationDispatchCoordinator.DispatchPressureConfinement(r);
            Check(roots.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer)),$"Graph first exact {r.Width}x{r.Height}");
            SimulationDispatchCoordinator.DispatchPressureConfinement(r);
            var schedule=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.PressureGraphSchedule.Buffer)).ToArray();
            Check(schedule[1]==0&&roots.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer)),$"Graph reuse exact {r.Width}x{r.Height}");
            using var graphOld=new GpuStageTimer(r.Device);using var graphNew=new GpuStageTimer(r.Device);
            for(int repeat=0;repeat<40;repeat++)
            {
                graphOld.Begin(c);Graph();graphOld.End(c);
                if(repeat%4==0){AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);yield return r;}
            }
            AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);
            for(int repeat=0;repeat<40;repeat++)
            {
                graphNew.Begin(c);SimulationDispatchCoordinator.DispatchPressureConfinement(r);graphNew.End(c);
                if(repeat%4==0){AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);yield return r;}
            }
            var finalSchedule=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.PressureGraphSchedule.Buffer)).ToArray();
            var arguments=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.PressureGraphArguments)).ToArray();
            Check(finalSchedule[1]==0 && finalSchedule.AsSpan().SequenceEqual(arguments),$"Graph indirect zero exact {r.Width}x{r.Height}");
            rows.Add(new{kind="Confinement",r.Width,r.Height,baseline=graphOld.Statistics,current=graphNew.Statistics});
            Console.WriteLine($"PHYXEL_WORKLOAD_TIME Confinement {r.Width}x{r.Height} old={graphOld.Statistics.AverageMilliseconds:F6} new={graphNew.Statistics.AverageMilliseconds:F6}");
        }
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{checks,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_WORKLOAD_COMPLETE checks={checks}");
    }
}
