using System;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class WaterContactRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var r = coordinator.DispatchFrame(new SimulationSettings { Paused = true },
            [new() { X=40, EndX=40, Y=40, EndY=40, Radius=1, Density=1,
                Mode=BrushCommandMode.Material, MaterialIndex=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }], 0);
        int w=r.Width, n=w*r.Height, origin=80*w+80;
        var context=r.Context; var table=registry.CreateGpuTable();
        GridCell Cell(string id, float temperature, float mass=1, float latent=0) => new()
        { IsActive=1, MaterialIndex=registry.GetRequiredRuntimeIndex(id), Mass=mass,
            Temperature=temperature, Lifetime=latent };
        bool passed=true;
        void Check(bool ok, string message)
        {
            if(!ok) { passed=false; Console.WriteLine($"PHYXEL_WATER_CONTACT_CHECK_FAILED {message}"); }
        }
        GridCell[] Read() => MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer)).ToArray();
        double Energy(GridCell[] cells) => cells.Where(c=>c.IsActive!=0)
            .Sum(c=>c.Mass*(double)PhaseEnthalpy.SpecificEnergy(c,table));
        GridCell[] Step(GridCell[] input, int ticks)
        {
            context.UpdateSubresource(input,r.Grid.ReadBuffer);
            for(uint i=0;i<ticks;i++) coordinator.DispatchThermalDiffusion(r,false,i,false);
            return Read();
        }

        // The water has a resolved path around a wet step, but no hot face.
        // Its only initially hot neighbour is diagonal. Test both orientations.
        foreach(int dx in new[] { -1,1 })
        {
            var grid=new GridCell[n];
            grid[origin]=Cell(CoreMaterialIds.Metal,200,7.8f);
            grid[origin+w]=Cell(CoreMaterialIds.Water,20);
            grid[origin+w+dx]=Cell(CoreMaterialIds.Water,20);
            double before=Energy(grid); var after=Step(grid,1);
            Check(after[origin+w+dx].Temperature>21,"Wet staircase corner did not exchange heat");
            Check(Math.Abs(Energy(after)-before)<.001,"Wet corner created energy");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_WET_CORNER direction={dx} water={after[origin+w+dx].Temperature:F6} energyError={Energy(after)-before:F8}"));
        }
        // A diagonal pair separated by two empty cells must remain insulated.
        var dry=new GridCell[n]; dry[origin]=Cell(CoreMaterialIds.Metal,200,7.8f);
        dry[origin+w+1]=Cell(CoreMaterialIds.Water,20);
        var dryAfter=Step(dry,20);
        Check(dryAfter[origin].Temperature==200 && dryAfter[origin+w+1].Temperature==20,"Heat crossed a dry gap");

        // Stress the maximum stencil with a tiny liquid packet surrounded by
        // metal. The small heat capacity must not cause temperature overshoot.
        var stress=new GridCell[n]; stress[origin]=Cell(CoreMaterialIds.Water,80,.001f);
        for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
            if(x!=0 || y!=0) stress[origin+y*w+x]=Cell(CoreMaterialIds.Metal,20,7.8f);
        double stressEnergy=Energy(stress); var stable=Step(stress,200);
        Check(stable.Where(c=>c.IsActive!=0).All(c=>float.IsFinite(c.Temperature) && c.Temperature>=19.999f && c.Temperature<=80.001f),
            "Wet stencil overshot its initial temperature range");
        Check(Math.Abs(Energy(stable)-stressEnergy)<.002,"Wet stencil lost energy with fractional water");

        // Cold water next to a 200 C metal cell must gain exactly what the
        // metal loses; neither an instantaneous boil nor a temperature clamp.
        var pair=new GridCell[n]; pair[origin]=Cell(CoreMaterialIds.Metal,200,7.8f);
        pair[origin+w]=Cell(CoreMaterialIds.Water,20);
        double initial=Energy(pair); var equilibrium=Step(pair,200);
        double expected=initial/(7.8*.13+4.18);
        Check(Math.Abs(equilibrium[origin].Temperature-expected)<.01 &&
            Math.Abs(equilibrium[origin+w].Temperature-expected)<.01,"Wet pair failed capacity-weighted equilibrium");
        Check(Math.Abs(Energy(equilibrium)-initial)<.01 && equilibrium[origin+w].Lifetime==0,"Wet pair lost energy or invented boiling");

        // At boiling, additional heat belongs to latent progress, not to
        // temperatures above 100 C. This also checks cooling reversal.
        pair[origin]=Cell(CoreMaterialIds.Metal,200,7.8f);
        pair[origin+w]=Cell(CoreMaterialIds.Water,100,1,500);
        initial=Energy(pair); var boiling=Step(pair,40);
        Check(boiling[origin+w].Temperature==100 && boiling[origin+w].Lifetime>500,
            "Wet wall did not supply boiling enthalpy");
        Check(Math.Abs(Energy(boiling)-initial)<.02,"Boiling contact created energy");

        // Exercise the actual cellular mass-transfer kernel without the phase
        // canonicalization pass, which previously concealed invalid mixtures.
        foreach(float latent in new[] { 100f,1000f })
        {
            var grid=new GridCell[n];
            grid[origin]=Cell(CoreMaterialIds.Water,100,.5f,latent);
            grid[origin+w]=Cell(CoreMaterialIds.Water,20,.5f);
            initial=Energy(grid); context.UpdateSubresource(grid,r.Grid.ReadBuffer);
            var map=grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray();
            context.UpdateSubresource(map,r.CellMaterials.Buffer);
            var constants=new SimulationFrameConstants { Width=(uint)w, Height=(uint)r.Height,
                DispatchExtentX=(uint)w, DispatchExtentY=(uint)((r.Height+1)/2), SimulationPhase=0 };
            context.UpdateSubresource(ref constants,r.FrameConstants);
            context.ComputeShader.Set(r.CellularAutomataShader);
            context.ComputeShader.SetConstantBuffer(0,r.FrameConstants);
            context.ComputeShader.SetShaderResources(0,r.Materials.View,r.Air.View);
            context.ComputeShader.SetUnorderedAccessView(0,r.Grid.ReadUnorderedView);
            context.ComputeShader.SetUnorderedAccessView(3,r.CellMaterials.UnorderedView);
            context.Dispatch((w+15)/16,((r.Height+1)/2+15)/16,1);
            for(int i=0;i<2;i++) context.ComputeShader.SetShaderResource(i,null);
            for(int i=0;i<12;i++) context.ComputeShader.SetUnorderedAccessView(i,null);
            var mixed=Read(); var c=mixed[origin+w];
            Check(mixed[origin].IsActive==0 && Math.Abs(c.Mass-1)<1e-6,"Liquid mass consolidation failed");
            Check(Math.Abs(Energy(mixed)-initial)<.001,"Liquid consolidation lost enthalpy");
            Check(latent==100 ? c.Temperature<100 && c.Lifetime==0 : c.Temperature==100 && c.Lifetime>0,
                "Liquid mixture retained latent heat below its boiling point");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_WATER_MIX latentInput={latent} water={c.Temperature:F6} latentOutput={c.Lifetime:F6} error={Energy(mixed)-initial:F8}"));
        }
        if(!passed) throw new InvalidOperationException("Water contact checks failed; see individual results.");
        Console.WriteLine("PHYXEL_WATER_CONTACT_SUCCESS");
    }
}
