using System;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Physics;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using KniTexture2D = Microsoft.Xna.Framework.Graphics.Texture2D;

namespace Phyxel.Graphics;

public sealed class GpuSimulationResources : IDisposable
{
    internal GpuStageTimer? AirTimer { get; init; }
    internal GpuStageTimer? CellularTimer { get; init; }
    internal GpuStageTimer? WaterConvectionTimer { get; init; }
    internal System.Collections.Generic.Dictionary<uint,GpuStageTimer>? CellularPhaseTimers { get; init; }
    internal GpuStageTimer? AirHeatTimer { get; init; }
    internal GpuStageTimer? GasMotionTimer { get; init; }
    internal GpuStageTimer? AirInjectTimer { get; init; }
    internal GpuStageTimer? ReactionGatherTimer { get; init; }
    internal GpuStageTimer? AirProjectionTimer { get; init; }
    public required Buffer PressureFrameConstants { get; init; }
    public required GpuStructuredBuffer<uint> FragmentPlans { get; init; }
    public required GpuStructuredBuffer<uint> FragmentClaims { get; init; }
    public required GpuStructuredBuffer<uint> FragmentRelease { get; init; }
    public required GpuStructuredBuffer<uint> PressureRoots { get; init; }
    public required GpuStructuredBuffer<uint> PressureLinks { get; init; }
    public required GpuStructuredBuffer<uint> PressureGraphSchedule { get; init; }
    public required Buffer PressureGraphArguments { get; init; }
    public required GpuStructuredBuffer<uint> PressureGeometry { get; init; }
    internal bool PressureGraphValid { get; set; }
    public ComputeShader? PressureGeometryShader { get; init; }
    public ComputeShader? PressureScheduleShader { get; init; }
    public ComputeShader? PressureInitializeShader { get; init; }
    public ComputeShader? PressureUnionShader { get; init; }
    public ComputeShader? PressureCompressShader { get; init; }
    internal bool LegacyFragmentDiagnostics { get; init; }
    public ComputeShader? FragmentReleaseShader { get; init; }
    public ComputeShader? PowderFrontShader { get; init; }
    public ComputeShader? FragmentUpdateOnlyShader { get; init; }
    public ComputeShader? FragmentAdvectOnlyShader { get; init; }
    public ComputeShader? FractureUpdateShader { get; init; }
    public ComputeShader? FragmentPlanShader { get; init; }
    public ComputeShader? FragmentApplyShader { get; init; }
    public required GpuStructuredBuffer<uint> GasActiveTiles { get; init; }
    public ComputeShader? GasActiveTilesShader { get; init; }
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
    public required GpuStructuredBuffer<BodyBalanceData> SolidBalance { get; init; }
    public required GpuStructuredBuffer<uint> SolidRotationTargets { get; init; }
    public required GpuStructuredBuffer<uint> SolidRotationBlocked { get; init; }
    public required GpuBufferPair<uint> SolidOrigins { get; init; }
    public required GpuStructuredBuffer<BodyRotationPlan> SolidRotationPlans { get; init; }
    public required GpuStructuredBuffer<uint> PathBlockerMasks { get; init; }
    public required GpuStructuredBuffer<uint> CellMaterials { get; init; }
    public required GpuStructuredBuffer<uint> Filters { get; init; }
    public required uint[] FilterMap { get; init; }
    public int FilterCount { get; set; }
    private uint[]? filterUpload;
    private Buffer? filterOccupancyStaging;
    internal Buffer GetFilterOccupancyStaging(int bytes)
    {
        if (filterOccupancyStaging is null || filterOccupancyStaging.Description.SizeInBytes < bytes)
        {
            filterOccupancyStaging?.Dispose();
            filterOccupancyStaging = new Buffer(Device, new BufferDescription
            {
                SizeInBytes = bytes,
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            });
        }
        return filterOccupancyStaging;
    }
    public void UploadFilters()
    {
        // GPU-only summary word avoids ray scans when the entire map is empty.
        // Saved/CPU maps contain cells only and keep the world v16 layout.
        filterUpload ??= new uint[FilterMap.Length + 1];
        filterUpload[0] = (uint)FilterCount;
        FilterMap.CopyTo(filterUpload, 1);
        Context.ComputeShader.SetShaderResource(15, null);
        Context.UpdateSubresource(filterUpload, Filters.Buffer);
        Context.ComputeShader.SetShaderResource(15, Filters.View);
    }
    public required GpuStructuredBuffer<WaterPressureRouteData> WaterPressureRoutes { get; init; }
    public required GpuStructuredBuffer<WaterPressureRouteData> WaterPressureRouteScratch { get; init; }
    public required GpuBufferPair<SimulationStatistics> Statistics { get; init; }
    public required GpuUploadBuffer<BrushDrawCommand> Commands { get; init; }
    public required GpuUploadBuffer<MaterialProperties> Materials { get; init; }
    internal bool PressureMechanicsPotential { get; set; }
    internal bool ReactionPulsePotential { get; set; }
    public required GpuUploadBuffer<MaterialEmissionProperties> Emissions { get; init; }
    public required Buffer FrameConstants { get; init; }
    public required Buffer ThermalConstants { get; init; }
    public required GpuBufferPair<float> Oxidizer { get; init; }
    public required GpuStructuredBuffer<float> OxidizerDemand { get; init; }
    public required GpuStructuredBuffer<float> OxidizerAvailable { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector2> OxidizerFlux { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector4> OxidizerCarrierFaces { get; init; }
    public required GpuBufferPair<System.Numerics.Vector2> OxidizerCarrierPotential { get; init; }
    public bool OxidizerCarrierWarm { get; set; }
    public ComputeShader? OxidizerCarrierFacesShader { get; init; }
    public ComputeShader? OxidizerCarrierDivergenceShader { get; init; }
    public ComputeShader? OxidizerCarrierJacobiShader { get; init; }
    public ComputeShader? OxidizerCarrierJacobiFourShader { get; init; }
    public required Buffer OxidizerConstants { get; init; }
    public required Buffer OxidizerStaging { get; init; }
    public ComputeShader? OxidizerTransportShader { get; init; }
    public ComputeShader? OxidizerFluxShader { get; init; }
    public ComputeShader? OxidizerConsumeShader { get; init; }
    public required int AirWidth { get; init; }
    public required int AirHeight { get; init; }
    public required Buffer AirConstants { get; init; }
    public required Buffer AirThermalConstants { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector2> AirThermal { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector4> AirThermalFlux { get; init; }
    public required Buffer AirThermalStaging { get; init; }
    public ComputeShader? AirHeatExchangeShader { get; init; }
    public ComputeShader? AirHeatFluxShader { get; init; }
    public ComputeShader? AirHeatTransportShader { get; init; }

    public required GpuStructuredBuffer<AirCell> Air { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector4> ReactionPending { get; init; }
    public required GpuBufferPair<System.Numerics.Vector4> ReactionPulse { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector4> ReactionPulseScratch { get; init; }
    public required Buffer ReactionPendingStaging { get; init; }
    public required Buffer ReactionPulseStaging { get; init; }
    public ComputeShader? ReactionGatherShader { get; init; }
    public ComputeShader? ReactionClearMappedShader { get; init; }
    public ComputeShader? ReactionFacesShader { get; init; }
    public ComputeShader? ReactionCommitShader { get; init; }
    public ComputeShader? ReactionFastFacesShader { get; init; }
    public ComputeShader? ReactionFastCommitShader { get; init; }
    public required GpuStructuredBuffer<AirCell> AirScratch { get; init; }
    // Derived from the current fine grid each air tick, never persisted.
    public required GpuStructuredBuffer<uint> AirFlowLinks { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector2> AirProjectionA { get; init; }
    public required GpuStructuredBuffer<System.Numerics.Vector2> AirProjectionB { get; init; }
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
    public GpuStructuredBuffer<SteamJetInjectionDistributionFrame>? SteamJetInjectionDistribution { get; init; }
    public Buffer? SteamJetInjectionDistributionStaging { get; init; }
    public GpuStructuredBuffer<SteamJetLateralBandStatistics>? SteamJetLateralBands { get; init; }
    public Buffer? SteamJetLateralBandsStaging { get; init; }
    public GpuStructuredBuffer<SteamJetBlockingSubstepStatistics>? SteamJetBlockingSubsteps { get; init; }
    public Buffer? SteamJetBlockingSubstepsStaging { get; init; }
    public GpuStructuredBuffer<SteamJetBlockingFrameStatistics>? SteamJetBlockingFrames { get; init; }
    public Buffer? SteamJetBlockingFramesStaging { get; init; }
    public GpuStructuredBuffer<SteamJetBlockingMarker>? SteamJetBlockingMarkers { get; init; }
    public GpuStructuredBuffer<uint>? SteamJetBlockingMovedFrames { get; init; }
    public GpuStructuredBuffer<SteamJetDiagonalIntentStatistics>? SteamJetDiagonalIntents { get; init; }
    public Buffer? SteamJetDiagonalIntentsStaging { get; init; }
    // Allocated only for PHYXEL_STEAM_JET_AIR_COUPLING_TRACE=1. These are
    // observer outputs and are never bound by a physical solver pass.
    public GpuStructuredBuffer<SteamJetGasMotionContribution>? SteamJetMotionContributions { get; init; }
    public Buffer? SteamJetMotionContributionsStaging { get; init; }
    public GpuStructuredBuffer<SteamJetAirCouplingCell>? SteamJetAirCoupling { get; init; }
    public Buffer? SteamJetAirCouplingStaging { get; init; }
    public required Buffer FireGlowConstants { get; init; }
    public required GpuStructuredBuffer<FireGlowCell> FireGlow { get; init; }
    public required GpuStructuredBuffer<FireGlowCell> FireGlowScratch { get; init; }
    public required GpuStructuredBuffer<FireGlowCell> FireGlowPresentation { get; init; }
    public required GpuStructuredBuffer<FireGlowCell> GasVisual { get; init; }
    public required GpuStructuredBuffer<FireGlowCell> GasVisualScratch { get; init; }
    public required Buffer ContactTransitionConstants { get; init; }
    public required Buffer PhaseConstants { get; init; }
    public required GpuStructuredBuffer<uint> PhaseSummary { get; init; }
    public required GpuStructuredBuffer<uint> ContactSummary { get; init; }
    public GpuStructuredBuffer<uint>? PhaseEventCounters { get; init; }
    public Buffer? PhaseEventStaging { get; init; }
    public GpuStructuredBuffer<ThermalEnergyLedgerCell>? ThermalEnergyLedger { get; init; }
    public required GpuStructuredBuffer<uint> BulkThermalDegrees { get; init; }
    public ComputeShader? BulkThermalDegreesShader { get; init; }
    public required GpuStructuredBuffer<float> RadiantDegrees { get; init; }
    public ComputeShader? RadiantDegreesShader { get; init; }
    public Buffer? ThermalEnergyStaging { get; init; }
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
    public ComputeShader? BroadSurfaceShader { get; init; }
    public ComputeShader? ParallelSurfaceShader { get; init; }
    public ComputeShader? VerticalPairShader { get; init; }
    public ComputeShader? HorizontalPairShader { get; init; }
    public ComputeShader? DiagonalPairShader { get; init; }
    public ComputeShader? AdjacentSurfaceShader { get; init; }
    public ComputeShader? LocalSurfaceShader { get; init; }
    public ComputeShader? ViscousSurfaceShader { get; init; }
    public required GpuStructuredBuffer<uint> PoolColumnSupport { get; init; }
    public ComputeShader? PoolSupportShader { get; init; }
    public ComputeShader? PoolCachedBalanceShader { get; init; }
    public ComputeShader? LiquidSurfaceBalanceShader { get; init; }
    public ComputeShader? GasRedistributionShader { get; init; }
    public ComputeShader? SteamGasStepObserverShader { get; init; }
    public ComputeShader? SteamJetLateralObserverShader { get; init; }
    public ComputeShader? SteamJetBlockingObserverShader { get; init; }
    public ComputeShader? SteamJetBlockingFrameObserverShader { get; init; }
    public ComputeShader? SteamJetDiagonalObserverShader { get; init; }
    public ComputeShader? SteamJetInjectionObserverShader { get; init; }
    public ComputeShader? SteamJetInjectionDistributionObserverShader { get; init; }
    public ComputeShader? SteamJetMotionObserverShader { get; init; }
    public ComputeShader? SteamJetAirCouplingObserverShader { get; init; }
    public ComputeShader? ComponentInitializeShader { get; init; }
    public ComputeShader? ComponentUnionShader { get; init; }
    public ComputeShader? ComponentCompressShader { get; init; }
    public ComputeShader? ComponentFinalizeShader { get; init; }
    public ComputeShader? SolidGeometryAnalyzeShader { get; init; }
    public ComputeShader? SolidBalanceShader { get; init; }
    public ComputeShader? SolidRotationPlanShader { get; init; }
    public ComputeShader? SolidRotationApplyShader { get; init; }
    public ComputeShader? SolidOriginsInitializeShader { get; init; }
    public ComputeShader? SolidRotationBuildShader { get; init; }
    public ComputeShader? SolidAnalyzeShader { get; init; }
    public ComputeShader? SolidDisplacementPlanShader { get; init; }
    public ComputeShader? SolidMoveShader { get; init; }
    public ComputeShader? SolidDisplacementApplyShader { get; init; }
    public ComputeShader? CompositionShader { get; init; }
    public ComputeShader? ThermalDiffusionShader { get; init; }
    public ComputeShader? WaterConvectionShader { get; init; }
    public ComputeShader? WaterQuenchShader { get; init; }
    public ComputeShader? WaterQuenchTilesShader { get; init; }
    public GpuStructuredBuffer<uint> WaterQuenchTiles { get; init; } = null!;
    public ComputeShader? WaterColumnsShader { get; init; }
    public ComputeShader? WaterConvectShader { get; init; }
    public ComputeShader? WaterMixShader { get; init; }
    public ComputeShader? WaterColumnMovementShader { get; init; }
    public required GpuStructuredBuffer<uint> WaterMovementColumns { get; init; }
    public ComputeShader? AirInjectShader { get; init; }
    public required GpuStructuredBuffer<uint> AirSourceNodes { get; init; }
    public ComputeShader? AirMapSourcesShader { get; init; }
    public ComputeShader? AirInjectMappedShader { get; init; }
    public ComputeShader? AirFineMaterialsShader { get; init; }
    public ComputeShader? AirPressureShader { get; init; }
    public ComputeShader? AirJacobiFourABShader { get; init; }
    public ComputeShader? AirJacobiFourBAShader { get; init; }
    public ComputeShader? AirVelocityShader { get; init; }
    public ComputeShader? AirAdvectShader { get; init; }
    public ComputeShader? AirCommitShader { get; init; }
    public ComputeShader? AirClearShader { get; init; }
    public ComputeShader? AirDivergenceShader { get; init; }
    public ComputeShader? AirFacesShader { get; init; }
    public ComputeShader? AirJacobiABShader { get; init; }
    public ComputeShader? AirJacobiBAShader { get; init; }
    internal GpuStageTimer? FragmentTimer { get; set; }
    internal GpuStageTimer? ConfinementTimer { get; set; }
    public ComputeShader? AirProjectShader { get; init; }
    public ComputeShader? FireGlowDepositShader { get; init; }
    public ComputeShader? FireGlowDiffuseShader { get; init; }
    public ComputeShader? FireGlowCommitShader { get; init; }
    public ComputeShader? FireGlowClearShader { get; init; }
    public ComputeShader? GasVisualDepositShader { get; init; }
    public ComputeShader? GasVisualDiffuseShader { get; init; }
    public ComputeShader? GasVisualCommitShader { get; init; }
    public ComputeShader? ContactTransitionShader { get; init; }
    public ComputeShader? MoistureShader { get; init; }
    public ComputeShader? PhaseTransitionShader { get; init; }
    public ComputeShader? CombustionShader { get; init; }
    public ComputeShader? EmissionResolveShader { get; init; }
    public ComputeShader? EmissionHeatConsumeShader { get; init; }
    public ComputeShader? TransientLifecycleShader { get; init; }
    public ComputeShader? TemperatureProbeShader { get; init; }
    public ComputeShader? TemperatureSensorsShader { get; init; }

    public void Dispose()
    {
        FragmentTimer?.Dispose(); ConfinementTimer?.Dispose();
        Context.ComputeShader.Set(null);
        Context.ComputeShader.SetShaderResources(0, null, null, null, null, null, null, null);
        Context.ComputeShader.SetUnorderedAccessViews(0, null, null, null, null, null, null);
        CompositionShader?.Dispose();
        TemperatureProbeShader?.Dispose();
        TemperatureSensorsShader?.Dispose();
        PhaseTransitionShader?.Dispose();
        CombustionShader?.Dispose();
        PressureFrameConstants.Dispose(); FragmentPlans.Dispose(); FragmentClaims.Dispose(); FragmentRelease.Dispose();
        PressureRoots.Dispose();PressureLinks.Dispose();
        PressureGraphSchedule.Dispose();
        PressureGraphArguments.Dispose();
        PressureGeometry.Dispose();
        PressureGeometryShader?.Dispose();PressureScheduleShader?.Dispose();
        PressureInitializeShader?.Dispose();PressureUnionShader?.Dispose();PressureCompressShader?.Dispose();
        FragmentReleaseShader?.Dispose(); PowderFrontShader?.Dispose();
        FractureUpdateShader?.Dispose(); FragmentUpdateOnlyShader?.Dispose(); FragmentPlanShader?.Dispose(); FragmentApplyShader?.Dispose();
        FragmentAdvectOnlyShader?.Dispose();
        EmissionResolveShader?.Dispose();
        EmissionHeatConsumeShader?.Dispose();
        TransientLifecycleShader?.Dispose();
        ThermalDiffusionShader?.Dispose();
        WaterConvectionShader?.Dispose();
        WaterQuenchShader?.Dispose(); WaterColumnsShader?.Dispose();
        WaterQuenchTilesShader?.Dispose(); WaterQuenchTiles.Dispose();
        WaterConvectShader?.Dispose(); WaterMixShader?.Dispose();
        WaterColumnMovementShader?.Dispose();
        WaterMovementColumns.Dispose();
        AirInjectShader?.Dispose();
        AirSourceNodes.Dispose();
        AirMapSourcesShader?.Dispose();
        AirInjectMappedShader?.Dispose();
        AirFineMaterialsShader?.Dispose();
        AirPressureShader?.Dispose();
        AirJacobiFourABShader?.Dispose();AirJacobiFourBAShader?.Dispose();
        AirVelocityShader?.Dispose();
        AirAdvectShader?.Dispose();
        AirCommitShader?.Dispose();
        AirClearShader?.Dispose();
        AirDivergenceShader?.Dispose();
        AirFacesShader?.Dispose();
        AirJacobiABShader?.Dispose();
        AirJacobiBAShader?.Dispose();
        AirProjectShader?.Dispose();
        FireGlowDepositShader?.Dispose();
        FireGlowDiffuseShader?.Dispose();
        FireGlowCommitShader?.Dispose();
        FireGlowClearShader?.Dispose();
        GasVisualDepositShader?.Dispose();
        GasVisualDiffuseShader?.Dispose();
        GasVisualCommitShader?.Dispose();
        ContactTransitionShader?.Dispose();
        MoistureShader?.Dispose();
        SolidDisplacementApplyShader?.Dispose();
        SolidMoveShader?.Dispose();
        SolidDisplacementPlanShader?.Dispose();
        SolidAnalyzeShader?.Dispose();
        SolidGeometryAnalyzeShader?.Dispose();
        SolidBalanceShader?.Dispose();
        SolidRotationPlanShader?.Dispose();
        SolidRotationApplyShader?.Dispose();
        SolidOriginsInitializeShader?.Dispose();
        SolidRotationBuildShader?.Dispose();
        ComponentFinalizeShader?.Dispose();
        ComponentCompressShader?.Dispose();
        ComponentUnionShader?.Dispose();
        ComponentInitializeShader?.Dispose();
        CellularAutomataShader?.Dispose();
        BroadSurfaceShader?.Dispose();
        ParallelSurfaceShader?.Dispose();
        VerticalPairShader?.Dispose();
        HorizontalPairShader?.Dispose();
        DiagonalPairShader?.Dispose();
        AdjacentSurfaceShader?.Dispose();
        LocalSurfaceShader?.Dispose();
        ViscousSurfaceShader?.Dispose();
        PoolColumnSupport.Dispose();
        PoolSupportShader?.Dispose();
        PoolCachedBalanceShader?.Dispose();
        LiquidSurfaceBalanceShader?.Dispose();
        GasRedistributionShader?.Dispose();
        SteamGasStepObserverShader?.Dispose();
        SteamJetLateralObserverShader?.Dispose();
        SteamJetBlockingObserverShader?.Dispose();
        SteamJetBlockingFrameObserverShader?.Dispose();
        SteamJetDiagonalObserverShader?.Dispose();
        SteamJetInjectionObserverShader?.Dispose();
        SteamJetInjectionDistributionObserverShader?.Dispose();
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
        filterOccupancyStaging?.Dispose();
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
        OxidizerTransportShader?.Dispose();
        OxidizerFluxShader?.Dispose();
        OxidizerConsumeShader?.Dispose();
        Oxidizer.Dispose();
        OxidizerDemand.Dispose();
        OxidizerAvailable.Dispose();
        OxidizerFlux.Dispose();
        OxidizerCarrierFaces.Dispose();
        OxidizerCarrierPotential.Dispose();
        OxidizerCarrierFacesShader?.Dispose();
        OxidizerCarrierDivergenceShader?.Dispose();
        OxidizerCarrierJacobiShader?.Dispose();
        OxidizerCarrierJacobiFourShader?.Dispose();
        OxidizerConstants.Dispose();
        OxidizerStaging.Dispose();
        FireGlowScratch.Dispose();
        FireGlowPresentation.Dispose();
        GasVisual.Dispose();
        GasVisualScratch.Dispose();
        FireGlow.Dispose();
        FireGlowConstants.Dispose();
        AirScratch.Dispose();
        ReactionPending.Dispose();ReactionPulse.Dispose();ReactionPulseScratch.Dispose();
        ReactionPendingStaging.Dispose();ReactionPulseStaging.Dispose();
        ReactionGatherShader?.Dispose();ReactionClearMappedShader?.Dispose();
        ReactionFacesShader?.Dispose();ReactionCommitShader?.Dispose();
        ReactionFastFacesShader?.Dispose();ReactionFastCommitShader?.Dispose();
        AirFlowLinks.Dispose();
        AirProjectionA.Dispose();
        AirProjectionB.Dispose();
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
        SteamJetInjectionDistribution?.Dispose();
        SteamJetInjectionDistributionStaging?.Dispose();
        SteamJetLateralBands?.Dispose();
        SteamJetLateralBandsStaging?.Dispose();
        SteamJetBlockingSubsteps?.Dispose();
        SteamJetBlockingSubstepsStaging?.Dispose();
        SteamJetBlockingFrames?.Dispose();
        SteamJetBlockingFramesStaging?.Dispose();
        SteamJetBlockingMarkers?.Dispose();
        SteamJetBlockingMovedFrames?.Dispose();
        SteamJetDiagonalIntents?.Dispose();
        SteamJetDiagonalIntentsStaging?.Dispose();
        SteamJetMotionContributions?.Dispose();
        SteamJetMotionContributionsStaging?.Dispose();
        SteamJetAirCoupling?.Dispose();
        SteamJetAirCouplingStaging?.Dispose();
        AirConstants.Dispose();
        AirTimer?.Dispose(); AirHeatTimer?.Dispose(); GasMotionTimer?.Dispose();
        CellularTimer?.Dispose(); WaterConvectionTimer?.Dispose();
        if(CellularPhaseTimers is not null)foreach(var timer in CellularPhaseTimers.Values)timer.Dispose();
        AirInjectTimer?.Dispose(); ReactionGatherTimer?.Dispose(); AirProjectionTimer?.Dispose();
        GasActiveTiles.Dispose(); GasActiveTilesShader?.Dispose();
        AirThermalConstants.Dispose(); AirThermal.Dispose(); AirThermalFlux.Dispose(); AirThermalStaging.Dispose();
        AirHeatExchangeShader?.Dispose(); AirHeatFluxShader?.Dispose(); AirHeatTransportShader?.Dispose();
        ContactTransitionConstants.Dispose();
        foreach (GpuPhaseSummaryReadbackSlot slot in PhaseSummaryReadbackSlots)
        {
            slot.Dispose();
        }
        PhaseSummary.Dispose();
        ContactSummary.Dispose();
        PhaseEventCounters?.Dispose();
        PhaseEventStaging?.Dispose();
        ThermalEnergyLedger?.Dispose();
        BulkThermalDegrees.Dispose(); BulkThermalDegreesShader?.Dispose();
        RadiantDegrees.Dispose(); RadiantDegreesShader?.Dispose();
        ThermalEnergyStaging?.Dispose();
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
        CellMaterials.Dispose(); Filters.Dispose();
        PathBlockerMasks.Dispose();
        BodyFlags.Dispose();
        SolidBodyMass.Dispose();
        SolidBalance.Dispose();
        SolidRotationTargets.Dispose();
        SolidRotationBlocked.Dispose();
        SolidOrigins.Dispose();
        SolidRotationPlans.Dispose();
        SolidBodyGeometry.Dispose();
        ComponentParents.Dispose();
        Grid.Dispose();
    }
}
