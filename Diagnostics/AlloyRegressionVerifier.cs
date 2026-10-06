using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using Phyxel.UI;

namespace Phyxel.Diagnostics;

internal static class AlloyRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry, GraphicsDevice device)
    {
        string dir=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/alloys";
        Directory.CreateDirectory(dir);
        var settings=new SimulationSettings { Paused=true,AirSimulation=false,OpenBoundaries=false };
        var r=coordinator.DispatchFrame(settings,[new(){X=20,Y=20,EndX=20,EndY=20,Radius=1,Density=1,
            Mode=BrushCommandMode.Material,MaterialIndex=registry.GetRequiredRuntimeIndex("core:steel")}],0);
        int w=r.Width,n=w*r.Height,o=80*w+80,checks=0;
        var table=registry.CreateGpuTable();
        var physical=table.ToArray();
        uint fixture=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        table[fixture].ThermalConductivity=0; r.Materials.Upload(r.Context,table);
        void Check(bool ok,string label){checks++;if(!ok)throw new InvalidOperationException(label);}
        GridCell Cell(string id,float t,float mass=1,float p=0)=>new(){IsActive=1,MaterialIndex=registry.GetRequiredRuntimeIndex(id),Temperature=t,Mass=mass,Lifetime=p};
        GridCell[] Read()=>MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        void Upload(GridCell[] g){r.Context.UpdateSubresource(g,r.Grid.ReadBuffer);r.Context.UpdateSubresource(g,r.Grid.WriteBuffer);
            r.Context.UpdateSubresource(g.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);}
        double Energy(GridCell[] g)=>g.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass*PhaseEnthalpy.SpecificEnergy(c,physical));
        void Balance(GridCell[] before,GridCell[] after,string label,bool largeScene=false){
            double q=Energy(after)-Energy(before),m=after.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass)-before.Where(c=>c.IsActive!=0).Sum(c=>(double)c.Mass);
            // The existing bulk/pool verifiers use a relative float precision
            // budget for large scenes. Keep the stricter absolute pair limit.
            double tolerance=largeScene?Math.Max(.01,Math.Abs(Energy(before))*.00005):.01;
            Check(Math.Abs(q)<tolerance && Math.Abs(m)<.0001,label+" mass/Q "+m+"/"+q);
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_ALLOY_BALANCE {label} Q={q:E6} MassError={m:E6}"));}
        void Phase(uint tick=0){var c=new PhaseTransitionConstants{Width=(uint)w,Height=(uint)r.Height,MaterialCount=(uint)table.Length,TickIndex=tick,TickCount=1};
            r.Context.UpdateSubresource(ref c,r.PhaseConstants);r.Context.ComputeShader.Set(r.PhaseTransitionShader);
            r.Context.ComputeShader.SetConstantBuffer(0,r.PhaseConstants);r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
            r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.PhaseSummary.UnorderedView);
            r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);r.Context.ComputeShader.SetShaderResource(0,null);
            for(int i=0;i<3;i++)r.Context.ComputeShader.SetUnorderedAccessView(i,null);r.Context.ComputeShader.Set(null);}
        var serializer=new SimulationStateSerializer();
        Check(physical[registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal)].TransitionAboveTemperature==1000,"Legacy threshold changed");
        foreach(var alloy in new[]{("steel",1500f,7.8f,.13f),("cast_iron",1200f,7.2f,.13f),("copper",1085f,8.9f,.10f)}){
            string solid="core:"+alloy.Item1,melt="core:molten_"+alloy.Item1;
            uint sid=registry.GetRequiredRuntimeIndex(solid),lid=registry.GetRequiredRuntimeIndex(melt);
            float solidCapacity=alloy.Item4, difference=.5f-solidCapacity;
            float tm=alloy.Item2,tf=tm-50,latent=difference*tm,freezing=latent-difference*50;
            float warm=alloy.Item1=="copper"?1000:1100;
            Check(registry.SelectableMaterials.Any(m=>m.Id==solid)&&!registry.SelectableMaterials.Any(m=>m.Id==melt),"Selection "+solid);
            foreach(var test in new[]{(solid,warm,0f,sid,warm),(solid,tm,latent/2,sid,tm),
                (solid,tm,latent,lid,tm),(melt,tf,-freezing,sid,tf),
                (solid,tm+10,0f,sid,tm),(melt,tf-10,0f,lid,tf)}){
                var g=new GridCell[n];g[o]=Cell(test.Item1,test.Item2,alloy.Item3,test.Item3);g[o].BodyId=123;
                Upload(g);Phase();var a=Read();Balance(g,a,solid+"/phase");
                Check(a[o].MaterialIndex==test.Item4&&Math.Abs(a[o].Temperature-test.Item5)<.003,"Phase "+solid);
                if(a[o].MaterialIndex!=g[o].MaterialIndex)Check(a[o].BodyId==0,"Stale body "+solid);
            }
            // A reversal before melting, then a paid phase cycle. Energies are
            // independently assigned; the plateau may not create a new stock.
            foreach(bool gpu in new[]{false,true}){
                var c=Cell(solid,30);foreach(float h in new[]{solidCapacity*tm+latent/2,solidCapacity*30,.5f*tm,.5f*tf-freezing,solidCapacity*30}){
                    c.Temperature=(h-c.Lifetime)/(c.MaterialIndex==sid?solidCapacity:.5f);
                    if(gpu){var g=new GridCell[n];g[o]=c;Upload(g);Phase();c=Read()[o];}
                    else PhaseTransitionRuntime.TryApply(ref c,physical,out _);
                    double actual=(c.MaterialIndex==sid?(double)solidCapacity:.5)*c.Temperature+c.Lifetime;
                    Check(Math.Abs(actual-h)<.003,"Reversal Q "+solid);
                }Check(c.MaterialIndex==sid&&Math.Abs(c.Temperature-30)<.003,"Return identity "+solid);}
            foreach(bool gap in new[]{false,true}){
                var g=new GridCell[n];g[o]=Cell(solid,warm,alloy.Item3);g[o+(gap?2:1)]=Cell(CoreMaterialIds.Water,30,10);
                Upload(g);for(uint i=0;i<100;i++)coordinator.DispatchThermalDiffusion(r,false,i,false);
                var a=Read();Balance(g,a,solid+"/water-"+gap);
                Check(gap?a[o+2].Temperature==30:a[o+1].Temperature>31,"Water contact/gap "+solid);}
            var pocket=new GridCell[n];for(int y=-1;y<=1;y++)for(int x=-1;x<=2;x++)if(y!=0||x<0||x>1)pocket[o+y*w+x]=Cell(CoreMaterialIds.Fixture,30);
            pocket[o]=Cell(solid,tm,alloy.Item3);pocket[o+1]=Cell(CoreMaterialIds.Stone,tm+100,3);
            float? reference=null;
            foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
                Upload(pocket);coordinator.RestoreWorldActivity(r,true,false,false);settings.Paused=false;settings.Mode=mode;
                for(int i=0;i<fps*3;i++)coordinator.DispatchFrame(settings,[],1f/fps);
                var a=Read();Balance(pocket,a,solid+"/"+mode+"/"+fps);
                Check(a[o].MaterialIndex==sid&&Math.Abs(a[o].Temperature-tm)<.003&&a[o].Lifetime>20,"Finite reservoir plateau "+solid);
                reference??=a[o].Lifetime;Check(Math.Abs(a[o].Lifetime-reference.Value)<1,"Cadence "+solid);
                settings.Paused=true;coordinator.DispatchFrame(settings,[],1);
                Check(MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"Pause "+solid);
                Console.WriteLine(FormattableString.Invariant($"PHYXEL_ALLOY_CADENCE id={solid} mode={mode} fps={fps} progress={a[o].Lifetime:F6}"));}
            var save=new GridCell[n];save[o]=Cell(solid,tm,alloy.Item3,latent/2);save[o+1]=Cell(melt,tf,1,-freezing/2);
            var snapshot=new SimulationWorldSnapshot(w,r.Height,MemoryMarshal.AsBytes(save.AsSpan()).ToArray());string path=Path.Combine(dir,alloy.Item1+".json");
            Task.Run(()=>serializer.SaveAsync(path,settings,(ushort)sid,snapshot,registry)).GetAwaiter().GetResult();
            var loaded=Task.Run(()=>serializer.LoadAsync(path,registry)).GetAwaiter().GetResult();
            Check(loaded?.World!=null&&snapshot.Grid.AsSpan().SequenceEqual(loaded.World.Grid),"Codec "+solid);
            GridCell[] Continue(GridCell[] g){Upload(g);for(uint i=0;i<20;i++){coordinator.DispatchThermalDiffusion(r,false,i,false);Phase(i);}return Read();}
            var memory=Continue(save);var reload=Continue(MemoryMarshal.Cast<byte,GridCell>(loaded!.World!.Grid).ToArray());
            Check(MemoryMarshal.AsBytes(memory.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(reload.AsSpan())),"Resume "+solid);
            var contact=new GridCell[n];contact[o]=Cell(CoreMaterialIds.Coal,30,.6f);contact[o].Lifetime=1;contact[o+1]=Cell(melt,tm+50);
            Upload(contact);for(uint tick=0;tick<100;tick++){
                var c=new ContactTransitionConstants{Width=(uint)w,Height=(uint)r.Height,DeltaTime=.05f,TickIndex=tick};
                r.Context.UpdateSubresource(ref c,r.ContactTransitionConstants);r.Context.ComputeShader.Set(r.MoistureShader);
                r.Context.ComputeShader.SetConstantBuffer(0,r.ContactTransitionConstants);r.Context.ComputeShader.SetShaderResource(0,r.Materials.View);
                r.Context.ComputeShader.SetUnorderedAccessViews(0,r.Grid.ReadUnorderedView,r.CellMaterials.UnorderedView,r.GasMotion.UnorderedView,r.ContactSummary.UnorderedView);
                r.Context.Dispatch((w+15)/16,(r.Height+15)/16,1);r.Context.ComputeShader.SetShaderResource(0,null);
                for(int i=0;i<4;i++)r.Context.ComputeShader.SetUnorderedAccessView(i,null);r.Context.ComputeShader.Set(null);}
            Check(MemoryMarshal.AsBytes(contact.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Read().AsSpan())),"Molten absorbed "+solid);
        }
        // A finite hot end drives the full production heat pass, including
        // bulk conduction. Compare the observed cold end, not JSON ratios.
        foreach(var mode in new[]{SimulationMode.Sandbox,SimulationMode.Simulation})foreach(int fps in new[]{30,60,100}){
            float steelEnd=0;
            foreach(string id in new[]{"core:steel","core:copper"}){
                var g=new GridCell[n];float mass=physical[registry.GetRequiredRuntimeIndex(id)].Density;
                // The production bulk pass requires a 3x3 interior. Use an
                // actual plate, not a one-pixel trace whose distant probe
                // cannot receive heat within this short observation window.
                for(int y=-1;y<=1;y++)for(int x=0;x<24;x++)g[o+y*w+x]=Cell(id,x<3?900:30,mass);
                Upload(g);coordinator.RestoreWorldActivity(r,true,false,false);
                settings.Mode=mode;settings.Paused=false;
                for(int frame=0;frame<fps*3/10;frame++)coordinator.DispatchFrame(settings,[],1f/fps);
                var a=Read();Balance(g,a,id+"/rod/"+mode+"/"+fps);
                Check(a.Where(c=>c.IsActive!=0).All(c=>float.IsFinite(c.Temperature)&&c.Temperature>=29.999&&c.Temperature<=900.001),"Rod overshoot "+id);
                float end=a[o+20].Temperature;
                Console.WriteLine(FormattableString.Invariant($"PHYXEL_COPPER_ROD mode={mode} fps={fps} id={id} coldEnd={end:F6} steelEnd={steelEnd:F6}"));
                if(id=="core:steel")steelEnd=end;
                else Check(end-30>(steelEnd-30)*1.1f,"Copper rod not faster");
            }
        }
        var plate=new GridCell[n];
        for(int y=0;y<16;y++)for(int x=0;x<32;x++)plate[o+y*w+x]=Cell("core:copper",(x+y)%2==0?900:30,8.9f);
        Upload(plate);coordinator.RestoreWorldActivity(r,true,false,false);settings.Paused=false;
        for(int frame=0;frame<60;frame++)coordinator.DispatchFrame(settings,[],1f/60);
        var cooled=Read();Balance(plate,cooled,"copper/dense-plate",largeScene:true);
        Check(cooled.Where(c=>c.IsActive!=0).All(c=>float.IsFinite(c.Temperature)&&c.Temperature>=29.999&&c.Temperature<=900.001),"Dense plate overshoot");
        string loaderDir=Path.Combine(dir,"loader");Directory.CreateDirectory(loaderDir);
        foreach(float conductivity in new[]{2f,2.001f}){
            File.WriteAllText(Path.Combine(loaderDir,"boundary.json"),System.Text.Json.JsonSerializer.Serialize(new{
                schema=1,id="core:boundary",name="Boundary",kind="solid",color="#C98043",
                thermal=new{conductivity,heatCapacity=.1f}}));
            bool rejected=false;
            try { MaterialFileLoader.LoadCore(loaderDir,1); }
            catch(InvalidDataException e) when(e.Message.Contains("thermal.conductivity")) { rejected=true; }
            Check(rejected==(conductivity>2),"Conductivity loader boundary "+conductivity);
        }
        using var previews=new MaterialCardPreviewCache(device,Path.Combine(AppContext.BaseDirectory,"Content","UI","MaterialCards"));
        Color[]? first=null;
        foreach(string id in new[]{"core:steel","core:cast_iron","core:copper"}){
            Check(previews.TryGetPreview(id,out var texture),"Missing preview "+id);var pixels=new Color[texture.Width*texture.Height];texture.GetData(pixels);
            if(first!=null)Check(!first.SequenceEqual(pixels),"Identical alloy previews");first=pixels;
            using var stream=File.Create(Path.Combine(dir,id.Split(':')[1]+".png"));texture.SaveAsPng(stream,texture.Width,texture.Height);}
        Console.WriteLine($"PHYXEL_ALLOY_COMPLETE checks={checks}");
    }
}
