using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Diagnostics;

internal static class WoodCycleRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/wood-cycle";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_WOOD_BASELINE")=="1";
        var settings=new SimulationSettings{Paused=true,AirSimulation=false,OpenBoundaries=false,SolidGravity=false};
        uint wood=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood),water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            coal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),fire=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
            fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture),oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=wood}],0);
        int w=r.Width,h=r.Height,n=w*h,p=120*w+160,checks=0,failures=0;
        var physical=registry.CreateGpuTable();var table=physical.ToArray();
        for(int i=0;i<table.Length;i++)table[i].ThermalConductivity=0;
        var serializer=new SimulationStateSerializer();var metrics=new List<object>();
        void Check(bool ok,string label){checks++;if(!ok){failures++;Console.WriteLine("PHYXEL_WOOD_FAIL "+label);}}
        GridCell Cell(uint id,float t=20,float mass=-1)=>new(){IsActive=1,MaterialIndex=id,Temperature=t,
            Mass=mass>=0?mass:physical[id].SimulationKind==(uint)MaterialSimulationKind.Solid?physical[id].Density:1};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Mass(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
        double Energy(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        void Balance(GridCell[] a,GridCell[] b,string label){Check(Math.Abs(Mass(a)-Mass(b))<.0001*Math.Max(1,Mass(a)),label+" mass");
            Check(Math.Abs(Energy(a)-Energy(b))<.0001*Math.Max(1,Math.Abs(Energy(a))),label+" energy");}
        void Upload(GridCell[] g){serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false);r.Materials.Upload(r.Context,table);}
        void Advance(int fps,float seconds){settings.Paused=false;for(int f=0;f<(int)Math.Round(fps*seconds);f++){
            coordinator.DispatchFrame(settings,[],1f/fps);
            coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);}}
        GridCell[] Pocket(uint id,float t=20){var g=new GridCell[n];g[p]=Cell(id,t);g[p+1]=Cell(water,20,.5f);
            foreach(int o in new[]{-1,-w,w,2,1-w,1+w})g[p+o]=Cell(fixture);return g;}
        // Real clocks, not direct moisture dispatch; the piece and donor cannot fall.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            settings.Mode=mode;var g=Pocket(wood);Upload(g);Advance(fps,1);var a=Read();
            Check(Math.Abs(a[p].MoistureMass-.20)<.0001,"water uptake .25 per dry mass "+mode+"/"+fps);
            Check(a[p].MaterialIndex==wood&&a[p].Mass==g[p].Mass,"same wood remains fixed "+mode+"/"+fps);
            Balance(g,a,"wood uptake "+mode+"/"+fps);
            metrics.Add(new{scenario="uptake",mode=mode.ToString(),fps,moisture=a[p].MoistureMass});
        }
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            settings.Mode=mode;var oily=Pocket(wood);oily[p+1]=Cell(oil);Upload(oily);Advance(fps,1);var a=Read();
            Check(a[p].MoistureMass==0&&Math.Abs(a[p].FuelMass-.032)<.0001,"oil separate and slower than water "+mode+"/"+fps);
            Balance(oily,a,"wood oil uptake "+mode+"/"+fps);
            metrics.Add(new{scenario="oil-uptake",mode=mode.ToString(),fps,fuel=a[p].FuelMass});
        }
        // Old JSON reproduces the absent mechanism; don't invent unsupported wet cells.
        if(!baseline){
            var g=Pocket(wood);Upload(g);Advance(60,8);var soaked=Read();
            Check(Math.Abs(soaked[p].MoistureMass-.24)<.0001,"wood pore capacity");Balance(g,soaked,"saturation");
            settings.Paused=true;var before=Read();coordinator.DispatchFrame(settings,[],1);
            Check(MemoryMarshal.AsBytes(before.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"paused water byte exact");
            string path=Path.Combine(dir,"wet-wood.json");var snapshot=new SimulationWorldSnapshot(w,h,MemoryMarshal.AsBytes(soaked.AsSpan()).ToArray());
            Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)wood,snapshot,registry)).GetAwaiter().GetResult();
            var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult()!;
            Check(snapshot.Grid.AsSpan().SequenceEqual(loaded.World!.Grid),"wood save/reload byte exact");
            Upload(soaked);Advance(60,1);var continued=Read();Upload(MemoryMarshal.Cast<byte,GridCell>(loaded.World.Grid).ToArray());Advance(60,1);
            Check(MemoryMarshal.AsBytes(continued.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"wet wood continuation same after reload");
            var probeConstants=new TemperatureProbeConstants{X=(uint)(p%w),Y=(uint)(p/w),Width=(uint)w,Height=(uint)h};
            r.Context.UpdateSubresource(ref probeConstants,r.TemperatureProbeConstants);
            r.Context.ComputeShader.Set(r.TemperatureProbeShader);r.Context.ComputeShader.SetConstantBuffer(0,r.TemperatureProbeConstants);
            r.Context.ComputeShader.SetShaderResources(0,r.Grid.ReadView,r.Materials.View);r.Context.ComputeShader.SetUnorderedAccessView(0,r.TemperatureProbeResult.UnorderedView);
            r.Context.Dispatch(1,1,1);for(int i=0;i<2;i++)r.Context.ComputeShader.SetShaderResource(i,null);
            r.Context.ComputeShader.SetUnorderedAccessView(0,null);r.Context.ComputeShader.Set(null);
            var probe=MemoryMarshal.Cast<byte,TemperatureProbeResult>(AirInventoryRegressionVerifier.Read(r,r.TemperatureProbeResult.Buffer))[0];
            Check(Phyxel.UI.UiStatusBar.FormatTemperatureProbe(registry,probe).Contains("влага"),"wood cursor shows moisture");
            foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
                settings.Mode=mode;settings.AirSimulation=false;
                var drying=new GridCell[n];drying[p]=Cell(wood,100);drying[p].MoistureMass=.1f;
                drying[p].MoistureEnergy=.1f*physical[water].TransitionAboveLatentHeat;
                foreach(int o in new[]{-1,-w,w})drying[p+o]=Cell(fixture,100);
                Upload(drying);Advance(fps,2);var dried=Read();
                Check(dried[p].MaterialIndex==wood&&dried[p].Mass==.8f&&dried[p].MoistureMass==0,"actual clock dries wood "+mode+"/"+fps);
                Check(Math.Abs(dried.Where(c=>c.IsActive!=0&&c.MaterialIndex==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam)).Sum(c=>c.Mass)-.1)<.00001,"actual clock returns steam "+mode+"/"+fps);
                Balance(drying,dried,"real drying "+mode+"/"+fps);
                metrics.Add(new{scenario="drying",mode=mode.ToString(),fps,moisture=dried[p].MoistureMass});
            }
            // Wood -> charcoal must preserve dry residue and mode/O2 distinction.
            void React(uint tick,bool finite,float oxygen){
                r.Context.UpdateSubresource(Enumerable.Repeat(oxygen,n).ToArray(),r.OxidizerAvailable.Buffer);
                r.Context.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new RawInt4(-1,-1,-1,-1));
                r.Context.ClearUnorderedAccessView(r.EmissionRequests.UnorderedView,new RawInt4());
                r.Context.ClearUnorderedAccessView(r.OxidizerDemand.UnorderedView,new RawInt4());
                r.Context.ClearUnorderedAccessView(r.ReactionPending.UnorderedView,new RawInt4());
                var c=new CombustionConstants{Width=(uint)w,Height=(uint)h,MaterialCount=(uint)registry.Count,DeltaTime=1f/60,TickIndex=tick,FiniteOxidizer=finite?1u:0u};
                r.Context.UpdateSubresource(ref c,r.CombustionConstants);r.Context.ComputeShader.Set(r.CombustionShader);
                r.Context.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);r.Context.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
                r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
                r.Context.Dispatch((w+15)/16,(h+15)/16,1);for(int i=0;i<3;i++)r.Context.ComputeShader.SetShaderResource(i,null);
                for(int i=0;i<6;i++)r.Context.ComputeShader.SetUnorderedAccessView(i,null);r.Context.ComputeShader.Set(null);
            }
            foreach(bool finite in new[]{false,true}){
                var wet=new GridCell[n];wet[p]=Cell(wood,100);wet[p].MoistureMass=.1f;Upload(wet);
                for(uint tick=0;tick<60;tick++)React(tick,finite,1);
                Check(Read()[p].Mass==.8f&&Read()[p].MaterialIndex==wood,"wet wood fuel suppressed "+finite);
                var dry=new GridCell[n];dry[p]=Cell(wood,400);Upload(dry);
                if(finite){for(uint tick=0;tick<60;tick++)React(tick,true,0);Check(Read()[p].Mass==.8f,"inert Simulation wood no burn");}
                float converted=0;
                for(uint tick=1;tick<=732;tick++){React(tick,finite,1);if(Read()[p].MaterialIndex==coal){converted=tick/60f;break;}}
                var residue=Read()[p];Check(converted>=6&&converted<=6.2f&&Math.Abs(residue.Mass-.2)<.00001,"dry wood leaves coal in6s "+finite);
                Check(residue.MoistureMass==0&&residue.FuelMass==0,"residue not born wet/oiled");
                metrics.Add(new{scenario="burnout",finite,seconds=converted,residue=residue.Mass});
            }
            foreach(bool finite in new[]{false,true}){
                var dry=new GridCell[n];dry[p]=Cell(wood,400);Upload(dry);React(1,finite,1);var dryAfter=Read();
                var soakedOil=new GridCell[n];soakedOil[p]=Cell(wood,400);soakedOil[p].FuelMass=.15f;Upload(soakedOil);React(1,finite,1);var oilAfter=Read();
                Check(oilAfter[p].Mass==.8f&&oilAfter[p].FuelMass<.15f,"wood oil burns before dry base "+finite);
                double dryQ=Energy(dryAfter)-Energy(dry),oilQ=Energy(oilAfter)-Energy(soakedOil);
                Check(oilQ>dryQ*1.5,"oil gives stronger heat than dry wood "+finite);
                Check(Math.Abs(oilQ-(.15-oilAfter[p].FuelMass)*(physical[oil].HeatPerMass-400*physical[oil].HeatCapacity))<.001,"wood oil energy ledger "+finite);
                foreach(string cover in new[]{"wet","solid","inert"}){
                    var closed=soakedOil.ToArray();if(cover=="wet"){closed[p].MoistureMass=.02f;closed[p].Temperature=100;}
                    if(cover=="solid")foreach(int offset in new[]{-1,1,-w,w})closed[p+offset]=Cell(fixture,400);
                    Upload(closed);React(1,finite,cover=="inert"?0:1);
                    Check(Read()[p].FuelMass==.15f||(!finite&&cover=="inert"),"wood oil inhibited "+finite+"/"+cover);
                }
            }
            var mixed=Pocket(wood);mixed[p].MoistureMass=.12f;mixed[p+1]=Cell(oil);Upload(mixed);Advance(60,5);var mixedAfter=Read();
            Balance(mixed,mixedAfter,"wood mixed pores");Check(mixedAfter[p].FuelMass>0&&mixedAfter[p].MoistureMass/(.8f*.3f)+mixedAfter[p].FuelMass/(.8f*.25f)<=1.0001,"wood shared pores");
            var oilChain=new GridCell[n];for(int x=0;x<5;x++)oilChain[p+x]=Cell(wood);oilChain[p].FuelMass=.2f;
            Upload(oilChain);Advance(60,8);var wicked=Read();Balance(oilChain,wicked,"wood oil wick");Check(wicked[p+2].FuelMass>1e-5,"wood oil reaches depth two");
            string oilPath=Path.Combine(dir,"oily-wood.json");settings.Paused=true;
            var oilSnapshot=new SimulationWorldSnapshot(w,h,MemoryMarshal.AsBytes(mixedAfter.AsSpan()).ToArray());
            Task.Run(()=>serializer.SaveAsync(oilPath,settings,(ushort)wood,oilSnapshot,registry)).GetAwaiter().GetResult();
            var oilLoaded=Task.Run(()=>serializer.LoadAsync(oilPath,registry)).GetAwaiter().GetResult()!;
            Check(oilSnapshot.Grid.AsSpan().SequenceEqual(oilLoaded.World!.Grid),"mixed wood reload byte exact");
            Upload(mixedAfter);coordinator.DispatchFrame(settings,[],1);Check(MemoryMarshal.AsBytes(mixedAfter.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"mixed wood pause byte exact");
            foreach(bool flat in new[]{false,true}){
                var colors=new GridCell[n];for(int group=0;group<4;group++)for(int y=80;y<110;y++)for(int x=80+group*45;x<110+group*45;x++){
                    int i=y*w+x;colors[i]=Cell(wood);if(group==1)colors[i].MoistureMass=.12f;if(group==2)colors[i].MoistureMass=.24f;if(group==3)colors[i].FuelMass=.2f;
                }
                Upload(colors);settings.RenderWithoutEffects=flat;coordinator.DispatchFrame(settings,[],0);
                using var png=File.Create(Path.Combine(dir,"wood-colors-"+(flat?"flat":"effects")+".png"));r.PresentationTexture.SaveAsPng(png,w,h);
            }
            settings.RenderWithoutEffects=false;
            // An actual flame brush at the right of a thin fixed plank. Preserve
            // production conduction/emissions: front behaviour has its own evidence.
            foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
                table=physical.ToArray();settings.Mode=mode;settings.AirSimulation=true;
                var plank=new GridCell[n];int right=120*w+190;
                // A four-cell board rather than a one-pixel film touching a
                // 100-mass heat sink. Insulate the stand, leaving production
                // wood conduction and all gas/combustion properties intact.
                table[fixture].ThermalConductivity=0;
                for(int x=150;x<=190;x++){
                    for(int y=120;y<124;y++)plank[y*w+x]=Cell(wood);
                    plank[124*w+x]=Cell(fixture);
                }
                Upload(plank);settings.Paused=true;
                coordinator.DispatchFrame(settings,[new(){X=191,Y=120,EndX=191,EndY=120,Radius=2,Density=1,Mode=BrushCommandMode.Material,MaterialIndex=fire}],0);
                settings.Paused=false;float firstRight=0,firstLeft=0;GridCell[] burning=plank;
                for(int f=0;f<fps*20;f++){
                    BrushDrawCommand[] commands=f<fps*2?[new(){X=191,Y=120,EndX=191,EndY=120,Radius=2,Density=1,Mode=BrushCommandMode.Material,MaterialIndex=fire}]:[];
                    coordinator.DispatchFrame(settings,commands,1f/fps);
                    coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
                    if(f%(fps/2)!=fps/2-1)continue;burning=Read();
                    if(f==fps/2-1)Check(burning[120*w+150].MaterialIndex==wood,"remote wood not instantly charred at0.5s "+mode+"/"+fps);
                    if(firstRight==0&&Enumerable.Range(171,20).Any(x=>Enumerable.Range(120,4).Any(y=>burning[y*w+x].MaterialIndex==coal)))firstRight=(f+1)/(float)fps;
                    if(firstLeft==0&&Enumerable.Range(150,20).Any(x=>Enumerable.Range(120,4).Any(y=>burning[y*w+x].MaterialIndex==coal)))firstLeft=(f+1)/(float)fps;
                }
                int rightCoal=Enumerable.Range(171,20).Sum(x=>Enumerable.Range(120,4).Count(y=>burning[y*w+x].MaterialIndex==coal));
                int leftCoal=Enumerable.Range(150,20).Sum(x=>Enumerable.Range(120,4).Count(y=>burning[y*w+x].MaterialIndex==coal));
                Check(firstRight>0&&(firstLeft==0||firstRight<firstLeft),"right ignition forms coal first "+mode+"/"+fps);
                File.WriteAllBytes(Path.Combine(dir,"plank-"+mode+"-"+fps+".grid"),MemoryMarshal.AsBytes(burning.AsSpan()).ToArray());
                if(fps==60){using var png=File.Create(Path.Combine(dir,"plank-"+mode+".png"));r.PresentationTexture.SaveAsPng(png,w,h);}
                metrics.Add(new{scenario="right-brush-front",mode=mode.ToString(),fps,seconds=20,heldBrushSeconds=2,firstRight,firstLeft,rightCoal,leftCoal,
                    rightTemperature=burning[right].Temperature,leftTemperature=burning[120*w+150].Temperature});
            }
        }
        // Real powder combustion over a thick board, without a held flame.
        // The same scene runs against the pre-change JSON for comparison.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation}){
            table=physical.ToArray();table[fixture].ThermalConductivity=0;settings.Mode=mode;settings.AirSimulation=true;
            var powderBoard=new GridCell[n];uint powder=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
            for(int x=150;x<=190;x++){for(int y=120;y<124;y++)powderBoard[y*w+x]=Cell(wood);powderBoard[124*w+x]=Cell(fixture);}
            for(int x=181;x<191;x++)for(int y=117;y<120;y++)powderBoard[y*w+x]=Cell(powder,400,physical[powder].Density);
            Upload(powderBoard);Advance(60,20);var after=Read();
            double remaining=after.Where(c=>c.IsActive!=0&&(c.MaterialIndex==wood||c.MaterialIndex==coal||c.MaterialIndex==physical[coal].MoistureWetMaterialIndex)).Sum(c=>(double)c.Mass);
            double consumed=41*4*.8-remaining;
            Check(consumed>.1,"powder ignites dry wood "+mode);
            metrics.Add(new{scenario="powder-board",mode=mode.ToString(),fps=60,seconds=20,consumed});
            using var png=File.Create(Path.Combine(dir,"powder-board-"+mode+".png"));r.PresentationTexture.SaveAsPng(png,w,h);
        }
        r.Materials.Upload(r.Context,physical);
        File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{passed=failures==0,checks,failures,baseline,width=w,height=h,metrics},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PHYXEL_WOOD_RESULT passed={failures==0} checks={checks} failures={failures} baseline={baseline}");
        if(failures>0&&!baseline)throw new InvalidOperationException("Wood cycle acceptance failed.");
    }
}
