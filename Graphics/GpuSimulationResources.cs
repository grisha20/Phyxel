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
    public required GpuStructuredBuffer<uint> ComponentParents { get; init; }
    public required GpuStructuredBuffer<uint> BodyFlags { get; init; }
    public required GpuStructuredBuffer<uint> SolidBodyGeometry { get; init; }
    public required GpuStructuredBuffer<uint> SolidBodyMass { get; init; }
    public required GpuStructuredBuffer<uint> PathBlockerMasks { get; init; }
    public required GpuStructuredBuffer<uint> CellMaterials { get; init; }
    public required GpuStructuredBuffer<WaterPressureRouteData> WaterPressureRoutes { get; init; }
    public required GpuStructuredBuffer<WaterPressureRouteData> WaterPressureRouteScratch { get; init; }
    public required GpuBufferPair<SimulationStatistics> Statistics { get; init; }
    public required GpuUploadBuffer<BrushDrawCommand> Commands { get; init; }
    public required GpuUploadBuffer<MaterialProperties> Materials { get; init; }
    public required GpuUploadBuffer<MaterialEmissionProperties> Emissions { get; init; }
    public required Buffer FrameConstants { get; init; }
    public required Buffer ThermalConstants { get; init; }
    public required Buffer ContactTransitionConstants { get; init; }
    public required Buffer PhaseConstants { get; init; }
    public required GpuStructuredBuffer<uint> PhaseSummary { get; init; }
    public required GpuPhaseSummaryReadbackSlot[] PhaseSummaryReadbackSlots { get; init; }
    public required Buffer CombustionConstants { get; init; }
    public required GpuStructuredBuffer<uint> CombustionSummary { get; init; }
    public required GpuStructuredBuffer<uint> EmissionClaims { get; init; }
    public required GpuStructuredBuffer<EmissionRequest> EmissionRequests { get; init; }
    public required Buffer EmissionConstants { get; init; }
    public required GpuPhaseSummaryReadbackSlot[] CombustionSummaryReadbackSlots { get; init; }
    public required Buffer TemperatureProbeConstants { get; init; }
    public required GpuStructuredBuffer<TemperatureProbeResult> TemperatureProbeResult { get; init; }
    public required Buffer TemperatureProbeStaging { get; init; }
    public required Query TemperatureProbeQuery { get; init; }
    public required Query ThermalTimestampDisjointQuery { get; init; }
    public required Query ThermalTimestampStartQuery { get; init; }
    public required Query ThermalTimestampEndQuery { get; init; }
    public required Query ContactTimestampDisjointQuery { get; init; }
    public required Query ContactTimestampStartQuery { get; init; }
    public required Query ContactTimestampEndQuery { get; init; }
    public required Query GasTimestampDisjointQuery { get; init; }
    public required Query GasTimestampStartQuery { get; init; }
    public required Query GasTimestampEndQuery { get; init; }
    public required Query PhaseTimestampDisjointQuery { get; init; }
    public required Query PhaseTimestampStartQuery { get; init; }
    public required Query PhaseTimestampEndQuery { get; init; }
    public required Query CombustionTimestampDisjointQuery { get; init; }
    public required Query CombustionTimestampStartQuery { get; init; }
    public required Query CombustionTimestampEndQuery { get; init; }
    public required Query ProbeTimestampDisjointQuery { get; init; }
    public required Query ProbeTimestampStartQuery { get; init; }
    public required Query ProbeTimestampEndQuery { get; init; }
    public required Buffer StatisticsStaging { get; init; }
    public required Query StatisticsQuery { get; init; }
    public required Buffer GridStaging { get; init; }
    public required Query SceneTransferQuery { get; init; }
    public required GpuRenderTexturePair CompositionTargets { get; init; }
    public required KniTexture2D[] PresentationTextures { get; init; }
    public required SharpDX.Direct3D11.Texture2D[] NativePresentationTextures { get; init; }
    public int PresentationIndex { get; set; }
    public KniTexture2D PresentationTexture => PresentationTextures[1 - PresentationIndex];
    public SharpDX.Direct3D11.Texture2D NativePresentationTexture => NativePresentationTextures[PresentationIndex];
    public SharpDX.Direct3D11.Texture2D NativeReadTexture => NativePresentationTextures[1 - PresentationIndex];
    public ComputeShader? BrushShader { get; init; }
    public ComputeShader? CellularAutomataShader { get; init; }
    public ComputeShader? GasRedistributionShader { get; init; }
    public ComputeShader? ComponentInitializeShader { get; init; }
    public ComputeShader? ComponentUnionShader { get; init; }
    public ComputeShader? ComponentCompressShader { get; init; }
    public ComputeShader? ComponentFinalizeShader { get; init; }
    public ComputeShader? SolidGeometryAnalyzeShader { get; init; }
    public ComputeShader? SolidAnalyzeShader { get; init; }
    public ComputeShader? SolidDisplacementPlanShader { get; init; }
    public ComputeShader? SolidMoveShader { get; init; }
    public ComputeShader? SolidDisplacementApplyShader { get; init; }
    public ComputeShader? CompositionShader { get; init; }
    public ComputeShader? ThermalDiffusionShader { get; init; }
    public ComputeShader? ContactTransitionShader { get; init; }
    public ComputeShader? PhaseTransitionShader { get; init; }
    public ComputeShader? CombustionShader { get; init; }
    public ComputeShader? EmissionResolveShader { get; init; }
    public ComputeShader? TransientLifecycleShader { get; init; }
    public ComputeShader? TemperatureProbeShader { get; init; }

    public void Dispose()
    {
        Context.ComputeShader.Set(null);
        Context.ComputeShader.SetShaderResources(0, null, null, null, null, null, null, null);
        Context.ComputeShader.SetUnorderedAccessViews(0, null, null, null, null, null, null);
        CompositionShader?.Dispose();
        TemperatureProbeShader?.Dispose();
        PhaseTransitionShader?.Dispose();
        CombustionShader?.Dispose();
        EmissionResolveShader?.Dispose();
        TransientLifecycleShader?.Dispose();
        ThermalDiffusionShader?.Dispose();
        ContactTransitionShader?.Dispose();
        SolidDisplacementApplyShader?.Dispose();
        SolidMoveShader?.Dispose();
        SolidDisplacementPlanShader?.Dispose();
        SolidAnalyzeShader?.Dispose();
        SolidGeometryAnalyzeShader?.Dispose();
        ComponentFinalizeShader?.Dispose();
        ComponentCompressShader?.Dispose();
        ComponentUnionShader?.Dispose();
        ComponentInitializeShader?.Dispose();
        CellularAutomataShader?.Dispose();
        GasRedistributionShader?.Dispose();
        BrushShader?.Dispose();
        foreach (KniTexture2D texture in PresentationTextures)
        {
            texture.Dispose();
        }
        CompositionTargets.Dispose();
        StatisticsQuery.Dispose();
        StatisticsStaging.Dispose();
        SceneTransferQuery.Dispose();
        GridStaging.Dispose();
        FrameConstants.Dispose();
        TemperatureProbeQuery.Dispose();
        ThermalTimestampEndQuery.Dispose();
        ThermalTimestampStartQuery.Dispose();
        ThermalTimestampDisjointQuery.Dispose();
        ContactTimestampEndQuery.Dispose();
        ContactTimestampStartQuery.Dispose();
        ContactTimestampDisjointQuery.Dispose();
        GasTimestampEndQuery.Dispose();
        GasTimestampStartQuery.Dispose();
        GasTimestampDisjointQuery.Dispose();
        PhaseTimestampEndQuery.Dispose();
        PhaseTimestampStartQuery.Dispose();
        PhaseTimestampDisjointQuery.Dispose();
        CombustionTimestampEndQuery.Dispose();
        CombustionTimestampStartQuery.Dispose();
        CombustionTimestampDisjointQuery.Dispose();
        ProbeTimestampEndQuery.Dispose();
        ProbeTimestampStartQuery.Dispose();
        ProbeTimestampDisjointQuery.Dispose();
        TemperatureProbeStaging.Dispose();
        TemperatureProbeResult.Dispose();
        TemperatureProbeConstants.Dispose();
        ThermalConstants.Dispose();
        ContactTransitionConstants.Dispose();
        foreach (GpuPhaseSummaryReadbackSlot slot in PhaseSummaryReadbackSlots)
        {
            slot.Dispose();
        }
        PhaseSummary.Dispose();
        PhaseConstants.Dispose();
        foreach (GpuPhaseSummaryReadbackSlot slot in CombustionSummaryReadbackSlots)
        {
            slot.Dispose();
        }
        CombustionSummary.Dispose();
        EmissionRequests.Dispose();
        EmissionClaims.Dispose();
        EmissionConstants.Dispose();
        CombustionConstants.Dispose();
        Materials.Dispose();
        Emissions.Dispose();
        Commands.Dispose();
        Statistics.Dispose();
        WaterPressureRouteScratch.Dispose();
        WaterPressureRoutes.Dispose();
        CellMaterials.Dispose();
        PathBlockerMasks.Dispose();
        BodyFlags.Dispose();
        SolidBodyMass.Dispose();
        SolidBodyGeometry.Dispose();
        ComponentParents.Dispose();
        Grid.Dispose();
    }
}
