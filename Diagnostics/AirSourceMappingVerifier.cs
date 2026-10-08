using System;
using System.Linq;
using System.Numerics;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class AirSourceMappingVerifier
{
    internal static int Run(GpuSimulationResources r, MaterialRegistry registry)
    {
        var c=r.Context;int checks=0,n=r.AirWidth*r.AirHeight;
        var table=registry.CreateGpuTable();
        uint[] palette=new[]{"core:fire","core:steam","core:sand","core:water","core:metal"}
            .Select(id => (uint)registry.GetRequiredRuntimeIndex(id)).ToArray();
        void Unbind(){for(int slot=0;slot<9;slot++){c.ComputeShader.SetShaderResource(slot,null);c.ComputeShader.SetUnorderedAccessView(slot,null);}}
        foreach(uint sandbox in new uint[]{0,1})foreach(string layout in new[]{"mixed","walls","filters"})
        {
            var random=new Random(144);var grid=new GridCell[r.Width*r.Height];
            var motion=new GasMotionState[grid.Length];var filters=new uint[grid.Length+1];
            for(int y=0;y<r.Height;y++)for(int x=0;x<r.Width;x++)
            {
                int i=y*r.Width+x;uint material=palette[random.Next(palette.Length)];
                if(layout=="walls"&&(x%17<2||y%23<2))material=palette[4];
                grid[i]=new(){MaterialIndex=material,IsActive=random.Next(8)==0?0u:1u,
                    Mass=table[material].Density,Temperature=random.Next(20,1600)};
                motion[i]=new(){VelocityX=random.Next(-30,31),VelocityY=random.Next(-30,31)};
                if(layout=="filters"&&x%13==0){filters[i+1]=1u<<19;filters[0]++;}
            }
            var air=Enumerable.Range(0,n).Select(_=>new AirCell{Pressure=random.Next(-30,31),
                VelocityX=random.Next(-15,16),VelocityY=random.Next(-15,16)}).ToArray();
            var pulse=Enumerable.Range(0,n).Select(_=>new Vector4(random.Next(5),.5f,-.3f,2)).ToArray();
            var projection=Enumerable.Range(0,n).Select(_=>new Vector2(17,-3)).ToArray();
            var impulses=Enumerable.Range(0,n).Select(_=>new GasAirImpulse{X=17,Y=-23}).ToArray();
            c.UpdateSubresource(grid,r.Grid.ReadBuffer);c.UpdateSubresource(motion,r.GasMotion.Buffer);
            c.UpdateSubresource(filters,r.Filters.Buffer);c.UpdateSubresource(pulse,r.ReactionPulse.ReadBuffer);
            var constants=new AirSimulationConstants{AirWidth=(uint)r.AirWidth,AirHeight=(uint)r.AirHeight,
                AirGridWidth=(uint)r.Width,AirGridHeight=(uint)r.Height,AirSandboxMode=sandbox,
                AirAmbientTemperature=20,AirHotScale=.1f};
            c.UpdateSubresource(ref constants,r.AirConstants);c.ComputeShader.SetConstantBuffer(0,r.AirConstants);
            c.ComputeShader.SetShaderResource(1,r.Grid.ReadView);
            c.ComputeShader.SetUnorderedAccessView(6,r.CellMaterials.UnorderedView);
            c.ComputeShader.Set(r.AirFineMaterialsShader);c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);Unbind();
            byte[][] Inject(bool mapped)
            {
                c.UpdateSubresource(air,r.Air.Buffer);c.UpdateSubresource(projection,r.AirProjectionB.Buffer);
                c.UpdateSubresource(impulses,r.GasAirImpulse.Buffer);
                c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Grid.ReadView,r.GasMotion.View,
                    r.AirThermal.View,r.ReactionPulse.ReadView,r.CellMaterials.View);
                c.ComputeShader.SetShaderResource(15,r.Filters.View);
                if(mapped)
                {
                    c.ComputeShader.SetUnorderedAccessView(6,r.AirSourceNodes.UnorderedView);
                    c.ComputeShader.Set(r.AirMapSourcesShader);c.Dispatch((r.Width+15)/16,(r.Height+15)/16,1);
                    c.ComputeShader.SetUnorderedAccessView(6,null);c.ComputeShader.SetShaderResource(6,r.AirSourceNodes.View);
                }
                c.ComputeShader.SetUnorderedAccessViews(0,r.Air.UnorderedView,r.AirScratch.UnorderedView,
                    r.GasAirImpulse.UnorderedView,r.AirFlowLinks.UnorderedView,r.AirProjectionA.UnorderedView,r.AirProjectionB.UnorderedView);
                c.ComputeShader.Set(mapped?r.AirInjectMappedShader:r.AirInjectShader);
                c.Dispatch((r.AirWidth+7)/8,(r.AirHeight+7)/8,1);Unbind();
                return new[]{r.Air.Buffer,r.AirFlowLinks.Buffer,r.AirProjectionB.Buffer,r.GasAirImpulse.Buffer}
                    .Select(buffer=>AirInventoryRegressionVerifier.Read(r,buffer)).ToArray();
            }
            byte[][] reference=Inject(false),actual=Inject(true);
            bool pass=reference.Zip(actual).All(pair=>pair.First.AsSpan().SequenceEqual(pair.Second));
            Console.WriteLine($"PHYXEL_SOURCE_MAP_CHECK pass={pass} byte exact {r.Width}x{r.Height} sandbox={sandbox} {layout}");
            if(!pass)throw new InvalidOperationException("Air source mapping changed the existing injection.");
            checks++;
        }
        c.UpdateSubresource(new uint[r.Width*r.Height+1],r.Filters.Buffer);
        return checks;
    }
}
