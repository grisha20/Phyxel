using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Diagnostics;

internal static class AirInventoryRegressionVerifier
{
    private static string DirectoryPath => Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? Path.Combine(AppContext.BaseDirectory,"artifacts/air-inventory");

    public static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        if (Marshal.SizeOf<SimulationFrameConstants>() != 80 || Marshal.SizeOf<OxidizerConstants>() != 32)
            throw new InvalidOperationException("Shader constant layout changed.");
        Directory.CreateDirectory(DirectoryPath);
        var settings = new SimulationSettings { Paused = true, Mode = SimulationMode.Simulation };
        var resources = coordinator.DispatchFrame(settings, [new() { X=40,EndX=40,Y=40,EndY=40,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }],0);
        int w=resources.Width,h=resources.Height;
        GridCell Cell(string id) => new() { MaterialIndex=registry.GetRequiredRuntimeIndex(id),IsActive=1,
            Mass=registry[id].Properties.Density,Temperature=200,Lifetime=registry[id].Properties.MaximumLifetime };
        void Upload(GridCell[] grid,float[] oxygen)
        {
            resources.Context.UpdateSubresource(grid,resources.Grid.ReadBuffer);
            resources.Context.UpdateSubresource(oxygen,resources.Oxidizer.ReadBuffer);
        }
        float[] Oxygen() => MemoryMarshal.Cast<byte,float>(Read(resources,resources.Oxidizer.ReadBuffer)).ToArray();
        void Tick(bool useAir=false,bool open=false,bool consume=false) => SimulationDispatchCoordinator.DispatchOxidizer(resources,1f/60,open,consume,useAir);
        static double Sum(float[] v) => v.Sum(x=>(double)x);
        static void Check(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
        void Balance(double expected,string label)
        {
            var stock=Oxygen(); double error=Math.Abs(Sum(stock)-expected);
            Check(stock.All(x=>float.IsFinite(x)&&x>=0),$"{label}: invalid inventory");
            Check(error<=Math.Max(1e-5,expected*1e-5),$"{label}: lost stock {error} / {expected}");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_INVENTORY {label} amount={Sum(stock):F6} error={error:E3} max={stock.Max():F4}"));
        }
        foreach(string gas in new[]{CoreMaterialIds.Co2,CoreMaterialIds.Steam})
        {
            var grid=new GridCell[w*h]; var oxygen=new float[w*h];
            for(int y=40;y<=64;y++) for(int x=40;x<=80;x++)
                if(x==40||x==80||y==40||y==64) grid[y*w+x]=Cell(CoreMaterialIds.Metal);
                else oxygen[y*w+x]=1;
            Upload(grid,oxygen); double initial=Sum(oxygen);
            for(int tick=0;tick<180;tick++)
            {
                // A deliberately repeated moving gas footprint, including
                // occupancy of previously fresh cells. Physical inventory
                // cannot be clipped when capacity changes under that footprint.
                for(int y=41;y<64;y++) for(int x=41;x<80;x++) grid[y*w+x]=default;
                int left=42+(tick/3)%28;
                for(int y=47;y<55;y++) for(int x=left;x<left+7;x++) grid[y*w+x]=Cell(gas);
                if(tick>=60&&tick<90) for(int x=45;x<65;x++) grid[59*w+x]=Cell(CoreMaterialIds.Water);
                resources.Context.UpdateSubresource(grid,resources.Grid.ReadBuffer);
                Tick();
                if(tick%30==29) Balance(initial,$"moving-{gas}-{tick+1}");
            }
            Check(Oxygen().Where((_,i)=>i/w<40||i/w>64||i%w<40||i%w>80).All(x=>x==0),"Leak through sealed vessel");
            // Persist compressed quantities and continue from the actual saved
            // world. Compare every byte before and after loading, then compare
            // the next deterministic transport step to uninterrupted execution.
            var snapshot=new SimulationWorldSnapshot(w,h,Read(resources,resources.Grid.ReadBuffer),Oxidizer:Read(resources,resources.Oxidizer.ReadBuffer));
            string path=Path.Combine(DirectoryPath,gas==CoreMaterialIds.Co2?"co2.json":"steam.json");
            var serializer=new SimulationStateSerializer();
            Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)registry.GetRequiredRuntimeIndex(gas),snapshot,registry)).GetAwaiter().GetResult();
            var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Saved inventory scene did not load.");
            var loadedWorld=loaded.World ?? throw new InvalidOperationException("Saved inventory world is missing.");
            Check(loadedWorld.Oxidizer!.AsSpan().SequenceEqual(snapshot.Oxidizer),"Inventory changed on save/load");
            Tick(); var continued=Read(resources,resources.Oxidizer.ReadBuffer);
            serializer.ApplyWorldSnapshot(resources,loadedWorld); Tick();
            Check(Read(resources,resources.Oxidizer.ReadBuffer).AsSpan().SequenceEqual(continued),"Save/load changed the next transport step");
            Balance(initial,$"reload-{gas}");
        }

        // An empty particle section must still restore compressed ambient stock.
        {
            var oxygen = new float[w*h]; oxygen[50*w+50] = 2.75f;
            var stockBytes = MemoryMarshal.AsBytes(oxygen.AsSpan()).ToArray();
            new SimulationStateSerializer().ApplyWorldSnapshot(resources,
                new SimulationWorldSnapshot(w,h,[],Oxidizer:stockBytes));
            Check(Read(resources,resources.Grid.ReadBuffer).All(x=>x==0),"Empty world retained old particles");
            Check(Read(resources,resources.Oxidizer.ReadBuffer).AsSpan().SequenceEqual(stockBytes),"Empty world discarded stock");
            Console.WriteLine("PHYXEL_AIR_INVENTORY empty-world restore=PASS");
        }

        // Fine walls at all four coarse-grid offsets, including a strong
        // carrier crossing the wall. A zero-inventory far side stays zero.
        for(int horizontal=0;horizontal<2;horizontal++) for(int shift=0;shift<4;shift++)
        {
            var grid=new GridCell[w*h]; var oxygen=new float[w*h];
            for(int y=0;y<h;y++) for(int x=0;x<w;x++)
            {
                int p=horizontal==0?x:y;
                if(p==100+shift) grid[y*w+x]=Cell(CoreMaterialIds.Metal);
                else if(p<100+shift) oxygen[y*w+x]=.37f;
            }
            Upload(grid,oxygen); var air=new AirCell[resources.AirWidth*resources.AirHeight];
            for(int i=0;i<air.Length;i++) air[i]=new(){VelocityX=horizontal==0?3:0,VelocityY=horizontal==0?0:3};
            resources.Context.UpdateSubresource(air,resources.Air.Buffer);
            double initial=Sum(oxygen);
            for(int tick=0;tick<40;tick++) Tick(true);
            var stock=Oxygen();
            Check(stock.Where((_,i)=>(horizontal==0?i%w:i/w)>100+shift).All(x=>x==0),"Air crossed a one-cell wall");
            Balance(initial,$"thin-wall-{horizontal}-{shift}");
        }

        // Distinguish directed transport from diffusion by the position of an
        // exhausted pocket. Reference runs share the same diffusion and walls.
        double Pocket(bool flow)
        {
            var grid=new GridCell[w*h];var oxygen=new float[w*h];
            for(int y=50;y<=70;y++)for(int x=30;x<=180;x++)
                if(x==30||x==180||y==50||y==70) grid[y*w+x]=Cell(CoreMaterialIds.Metal);
                else oxygen[y*w+x]=(x>=65&&x<80)?0:1;
            Upload(grid,oxygen);var air=new AirCell[resources.AirWidth*resources.AirHeight];
            if(flow) for(int i=0;i<air.Length;i++)air[i]=new(){VelocityX=.5f};
            resources.Context.UpdateSubresource(air,resources.Air.Buffer);
            for(int tick=0;tick<20;tick++)Tick(true);
            Balance(Sum(oxygen),flow?"advected-pocket":"diffusion-pocket");
            var stock=Oxygen();double deficit=0,weighted=0;
            for(int y=53;y<=67;y++)for(int x=45;x<140;x++)
            { double missing=Math.Max(0,1-stock[y*w+x]);deficit+=missing;weighted+=x*missing; }
            return weighted/deficit;
        }
        double diffusionX=Pocket(false),flowX=Pocket(true);
        Check(flowX>diffusionX+5,$"Oxygen deficit did not follow flow: {diffusionX} -> {flowX}");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_DEFICIT diffusionX={diffusionX:F3} flowX={flowX:F3}"));

        // Open boundary only, including a sealed floor and a blocked ceiling.
        {
            var grid=new GridCell[w*h];var oxygen=new float[w*h];
            for(int x=0;x<w;x++) grid[x]=Cell(CoreMaterialIds.Metal);
            for(int y=0;y<h;y++){grid[y*w]=Cell(CoreMaterialIds.Metal);grid[y*w+w-1]=Cell(CoreMaterialIds.Metal);}
            Upload(grid,oxygen);for(int tick=0;tick<20;tick++)Tick(false,true);
            Check(Sum(Oxygen())==0,"Blocked boundary or floor invented air");
            grid[60]=default; resources.Context.UpdateSubresource(grid,resources.Grid.ReadBuffer);
            double exchange=0;
            for(int tick=0;tick<20;tick++)
            {
                var before=Oxygen();Tick(false,true);
                var faces=MemoryMarshal.Cast<byte,System.Numerics.Vector2>(Read(resources,resources.OxidizerFlux.Buffer)).ToArray();
                double Out(int i) => Math.Max(0,faces[i].X)+Math.Max(0,faces[i].Y)+
                    (i%w>0?Math.Max(0,-faces[i-1].X):0)+(i/w>0?Math.Max(0,-faces[i-w].Y):0);
                double Limited(double flux,int a,int b) => flux==0?0:flux*Math.Min(1,before[flux>0?a:b]/Math.Max(Out(flux>0?a:b),1e-20));
                // Only this exposed ceiling pixel is a boundary reservoir.
                int i=60;
                double predicted=before[i]-Limited(faces[i].X,i,i+1)-Limited(faces[i].Y,i,i+w)+Limited(faces[i-1].X,i-1,i);
                exchange+=1-predicted;
            }
            Check(Sum(Oxygen())>1,"Opening a boundary did not replenish stock");
            Check(Math.Abs(Sum(Oxygen())-exchange)<1e-4,"Boundary exchange did not account for the changed inventory");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_BOUNDARY amount={Sum(Oxygen()):F6} exchange={exchange:F6}"));
        }

        // Exact consumption from immutable accessible donors, including a
        // compressed donor >1. Solids and newly emitted gases cannot alter
        // the donor shares between combustion and its consumption pass.
        {
            var grid=new GridCell[w*h];var oxygen=new float[w*h];var demand=new float[w*h];
            int i=60*w+60; oxygen[i-1]=2;oxygen[i+1]=.5f;oxygen[i-w]=.75f;oxygen[i+w]=.25f;
            Upload(grid,oxygen);Tick();var available=MemoryMarshal.Cast<byte,float>(Read(resources,resources.OxidizerAvailable.Buffer)).ToArray();
            demand[i]=.1f; resources.Context.UpdateSubresource(demand,resources.OxidizerDemand.Buffer);
            double before=Sum(Oxygen());
            grid[i-1]=Cell(CoreMaterialIds.Co2);resources.Context.UpdateSubresource(grid,resources.Grid.ReadBuffer);
            Tick(consume:true);
            Check(Math.Abs(before-Sum(Oxygen())-.1)<1e-5,"Consumption changed due to post-emission occupancy");
            Check(available[i-1]>0,"Compressed donor unexpectedly unavailable");
        }
        foreach(string gas in new[]{CoreMaterialIds.Co2,CoreMaterialIds.Steam})
        foreach(bool useAir in new[]{false,true}) foreach(int fps in new[]{30,60,100})
        {
            // Real solver motion, heat and transport at three rendered update
            // rates. This complements the forced-footprint adversarial test.
            settings.Paused=false;settings.AirSimulation=useAir;settings.OpenBoundaries=false;
            var grid=new GridCell[w*h];var oxygen=new float[w*h];
            for(int y=80;y<=150;y++)for(int x=80;x<=150;x++)
                if(x==80||x==150||y==80||y==150)grid[y*w+x]=Cell(CoreMaterialIds.Metal);
                else oxygen[y*w+x]=.37f;
            for(int y=105;y<113;y++)for(int x=110;x<118;x++){grid[y*w+x]=Cell(gas);oxygen[y*w+x]=0;}
            var snapshot=new SimulationWorldSnapshot(w,h,MemoryMarshal.AsBytes(grid.AsSpan()).ToArray(),Oxidizer:MemoryMarshal.AsBytes(oxygen.AsSpan()).ToArray());
            new SimulationStateSerializer().ApplyWorldSnapshot(resources,snapshot);
            coordinator.RestoreWorldActivity(resources,true,false,false,true);
            resources.Context.UpdateSubresource(new AirCell[resources.AirWidth*resources.AirHeight],resources.Air.Buffer);
            resources.Context.ClearUnorderedAccessView(resources.GasMotion.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4());
            ulong firstAirTick=coordinator.AirTicks,firstGasTick=coordinator.GasMotionTicks;
            for(int frame=0;frame<fps*2;frame++)coordinator.DispatchFrame(settings,[],1f/fps);
            Balance(Sum(oxygen),$"actual-{gas}-air{useAir}-fps{fps}");
            var after=MemoryMarshal.Cast<byte,GridCell>(Read(resources,resources.Grid.ReadBuffer));
            uint gasIndex=registry.GetRequiredRuntimeIndex(gas);double mass=0,center=0;int count=0;
            for(int i=0;i<after.Length;i++)if(after[i].IsActive!=0&&after[i].MaterialIndex==gasIndex)
            {mass+=after[i].Mass;center+=i/w;count++;Check(i%w>80&&i%w<150&&i/w>80&&i/w<150,"Gas crossed the vessel wall");}
            Check(Math.Abs(mass-64*registry[gas].Properties.Density)<1e-3,"Actual gas motion lost gas mass");
            Check(count>0&&Math.Abs(center/count-108.5)>.1,"Actual gas test did not move its cloud");
            Check(coordinator.AirTicks-firstAirTick==(useAir?120ul:0ul)&&coordinator.GasMotionTicks-firstGasTick==120&&coordinator.CombustionDispatches==120,
                $"Actual clocks differ at {fps} FPS: air={coordinator.AirTicks} gas={coordinator.GasMotionTicks} fire={coordinator.CombustionDispatches}");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_AIR_ACTUAL gas={gas} air={useAir} fps={fps} mass={mass:F5} centerY={center/count:F3} gasTicks={coordinator.GasMotionTicks}"));
        }
        File.WriteAllText(Path.Combine(DirectoryPath,"report.txt"),"PASS: displaced stock, moving gas/liquid footprints, eight fine walls, directed oxygen deficit, closed floor/open boundary, compressed save/load continuation, exact donor consumption, real CO2/steam motion at 30/60/100 FPS and Air=0/1.");
        Console.WriteLine("PHYXEL_AIR_INVENTORY_SUCCESS");
    }

    internal static byte[] Read(GpuSimulationResources resources,Buffer source)
    {
        using var staging=new Buffer(resources.Device,new BufferDescription{SizeInBytes=source.Description.SizeInBytes,
            Usage=ResourceUsage.Staging,CpuAccessFlags=CpuAccessFlags.Read,BindFlags=BindFlags.None,OptionFlags=ResourceOptionFlags.None});
        resources.Context.CopyResource(source,staging);
        var mapping=resources.Context.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
        try{var bytes=new byte[source.Description.SizeInBytes];Marshal.Copy(mapping.DataPointer,bytes,0,bytes.Length);return bytes;}
        finally{resources.Context.UnmapSubresource(staging,0);}
    }
}
