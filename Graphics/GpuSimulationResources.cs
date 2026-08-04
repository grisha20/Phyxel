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
    public required int AirWidth { get; init; }
    public required int AirHeight { get; init; }
    public required Buffer AirConstants { get; init; }
    public required GpuStructuredBuffer<AirCell> Air { get; init; }
    public required GpuStructuredBuffer<AirCell> AirScratch { get; init; }
    public required GpuStructuredBuffer<GasAirImpulse> GasAirImpulse { get; init; }
    public required Buffer AirStaging { get; init; }
    public required GpuStructuredBuffer<GasMotionState> GasMotion { get; init; }
    public required Buffer GasMotionStaging { get; init; }
    public required GpuStructuredBuffer<GasObstacleBypassStatistics> GasObstacleBypassStatistics { get; init; }
    public required Buffer GasObstacleBypassStatisticsStaging { get; init; }
    public required GpuStructuredBuffer<GasVerticalMotionStatistics> GasVerticalMotionStatistics { get; init; }
    public required Buffer GasVerticalMotionStatisticsStaging { get; init; }
    public required GpuStructuredBuffer<uint> GasVerticalBlockFrameMarkers { get; init; }
    public required GpuStructuredBuffer<uint> GasLateralTransferStatistics { get; init; }
    public required Buffer GasLateralTransferStatisticsStaging { get; init; }
    public required GpuStructuredBuffer<SteamGasStepStatistics> SteamGasStepStatistics { get; init; }
    public required Buffer SteamGasStepStatisticsStaging { get; init; }
    public required GpuStructuredBuffer<GridCell> SteamGasStepPreviousGrid { get; init; }
    public required GpuStructuredBuffer<GasMotionState> SteamGasStepPreviousMotion { get; init; }
    public required GpuStructuredBuffer<SteamJetInjectionStatistics> SteamJetInjectionStatistics { get; init; }
    public required Buffer SteamJetInjectionStatisticsStaging { get; init; }
    public GpuStructuredBuffer<SteamJetLateralBandStatistics>? SteamJetLateralBands { get; init; }
    public Buffer? SteamJetLateralBandsStaging { get; init; }
    public GpuStructuredBuffer<SteamJetBlockingSubstepStatistics>? SteamJetBlockingSubsteps { get; init; }
    public Buffer? SteamJetBlockingSubstepsStaging { get; init; }
    public GpuStructuredBuffer<SteamJetBlockingFrameStatistics>? SteamJetBlockingFrames { get; init; }
    public Buffer? SteamJetBlockingFramesStaging { get; init; }
    public GpuStructuredBuffer<SteamJetBlockingMarker>? SteamJetBlockingMarkers { get; init; }
    public GpuStructuredBuffer<uint>? SteamJetBlockingMovedFrames { get; init; }
    // Allocated only for PHYXEL_STEAM_JET_AIR_COUPLING_TRACE=1. These are
    // observer outputs and are never bound by a physical solver pass.
    public GpuStructuredBuffer<SteamJetGasMotionContribution>? SteamJetMotionContributions { get; init; }
    public Buffer? SteamJetMotionContributionsStaging { get; init; }
    public GpuStructuredBuffer<SteamJetAirCouplingCell>? SteamJetAirCoupling { get; init; }
    public Buffer? SteamJetAirCouplingStaging { get; init; }
    public required Buffer FireGlowConstants { get; init; }
    public required GpuStructuredBuffer<FireGlowCell> FireGlow { get; init; }
    public required GpuStructuredBuffer<FireGlowCell> FireGlowScratch { get; init; }
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
    public ComputeShader? SteamGasStepObserverShader { get; init; }
    public ComputeShader? SteamJetLateralObserverShader { get; init; }
    public ComputeShader? SteamJetBlockingObserverShader { get; init; }
    public ComputeShader? SteamJetBlockingFrameObserverShader { get; init; }
    public ComputeShader? SteamJetInjectionObserverShader { get; init; }
    public ComputeShader? SteamJetMotionObserverShader { get; init; }
    public ComputeShader? SteamJetAirCouplingObserverShader { get; init; }
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
    public ComputeShader? AirInjectShader { get; init; }
    public ComputeShader? AirPressureShader { get; init; }
    public ComputeShader? AirVelocityShader { get; init; }
    public ComputeShader? AirAdvectShader { get; init; }
    public ComputeShader? AirCommitShader { get; init; }
    public ComputeShader? AirClearShader { get; init; }
    public ComputeShader? FireGlowDepositShader { get; init; }
    public ComputeShader? FireGlowDiffuseShader { get; init; }
    public ComputeShader? FireGlowCommitShader { get; init; }
    public ComputeShader? FireGlowClearShader { get; init; }
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
        AirInjectShader?.Dispose();
        AirPressureShader?.Dispose();
        AirVelocityShader?.Dispose();
        AirAdvectShader?.Dispose();
        AirCommitShader?.Dispose();
        AirClearShader?.Dispose();
        FireGlowDepositShader?.Dispose();
        FireGlowDiffuseShader?.Dispose();
        FireGlowCommitShader?.Dispose();
        FireGlowClearShader?.Dispose();
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
        SteamGasStepObserverShader?.Dispose();
        SteamJetLateralObserverShader?.Dispose();
        SteamJetBlockingObserverShader?.Dispose();
        SteamJetBlockingFrameObserverShader?.Dispose();
        SteamJetInjectionObserverShader?.Dispose();
        SteamJetMotionObserverShader?.Dispose();
        SteamJetAirCouplingObserverShader?.Dispose();
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
        FireGlowScratch.Dispose();
        FireGlow.Dispose();
        FireGlowConstants.Dispose();
        AirScratch.Dispose();
        Air.Dispose();
        GasAirImpulse.Dispose();
        AirStaging.Dispose();
        GasMotion.Dispose();
        GasMotionStaging.Dispose();
        GasObstacleBypassStatistics.Dispose();
        GasObstacleBypassStatisticsStaging.Dispose();
        GasVerticalMotionStatistics.Dispose();
        GasVerticalMotionStatisticsStaging.Dispose();
        GasVerticalBlockFrameMarkers.Dispose();
        GasLateralTransferStatistics.Dispose();
        GasLateralTransferStatisticsStaging.Dispose();
        SteamGasStepStatistics.Dispose();
        SteamGasStepStatisticsStaging.Dispose();
        SteamGasStepPreviousGrid.Dispose();
        SteamGasStepPreviousMotion.Dispose();
        SteamJetInjectionStatistics.Dispose();
        SteamJetInjectionStatisticsStaging.Dispose();
        SteamJetLateralBands?.Dispose();
        SteamJetLateralBandsStaging?.Dispose();
        SteamJetBlockingSubsteps?.Dispose();
        SteamJetBlockingSubstepsStaging?.Dispose();
        SteamJetBlockingFrames?.Dispose();
        SteamJetBlockingFramesStaging?.Dispose();
        SteamJetBlockingMarkers?.Dispose();
        SteamJetBlockingMovedFrames?.Dispose();
        SteamJetMotionContributions?.Dispose();
        SteamJetMotionContributionsStaging?.Dispose();
        SteamJetAirCoupling?.Dispose();
        SteamJetAirCouplingStaging?.Dispose();
        AirConstants.Dispose();
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
