using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX.Mathematics.Interop;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;

namespace Phyxel.Diagnostics;

internal static class GasTilePerformanceVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        uint steam=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        foreach(var size in new[]{(320,180),(968,564),(131,129)})
        {
            var settings=new SimulationSettings{Width=size.Item1,Height=size.Item2,Paused=true};
            var r=coordinator.DispatchFrame(settings,[new(){X=10,Y=10,Radius=1,Density=1,MaterialIndex=steam}],0);
            int w=r.Width,h=r.Height,tw=(w+63)/64,th=(h+63)/64;
            var constants=new SimulationFrameConstants{Width=(uint)w,Height=(uint)h};
            r.Context.UpdateSubresource(ref constants,r.FrameConstants);
            ComputeShader? reference=null;
            if(Environment.GetEnvironmentVariable("PHYXEL_GAS_TILE_REFERENCE_SHADER") is {Length:>0} referencePath)
            {
                string code=File.ReadAllText(referencePath).Replace("#include \"PhysicsShared.hlsli\"",
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Content","Shaders","PhysicsShared.hlsli")));
                using var compiled=ShaderBytecode.Compile(code,"CSMain","cs_5_0",ShaderFlags.OptimizationLevel3);
                reference=new ComputeShader(r.Device,compiled.Bytecode);
            }
            foreach(string layout in new[]{"empty","boundary","sparse","dense"})
            {
                var cells=new GridCell[w*h]; var random=new Random(1234);
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                    if(layout=="dense" || layout=="sparse"&&random.Next(1000)==0 ||
                        layout=="boundary"&&(x%64==0||x==w-1)&&(y%64==0||y==h-1))
                        cells[y*w+x]=new(){IsActive=1,MaterialIndex=steam,Mass=1,Temperature=123};
                r.Context.UpdateSubresource(cells,r.Grid.ReadBuffer);
                void Classify(ComputeShader? shader=null)
                {
                    var c=r.Context;c.ClearUnorderedAccessView(r.GasActiveTiles.UnorderedView,new RawInt4());
                    c.ComputeShader.Set(shader??r.GasActiveTilesShader);c.ComputeShader.SetConstantBuffer(0,r.FrameConstants);
                    c.ComputeShader.SetShaderResources(0,r.Grid.ReadView,r.Materials.View);
                    c.ComputeShader.SetUnorderedAccessView(0,r.GasActiveTiles.UnorderedView);
                    c.Dispatch((w+15)/16,(h+15)/16,1);
                    c.ComputeShader.SetShaderResource(0,null);c.ComputeShader.SetShaderResource(1,null);c.ComputeShader.SetUnorderedAccessView(0,null);
                }
                var expected=new uint[tw*th];
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(cells[y*w+x].IsActive!=0)
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {int tx=x/64+dx,ty=y/64+dy;if(tx>=0&&ty>=0&&tx<tw&&ty<th)expected[ty*tw+tx]=1;}
                Classify();
                var actual=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.GasActiveTiles.Buffer)).ToArray();
                if(!expected.AsSpan().SequenceEqual(actual))throw new InvalidOperationException("Gas tile mask differs from original per-cell mask.");
                using var timer=new GpuStageTimer(r.Device);
                using var referenceTimer=new GpuStageTimer(r.Device);
                // Batch classification to amortize CPU submission and query resolution.
                for(int batch=0;batch<20;batch++)
                {
                    timer.Begin(r.Context);for(int repeat=0;repeat<20;repeat++)Classify();timer.End(r.Context);
                    AirInventoryRegressionVerifier.Read(r,r.GasActiveTiles.Buffer);
                    if(reference is not null)
                    {
                        referenceTimer.Begin(r.Context);for(int repeat=0;repeat<20;repeat++)Classify(reference);referenceTimer.End(r.Context);
                        var old=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.GasActiveTiles.Buffer)).ToArray();
                        if(!actual.AsSpan().SequenceEqual(old))throw new InvalidOperationException("Old/new GPU tile masks differ.");
                    }
                    yield return r;
                }
                Console.WriteLine(FormattableString.Invariant($"PHYXEL_GAS_TILES {w}x{h} {layout} mask=PASS gpuMs={timer.Statistics.AverageMilliseconds/20:F6} referenceGpuMs={referenceTimer.Statistics.AverageMilliseconds/20:F6}"));
            }
            reference?.Dispose();
        }
        Console.WriteLine("PHYXEL_GAS_TILES_COMPLETE");
    }
}
