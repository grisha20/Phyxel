using System;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class NativeThermalKernelVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator,GpuSimulationResources r,MaterialRegistry registry)
    {
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
}
