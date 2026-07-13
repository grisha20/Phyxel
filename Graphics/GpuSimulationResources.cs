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
    public required bool IsSimulationAllocated { get; init; }
    public required GpuBufferPair<GridCell> Grid { get; init; }
    public required GpuBufferPair<LatticeParticle> Particles { get; init; }
    public required GpuBufferPair<LatticeBond> Bonds { get; init; }
    public required GpuBufferPair<uint> ActivatedBodyWords { get; init; }
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
    public ComputeShader? BrushShader { get; init; }
    public ComputeShader? CellularAutomataShader { get; init; }
    public ComputeShader? LatticeShader { get; init; }
    public ComputeShader? LatticeTopologyShader { get; init; }
    public ComputeShader? LatticeOccupancyClearShader { get; init; }
    public ComputeShader? LatticeProjectionShader { get; init; }
    public ComputeShader? CompositionShader { get; init; }

    public void Dispose()
    {
        Context.ComputeShader.Set(null);
        Context.ComputeShader.SetShaderResources(0, null, null, null, null);
        Context.ComputeShader.SetUnorderedAccessViews(0, null, null, null, null);
        CompositionShader?.Dispose();
        LatticeProjectionShader?.Dispose();
        LatticeOccupancyClearShader?.Dispose();
        LatticeTopologyShader?.Dispose();
        LatticeShader?.Dispose();
        CellularAutomataShader?.Dispose();
        BrushShader?.Dispose();
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
        ActivatedBodyWords.Dispose();
        Bonds.Dispose();
        Particles.Dispose();
        Grid.Dispose();
    }
}
