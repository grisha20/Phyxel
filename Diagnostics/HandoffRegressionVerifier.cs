using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using Point = Microsoft.Xna.Framework.Point;

namespace Phyxel.Diagnostics;

// Runs through Update/Draw. Every yield presents a frame and pumps messages.
// Reconstructed geometry is deliberately separate from the user's missing save.
internal static class HandoffRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings, GpuTemperatureProbe temperatureProbe,
        Action<string> status, Action<string> captureUi, Action<int> setFrameRate)
    {
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/handoff";
        Directory.CreateDirectory(dir);
        string cases = Environment.GetEnvironmentVariable("PHYXEL_HANDOFF_CASES") ?? "gas,parcel,probe,oil,fuel,furnace,performance";
        bool baseline = Environment.GetEnvironmentVariable("PHYXEL_HANDOFF_BASELINE") == "1";
        int[] rates = Environment.GetEnvironmentVariable("PHYXEL_HANDOFF_MATRIX") == "1" ? [30,60,100] : [60];
        var results = new List<object>();
        int failures = 0;
        var materials = registry.CreateGpuTable();
        uint oil = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil), water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal), coal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),
            fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire), co2 = registry.GetRequiredRuntimeIndex("core:co2"),
            fixture = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        settings.ApplyScale(.25f); settings.Paused = true;
        var r = coordinator.DispatchFrame(settings, [new() { X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,MaterialIndex=oil }], 0);
        yield return r;
        int w = r.Width, h = r.Height;
        var serializer = new SimulationStateSerializer();
        var saves = new List<Task>();
        GridCell Cell(uint id, float t=30) => new() { IsActive=1,MaterialIndex=id,Mass=materials[id].SimulationKind==2 ? materials[id].Density : 1,
            Temperature=t,Lifetime=id==fire ? 2 : 0,RestFrames=id==fixture||id==metal ? 2u : 0u };
        GridCell[] Read() => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Load(GridCell[] g, string label, MaterialProperties[] table)
        {
            settings.Paused=true;
            var snapshot = new SimulationWorldSnapshot(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray());
            serializer.ApplyWorldSnapshot(r,snapshot);
            coordinator.RestoreWorldActivity(r,true,true,settings.HydraulicPressure);
            r.Materials.Upload(r.Context,table);
            File.WriteAllBytes(Path.Combine(dir,label+"-initial.bin"),snapshot.Grid);
            // Save continuations run on the game context; keep Update/Draw pumping.
            saves.Add(serializer.SaveAsync(Path.Combine(dir,label+".json"),settings,(ushort)oil,snapshot,registry));
            status(label);
            settings.Paused=false;
        }
        void Observe() => coordinator.ObserveStatistics(MemoryMarshal.Cast<byte,SimulationStatistics>(
            AirInventoryRegressionVerifier.Read(r,r.Statistics.ReadBuffer))[0]);
        void Result(object value, bool pass)
        {
            results.Add(value); if(!pass) failures++;
            var json=new JsonSerializerOptions {WriteIndented=true,IncludeFields=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals};
            File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new {w,h,scale=settings.Scale,failures,results},json));
            Console.WriteLine("PHYXEL_HANDOFF "+JsonSerializer.Serialize(value,new JsonSerializerOptions {IncludeFields=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
        }
        var isolated=materials.ToArray();
        for(int i=0;i<isolated.Length;i++) isolated[i].ThermalConductivity=0;
        setFrameRate(60);
        if(cases.Contains("parcel"))
        foreach(float parcelMass in new[]{1f,.0001f,1e-10f,1e-22f})
        {
            settings.Mode=SimulationMode.Sandbox;settings.HydraulicPressure=false;settings.AirSimulation=true;settings.OpenBoundaries=false;
            var still=isolated.ToArray();still[co2].GasDiffusion=0;still[co2].GasBuoyancy=0;still[co2].MotionAdvection=0;
            var g=new GridCell[w*h];g[130*w+240]=Cell(co2,404);g[130*w+240].Mass=parcelMass;
            Load(g,$"parcel-{parcelMass}",still);bool bounded=true;
            double Energy(GridCell[] a)
            {
                double energy=a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,still));
                var air=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer));
                for(int i=0;i<air.Length;i+=2)energy+=(double)air[i]-273.15*air[i+1];
                return energy;
            }
            double energy0=Energy(g);
            for(int f=0;f<300;f++)
            {
                coordinator.DispatchFrame(settings,[],1f/60);
                if(f%10==0)bounded &= Read().Where(c=>c.IsActive!=0).All(c=>float.IsFinite(c.Temperature)&&c.Temperature>=19.99&&c.Temperature<=404.01);
                yield return r;
            }
            var end=Read();double mass=end.Where(c=>c.IsActive!=0&&c.MaterialIndex==co2).Sum(c=>(double)c.Mass);
            double energyError=Math.Abs(Energy(end)-energy0)/Math.Max(1,Math.Abs(energy0));
            bool pass=bounded&&Math.Abs(mass-parcelMass)<=parcelMass*1e-5&&energyError<1e-5;
            Result(new {test="parcel",parcelMass,mass,bounded,energyError,pass},pass);
        }
        if(cases.Contains("gas"))
        {
            setFrameRate(100);
            settings.Mode=SimulationMode.Sandbox; settings.HydraulicPressure=false; settings.AirSimulation=false; settings.OpenBoundaries=false;
            var still=isolated.ToArray();still[co2].GasDiffusion=0;still[co2].GasBuoyancy=0;still[co2].MotionAdvection=0;
            var g=new GridCell[w*h];for(int y=90;y<130;y++)for(int x=200;x<240;x++)g[y*w+x]=Cell(co2);
            Load(g,"gas",still); var brightness=new List<double>();
            for(int f=0;f<140;f++)
            {
                coordinator.DispatchFrame(settings,[],.01f);
                if(f>=100)
                {
                    string path=Path.Combine(dir,$"gas-{f}.png"); SimulationScreenshotWriter.Save(r,path);
                    using var bitmap=new Bitmap(path);double sum=0;
                    for(int y=98;y<122;y++)for(int x=208;x<232;x++)sum+=bitmap.GetPixel(x,y).R;
                    brightness.Add(sum/(24*24));
                }
                yield return r;
            }
            var end=Read();double mass=end.Where(c=>c.IsActive!=0&&c.MaterialIndex==co2).Sum(c=>(double)c.Mass);
            double swing=brightness.Max()-brightness.Min();bool pass=swing<=1 && Math.Abs(mass-1600)<1e-4;
            Result(new {test="gas",swing,mass,brightness,pass},pass);
        }
        if(cases.Contains("probe"))
        {
            setFrameRate(60);
            settings.Mode=SimulationMode.Sandbox; settings.HydraulicPressure=false;settings.AirSimulation=false;settings.OpenBoundaries=false;
            var g=new GridCell[w*h];g[180*w+400]=Cell(metal,73);g[180*w+401]=Cell(oil,40);
            Load(g,"probe",isolated);status("");settings.Paused=true;var probe=temperatureProbe;probe.Reset();
            var rows=new List<object>();bool pass=true;
            for(int phase=0;phase<4;phase++)
            {
                Point point=new(phase==1 ? 401 : 400,180);
                if(phase==2){g[180*w+400].Temperature=123;serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()));}
                settings.Paused=phase!=3;
                for(int f=0;f<35;f++) {coordinator.DispatchFrame(settings,[],.01f);probe.Update(r,point,.01f);yield return r;}
                var value=probe.Latest;uint expected=phase==1?oil:metal;float temperature=phase==1?40:phase<2?73:123;
                bool ok=value is {} v&&v.IsActive!=0&&v.MaterialIndex==expected&&Math.Abs(v.Temperature-temperature)<.01;
                pass &= ok;rows.Add(new {phase,expected,temperature,value,ok});
                captureUi(Path.Combine(dir,$"probe-ui-{phase}.png"));yield return r;
            }
            probe.Update(r,null,.01f);pass &= probe.Latest is null;
            // Exercise the resource resize and cells outside the original world.
            Point expandedSize=new(w+80,h+40), expandedPoint=new(w+50,h+20);
            var expanded=CanvasWorldExpansion.Expand(new(w,h,MemoryMarshal.AsBytes(g.AsSpan()).ToArray()),expandedSize);
            GridCell expandedCell=Cell(metal,161);
            MemoryMarshal.Write(expanded.Grid.AsSpan((expandedPoint.Y*expandedSize.X+expandedPoint.X)*Marshal.SizeOf<GridCell>()),in expandedCell);
            settings.Paused=true;settings.Width=expandedSize.X;settings.Height=expandedSize.Y;
            r=coordinator.DispatchFrame(settings,[],0);
            serializer.ApplyWorldSnapshot(r,expanded);r.Materials.Upload(r.Context,isolated);
            coordinator.RestoreWorldActivity(r,true,true,false);
            coordinator.DispatchFrame(settings,[],0);
            for(int f=0;f<35;f++){probe.Update(r,expandedPoint,.01f);yield return r;}
            bool expandedOk=probe.Latest is {IsActive:1} ep&&ep.MaterialIndex==metal&&Math.Abs(ep.Temperature-161)<.01;
            pass &= expandedOk;rows.Add(new {expandedSize,expandedPoint,value=probe.Latest,ok=expandedOk});
            for(int settle=0;settle<3;settle++)yield return r;
            captureUi(Path.Combine(dir,"probe-ui-expanded.png"));yield return r;
            Result(new {test="probe",rows,pass},pass);
            settings.Width=w;settings.Height=h;
            r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,MaterialIndex=metal}],0);
            yield return r;
        }
        if(cases.Contains("oil"))
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(int fps in rates)
        foreach(bool hydraulic in new[]{false,true})
        {
            settings.Mode=mode;settings.HydraulicPressure=hydraulic;settings.AirSimulation=false;settings.OpenBoundaries=false;
            setFrameRate(fps);
            string label=$"oil-{mode}-{fps}-{hydraulic}";var g=new GridCell[w*h];
            for(int y=130;y<=205;y++)for(int x=150;x<=155;x++){g[y*w+x]=Cell(fixture);g[y*w+x+140]=Cell(fixture);}
            for(int x=150;x<=295;x++)for(int y=200;y<=205;y++)g[y*w+x]=Cell(fixture);
            for(int x=156;x<290;x++)for(int y=200-Math.Max(4,64-(int)(Math.Abs(x-224)*2.3));y<200;y++)g[y*w+x]=Cell(oil);
            double mass0=g.Where(c=>c.MaterialIndex==oil).Sum(c=>(double)c.Mass);
            double energy0=g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,isolated));
            Load(g,label,isolated);var clock=Stopwatch.StartNew();var rows=new List<object>();int gap=0;double error=0,energyError=0;int gapAtTwo=0;
            for(int f=0;f<fps*8;f++)
            {
                coordinator.DispatchFrame(settings,[],1f/fps);
                if((f+1)%fps==0)
                {
                    Observe();var a=Read();var tops=Enumerable.Range(162,122).Select(x=>Enumerable.Range(0,h).FirstOrDefault(y=>a[y*w+x].IsActive!=0&&a[y*w+x].MaterialIndex==oil,h)).ToArray();
                    gap=tops.Max()-tops.Min();error=Math.Abs(a.Where(c=>c.MaterialIndex==oil&&c.IsActive!=0).Sum(c=>(double)c.Mass)-mass0);
                    if(f+1==fps*2)gapAtTwo=gap;
                    double energy=a.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,isolated));
                    energyError=Math.Abs(energy-energy0)/Math.Max(1,Math.Abs(energy0));
                    rows.Add(new {seconds=(f+1)/fps,gap,error,energyError}); SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{label}-{(f+1)/fps}.png"));
                }
                yield return r;
            }
            bool pass=gapAtTwo<=3&&gap<=3&&error<1e-4&&energyError<1e-5;Result(new {test="oil",label,wallSeconds=clock.Elapsed.TotalSeconds,gapAtTwo,gap,error,energyError,rows,pass},pass);
        }
        setFrameRate(60);
        if(cases.Contains("fuel"))
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(bool impregnated in new[]{false,true})
        {
            settings.Mode=mode;settings.HydraulicPressure=false;settings.AirSimulation=false;settings.OpenBoundaries=false;
            string label=$"fuel-{mode}-{impregnated}";var g=new GridCell[w*h];g[180*w+240]=Cell(coal,130);
            g[180*w+240].Lifetime=1;g[180*w+240].FuelMass=impregnated?.01f:0;
            Load(g,label,isolated);
            for(int f=0;f<60;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
            var end=Read();var c=end.FirstOrDefault(c=>c.IsActive!=0&&c.MaterialIndex==coal);double dryMass=end.Where(c=>c.IsActive!=0&&c.MaterialIndex==coal).Sum(c=>(double)c.Mass);
            bool pass=dryMass<.99;
            Result(new {test="fuel",label,dryMass,c.Temperature,c.FuelMass,c.Lifetime,pass},pass);
        }
        if(cases.Contains("furnace"))
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})
        foreach(bool feed in new[]{false,true})
        {
            if(cases.Contains("furnace-feed") && !feed)continue;
            settings.Mode=mode;settings.HydraulicPressure=true;settings.AirSimulation=true;settings.OpenBoundaries=true;
            string label=$"furnace-{mode}-{feed}";var g=new GridCell[w*h];
            void Rect(int x0,int y0,int x1,int y1,uint id,float t=30){for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)g[y*w+x]=Cell(id,t);}
            // Video's left chimney, tank over triangular coal, bottom inlet at right.
            Rect(100,25,107,253,metal);Rect(138,25,146,168,metal);Rect(100,250,370,258,metal);
            Rect(138,101,350,109,metal,100);Rect(345,103,353,227,metal,100);Rect(140,165,350,173,metal,180);
            for(int y=185;y<250;y++)for(int x=190+(250-y);x<=320-(250-y);x++){g[y*w+x]=Cell(coal,500);g[y*w+x].Lifetime=1;}
            float feedTemperature=float.TryParse(Environment.GetEnvironmentVariable("PHYXEL_HANDOFF_OIL_TEMPERATURE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float configuredTemperature) && float.IsFinite(configuredTemperature) ? configuredTemperature : 30;
            if(feed) Rect(147,113,343,164,oil,feedTemperature);
            Load(g,label,materials);var rows=new List<object>();double mass0=g.Where(c=>c.MaterialIndex==coal).Sum(c=>(double)c.Mass);
            var furnaceProbe=temperatureProbe;furnaceProbe.Reset();int probeFailures=0;bool finiteSamples=true;
            for(int f=0;f<60*20;f++)
            {
                BrushDrawCommand[] brush=f==120?[new(){X=228,Y=164,EndX=228,EndY=174,Radius=2,Density=1,Mode=BrushCommandMode.Erase,Shape=BrushCommandShape.Segment}]:[];
                coordinator.DispatchFrame(settings,brush,1f/60);
                furnaceProbe.Update(r,new Point(103,200),1f/60);
                if(f>180 && (furnaceProbe.Latest is not {IsActive:1} p || !float.IsFinite(p.Temperature)))probeFailures++;
                if((f+1)%60==0)
                {
                    Observe();var a=Read();double mass=a.Where(c=>c.IsActive!=0&&c.MaterialIndex==coal).Sum(c=>(double)c.Mass);
                    File.WriteAllBytes(Path.Combine(dir,$"{label}-{(f+1)/60}.bin"),MemoryMarshal.AsBytes(a.AsSpan()).ToArray());
                    double stock=a.Sum(c=>(double)c.FuelMass);double temp=a.Where(c=>c.IsActive!=0&&c.MaterialIndex==coal).Select(c=>(double)c.Temperature).DefaultIfEmpty(0).Average();
                    int flames=a.Count(c=>c.IsActive!=0&&c.MaterialIndex==fire);
                    int chimney=Enumerable.Range(0,a.Length).Count(i=>i%w>107&&i%w<138&&i/w<101&&a[i].IsActive!=0&&materials[a[i].MaterialIndex].SimulationKind==5);
                    double liquid=a.Where(c=>c.IsActive!=0&&c.MaterialIndex==oil).Sum(c=>(double)c.Mass);
                    int nonfinite=a.Count(c=>c.IsActive!=0&&(!float.IsFinite(c.Temperature)||!float.IsFinite(c.Mass)||!float.IsFinite(c.FuelMass)));
                    finiteSamples &= nonfinite==0;
                    rows.Add(new {seconds=(f+1)/60,mass,stock,temp,flames,chimney,liquid,nonfinite,probe=furnaceProbe.Latest});
                    if((f+1)%300==0){SimulationScreenshotWriter.Save(r,Path.Combine(dir,$"{label}-{(f+1)/60}.png"));captureUi(Path.Combine(dir,$"{label}-ui-{(f+1)/60}.png"));}
                }
                yield return r;
            }
            var end=Read();double remaining=end.Where(c=>c.IsActive!=0&&c.MaterialIndex==coal).Sum(c=>(double)c.Mass);
            bool finite=finiteSamples&&end.All(c=>c.IsActive==0||float.IsFinite(c.Temperature));
            bool pass=remaining<mass0*.99 && probeFailures==0 && finite;Result(new {test="furnace",label,feedTemperature,mass0,remaining,probeFailures,finite,rows,pass},pass);
        }
        if(cases.Contains("performance"))
        foreach(bool filled in new[]{false,true})
        {
            settings.Mode=SimulationMode.Sandbox;settings.HydraulicPressure=true;settings.AirSimulation=true;settings.OpenBoundaries=true;
            setFrameRate(int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_HANDOFF_PERFORMANCE_FPS"),out int performanceFps) && performanceFps is >=30 and <=1000 ? performanceFps : 100);
            string label=$"performance-{filled}";var g=new GridCell[w*h];
            for(int x=150;x<=300;x++)for(int y=200;y<=210;y++)g[y*w+x]=Cell(metal);
            for(int y=130;y<=200;y++)for(int x=150;x<155;x++){g[y*w+x]=Cell(metal);g[y*w+x+145]=Cell(metal);}
            if(filled)for(int x=0;x<w;x++)for(int y=260-Math.Clamp((int)((x-180)*.6),0,150);y<h;y++)if(g[y*w+x].IsActive==0)g[y*w+x]=Cell(water);
            Load(g,label,materials);var clock=Stopwatch.StartNew();var debug=new GpuDebugProbe();uint frames=0;double last=0,simulation=0;
            double startWall=0,startSimulation=0;uint startFrames=0;ulong startThermal=0,startAir=0;
            while(clock.Elapsed.TotalSeconds<12)
            {
                double now=clock.Elapsed.TotalSeconds;float dt=(float)Math.Clamp(now-last,0,.05);last=now;
                BrushDrawCommand[] brush=filled?[new(){X=220,Y=65,EndX=220,EndY=65,Radius=17,Density=.82f,MaterialIndex=water,Seed=frames+71001}]:[];
                coordinator.DispatchFrame(settings,brush,dt);simulation+=dt;debug.Update(r,++frames);coordinator.ObserveStatistics(debug.Latest);
                if(startWall==0&&now>=3){startWall=now;startSimulation=simulation;startFrames=frames;startThermal=coordinator.ThermalTicks;startAir=coordinator.AirTicks;}
                yield return r;
            }
            double duration=last-startWall,realFps=(frames-startFrames)/duration,rate=(simulation-startSimulation)/duration;
            double thermalHz=(coordinator.ThermalTicks-startThermal)/duration,airHz=(coordinator.AirTicks-startAir)/duration;
            bool pass=rate>=.95&&thermalHz>=19;SimulationScreenshotWriter.Save(r,Path.Combine(dir,label+".png"));var end=Read();
            Result(new {test="performance",label,w,h,settings.Scale,cells=g.Count(c=>c.IsActive!=0),waterCells=g.Count(c=>c.IsActive!=0&&c.MaterialIndex==water),finalWaterCells=end.Count(c=>c.IsActive!=0&&c.MaterialIndex==water),realFps,rate,thermalHz,airHz,pass},pass);
        }
        r.Materials.Upload(r.Context,materials);
        foreach(var save in saves)
        {
            while(!save.IsCompleted)yield return r;
            save.GetAwaiter().GetResult();
        }
        Console.WriteLine($"PHYXEL_HANDOFF_COMPLETE cases={results.Count} failures={failures}");
        if(failures>0&&!baseline)throw new InvalidOperationException($"Handoff checks failed: {failures}");
    }
}
