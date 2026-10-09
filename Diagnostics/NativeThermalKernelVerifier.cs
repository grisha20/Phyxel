using System;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

internal static class NativeThermalKernelVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,GpuSimulationResources r,MaterialRegistry registry)
    {
        CompareWaterKernels(r, registry);
        uint water=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        string[] ids=["water","oil","steel","cast_iron","copper","ice","steam","wet_charcoal","heater","cooler"];
        int checks=0;
        foreach(string scene in new[]{"ambient","uniform","gradient","latent","devices","overfill","flight"})
        foreach(bool convection in new[]{false,true})
        {
            var grid=new GridCell[r.Width*r.Height];
            for(int y=40;y<200;y++)for(int x=40;x<300;x++){
                uint id=scene=="devices"?registry.GetRequiredRuntimeIndex("core:"+ids[(x/20)%ids.Length]):water;
                float t=scene=="ambient"?20:scene=="uniform"?30:scene=="latent"?100:(x<170?30:900);
                grid[y*r.Width+x]=new(){IsActive=1,MaterialIndex=id,Mass=(x%4+1)*.25f,Temperature=t,
                    Lifetime=scene=="latent"?(x%3)*300:scene=="devices"?500:0,Pressure=scene=="devices"?600:0};
                if(scene=="devices" && ids[(x/20)%ids.Length]=="wet_charcoal"){
                    grid[y*r.Width+x].MoistureMass=.1f;grid[y*r.Width+x].MoistureEnergy=40;
                }
            }
            if(scene=="overfill")for(int y=201;y<206;y++)grid[y*r.Width+80]=new(){IsActive=1,MaterialIndex=water,Mass=y==205?1.75f:1,Temperature=30};
            if(scene=="flight")grid[20*r.Width+25]=new(){IsActive=1,MaterialIndex=water,Mass=.25f,Temperature=100,Lifetime=20,BodyId=0x40000000u,VelocityY=-40};
            byte[] Execute(bool reference){
                r.Context.UpdateSubresource(grid,r.Grid.ReadBuffer);
                r.Context.UpdateSubresource(grid.Select(c=>c.IsActive!=0?c.MaterialIndex:0).ToArray(),r.CellMaterials.Buffer);
                r.Context.UpdateSubresource(new GasMotionState[grid.Length],r.GasMotion.Buffer);
                if(r.ThermalEnergyLedger is not null)
                    r.Context.UpdateSubresource(new ThermalEnergyLedgerCell[grid.Length],r.ThermalEnergyLedger.Buffer);
                coordinator.DispatchThermalDiffusion(r,false,1,convection,reference);
                var bytes=AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer);
                if(r.ThermalEnergyLedger is not null)bytes=bytes.Concat(AirInventoryRegressionVerifier.Read(r,r.ThermalEnergyLedger.Buffer)).ToArray();
                return bytes;
            }
            var previous=Execute(true);var current=Execute(false);checks++;
            if(!previous.AsSpan().SequenceEqual(current))
                throw new InvalidOperationException($"Thermal kernel mismatch {scene}/convection{convection}");
        }
        Console.WriteLine($"PHYXEL_NATIVE_THERMAL_SUCCESS checks={checks}");
    }

    private static void CompareWaterKernels(GpuSimulationResources r, MaterialRegistry registry)
    {
        uint water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint metal = registry.GetRequiredRuntimeIndex("core:cast_iron");
        uint oil = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
        int checks = 0;
        foreach (string scene in new[] { "cold", "gradient", "wet-wall", "condensation", "flight", "overfill", "filter", "edges" })
        {
            var grid = new GridCell[r.Width * r.Height];
            var mask = new uint[grid.Length];
            var filters = new uint[grid.Length + 1];
            for (int y = 30; y < 220; y++) for (int x = 30; x < 310; x++)
            {
                uint material = water;
                float temperature = scene == "cold" ? 20 : x < 170 ? 50 : 100;
                if (scene == "wet-wall" && y >= 190) { material = metal; temperature = 900; }
                if (scene == "condensation" && y < 80) { material = steam; temperature = 110; }
                if (scene == "edges") material = x < 100 ? oil : water;
                grid[y * r.Width + x] = new() { IsActive = 1, MaterialIndex = material,
                    Mass = 1, Temperature = temperature, Lifetime = temperature == 100 ? 600 : 0 };
                if (material == metal) mask[y * r.Width + x] = 0x40000000u;
            }
            if (scene == "flight") for (int y = 8; y < 12; y++)
                grid[y * r.Width + 80] = new() { IsActive = 1, MaterialIndex = water, Mass = .25f,
                    Temperature = 100, Lifetime = 20, BodyId = 0x40000000u, VelocityY = -40 };
            if (scene == "overfill") grid[180 * r.Width + 80].Mass = 1.75f;
            if (scene == "edges") foreach (int index in new[] { 0, 1, r.Width - 1, grid.Length - 1, grid.Length - 2 })
                grid[index] = new() { IsActive = 1, MaterialIndex = water, Mass = .5f, Temperature = 80 };
            if (scene == "filter") { filters[0] = 1; for (int y = 30; y < 220; y++) filters[y * r.Width + 170 + 1] = FilterRules.Closed; }
            r.Context.UpdateSubresource(filters, r.Filters.Buffer);
            for (uint step = 0; step < 5; step++)
            for (uint parity = 0; parity < (step < 2 ? 4u : 1u); parity++)
            {
                // Four disjoint quench pairs, four rotations, two row spans,
                // and the serial-column task. Each comparison starts identically.
                uint subStep = step == 0 ? 3u : step == 1 ? 0u : step == 2 ? 4u : step == 3 ? 8u : 2u;
                var constants = new SimulationFrameConstants { Width = (uint)r.Width, Height = (uint)r.Height,
                    FrameIndex = 79, GasSubStep = subStep, SimulationPhase = parity,
                    DispatchOffsetX = step == 0 ? parity < 2 ? parity : 0 : parity & 1,
                    DispatchOffsetY = step == 0 ? parity >= 2 ? parity - 2 : 0 : parity >> 1,
                    DebugReserved2 = 1 };
                int xCount = step == 0 ? parity < 2 ? (r.Width + 1) / 2 : r.Width :
                    step == 1 ? (r.Width + 1) / 2 : step == 4 ? r.Width : (r.Width + (int)subStep * 2 - 1) / ((int)subStep * 2);
                int yCount = step == 0 ? parity >= 2 ? (r.Height + 1) / 2 : r.Height :
                    step == 1 ? (r.Height + 1) / 2 : step == 4 ? 1 : r.Height;
                byte[] Execute(ComputeShader? shader)
                {
                    var c = r.Context;
                    c.UpdateSubresource(grid, r.Grid.ReadBuffer);
                    c.UpdateSubresource(mask, r.BulkThermalDegrees.Buffer);
                    c.UpdateSubresource(grid.Select(p => p.MaterialIndex).ToArray(), r.CellMaterials.Buffer);
                    c.UpdateSubresource(new GasMotionState[grid.Length], r.GasMotion.Buffer);
                    c.UpdateSubresource(new uint[1], r.ContactSummary.Buffer);
                    c.UpdateSubresource(ref constants, r.FrameConstants);
                    c.ComputeShader.Set(shader);
                    c.ComputeShader.SetConstantBuffer(0, r.FrameConstants);
                    c.ComputeShader.SetShaderResources(0, r.Materials.View, r.BulkThermalDegrees.View);
                    c.ComputeShader.SetShaderResource(15, r.Filters.View);
                    c.ComputeShader.SetUnorderedAccessViews(0, r.Grid.ReadUnorderedView, r.ContactSummary.UnorderedView,
                        r.CellMaterials.UnorderedView, r.GasMotion.UnorderedView);
                    if (shader == r.WaterQuenchShader)
                    {
                        c.ComputeShader.Set(r.WaterQuenchTilesShader);
                        c.ComputeShader.SetUnorderedAccessView(5,r.WaterQuenchTiles.UnorderedView);
                        c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
                        c.ComputeShader.SetUnorderedAccessView(5,null);
                        c.ComputeShader.SetShaderResource(2,r.WaterQuenchTiles.View);
                        c.ComputeShader.Set(shader);
                    }
                    c.Dispatch((xCount + 15) / 16, (yCount + 15) / 16, 1);
                    for (int slot = 0; slot < 4; slot++) c.ComputeShader.SetUnorderedAccessView(slot, null);
                    c.ComputeShader.SetShaderResource(0, null); c.ComputeShader.SetShaderResource(1, null);
                    c.ComputeShader.SetShaderResource(2,null);
                    return new[] { r.Grid.ReadBuffer, r.ContactSummary.Buffer, r.CellMaterials.Buffer, r.GasMotion.Buffer }
                        .SelectMany(buffer => AirInventoryRegressionVerifier.Read(r, buffer)).ToArray();
                }
                var before = Execute(r.WaterConvectionShader);
                var after = Execute(step == 0 ? r.WaterQuenchShader : step == 1 ? r.WaterConvectShader :
                    step == 4 ? r.WaterColumnsShader : r.WaterMixShader);
                checks++;
                if (!before.AsSpan().SequenceEqual(after)) throw new InvalidOperationException($"Water kernel mismatch {scene}/{subStep}/{parity}");
            }
        }
        r.Context.UpdateSubresource(new uint[r.Width * r.Height + 1], r.Filters.Buffer);
        Console.WriteLine($"PHYXEL_WATER_THERMAL_KERNEL_SUCCESS checks={checks}");
    }
}
