using System;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Physics;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using KniTexture2D = Microsoft.Xna.Framework.Graphics.Texture2D;

namespace Phyxel.Graphics;

public sealed class GpuSimulationResources : IDisposable
{
    public required Device Device { get; init; }
    public required DeviceContext Context { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required GpuBufferPair<GridCell> Grid { get; init; }
    public required GpuBufferPair<LatticeParticle> Particles { get; init; }
    public required GpuBufferPair<LatticeBond> Bonds { get; init; }
    public required GpuBufferPair<SimulationStatistics> Statistics { get; init; }
    public required GpuUploadBuffer<BrushDrawCommand> Commands { get; init; }
    public required GpuUploadBuffer<MaterialProperties> Materials { get; init; }
    public required Buffer FrameConstants { get; init; }
    public required Buffer StatisticsStaging { get; init; }
    public required Query StatisticsQuery { get; init; }
    public required Buffer GridStaging { get; init; }
    public required Buffer ParticlesStaging { get; init; }
    public required Buffer BondsStaging { get; init; }
    public required Query SceneTransferQuery { get; init; }
    public required GpuRenderTexturePair CompositionTargets { get; init; }
    public required KniTexture2D PresentationTexture { get; init; }
    public required SharpDX.Direct3D11.Texture2D NativePresentationTexture { get; init; }
    public required ComputeShader BrushShader { get; init; }
    public required ComputeShader CellularAutomataShader { get; init; }
    public required ComputeShader LatticeShader { get; init; }
    public required ComputeShader CompositionShader { get; init; }

    public void Dispose()
    {
        Context.ComputeShader.Set(null);
        Context.ComputeShader.SetShaderResources(0, null, null, null, null);
        Context.ComputeShader.SetUnorderedAccessViews(0, null, null, null, null);
        CompositionShader.Dispose();
        LatticeShader.Dispose();
        CellularAutomataShader.Dispose();
        BrushShader.Dispose();
        PresentationTexture.Dispose();
        CompositionTargets.Dispose();
        StatisticsQuery.Dispose();
        StatisticsStaging.Dispose();
        SceneTransferQuery.Dispose();
        BondsStaging.Dispose();
        ParticlesStaging.Dispose();
        GridStaging.Dispose();
        FrameConstants.Dispose();
        Materials.Dispose();
        Commands.Dispose();
        Statistics.Dispose();
        Bonds.Dispose();
        Particles.Dispose();
        Grid.Dispose();
    }
}
