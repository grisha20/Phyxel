using System;
using System.Collections.Generic;
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

internal static class FilterBrushRegressionVerifier
{
    internal static IEnumerable<GpuSimulationResources> Run(SimulationDispatchCoordinator coordinator,
        MaterialRegistry registry, SimulationSettings settings)
    {
        string dir = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/filter-brushes";
        Directory.CreateDirectory(dir);
        settings.ApplyScale(.25f); settings.Paused = true; settings.AirSimulation = false;
        settings.OpenBoundaries = false; settings.HydraulicPressure = false;
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint fixture = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        uint steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint ice = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Ice);
        var r = coordinator.DispatchFrame(settings, [new(){X=20,Y=20,Radius=1,Density=1,MaterialIndex=water}], 0);
        yield return r;
        int w=r.Width,h=r.Height,n=w*h,failures=0;
        var serializer=new SimulationStateSerializer(); var physical=registry.CreateGpuTable();
        var isolated=physical.ToArray();
        foreach (int id in Enumerable.Range(0,isolated.Length))
        {
            isolated[id].ThermalConductivity=0; isolated[id].BurnRate=0; isolated[id].AmbientCoolingRate=0;
        }
        var results=new List<object>();
        void Record(string test,bool ok,object? details=null)
        {
            if(!ok)failures++;
            var result=new{test,ok,details};results.Add(result);
            Console.WriteLine("PHYXEL_FILTER_BRUSH "+JsonSerializer.Serialize(result));
            File.WriteAllText(Path.Combine(dir,"measurements.json"),JsonSerializer.Serialize(new{failures,results},new JsonSerializerOptions{WriteIndented=true}));
        }
        GridCell Cell(uint id)=>new(){IsActive=1,MaterialIndex=id,Mass=1,
            Temperature=id==steam?130:id==ice?-5:30,Lifetime=physical[id].MaximumLifetime};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Load(GridCell[] cells,uint[] map)
        {
            serializer.ApplyWorldSnapshot(r,new(w,h,MemoryMarshal.AsBytes(cells.AsSpan()).ToArray(),Filters:MemoryMarshal.AsBytes(map.AsSpan()).ToArray()));
            coordinator.RestoreWorldActivity(r,true,true,false);r.Materials.Upload(r.Context,isolated);
        }
        // Every occupied kind, including low-mass invisible gas, is protected.
        var cells=new GridCell[n];var map=new uint[n];
        uint[] occupants=[water,steam,ice,fixture,
            registry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand),registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil)];
        for(int i=0;i<occupants.Length;i++)cells[120*w+217+i]=Cell(occupants[i]);
        cells[120*w+218].Mass=.000001f;map[119*w+220]=FilterRules.Gas|FilterRules.AmbientAir;
        Load(cells,map);byte[] before=MemoryMarshal.AsBytes(Read().AsSpan()).ToArray();
        var command=new BrushDrawCommand{X=220,Y=120,EndX=220,EndY=120,Radius=5,Density=1,Mode=BrushCommandMode.Filter,Reserved=steam+1};
        coordinator.DispatchFrame(settings,[command],0);yield return r;
        int expected=1;
        for(int y=115;y<=125;y++)for(int x=215;x<=225;x++)
            if((x-220)*(x-220)+(y-120)*(y-120)<=25 && cells[y*w+x].IsActive==0 && map[y*w+x]==0)expected++;
        bool protectedCells=Enumerable.Range(0,n).Where(i=>cells[i].IsActive!=0).All(i=>r.FilterMap[i]==0);
        Record("paint-empty-only",protectedCells&&r.FilterCount==expected&&before.SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan()).ToArray()),new{expected,actual=r.FilterCount});
        Record("existing-filter-preserved",r.FilterMap[119*w+220]==map[119*w+220]);
        uint[] painted=r.FilterMap.ToArray();command.Reserved=FilterRules.Closed;
        coordinator.DispatchFrame(settings,[command],0);yield return r;
        Record("repaint-does-not-replace",painted.SequenceEqual(r.FilterMap));
        // A save capture and an occupancy readback use separate staging storage.
        serializer.BeginWorldCapture(r);command.X=240;command.EndX=240;
        coordinator.DispatchFrame(settings,[command],0);yield return r;
        SimulationWorldSnapshot? captured=null;
        for(int f=0;f<120;f++){r.Context.Flush();if(serializer.TryCompleteWorldCapture(r,out captured))break;yield return r;}
        Record("save-capture-during-brush",captured is not null&&captured.Grid.SequenceEqual(before)&&
            MemoryMarshal.Cast<byte,uint>(captured.Filters!).SequenceEqual(painted));
        command.X=220;command.EndX=220;command.Reserved=0;
        coordinator.DispatchFrame(settings,[command],0);yield return r;
        Record("remove-preserves-particles",before.SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan()).ToArray())&&r.FilterMap[119*w+220]==0);
        cells=new GridCell[n];map=new uint[n];cells[10*w+8]=Cell(water);Load(cells,map);
        command=new(){X=-5,Y=10,EndX=18,EndY=10,Radius=3,Shape=BrushCommandShape.Segment,Mode=BrushCommandMode.Filter,Reserved=FilterRules.Closed};
        coordinator.DispatchFrame(settings,[command],0);yield return r;
        int wanted=0;
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            float closest=Math.Clamp(x,-5,18);
            bool inside=(x-closest)*(x-closest)+(y-10)*(y-10)<=9&&cells[y*w+x].IsActive==0;
            if(inside)wanted++;
            if((r.FilterMap[y*w+x]!=0)!=inside)throw new InvalidOperationException("Clipped segment mismatch");
        }
        Record("clipped-segment-empty-only",r.FilterCount==wanted,new{wanted,actual=r.FilterCount});
        // The new channels preserve particle and air independence in both modes.
        foreach(var mode in Enum.GetValues<SimulationMode>())
        foreach(var preset in new[]{FilterSelection.Wall,FilterSelection.AirOnly,FilterSelection.NoAir})
        foreach(uint id in new[]{water,ice})
        {
            cells=new GridCell[n];map=new uint[n];
            for(int y=60;y<=220;y++){cells[y*w+200]=Cell(fixture);cells[y*w+239]=Cell(fixture);}
            for(int x=200;x<=239;x++){cells[60*w+x]=Cell(fixture);cells[220*w+x]=Cell(fixture);}
            for(int x=201;x<239;x++)map[140*w+x]=FilterRules.Select(preset,registry,0);
            for(int y=125;y<130;y++)for(int x=211;x<229;x++){cells[y*w+x]=Cell(id);if(id==ice)cells[y*w+x].BodyId=123;}
            Load(cells,map);settings.Mode=mode;settings.Paused=false;settings.SolidGravity=true;
            for(int f=0;f<180;f++){coordinator.DispatchFrame(settings,[],1f/60);yield return r;}
            GridCell[] after=Read();double mass=after.Where(c=>c.IsActive!=0&&c.MaterialIndex==id).Sum(c=>(double)c.Mass);
            double crossed=Enumerable.Range(0,n).Where(i=>i/w>140&&after[i].IsActive!=0&&after[i].MaterialIndex==id).Sum(i=>(double)after[i].Mass);
            double beforeQ=cells.Where(c=>c.MaterialIndex==id&&c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
            double afterQ=after.Where(c=>c.MaterialIndex==id&&c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
            double qError=Math.Abs(afterQ-beforeQ)/Math.Max(1,Math.Abs(beforeQ));
            bool ok=Math.Abs(mass-90)<1e-4&&qError<1e-4&&(preset==FilterSelection.NoAir?crossed>0:crossed==0);
            Record("particle-channel",ok,new{mode,preset,id,mass,crossed,qError});
            settings.Paused=true;
        }
        foreach(var preset in new[]{FilterSelection.Wall,FilterSelection.AirOnly,FilterSelection.NoAir})
        {
            cells=new GridCell[n];map=new uint[n];
            for(int y=60;y<=220;y++){cells[y*w+200]=Cell(fixture);cells[y*w+239]=Cell(fixture);}
            for(int x=200;x<=239;x++){cells[60*w+x]=Cell(fixture);cells[220*w+x]=Cell(fixture);}
            for(int x=201;x<239;x++)map[140*w+x]=FilterRules.Select(preset,registry,0);
            Load(cells,map);float[] oxygen=new float[n];
            for(int y=61;y<140;y++)for(int x=201;x<239;x++)oxygen[y*w+x]=1;
            r.Context.UpdateSubresource(oxygen,r.Oxidizer.ReadBuffer);
            for(int t=0;t<240;t++){SimulationDispatchCoordinator.DispatchOxidizer(r,1f/60,false,false,false);if(t%8==7)yield return r;}
            float[] after=MemoryMarshal.Cast<byte,float>(AirInventoryRegressionVerifier.Read(r,r.Oxidizer.ReadBuffer)).ToArray();
            double crossed=Enumerable.Range(0,n).Where(i=>i/w>140).Sum(i=>(double)after[i]);
            double error=Math.Abs(after.Sum(x=>(double)x)-oxygen.Sum(x=>(double)x));
            Record("ambient-channel",(preset==FilterSelection.AirOnly?crossed>0:crossed==0)&&error<1e-4,new{preset,crossed,error});
        }
        // New flags round-trip alongside old ID rules, including real GPU upload.
        cells=new GridCell[n];map=new uint[n];cells[200*w+220]=Cell(water);
        map[140*w+220]=FilterRules.AllParticles;map[140*w+221]=FilterRules.AmbientAir;map[140*w+222]=FilterRules.Closed;map[140*w+223]=steam+1;
        Load(cells,map);string scene=Path.Combine(dir,"brush-scene.json");
        var saving=serializer.SaveAsync(scene,new(){FilterSelection=FilterSelection.NoAir},(ushort)water,
            new(w,h,MemoryMarshal.AsBytes(cells.AsSpan()).ToArray(),Filters:MemoryMarshal.AsBytes(map.AsSpan()).ToArray()),registry);
        while(!saving.IsCompleted)yield return r;
        saving.GetAwaiter().GetResult();
        var loading=serializer.LoadAsync(scene,registry);
        while(!loading.IsCompleted)yield return r;
        var loaded=loading.GetAwaiter().GetResult()!;
        Array.Clear(r.FilterMap);r.FilterCount=0;r.UploadFilters();
        r.Context.UpdateSubresource(new GridCell[n],r.Grid.ReadBuffer);
        serializer.ApplyWorldSnapshot(r,loaded.World!);yield return r;
        uint[] gpuMap=MemoryMarshal.Cast<byte,uint>(AirInventoryRegressionVerifier.Read(r,r.Filters.Buffer)).ToArray();
        Record("v17-roundtrip",loaded.State.FilterSelection==FilterSelection.NoAir&&map.SequenceEqual(r.FilterMap)&&
            gpuMap[0]==4&&gpuMap.AsSpan(1).SequenceEqual(map)&&Read()[200*w+220].Mass==1);
        Console.WriteLine($"PHYXEL_FILTER_BRUSH_COMPLETE cases={results.Count} failures={failures}");
        Environment.ExitCode=failures==0?0:1;
    }
}
