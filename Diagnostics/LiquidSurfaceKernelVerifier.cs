using System;
using System.Linq;
using Phyxel.Graphics;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class LiquidSurfaceKernelVerifier
{
    internal static void Run(GpuSimulationResources r, MaterialRegistry registry)
    {
        int w=r.Width,h=r.Height,checks=0;
        var materials=registry.CreateGpuTable();
        var buffers=new[]{r.Grid.ReadBuffer,r.BodyFlags.Buffer,r.PathBlockerMasks.Buffer,
            r.CellMaterials.Buffer,r.WaterPressureRoutes.Buffer,r.WaterPressureRouteScratch.Buffer,r.GasMotion.Buffer};
        byte[][] Capture()=>buffers.Select(b=>AirInventoryRegressionVerifier.Read(r,b)).ToArray();
        void Restore(byte[][] data){for(int i=0;i<buffers.Length;i++)r.Context.UpdateSubresource(data[i],buffers[i]);}
        void Bind(){r.Context.ComputeShader.SetConstantBuffer(0,r.FrameConstants);
            r.Context.ComputeShader.SetShaderResources(0,r.Materials.View,r.Air.View);
            r.Context.ComputeShader.SetShaderResource(15,r.Filters.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.BodyFlags.UnorderedView,
                r.PathBlockerMasks.UnorderedView,r.CellMaterials.UnorderedView,r.WaterPressureRoutes.UnorderedView,
                r.WaterPressureRouteScratch.UnorderedView,r.GasMotion.UnorderedView);}
        void Unbind(){for(int i=0;i<7;i++)r.Context.ComputeShader.SetUnorderedAccessView(i,null);
            r.Context.ComputeShader.SetShaderResources(0,null,null);r.Context.ComputeShader.SetShaderResource(15,null);}
        foreach(string liquid in new[]{"water","oil","molten_metal","molten_steel","molten_cast_iron","molten_copper"})
        foreach(string geometry in new[]{"basin","filter","layers","drain","jet","bubble","neighbours","wide","gaps"})
        {
            uint id=registry.GetRequiredRuntimeIndex("core:"+liquid),steel=registry.GetRequiredRuntimeIndex("core:steel");
            uint gas=registry.GetRequiredRuntimeIndex("core:steam"),water=registry.GetRequiredRuntimeIndex("core:water");
            GridCell Cell(uint material,float temperature)=>new(){IsActive=1,MaterialIndex=material,Mass=1,Temperature=temperature};
            var grid=new GridCell[w*h];
            for(int x=99;x<=221;x++)for(int y=125;y<=220;y++)
            {
                int top=x<160?155:175;
                if(x==99||x==221||y==220)grid[y*w+x]=Cell(steel,30);
                else if(y>=top)grid[y*w+x]=Cell(geometry=="layers"&&y>=200?water:id,
                    materials[id].InitialTemperature);
            }
            for(int i=0;i<grid.Length;i++)if(grid[i].IsActive!=0 && grid[i].MaterialIndex==id){
                grid[i].Mass=.25f+.25f*(i%4);
                grid[i].Lifetime=-10*(i%3);
            }
            if(geometry=="drain")for(int x=159;x<=162;x++)grid[220*w+x]=default;
            if(geometry=="bubble")for(int x=105;x<215;x++)grid[190*w+x]=Cell(gas,110);
            if(geometry=="jet")for(int y=130;y<155;y++){grid[y*w+125]=Cell(id,materials[id].InitialTemperature);grid[y*w+125].VelocityY=60;}
            if(geometry=="gaps")for(int x=105;x<215;x+=3)grid[185*w+x]=Cell(gas,110);
            if(geometry=="wide"){
                for(int x=2;x<w-2;x++)for(int y=120;y<=220;y++){
                    int top=x<w/2?140:175;
                    grid[y*w+x]=y==220?Cell(steel,30):y>=top?Cell(id,materials[id].InitialTemperature):default;
                }
            }
            if(geometry=="neighbours"){
                // Separate elevated basins, adjacent vertical runs and a
                // neighbouring different liquid must keep distinct ownership.
                for(int x=222;x<400;x++)for(int y=60;y<=110;y++){
                    uint material=x<310?id:water;
                    if(y==110 || x==399)grid[y*w+x]=Cell(steel,30);
                    else if(y>=(x%32<16?75:90))grid[y*w+x]=Cell(material,materials[material].InitialTemperature);
                }
            }
            Array.Clear(r.FilterMap);r.FilterCount=0;
            if(geometry=="filter")for(int y=125;y<220;y++){r.FilterMap[y*w+160]=FilterRules.Closed;r.FilterCount++;}
            r.UploadFilters();
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);
            var motion=new GasMotionState[w*h];
            for(int i=0;i<grid.Length;i++)if(grid[i].IsActive!=0 && grid[i].MaterialIndex==gas)
                motion[i]=new(){VelocityX=(i%7)-3,VelocityY=-2,OffsetX=.3f,OffsetY=-.4f};
            r.Context.UpdateSubresource(motion,r.GasMotion.Buffer);
            for(uint parity=0;parity<2;parity++)foreach(uint phase in new uint[]{0,1,2,3,5,6,7,8,9,10,11,12,13,56,57,58,158})
            {
                var c=new SimulationFrameConstants{Width=(uint)w,Height=(uint)h,FrameIndex=parity,DeltaTime=1f/120,
                    DispatchExtentX=(uint)w,DispatchExtentY=1,SimulationPhase=33};
                Bind();r.Context.UpdateSubresource(ref c,r.FrameConstants);r.Context.ComputeShader.Set(r.CellularAutomataShader);
                r.Context.Dispatch((w+15)/16,1,1);Unbind();
                var before=Capture();
                c.SimulationPhase=phase==158?58:phase;
                if(phase<=3 || phase is >=5 and <=12){
                    // Pair handlers read support outside their owned cells.
                    // Isolate ownership so those neighbours cannot change while
                    // comparing the exact handler with its specialized entry.
                    c.DispatchOffsetX=phase%3==0?125u:phase%3==1?159u:219u;
                    c.DispatchOffsetY=phase%3==0?154u:phase%3==1?190u:218u;
                    c.DispatchExtentX=1;c.DispatchExtentY=1;
                }
                byte[][] Execute(bool reference){Restore(before);Bind();r.Context.UpdateSubresource(ref c,r.FrameConstants);
                    if(phase==158){
                        if(!reference){r.Context.ComputeShader.SetUnorderedAccessView(2,r.PoolColumnSupport.UnorderedView);
                            r.Context.ComputeShader.Set(r.PoolSupportShader);r.Context.Dispatch((w+63)/64,1,1);}
                        r.Context.ComputeShader.Set(reference?r.LiquidSurfaceBalanceShader:r.PoolCachedBalanceShader);
                        r.Context.Dispatch(reference?1:w,1,1);
                    }else{
                        r.Context.ComputeShader.Set(reference?r.CellularAutomataShader:phase switch{
                            0 or 1=>r.VerticalPairShader,2 or 3=>r.HorizontalPairShader,>=5 and <=12=>r.DiagonalPairShader,
                            13=>r.AdjacentSurfaceShader,56 or 57=>r.ParallelSurfaceShader,_=>r.ViscousSurfaceShader});
                        r.Context.Dispatch(!reference && phase is 56 or 57?(w+(phase==56?2048:256)-1)/(phase==56?2048:256)+1:(int)(c.DispatchExtentX+15)/16,
                            !reference && phase is 56 or 57?1:(int)(c.DispatchExtentY+15)/16,1);
                    }
                    Unbind();return Capture();}
                var old=Execute(true);var current=Execute(false);
                for(int b=0;b<buffers.Length;b++){
                    checks++;
                    if(!old[b].AsSpan().SequenceEqual(current[b])){
                        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!;
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir,"before-grid.bin"),before[0]);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir,"before-columns.bin"),before[1]);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir,"before-materials.bin"),before[3]);
                        if(b==0){
                            var a=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,GridCell>(old[b]);
                            var z=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,GridCell>(current[b]);
                            for(int i=0,count=0;i<a.Length && count<12;i++)if(!old[b].AsSpan(i*56,56).SequenceEqual(current[b].AsSpan(i*56,56))){
                                Console.WriteLine($"MISMATCH x{i%w} y{i/w} old={a[i].MaterialIndex}/{a[i].Mass}/{a[i].VelocityX}/{a[i].VelocityY} new={z[i].MaterialIndex}/{z[i].Mass}/{z[i].VelocityX}/{z[i].VelocityY}");count++;
                            }
                        }
                        throw new InvalidOperationException($"Liquid kernel mismatch {liquid}/{geometry} phase{phase} parity{parity} buffer{b}");
                    }
                }
            }
        }
        Console.WriteLine($"PHYXEL_LIQUID_KERNELS_SUCCESS checks={checks}");
    }
}
