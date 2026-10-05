using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
namespace Phyxel.Diagnostics;

internal static class AbsorptionRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry, Action<int> setFrameRate)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/absorption";
        Directory.CreateDirectory(dir);
        bool baseline=Environment.GetEnvironmentVariable("PHYXEL_ABSORPTION_BASELINE")=="1";
        var settings=new SimulationSettings {Paused=true,AirSimulation=false,OpenBoundaries=false};settings.ApplyScale(.25f);
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,MaterialIndex=water}],0);
        yield return r;
        setFrameRate(1000);
        int w=r.Width,h=r.Height,index=120*w+220;var table=registry.CreateGpuTable();
        var serializer=new SimulationStateSerializer();var results=new List<object>();int failures=0;
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Load(GridCell[] g){serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));coordinator.RestoreWorldActivity(r,true,true,false);}
        void Tick(int tick){var c=new ContactTransitionConstants {DeltaTime=.05f,Width=(uint)w,Height=(uint)h,TickIndex=(uint)tick};
            var context=r.Context;context.UpdateSubresource(ref c,r.ContactTransitionConstants);context.ComputeShader.Set(r.MoistureShader);
            context.ComputeShader.SetConstantBuffer(0,r.ContactTransitionConstants);context.ComputeShader.SetShaderResource(0,r.Materials.View);
            context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CellMaterials.UnorderedView,r.GasMotion.UnorderedView,r.ContactSummary.UnorderedView);
            context.Dispatch((w+15)/16,(h+15)/16,1);context.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<4;i++)context.ComputeShader.SetUnorderedAccessView(i,null);context.ComputeShader.Set(null);}
        void Record(object value,bool pass){if(!pass)failures++;results.Add(value);Console.WriteLine("PHYXEL_ABSORPTION "+JsonSerializer.Serialize(value));}
        void Complete(){
            File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{failures,results},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PHYXEL_ABSORPTION_COMPLETE cases={results.Count} failures={failures}");
            if(failures>0&&!baseline)throw new InvalidOperationException("Absorption checks failed");
        }
        // Real asynchronous GPU readback, including the appended species field.
        uint probeHost=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        foreach(uint species in new[]{water,oil,(uint)registry.GetRequiredRuntimeIndex("test:absorption_31")}){
            var g=new GridCell[w*h];g[index]=new(){IsActive=1,MaterialIndex=probeHost,Mass=1,Temperature=70,MoistureMass=.06f};
            if(species!=water){g[index].FuelMass=.12f;g[index].RetainedLiquidMaterialIndex=species;}
            Load(g);var cursorProbe=new GpuTemperatureProbe();
            for(int frame=0;frame<30;frame++){cursorProbe.Update(r,new Microsoft.Xna.Framework.Point(index%w,index/w),.01f);yield return r;}
            var sample=cursorProbe.Latest;
            bool pass=sample is {} p && p.IsActive!=0 && p.MaterialIndex==probeHost && Math.Abs(p.Temperature-70)<.0001f &&
                p.RetainedLiquidMaterialIndex==(species==water?0:species) &&
                Math.Abs(p.FuelFraction-(species==water?0:.12f/1.18f))<1e-5 &&
                Phyxel.UI.UiStatusBar.FormatTemperatureProbe(registry,p).Contains(species==water?"влага":registry[species].Name,StringComparison.Ordinal);
            Record(new{test="cursor-probe",species,active=sample?.IsActive,temperature=sample?.Temperature,
                retainedId=sample?.RetainedLiquidMaterialIndex,fraction=sample?.FuelFraction,pass},pass);
        }
        // The host and its stock have independent combustible properties.
        foreach(var mode in Enum.GetValues<SimulationMode>())
        foreach(string id in new[]{CoreMaterialIds.Sand,CoreMaterialIds.Gunpowder,CoreMaterialIds.Wood})
        foreach(uint species in new[]{oil,(uint)registry.GetRequiredRuntimeIndex("test:absorption_31")})
        foreach(float initial in id==CoreMaterialIds.Gunpowder&&species==oil?new[]{400f,1000f}:new[]{400f}){
            uint host=registry.GetRequiredRuntimeIndex(id);var g=new GridCell[w*h];
            g[index]=new(){IsActive=1,MaterialIndex=host,Mass=1,Temperature=initial,FuelMass=.10f,RetainedLiquidMaterialIndex=species};
            Load(g);var ctx=r.Context;
            ctx.UpdateSubresource(Enumerable.Repeat(1f,w*h).ToArray(),r.OxidizerAvailable.Buffer);
            ctx.ClearUnorderedAccessView(r.EmissionClaims.UnorderedView,new SharpDX.Mathematics.Interop.RawInt4(-1,-1,-1,-1));
            var c=new CombustionConstants{Width=(uint)w,Height=(uint)h,DeltaTime=1f/60,TickIndex=1,
                MaterialCount=(uint)registry.Count,FiniteOxidizer=mode==SimulationMode.Simulation?1u:0u,Reserved1=1};
            ctx.UpdateSubresource(ref c,r.CombustionConstants);ctx.ComputeShader.Set(r.CombustionShader);
            ctx.ComputeShader.SetConstantBuffer(0,r.CombustionConstants);
            ctx.ComputeShader.SetShaderResources(0,r.Materials.View,r.Emissions.View,r.OxidizerAvailable.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CombustionSummary.UnorderedView,
                r.EmissionClaims.UnorderedView,r.EmissionRequests.UnorderedView,r.OxidizerDemand.UnorderedView,r.ReactionPending.UnorderedView);
            ctx.Dispatch((w+15)/16,(h+15)/16,1);
            for(int i=0;i<3;i++)ctx.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<6;i++)ctx.ComputeShader.SetUnorderedAccessView(i,null);ctx.ComputeShader.Set(null);yield return r;
            var a=Read()[index];bool pass=a.IsActive!=0&&a.RetainedLiquidMaterialIndex==species&&float.IsFinite(a.Temperature)&&
                (species==oil&&initial==400?a.FuelMass<.10f:a.FuelMass==.10f)&&
                (initial!=1000||a.Temperature>=table[oil].MaximumCombustionTemperature)&&
                (id==CoreMaterialIds.Sand?a.Mass==1:a.Mass<1);
            Record(new{test="stock-reaction",mode=mode.ToString(),id,species,initial,temperature=a.Temperature,dryMass=a.Mass,retained=a.FuelMass,pass},pass);
        }
        if(Environment.GetEnvironmentVariable("PHYXEL_ABSORPTION_QUICK_ONLY")=="1"){Complete();yield break;}
        foreach(var mode in Enum.GetValues<SimulationMode>())foreach(var host in registry.Materials.Where(m=>m.IsBundled))
        foreach(uint liquid in new[]{water,oil})
        {
            settings.Mode=mode;var g=new GridCell[w*h];
            g[index]=new(){IsActive=1,MaterialIndex=host.RuntimeIndex,Mass=1,Temperature=30,RestFrames=2};
            g[index+1]=new(){IsActive=1,MaterialIndex=liquid,Mass=liquid==water?.30f:.20f,Temperature=40};
            Load(g);double mass0=g.Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
            double e0=g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
            for(int t=0;t<24;t++){Tick(t);if(t%4==3)yield return r;}
            var a=Read();float retained=liquid==water?a[index].MoistureMass:a[index].FuelMass;
            bool expected=host.Id is "core:wood" or "core:coal" or "core:wet_charcoal" or "core:gunpowder" or "core:sand";
            double massError=Math.Abs(a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass)-mass0);
            double e1=a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
            double energyError=Math.Abs(e1-e0)/Math.Max(1,Math.Abs(e0));
            bool pass=(expected?retained>0:retained==0)&&massError<1e-5&&energyError<1e-4;
            if(host.Id is "core:gunpowder" or "core:sand")pass &= a[index+1].IsActive==0;
            Record(new{test="catalog",mode=mode.ToString(),host=host.Id,liquid=registry[liquid].Id,retained,free=a[index+1].Mass,massError,energyError,pass},pass);
        }
        foreach(string id in new[]{"core:gunpowder","core:sand","core:wood","core:coal","core:wet_charcoal"})
        foreach(bool effects in new[]{false,true})
        {
            settings.RenderWithoutEffects=!effects;uint host=registry.GetRequiredRuntimeIndex(id);var colors=new List<double>();
            foreach(float fraction in new[]{0f,.25f,1f})
            {
                var g=new GridCell[w*h];for(int y=110;y<125;y++)for(int x=210;x<225;x++)
                    g[y*w+x]=new(){IsActive=1,MaterialIndex=host,Mass=1,Temperature=30,MoistureMass=fraction*(table[host].MoistureCapacity>0?table[host].MoistureCapacity:.3f)};
                Load(g);coordinator.DispatchFrame(settings,[],0);yield return r;
                string path=Path.Combine(dir,$"color-{id.Split(':')[1]}-{effects}-{fraction}.png");SimulationScreenshotWriter.Save(r,path);
                using var bmp=new Bitmap(path);var c=bmp.GetPixel(216,116);colors.Add(.2126*c.R+.7152*c.G+.0722*c.B);
            }
            bool pass=colors[2]<=colors[0]*.8&&colors[1]<colors[0]&&colors[2]<colors[1];
            Record(new{test="color",host=id,effects,colors,pass},pass);
        }
        foreach(string id in new[]{"core:gunpowder","core:sand"})
        foreach(var mode in Enum.GetValues<SimulationMode>())foreach(int fps in new[]{30,60,100})
        foreach(uint liquid in new[]{water,oil})
        {
            settings.Mode=mode;uint host=registry.GetRequiredRuntimeIndex(id);var g=new GridCell[w*h];
            g[index]=new(){IsActive=1,MaterialIndex=host,Mass=1,Temperature=30};
            g[index+1]=new(){IsActive=1,MaterialIndex=liquid,Mass=liquid==water?.30f:.20f,Temperature=40};
            Load(g);int ticks=0;bool deadline=false;
            for(int frame=1;frame<=fps*2;frame++)
            {
                while(ticks<(int)(frame*20d/fps+1e-6))Tick(ticks++);
                if(frame==(liquid==water ? (int)Math.Ceiling(.4*fps) : (int)Math.Ceiling(1.2*fps)))
                    deadline=Read()[index+1].IsActive==0;
                yield return r;
            }
            var a=Read();float retained=liquid==water?a[index].MoistureMass:a[index].FuelMass;
            bool pass=deadline&&Math.Abs(retained-(liquid==water?.30f:.20f))<1e-5;
            Record(new{test="cadence",id,mode=mode.ToString(),fps,liquid,deadline,retained,pass},pass);
            // The paused coordinator must not spend any stock.
            var before=MemoryMarshal.AsBytes(a.AsSpan()).ToArray();coordinator.DispatchFrame(settings,[],1);yield return r;
            bool paused=before.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan()));
            Record(new{test="pause",id,mode=mode.ToString(),fps,liquid,pass=paused},paused);
        }
        // 32 distinct registered-table species exercise one general GPU path.
        // These fixtures are noncombustible liquids, with different heat capacity.
        var original=table.ToArray();uint porous=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        foreach(var liquidDefinition in registry.Materials.Where(m=>m.Id.StartsWith("test:absorption_",StringComparison.Ordinal)))
        foreach(string genericHost in new[]{"core:wood","core:coal","core:wet_charcoal","core:gunpowder","core:sand"})
        {
            uint genericPorous=registry.GetRequiredRuntimeIndex(genericHost);
            uint species=liquidDefinition.RuntimeIndex;
            var g=new GridCell[w*h];g[index]=new(){IsActive=1,MaterialIndex=genericPorous,Mass=1,Temperature=30};
            g[index+1]=new(){IsActive=1,MaterialIndex=species,Mass=.20f,Temperature=45};Load(g);
            double e0=g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
            for(int t=0;t<24;t++){Tick(t);if(t%4==3)yield return r;}
            var a=Read();double e1=a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,table));
            bool pass=a[index].RetainedLiquidMaterialIndex==species&&a[index].FuelMass>0&&Math.Abs(e1-e0)/Math.Max(1,Math.Abs(e0))<1e-4;
            if(genericHost is "core:gunpowder" or "core:sand")pass &=Math.Abs(a[index].FuelMass-.20f)<1e-5;
            Record(new{test="generic",species,genericHost,retained=a[index].FuelMass,energyError=e1-e0,pass},pass);
        }
        // An occupied extra species cannot turn into another liquid or pass through it.
        foreach(bool porousNeighbour in new[]{false,true})
        {
            var g=new GridCell[w*h];g[index]=new(){IsActive=1,MaterialIndex=porous,Mass=1,Temperature=30,FuelMass=.1f,RetainedLiquidMaterialIndex=registry.GetRequiredRuntimeIndex("test:absorption_00")};
            g[index+1]=porousNeighbour?new(){IsActive=1,MaterialIndex=porous,Mass=1,Temperature=30,FuelMass=.1f,RetainedLiquidMaterialIndex=registry.GetRequiredRuntimeIndex("test:absorption_01")}:new(){IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex("test:absorption_01"),Mass=.2f,Temperature=30};
            Load(g);for(int t=0;t<24;t++){Tick(t);if(t%4==3)yield return r;}
            var a=Read();bool pass=a[index].FuelMass==.1f&&a[index].RetainedLiquidMaterialIndex==registry.GetRequiredRuntimeIndex("test:absorption_00")&&a[index+1].Mass==g[index+1].Mass&&a[index+1].FuelMass==g[index+1].FuelMass;
            Record(new{test="different-species",porousNeighbour,pass},pass);
        }
        var mixture=new GridCell[w*h];mixture[index]=new(){IsActive=1,MaterialIndex=porous,Mass=1,Temperature=30,MoistureMass=.24f};
        mixture[index+1]=new(){IsActive=1,MaterialIndex=oil,Mass=1,Temperature=30};Load(mixture);
        for(int t=0;t<40;t++){Tick(t);if(t%4==3)yield return r;}
        var saturated=Read()[index];float occupied=saturated.MoistureMass/table[porous].MoistureCapacity+saturated.FuelMass/table[porous].FuelCapacity;
        Record(new{test="shared-pores",occupied,pass=occupied<=1.0001f&&saturated.FuelMass>0},occupied<=1.0001f&&saturated.FuelMass>0);
        var mixed=new GridCell[w*h];mixed[index]=new(){IsActive=1,MaterialIndex=porous,Mass=1,Temperature=30,MoistureMass=.15f,FuelMass=.10f,RetainedLiquidMaterialIndex=oil};
        mixed[index+1]=new(){IsActive=1,MaterialIndex=porous,Mass=1,Temperature=30,MoistureMass=.05f,FuelMass=.02f,RetainedLiquidMaterialIndex=oil};
        Load(mixed);Tick(0);yield return r;var combined=Read()[index+1];
        bool both=combined.MoistureMass>.05f&&combined.FuelMass>.02f;
        Record(new{test="both-stocks",water=combined.MoistureMass,oil=combined.FuelMass,pass=both},both);
        r.Materials.Upload(r.Context,original);
        setFrameRate(60);
        foreach(string id in new[]{"core:gunpowder","core:sand"})foreach(var mode in Enum.GetValues<SimulationMode>())
        foreach(uint liquid in new[]{water,oil})
        {
            settings.Mode=mode;settings.RenderWithoutEffects=false;settings.HydraulicPressure=false;uint host=registry.GetRequiredRuntimeIndex(id),fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
            var g=new GridCell[w*h];for(int y=175;y<=195;y++){g[y*w+199]=new(){IsActive=1,MaterialIndex=fixture,Mass=1,Temperature=30};g[y*w+220]=g[y*w+199];}
            for(int x=199;x<=220;x++)g[195*w+x]=new(){IsActive=1,MaterialIndex=fixture,Mass=1,Temperature=30};
            for(int x=200;x<220;x++){
                for(int y=191;y<195;y++)g[y*w+x]=new(){IsActive=1,MaterialIndex=host,Mass=1,Temperature=30};
                g[190*w+x]=new(){IsActive=1,MaterialIndex=liquid,Mass=liquid==water?.5f:.4f,Temperature=30};
            }
            Load(g);string label=$"cup-{id.Split(':')[1]}-{mode}-{registry[liquid].Id.Split(':')[1]}";
            File.WriteAllBytes(Path.Combine(dir,label+"-initial.bin"),MemoryMarshal.AsBytes(g.AsSpan()).ToArray());
            double mass0=g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
            double energy0=g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,original));
            settings.Paused=false;var clock=System.Diagnostics.Stopwatch.StartNew();
            for(int frame=0;frame<480;frame++){
                coordinator.DispatchFrame(settings,[],1f/60);yield return r;
                if(frame%60==59){
                    coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
                    SimulationScreenshotWriter.Save(r,Path.Combine(dir,label+$"-{(frame+1)/60}.png"));
                }
            }
            settings.Paused=true;var a=Read();double retained=a.Sum(c=>liquid==water?(double)c.MoistureMass:c.FuelMass);
            double massError=Math.Abs(a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass)-mass0);
            double energyError=Math.Abs(a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,original))-energy0)/Math.Max(1,Math.Abs(energy0));
            bool deep=Enumerable.Range(200,20).Any(x=>Enumerable.Range(192,3).Any(y=>liquid==water?a[y*w+x].MoistureMass>0:a[y*w+x].FuelMass>0));
            bool pass=retained>=(liquid==water?10:8)*.75&&massError<1e-4&&energyError<1e-4&&deep;
            Record(new{test="cup",label,wallSeconds=clock.Elapsed.TotalSeconds,retained,massError,energyError,deep,pass},pass);
        }
        Complete();
    }
}
