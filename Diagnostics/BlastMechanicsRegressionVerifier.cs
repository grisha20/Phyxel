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
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;

namespace Phyxel.Diagnostics;

internal static class BlastMechanicsRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry,SimulationSettings settings)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/blast-mechanics";
        Directory.CreateDirectory(dir);
        uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),gas=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Co2);
        settings.Width=128;settings.Height=128;settings.Paused=true;settings.OpenBoundaries=false;
        var r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=metal}],0);
        var constants=new SimulationFrameConstants{Width=128,Height=128,Gravity=0};
        int checks=0;
        void Check(bool pass,string name){checks++;Console.WriteLine($"PHYXEL_BD_CHECK pass={pass} {name}");if(!pass)throw new InvalidOperationException(name);}
        GridCell[] Cells()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        foreach(int depth in new[]{1,4,8})foreach(int axis in new[]{0,1,2,3})
        {
            var grid=new GridCell[128*128];
            for(int y=0;y<6;y++)for(int x=0;x<depth;x++)
            {
                int xx=axis<2?40+x:50+y,yy=axis<2?50+y:40+x;
                grid[yy*128+xx]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=42,Lifetime=10+x+y*10,
                    BodyId=0x40000000u,VelocityX=axis==0?120:axis==1?-120:0,VelocityY=axis==2?120:axis==3?-120:0};
            }
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            for(int tick=0;tick<15;tick++)SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);
            var moved=Cells().Select((c,i)=>(c,i)).Where(v=>v.c.IsActive!=0).ToArray();
            Check(moved.Length==depth*6 && moved.All(v=>v.c.Mass==7.8f&&v.c.Temperature==42)&&
                moved.Select(v=>v.c.Lifetime).Order().SequenceEqual(grid.Where(c=>c.IsActive!=0).Select(c=>c.Lifetime).Order()),$"BD01 payload depth={depth} axis={axis}");
            Check(moved.All(v=>axis==0?v.i%128>40+depth+12:axis==1?v.i%128<28:axis==2?v.i/128>40+depth+12:v.i/128<28),$"BD01 clear strip depth={depth} axis={axis}");
            Check(moved.All(v=>Math.Abs(v.c.VelocityX)+Math.Abs(v.c.VelocityY)==120),$"BD01 impulse depth={depth} axis={axis}");
            yield return r;
        }
        // A non-vacating endpoint invalidates the entire chain. The solid wall
        // cannot be overwritten even when the preceding fragments all claim it.
        foreach(bool wall in new[]{false,true})
        {
            var grid=new GridCell[128*128];
            for(int x=40;x<48;x++)grid[64*128+x]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=42,Lifetime=x,BodyId=0x40000000u,VelocityX=120};
            grid[64*128+48]=new(){IsActive=1,MaterialIndex=wall?metal:gas,Mass=wall?7.8f:2,Temperature=77,Lifetime=99};
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);
            var after=Cells();
            Check(after.Count(c=>c.IsActive!=0)==9&&after.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass)==grid.Sum(c=>(double)c.Mass),$"BD01 endpoint conservation wall={wall}");
            Check(wall?after[64*128+48].Lifetime==99:after.Count(c=>c.MaterialIndex==gas&&c.IsActive!=0)==1,$"BD01 wall/gas packet wall={wall}");
        }
        foreach(string layout in new[]{"competition","opposing","dense","corner","filter","long","cycle","fraction","selected"})
        {
            var grid=new GridCell[128*128];var filters=new uint[grid.Length];
            (int x,int y)[] ring=[(0,-2),(1,-2),(2,-1),(2,0),(2,1),(1,2),(0,2),(-1,2),(-2,1),(-2,0),(-2,-1),(-1,-2)];
            GridCell Fragment(int label,float vx,float vy)=>new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=42,
                Lifetime=label,MoistureMass=.01f,MoistureEnergy=3,FuelMass=.02f,BodyId=0x40000000u,VelocityX=vx,VelocityY=vy};
            if(layout=="competition"||layout=="opposing")
            {grid[64*128+40]=Fragment(1,120,0);grid[64*128+(layout=="opposing"?41:44)]=Fragment(2,-120,0);}
            if(layout=="dense")for(int y=40;y<52;y++)for(int x=40;x<52;x++)grid[y*128+x]=Fragment(x+y*128,(x-45)*24,(y-45)*24);
            if(layout=="long")for(int x=8;x<108;x++)grid[64*128+x]=Fragment(x,60,0);
            if(layout=="fraction")for(int x=40;x<48;x++)grid[64*128+x]=Fragment(x,37.5f,0);
            if(layout=="selected")
            {
                grid[64*128+40]=Fragment(1,120,0);grid[64*128+41]=Fragment(2,120,0);
                grid[64*128+41].MaterialIndex=registry.GetRequiredRuntimeIndex("core:cast_iron");
                filters[64*128+41]=grid[64*128+41].MaterialIndex+1;
            }
            if(layout=="cycle")for(int k=0;k<ring.Length;k++)
            {var p=ring[k];var q=ring[(k+1)%ring.Length];grid[(64+p.y)*128+64+p.x]=Fragment(k+1,(q.x-p.x)*60,(q.y-p.y)*60);}
            if(layout=="corner")
            {grid[64*128+40]=Fragment(1,120,120);grid[64*128+41]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=30,Lifetime=99};
             grid[65*128+40]=grid[64*128+41];grid[65*128+40].Lifetime=98;}
            if(layout=="filter")
            {for(int x=40;x<48;x++)grid[64*128+x]=Fragment(x,120,0);for(int y=0;y<128;y++)filters[y*128+49]=FilterRules.Closed;}
            var filterUpload=new uint[filters.Length+1];filterUpload[0]=filters.Any(v=>v!=0)?1u:0u;filters.CopyTo(filterUpload,1);
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);r.Context.UpdateSubresource(filterUpload,r.Filters.Buffer);
            r.Context.ComputeShader.SetShaderResource(15,r.Filters.View);
            var labels=grid.Where(c=>c.IsActive!=0).Select(c=>c.Lifetime).Order().ToArray();
            for(int tick=0;tick<40;tick++)
            {
                SimulationDispatchCoordinator.DispatchPressureFragments(r,constants,false,false);var cells=Cells();
                Check(cells.Where(c=>c.IsActive!=0).Select(c=>c.Lifetime).Order().SequenceEqual(labels)&&
                    cells.Sum(c=>(double)c.Mass)==grid.Sum(c=>(double)c.Mass)&&cells.Sum(c=>(double)c.MoistureEnergy)==grid.Sum(c=>(double)c.MoistureEnergy),$"BD01 conservation {layout} tick={tick}");
                if(layout=="filter")Check(cells.Select((c,i)=>(c,i)).Where(v=>v.c.IsActive!=0).All(v=>v.i%128<49),"BD01 sealed filter");
                if(layout=="corner")Check(cells[64*128+41].Lifetime==99&&cells[65*128+40].Lifetime==98,"BD01 intact corner");
                if(layout=="cycle"&&tick==0)Check(ring.Select((p,k)=>cells[(64+p.y)*128+64+p.x].Lifetime==(k+ring.Length-1)%ring.Length+1).All(v=>v),"BD01 cyclic permutation");
                if(layout=="fraction"&&tick==39)Check(cells.Select((c,i)=>(c,i)).Where(v=>v.c.IsActive!=0).Average(v=>v.i%128)>47.5,"BD01 subcell pack advances");
                if(layout=="selected"&&tick==0)Check(cells[64*128+40].Lifetime==1&&Math.Abs(cells[64*128+40].VelocityX-102)<.01,"BD01 species filter remains a real collision");
            }
            yield return r;
        }
        r.Context.UpdateSubresource(new uint[128*128+1],r.Filters.Buffer);
        var baseDir=Path.Combine(AppContext.BaseDirectory,"Content","Shaders");
        string Expand(string path)=>File.ReadAllText(path).Replace("#include \"PhysicsShared.hlsli\"",File.ReadAllText(Path.Combine(baseDir,"PhysicsShared.hlsli")))
            .Replace("#include \"FineAirGeometry.hlsli\"",File.ReadAllText(Path.Combine(baseDir,"FineAirGeometry.hlsli")));
        string reference=Environment.GetEnvironmentVariable("PHYXEL_CONFINEMENT_REFERENCE")??throw new InvalidOperationException("Missing reference shader");
        using var code0=ShaderBytecode.Compile(Expand(reference),"CSInitialize","cs_5_0",ShaderFlags.OptimizationLevel3);
        using var code1=ShaderBytecode.Compile(Expand(reference),"CSUnion","cs_5_0",ShaderFlags.OptimizationLevel3);
        using var code2=ShaderBytecode.Compile(Expand(reference),"CSCompress","cs_5_0",ShaderFlags.OptimizationLevel3);
        using var old0=new ComputeShader(r.Device,code0.Bytecode);using var old1=new ComputeShader(r.Device,code1.Bytecode);using var old2=new ComputeShader(r.Device,code2.Bytecode);
        foreach(var size in new[]{(128,128),(131,129),(968,564)})foreach(string layout in new[]{"empty","split","box","random","filters","saved"})
        {
            if(layout=="saved"&&size.Item1!=968)continue;
            settings.Width=size.Item1;settings.Height=size.Item2;
            r=coordinator.DispatchFrame(settings,[new(){X=1,Y=1,Radius=0,Density=1,MaterialIndex=metal}],0);int w=r.Width,h=r.Height,aw=r.AirWidth,ah=r.AirHeight;
            var grid=new GridCell[w*h];var rng=new Random(726);
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                if(layout=="split"&&x==w/2||layout=="box"&&((x==w/4||x==3*w/4)&&y>=h/4&&y<=3*h/4||(y==h/4||y==3*h/4)&&x>=w/4&&x<=3*w/4)||layout=="random"&&rng.Next(8)==0)
                    grid[y*w+x]=new(){IsActive=1,MaterialIndex=metal,Mass=7.8f,Temperature=30};
            if(layout=="saved")
            {
                string path=Environment.GetEnvironmentVariable("PHYXEL_SENSOR_SCENE")??Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Phyxel","Бомба тратил.json");
                var saved=System.Threading.Tasks.Task.Run(()=>new SimulationStateSerializer().LoadAsync(path,registry)).GetAwaiter().GetResult()!;
                if(saved.World!.Width!=w||saved.World.Height!=h)throw new InvalidOperationException("Fixture size changed");
                grid=MemoryMarshal.Cast<byte,GridCell>(saved.World.Grid).ToArray();
            }
            var filterData=new uint[w*h+1];
            if(layout=="filters")
            {
                filterData[0]=1;
                for(int y=0;y<h;y++)filterData[y*w+w/2+1]=y<h/2?FilterRules.Closed:FilterRules.AmbientAir;
            }
            r.Context.UpdateSubresource(filterData,r.Filters.Buffer);
            r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            constants.Width=(uint)w;constants.Height=(uint)h;r.Context.UpdateSubresource(ref constants,r.PressureFrameConstants);
            void Graph(bool old)
            {
                var c=r.Context;c.ComputeShader.SetConstantBuffer(0,r.PressureFrameConstants);
                c.ComputeShader.SetShaderResources(0,r.Grid.ReadView,r.Materials.View);
                c.ComputeShader.SetShaderResource(15,r.Filters.View);
                c.ComputeShader.SetUnorderedAccessViews(0,r.PressureRoots.UnorderedView,r.PressureLinks.UnorderedView);
                c.ComputeShader.Set(old?old0:r.PressureInitializeShader);c.Dispatch((aw+7)/8,(ah+7)/8,1);
                for(int iteration=0;iteration<8;iteration++)
                {
                    c.ComputeShader.Set(old?old1:r.PressureUnionShader);c.Dispatch((aw+7)/8,(ah+7)/8,1);
                    c.ComputeShader.Set(old?old2:r.PressureCompressShader);c.Dispatch((aw*ah+256)/256,1,1);
                }
                c.ComputeShader.SetUnorderedAccessView(0,null);c.ComputeShader.SetUnorderedAccessView(1,null);c.ComputeShader.SetShaderResources(0,null,null);
            }
            Graph(true);var oldRoots=AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);var links=AirInventoryRegressionVerifier.Read(r,r.PressureLinks.Buffer);
            Graph(false);var roots=AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);
            Check(oldRoots.AsSpan().SequenceEqual(roots)&&links.AsSpan().SequenceEqual(AirInventoryRegressionVerifier.Read(r,r.PressureLinks.Buffer)),$"BD03 graph byte exact {w}x{h} {layout}");
            var adjacency=MemoryMarshal.Cast<byte,uint>(links).ToArray();var parents=Enumerable.Range(0,adjacency.Length+1).ToArray();
            int Root(int i){while(parents[i]!=i)i=parents[i];return i;}
            void Join(int a,int b){a=Root(a);b=Root(b);parents[Math.Max(a,b)]=Math.Min(a,b);}
            for(int i=0;i<adjacency.Length;i++){if((adjacency[i]&4)!=0)Join(i+1,0);if((adjacency[i]&1)!=0)Join(i+1,i+2);if((adjacency[i]&2)!=0)Join(i+1,i+aw+1);}
            var gpu=MemoryMarshal.Cast<byte,uint>(roots).ToArray();Check(gpu.Select((v,i)=>v==(uint)Root(i)).All(v=>v),$"BD03 CPU graph {layout}");
            using var oldTimer=new GpuStageTimer(r.Device);using var newTimer=new GpuStageTimer(r.Device);
            bool repeatedExact=true;
            for(int batch=0;batch<12;batch++)
            {oldTimer.Begin(r.Context);Graph(true);oldTimer.End(r.Context);var repeatedOld=AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);
             newTimer.Begin(r.Context);Graph(false);newTimer.End(r.Context);var repeatedNew=AirInventoryRegressionVerifier.Read(r,r.PressureRoots.Buffer);
             repeatedExact&=repeatedOld.AsSpan().SequenceEqual(roots)&&repeatedNew.AsSpan().SequenceEqual(roots);yield return r;}
            Check(repeatedExact,$"BD03 repeated graph {w}x{h} {layout}");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_BD_GRAPH {w}x{h} {layout} oldMs={oldTimer.Statistics.AverageMilliseconds:F6} newMs={newTimer.Statistics.AverageMilliseconds:F6}"));
        }
        Console.WriteLine($"PHYXEL_BD_MECHANICS_COMPLETE checks={checks}");
    }
}
