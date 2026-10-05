using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
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
internal static class FilterRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,MaterialRegistry registry,SimulationSettings settings,Action<int> setFps)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/filters";Directory.CreateDirectory(dir);
        settings.ApplyScale(.25f);settings.Paused=true;settings.OpenBoundaries=false;settings.AirSimulation=false;
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),steam=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
            oil=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil),fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture),
            sand=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand),ice=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice);
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,MaterialIndex=water}],0);yield return r;
        int w=r.Width,h=r.Height,n=w*h,failures=0;var serializer=new SimulationStateSerializer();var physical=registry.CreateGpuTable();
        var isolated=physical.ToArray();for(int i=0;i<isolated.Length;i++){isolated[i].ThermalConductivity=0;isolated[i].BurnRate=0;isolated[i].AmbientCoolingRate=0;}
        var results=new List<object>();
        void Record(object value,bool ok){results.Add(value);if(!ok)failures++;Console.WriteLine("PHYXEL_FILTER "+JsonSerializer.Serialize(value));
            File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{failures,results},new JsonSerializerOptions{WriteIndented=true}));}
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Observe()=>coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
        GridCell Cell(uint id)=>new(){IsActive=1,MaterialIndex=id,Mass=1,Temperature=id==steam?130:id==ice?-5:id==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire)?900:30,Lifetime=id==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire)?1000:physical[id].MaximumLifetime,BodyId=id==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire)?0x80000000u:0,RestFrames=id==fixture?2u:0};
        void Load(GridCell[] g,uint[] map){serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray(),Filters:MemoryMarshal.AsBytes(map.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,settings.HydraulicPressure);r.Materials.Upload(r.Context,isolated);}
        (GridCell[] grid,uint[] map) Channel(uint id,uint rule,int thickness){
            var g=new GridCell[n];var map=new uint[n];
            for(int y=60;y<=220;y++){g[y*w+200]=Cell(fixture);g[y*w+239]=Cell(fixture);}
            for(int x=200;x<=239;x++){g[60*w+x]=Cell(fixture);g[220*w+x]=Cell(fixture);}
            for(int y=140;y<140+thickness;y++)for(int x=201;x<239;x++)map[y*w+x]=rule;
            bool gas=physical[id].SimulationKind==(uint)MaterialSimulationKind.Gas && physical[id].GasBuoyancy<0;
            for(int y=gas?151:125;y<(gas?156:130);y++)for(int x=211;x<229;x++)g[y*w+x]=Cell(id);
            return(g,map);
        }
        double Mass(GridCell[] g,uint id)=>g.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
        double Heat(GridCell[] g,uint id)=>g.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        double Beyond(GridCell[] g,uint id,int thickness)=>Enumerable.Range(0,n).Where(i=>g[i].IsActive!=0&&g[i].MaterialIndex==id&&
            (physical[id].SimulationKind==(uint)MaterialSimulationKind.Gas&&physical[id].GasBuoyancy<0?i/w<140:i/w>=140+thickness)).Sum(i=>(double)g[i].Mass);
        var scenarios=new List<(FilterSelection preset,string id,bool allowed,int thickness)>();
        foreach(int thickness in new[]{1,2,4}){
            scenarios.Add((FilterSelection.Steam,CoreMaterialIds.Steam,true,thickness));
            foreach(string id in new[]{CoreMaterialIds.Water,CoreMaterialIds.Oil,CoreMaterialIds.Smoke,CoreMaterialIds.Co2,CoreMaterialIds.Fire,CoreMaterialIds.Sand})scenarios.Add((FilterSelection.Steam,id,false,thickness));
        }
        scenarios.AddRange(new[]{(FilterSelection.Water,CoreMaterialIds.Water,true,2),(FilterSelection.Water,CoreMaterialIds.Oil,false,2),
            (FilterSelection.Oil,CoreMaterialIds.Oil,true,2),(FilterSelection.Oil,CoreMaterialIds.Water,false,2),
            (FilterSelection.Gases,CoreMaterialIds.Co2,true,2),(FilterSelection.Gases,CoreMaterialIds.Water,false,2),
            (FilterSelection.Liquids,CoreMaterialIds.Oil,true,2),(FilterSelection.Liquids,CoreMaterialIds.Steam,false,2),
            (FilterSelection.Powders,CoreMaterialIds.Sand,true,2),(FilterSelection.Powders,CoreMaterialIds.Water,false,2),
            (FilterSelection.SelectedMaterial,"test:filter_liquid",true,2),(FilterSelection.SelectedMaterial,CoreMaterialIds.Water,false,2)});
        setFps(1000);
        foreach(var mode in Enum.GetValues<SimulationMode>())foreach(var scenario in scenarios){
            settings.Mode=mode;settings.HydraulicPressure=false;settings.SolidGravity=false;
            uint id=registry.GetRequiredRuntimeIndex(scenario.id),rule=FilterRules.Select(scenario.preset,registry,registry.GetRequiredRuntimeIndex("test:filter_liquid"));
            var (g,map)=Channel(id,rule,scenario.thickness);Load(g,map);settings.Paused=false;
            for(int f=0;f<120;f++){coordinator.DispatchFrame(settings,[],1f/60);if(f%16==0)Observe();yield return r;}
            var after=Read();double beyond=Beyond(after,id,scenario.thickness),massError=Math.Abs(Mass(after,id)-Mass(g,id))/Math.Max(1,Mass(g,id)),
                heatError=Math.Abs(Heat(after,id)-Heat(g,id))/Math.Max(1,Math.Abs(Heat(g,id)));
            // FIRE is a heat tracer with lifecycle; its transport blocking is audited separately from conserved gases.
            bool fire=id==registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
            bool ok=(scenario.allowed?beyond>.01:beyond==0)&&massError<1e-4&&(fire||heatError<1e-4)&&r.FilterMap.SequenceEqual(map);
            Record(new{test="channel",mode=mode.ToString(),preset=scenario.preset.ToString(),scenario.id,scenario.allowed,scenario.thickness,beyond,massError,heatError,ok},ok);
        }
        foreach(bool filtered in new[]{false,true}){
            settings.SolidGravity=true;var(g,map)=Channel(ice,filtered?steam+1:0,1);Load(g,map);settings.Paused=false;
            for(int f=0;f<90;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
            var after=Read();double beyond=Beyond(after,ice,1),error=Math.Abs(Mass(after,ice)-Mass(g,ice));bool ok=(filtered?beyond==0:beyond>0)&&error<1e-4;
            Record(new{test="solid-body",filtered,beyond,error,ok},ok);
        }
        settings.SolidGravity=false;
        // The only open outlet is a diagonal; one of its two faces has a steam filter.
        foreach(bool filtered in new[]{false,true}){
            var g=new GridCell[n];var map=new uint[n];
            for(int y=128;y<=133;y++)for(int x=218;x<=222;x++)g[y*w+x]=Cell(fixture);
            g[130*w+220]=Cell(water);g[130*w+221]=default;g[131*w+221]=default;g[132*w+221]=default;
            if(filtered)map[130*w+221]=steam+1;Load(g,map);settings.Paused=false;
            for(int f=0;f<30;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
            var after=Read();double escaped=after[131*w+221].Mass+after[132*w+221].Mass;
            bool ok=filtered?after[130*w+220].MaterialIndex==water&&after[130*w+220].Mass==1:escaped>0;
            Record(new{test="diagonal-outlet",filtered,escaped,ok},ok);
        }
        // Presentation rates are actual Update/Draw pacing, not a loop labelled FPS.
        foreach(int fps in new[]{30,60,100})foreach(uint id in new[]{steam,water}){
            settings.Mode=SimulationMode.Simulation;setFps(fps);settings.HydraulicPressure=true;
            var(g,map)=Channel(id,steam+1,2);Load(g,map);settings.Paused=false;
            var timer=Stopwatch.StartNew();
            for(int f=0;f<fps*2;f++){coordinator.DispatchFrame(settings,[],1f/fps);Observe();yield return r;}
            double wallSeconds=timer.Elapsed.TotalSeconds,actualFps=fps*2/wallSeconds;
            double beyond=Beyond(Read(),id,2);bool ok=id==steam?beyond>.01:beyond==0;
            Record(new{test="paced",fps,id,beyond,wallSeconds,actualFps,ok},ok);
        }
        setFps(1000);settings.Paused=true;settings.HydraulicPressure=false;
        // Layer painting/removal uses the same public dispatch path as the UI while paused.
        var cells=new GridCell[n];cells[120*w+220]=Cell(water);Load(cells,new uint[n]);
        coordinator.DispatchFrame(settings,[],0);yield return r;
        string unfilteredPath=Path.Combine(dir,"unfiltered.png");SimulationScreenshotWriter.Save(r,unfilteredPath);
        int unfilteredBlue;using(var image=new Bitmap(unfilteredPath))unfilteredBlue=image.GetPixel(218,120).B;
        var command=new BrushDrawCommand{X=220,Y=120,EndX=220,EndY=120,Radius=3,Density=1,Mode=BrushCommandMode.Filter,Reserved=steam+1};
        coordinator.DispatchFrame(settings,[command],0);yield return r;
        Record(new{test="paint-paused",ok=r.FilterCount>0&&Read()[120*w+220].Mass==1},r.FilterCount>0&&Read()[120*w+220].Mass==1);
        foreach(bool effects in new[]{true,false}){settings.RenderWithoutEffects=!effects;coordinator.DispatchFrame(settings,[],0);yield return r;
            string path=Path.Combine(dir,$"filter-{effects}.png");SimulationScreenshotWriter.Save(r,path);using var bitmap=new Bitmap(path);var color=bitmap.GetPixel(218,120);
            bool ok=color.B>unfilteredBlue+10;Record(new{test="visible",effects,unfilteredBlue,color.R,color.G,color.B,ok},ok);}
        command.Reserved=0;coordinator.DispatchFrame(settings,[command],0);yield return r;
        Record(new{test="right-remove",ok=r.FilterCount==0&&Read()[120*w+220].Mass==1},r.FilterCount==0&&Read()[120*w+220].Mass==1);
        command.Reserved=steam+1;coordinator.DispatchFrame(settings,[command],0);command.Mode=BrushCommandMode.Erase;coordinator.DispatchFrame(settings,[command],0);yield return r;
        Record(new{test="eraser",ok=r.FilterCount==0&&Read()[120*w+220].IsActive==0},r.FilterCount==0&&Read()[120*w+220].IsActive==0);
        command.Mode=BrushCommandMode.Filter;coordinator.DispatchFrame(settings,[command],0);yield return r;
        serializer.BeginWorldCapture(r);SimulationWorldSnapshot? captured=null;
        for(int f=0;f<120;f++){r.Context.Flush();if(serializer.TryCompleteWorldCapture(r,out captured))break;yield return r;}
        bool capturedOk=captured?.Filters is {Length:>0} && MemoryMarshal.Cast<byte,uint>(captured.Filters).SequenceEqual(r.FilterMap);
        Record(new{test="gpu-capture-overlay",ok=capturedOk},capturedOk);
        if(captured is not null){
            string path=Path.Combine(dir,"filter-only.json");var saving=serializer.SaveAsync(path,settings,(ushort)steam,captured,registry);
            while(!saving.IsCompleted)yield return r;saving.GetAwaiter().GetResult();var loading=serializer.LoadAsync(path,registry);
            while(!loading.IsCompleted)yield return r;var restored=loading.GetAwaiter().GetResult();
            serializer.ApplyWorldSnapshot(r,restored!.World!);coordinator.RestoreWorldActivity(r,false,false,false);coordinator.DispatchFrame(settings,[],0);yield return r;
            bool restoredOk=r.FilterCount>0&&r.FilterMap[120*w+220]==steam+1&&Read().All(c=>c.IsActive==0);
            Record(new{test="filter-only-reload",ok=restoredOk},restoredOk);
        }
        coordinator.ClearCurrentWorld(settings);r=coordinator.DispatchFrame(settings,[],0);yield return r;
        Record(new{test="clear",ok=r.FilterCount==0},r.FilterCount==0);
        r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,MaterialIndex=water}],0);yield return r;
        // An existing forbidden packet must remain intact, then resume after removal.
        var (trapped,mapTrapped)=Channel(water,steam+1,4);for(int y=125;y<130;y++)for(int x=211;x<229;x++)trapped[y*w+x]=default;
        trapped[141*w+220]=Cell(water);Load(trapped,mapTrapped);settings.Paused=false;
        for(int f=0;f<60;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
        bool trappedOk=Read()[141*w+220].MaterialIndex==water&&Mass(Read(),water)==1;
        Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();coordinator.RestoreWorldActivity(r,true,true,false);
        for(int f=0;f<60;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
        bool resume=Beyond(Read(),water,4)>.9;Record(new{test="trapped-resume",trappedOk,resume,ok=trappedOk&&resume},trappedOk&&resume);
        // Finite O2 gets a separate map; fine barriers must not be bridged by coarse air cells.
        foreach(bool gasFilter in new[]{false,true}){
            var(g,map)=Channel(water,gasFilter?FilterRules.Gas|FilterRules.AmbientAir:steam+1,1);Array.Clear(g);
            for(int y=60;y<=220;y++){g[y*w+200]=Cell(fixture);g[y*w+239]=Cell(fixture);}
            for(int x=200;x<=239;x++){g[60*w+x]=Cell(fixture);g[220*w+x]=Cell(fixture);}
            Load(g,map);float[] oxygen=new float[n];for(int y=61;y<140;y++)for(int x=201;x<239;x++)oxygen[y*w+x]=1;
            r.Context.UpdateSubresource(oxygen,r.Oxidizer.ReadBuffer);
            for(int t=0;t<240;t++){SimulationDispatchCoordinator.DispatchOxidizer(r,1f/60,false,false,false);if(t%8==7)yield return r;}
            var after=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.Oxidizer.ReadBuffer)).ToArray();
            double crossed=Enumerable.Range(0,n).Where(i=>i/w>140).Sum(i=>(double)after[i]),error=Math.Abs(after.Sum(x=>(double)x)-oxygen.Sum(x=>(double)x));
            bool ok=(gasFilter?crossed>0:crossed==0)&&error<.01;Record(new{test="oxygen",gasFilter,crossed,error,ok},ok);
        }
        // Connected liquid columns require a remote pressure route; a fine filter cuts that route.
        foreach(bool allowed in new[]{false,true}){
            settings.Mode=SimulationMode.Simulation;settings.HydraulicPressure=true;settings.Paused=false;
            var g=new GridCell[n];var map=new uint[n];
            for(int y=100;y<=220;y++){g[y*w+200]=Cell(fixture);g[y*w+260]=Cell(fixture);}
            for(int x=200;x<=260;x++){g[100*w+x]=Cell(fixture);g[220*w+x]=Cell(fixture);}
            for(int x=201;x<260;x++)for(int y=x<=230?150:180;y<220;y++)g[y*w+x]=Cell(water);
            for(int y=101;y<220;y++)map[y*w+230]=allowed?water+1:steam+1;Load(g,map);
            double RightMass(GridCell[] cells)=>Enumerable.Range(0,n).Where(i=>i%w>230&&cells[i].IsActive!=0&&cells[i].MaterialIndex==water).Sum(i=>(double)cells[i].Mass);
            double before=RightMass(g);
            for(int f=0;f<240;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
            var after=Read();double change=RightMass(after)-before,error=Math.Abs(after.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass)-g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass));
            bool ok=(allowed?change>0:change==0)&&error<1e-4;
            Record(new{test="hydraulic-divider",allowed,change,error,ok},ok);
        }
        settings.HydraulicPressure=false;
        int pore=120*w+220;uint wood=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood);
        void MoistureTick(uint tick){
            var c=new ContactTransitionConstants{Width=(uint)w,Height=(uint)h,DeltaTime=.05f,TickIndex=tick};var ctx=r.Context;
            ctx.UpdateSubresource(ref c,r.ContactTransitionConstants);ctx.ComputeShader.Set(r.MoistureShader);
            ctx.ComputeShader.SetConstantBuffer(0,r.ContactTransitionConstants);ctx.ComputeShader.SetShaderResource(0,r.Materials.View);
            ctx.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CellMaterials.UnorderedView,r.GasMotion.UnorderedView,r.ContactSummary.UnorderedView);
            ctx.Dispatch((w+15)/16,(h+15)/16,1);ctx.ComputeShader.SetShaderResource(0,null);
            for(int slot=0;slot<4;slot++)ctx.ComputeShader.SetUnorderedAccessView(slot,null);ctx.ComputeShader.Set(null);
        }
        double TotalMass(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass+c.MoistureMass+c.FuelMass);
        double TotalHeat(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        settings.Paused=true;
        foreach(bool allowed in new[]{false,true})foreach(uint species in new[]{water,oil})foreach(bool porousDonor in new[]{false,true}){
            var g=new GridCell[n];var map=new uint[n];g[pore]=Cell(wood);g[pore+1]=porousDonor?Cell(wood):Cell(species);
            if(porousDonor){if(species==water)g[pore+1].MoistureMass=.1f;else{g[pore+1].FuelMass=.1f;g[pore+1].RetainedLiquidMaterialIndex=oil;}}
            else g[pore+1].Mass=.1f;
            map[pore]=allowed?species+1:steam+1;Load(g,map);
            for(uint t=0;t<16;t++){MoistureTick(t);if(t%4==3)yield return r;}
            var after=Read();float absorbed=species==water?after[pore].MoistureMass:after[pore].FuelMass;
            double massError=Math.Abs(TotalMass(after)-TotalMass(g)),heatError=Math.Abs(TotalHeat(after)-TotalHeat(g))/Math.Max(1,Math.Abs(TotalHeat(g)));
            bool ok=(allowed?absorbed>0:absorbed==0)&&massError<1e-5&&heatError<1e-4;
            Record(new{test="pore-transfer",allowed,species,porousDonor,absorbed,massError,heatError,ok},ok);
        }
        foreach(bool allowed in new[]{false,true}){
            var g=new GridCell[n];var map=new uint[n];g[pore]=Cell(wood);g[pore].Temperature=100;g[pore].MoistureMass=.1f;
            g[pore].MoistureEnergy=.1f*physical[water].TransitionAboveLatentHeat;
            foreach(int offset in new[]{-1,-w,w})g[pore+offset]=Cell(fixture);map[pore+1]=allowed?steam+1:water+1;Load(g,map);
            for(uint t=0;t<16;t++){MoistureTick(t);if(t%4==3)yield return r;}
            var after=Read();double emitted=Mass(after,steam),error=Math.Abs(TotalMass(after)-TotalMass(g));
            bool ok=(allowed?emitted>0:emitted==0)&&error<1e-5;
            Record(new{test="pore-steam-release",allowed,emitted,error,ok},ok);
        }
        foreach(bool allowed in new[]{false,true}){
            var g=new GridCell[n];var map=new uint[n];for(int y=120;y<=128;y++)g[y*w+220]=Cell(wood);g[128*w+220].MoistureMass=.2f;
            map[124*w+220]=allowed?water+1:steam+1;Load(g,map);
            for(uint t=0;t<48;t++){MoistureTick(t|0x80000000u);MoistureTick(t);if(t%4==3)yield return r;}
            var after=Read();float beyond=Enumerable.Range(120,4).Sum(y=>after[y*w+220].MoistureMass);
            bool ok=(allowed?beyond>0:beyond==0)&&Math.Abs(TotalMass(after)-TotalMass(g))<1e-5;
            Record(new{test="capillary-eight-cell",allowed,beyond,ok},ok);
        }
        foreach(bool gasFilter in new[]{false,true}){
            var(g,map)=Channel(water,gasFilter?FilterRules.Gas|FilterRules.AmbientAir:steam+1,1);
            for(int i=0;i<n;i++)if(g[i].MaterialIndex==water)g[i]=default;Load(g,map);
            settings.Paused=false;settings.AirSimulation=true;
            for(int f=0;f<20;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
            var links=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.AirFlowLinks.Buffer)).ToArray();
            uint down=links[34*r.AirWidth+55]&(1u<<7);bool ok=gasFilter?down!=0:down==0;
            Record(new{test="coarse-air-link",gasFilter,down,ok},ok);settings.AirSimulation=false;
        }
        // Direct conduction remains possible through a particle filter.
        settings.Paused=false;uint metal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        var heated=new GridCell[n];var heatMap=new uint[n];heated[pore]=Cell(metal);heated[pore].Temperature=400;heated[pore+1]=Cell(metal);
        heatMap[pore]=water+1;Load(heated,heatMap);r.Materials.Upload(r.Context,physical);
        for(int f=0;f<30;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
        var cooled=Read();bool heatPass=cooled[pore].Temperature<400&&cooled[pore+1].Temperature>30;
        Record(new{test="heat-permitted",hot=cooled[pore].Temperature,cold=cooled[pore+1].Temperature,ok=heatPass},heatPass);
        Console.WriteLine($"PHYXEL_FILTER_COMPLETE cases={results.Count} failures={failures}");
        if(failures>0)throw new InvalidOperationException("Filter checks failed.");
    }
}
