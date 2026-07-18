using System;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Graphics;

public sealed class SimulationDispatchCoordinator
{
    public const float FixedThermalStep = 0.05f;
    public const float ThermalExchangeRate = 4f;
    public const int MaximumThermalTicksPerFrame = 4;
    private static readonly uint[] PrimaryEvenPhases =
    [
        32, 0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
        2, 3, 2, 3, 2, 3, 2, 3,
        40, 41, 42, 43, 44, 45, 46, 47,
        48, 50, 52, 54,
        33, 56, 57, 13, 29,
        34, 35, 34, 35, 34, 35, 34, 35,
        36, 37, 71, 38, 39, 70,
        4
    ];

    private static readonly uint[] PrimaryOddPhases =
    [
        32, 1, 0, 1, 0, 1, 0, 1, 0, 6, 5, 8, 7, 10, 9, 12, 11,
        3, 2, 3, 2, 3, 2, 3, 2,
        47, 46, 45, 44, 43, 42, 41, 40,
        55, 53, 51, 49,
        33, 56, 57, 13, 29,
        34, 35, 34, 35, 34, 35, 34, 35,
        36, 37, 71, 38, 39, 70,
        4
    ];

    private static readonly uint[] SecondaryEvenPhases =
    [
        0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
        2, 3, 2, 3, 2, 3, 2, 3,
        40, 41, 42, 43, 44, 45, 46, 47,
        33, 13, 29,
        4
    ];

    private static readonly uint[] SecondaryOddPhases =
    [
        1, 0, 1, 0, 1, 0, 1, 0, 6, 5, 8, 7, 10, 9, 12, 11,
        3, 2, 3, 2, 3, 2, 3, 2,
        47, 46, 45, 44, 43, 42, 41, 40,
        33, 13, 29,
        4
    ];

    private static readonly uint[] OptimizedEvenPhases =
    [
        32, 0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
        2, 3, 2, 3, 2, 3, 2, 3,
        40, 42, 44, 46,
        48, 52,
        33, 56, 57, 13, 29,
        34, 35, 34, 35,
        36, 37, 71, 38, 39, 70,
        4
    ];

    private static readonly uint[] OptimizedOddPhases =
    [
        32, 1, 0, 1, 0, 1, 0, 1, 0, 6, 5, 8, 7, 10, 9, 12, 11,
        3, 2, 3, 2, 3, 2, 3, 2,
        47, 45, 43, 41,
        48, 52,
        33, 56, 57, 13, 29,
        34, 35, 34, 35,
        36, 37, 71, 38, 39, 70,
        4
    ];

    private readonly GpuResourceLifecycleManager lifecycleManager;
    private readonly MaterialRegistry materialRegistry;
    private GpuSimulationResources? boundResources;
    private uint frameIndex;
    private uint lastObservedStatisticsFrame;
    private bool worldHasMatter;
    private bool cellularMatter;
    private bool fluidMatter;
    private bool liquidMatter;
    private bool gasMatter;
    private bool solidMatter;
    private bool cellularSleeping;
    private bool solidSleeping;
    private bool topologyDirty;
    private bool previousSolidGravity;
    private bool previousHydraulicPressure;
    private bool presentationDirty = true;
    private bool finalizeCellularRest;
    private bool waterPressureRoutesDirty = true;
    private bool solidMotionNeedsCellular = true;
    private bool cellMaterialsDirty;
    private bool thermalActive;
    private readonly FixedStepThermalScheduler thermalScheduler = new();
    private bool thermalTimingPending;
    private int thermalTimingSamples;
    private double thermalTimingTotalMilliseconds;
    private double thermalTimingMinimumMilliseconds = double.PositiveInfinity;
    private double thermalTimingMaximumMilliseconds;
    private readonly PhaseTransitionWakeUpGate phaseWakeUpGate = new();
    private ulong phaseReadbackGeneration;
    private ulong phaseFallbackWakeUps;
    private bool phaseTimingPending;
    private int phaseTimingSamples;
    private double phaseTimingTotalMilliseconds;
    private double phaseTimingMinimumMilliseconds = double.PositiveInfinity;
    private double phaseTimingMaximumMilliseconds;
    private ulong phaseDispatches;
    private int maximumPhaseDispatchesPerFrame;
    private PhaseTransitionSummaryFlags lastPhaseSummary;
    private uint lastPhaseDispatchFrame;
    private uint lastCompositionFrame;
    private int settledObservations;
    private int hydraulicWarmupFrames;
    private int fastSettleFrames;
    private int fastMaximumAwakeFrames;

    // Active Region tracking
    private int activeMinX;
    private int activeMinY;
    private int activeMaxX;
    private int activeMaxY;
    private bool activeRegionValid;

    public SimulationDispatchCoordinator(
        GpuResourceLifecycleManager lifecycleManager,
        MaterialRegistry materialRegistry)
    {
        this.lifecycleManager = lifecycleManager;
        this.materialRegistry = materialRegistry;
        ResetActiveRegion();
    }

    private void ResetActiveRegion()
    {
        activeMinX = int.MaxValue;
        activeMinY = int.MaxValue;
        activeMaxX = int.MinValue;
        activeMaxY = int.MinValue;
        activeRegionValid = false;
    }

    private void ExpandActiveRegion(int x, int y, int radius, int width, int height)
    {
        int minX = Math.Clamp(x - radius, 0, width - 1);
        int maxX = Math.Clamp(x + radius, 0, width - 1);
        int minY = Math.Clamp(y - radius, 0, height - 1);
        int maxY = Math.Clamp(y + radius, 0, height - 1);

        if (minX > maxX || minY > maxY)
        {
            return;
        }

        if (!activeRegionValid)
        {
            activeMinX = minX;
            activeMaxX = maxX;
            activeMinY = minY;
            activeMaxY = maxY;
            activeRegionValid = true;
        }
        else
        {
            activeMinX = Math.Min(activeMinX, minX);
            activeMaxX = Math.Max(activeMaxX, maxX);
            activeMinY = Math.Min(activeMinY, minY);
            activeMaxY = Math.Max(activeMaxY, maxY);
        }
    }

    public bool CellularSleeping => cellularSleeping;
    public bool SolidSleeping => solidSleeping;
    public bool SolidMotionNeedsCellular => solidMotionNeedsCellular;
    public int SettledObservations => settledObservations;
    public bool ThermalActive => thermalActive;
    public ulong ThermalTicks => thermalScheduler.TotalTicks;
    public ThermalGpuTimingStatistics ThermalGpuTiming => new(
        thermalTimingSamples,
        thermalTimingSamples == 0 ? 0 : thermalTimingTotalMilliseconds / thermalTimingSamples,
        thermalTimingSamples == 0 ? 0 : thermalTimingMinimumMilliseconds,
        thermalTimingMaximumMilliseconds);
    public ThermalGpuTimingStatistics PhaseGpuTiming => new(
        phaseTimingSamples,
        phaseTimingSamples == 0 ? 0 : phaseTimingTotalMilliseconds / phaseTimingSamples,
        phaseTimingSamples == 0 ? 0 : phaseTimingMinimumMilliseconds,
        phaseTimingMaximumMilliseconds);
    public ulong PhaseDispatches => phaseDispatches;
    public ulong PhaseFallbackWakeUps => phaseFallbackWakeUps;
    public int MaximumPhaseDispatchesPerFrame => maximumPhaseDispatchesPerFrame;
    public PhaseTransitionSummaryFlags LastPhaseSummary => lastPhaseSummary;
    public bool PhasePresentationIsCurrent => phaseDispatches == 0 || lastCompositionFrame >= lastPhaseDispatchFrame;

    public void SetSolidGravityEnabled(bool enabled)
    {
        if (enabled)
        {
            // A UI toggle is an explicit wake-up request. Rebuild body IDs even
            // for a restored world or for solids that were previously sleeping.
            solidMatter |= worldHasMatter;
            topologyDirty = worldHasMatter;
            solidSleeping = false;
            cellularSleeping = false;
            solidMotionNeedsCellular = true;
            settledObservations = 0;
            presentationDirty = true;
            return;
        }
        solidSleeping = true;
    }

    private void ApplyHydraulicMode(GpuSimulationResources resources, bool enabled)
    {
        waterPressureRoutesDirty = true;
        hydraulicWarmupFrames = enabled ? 128 : 0;
        fastSettleFrames = enabled ? 0 : 300;
        fastMaximumAwakeFrames = enabled ? 0 : 3600;
        settledObservations = 0;
        finalizeCellularRest = false;
        presentationDirty = true;

        if (!enabled && resources.IsSimulationAllocated)
        {
            // Pressure activity shares this scratch buffer with the solid-body
            // flags. Solid analysis rebuilds its entries later in the frame.
            resources.Context.ClearUnorderedAccessView(
                resources.BodyFlags.UnorderedView,
                new RawInt4(0, 0, 0, 0));
        }

        if (!worldHasMatter)
        {
            return;
        }

        cellularSleeping = false;
        activeMinX = 0;
        activeMinY = 0;
        activeMaxX = resources.Width - 1;
        activeMaxY = resources.Height - 1;
        activeRegionValid = true;
    }

    public GpuSimulationResources DispatchFrame(
        SimulationSettings settings,
        ReadOnlySpan<BrushDrawCommand> commands,
        float elapsedSeconds)
    {
        bool containsMaterialCommand = ContainsMaterialCommand(commands);
        GpuSimulationResources resources = lifecycleManager.CreateOrResize(
            settings,
            worldHasMatter || thermalActive || containsMaterialCommand);
        if (!ReferenceEquals(resources, boundResources))
        {
            Clear(resources);
            boundResources = resources;
            ResetActivity(resources.IsSimulationAllocated);
        }

        PollPhaseSummary(resources);
        PollPhaseTiming(resources);
        ApplyPendingPhaseFallback(resources);

        if (settings.HydraulicPressure != previousHydraulicPressure)
        {
            ApplyHydraulicMode(resources, settings.HydraulicPressure);
            previousHydraulicPressure = settings.HydraulicPressure;
        }

        SimulationFrameConstants constants = CreateConstants(settings, commands);
        if (commands.Length > 0 && resources.IsSimulationAllocated)
        {
            resources.Commands.Upload(resources.Context, commands);
            DispatchBrush(resources, ref constants);
            cellMaterialsDirty |= ContainsGridTopologyCommand(commands);
            RegisterActivity(
                commands,
                settings.Width,
                settings.Height,
                settings.HydraulicPressure);
            worldHasMatter |= containsMaterialCommand;
            presentationDirty = true;
        }

        if (settings.SolidGravity && !previousSolidGravity)
        {
            solidMatter |= worldHasMatter;
            topologyDirty = true;
            solidSleeping = false;
            solidMotionNeedsCellular = true;
            settledObservations = 0;
        }
        previousSolidGravity = settings.SolidGravity;

        if (finalizeCellularRest && resources.IsSimulationAllocated)
        {
            DispatchCellularRest(resources, ref constants);
            finalizeCellularRest = false;
            presentationDirty = true;
        }

        if (!settings.Paused && settings.SolidGravity && solidMatter && !solidSleeping)
        {
            // If active solids are moving, we must simulate the entire world to catch all cellular updates safely
            activeMinX = 0;
            activeMinY = 0;
            activeMaxX = settings.Width - 1;
            activeMaxY = settings.Height - 1;
            activeRegionValid = true;

            if (topologyDirty)
            {
                DispatchComponentLabeling(resources, ref constants);
                DispatchSolidGeometry(resources, ref constants);
                topologyDirty = false;
            }
            // Once the liquid is asleep, resting bodies cannot lose support.
            // Re-evaluate only the still-moving bodies instead of rescanning a
            // large stationary hull because a few detached pixels are falling.
            DispatchSolidPass(resources, ref constants, cellularSleeping ? 0u : 1u);
            cellMaterialsDirty = true;
            if (solidMotionNeedsCellular)
            {
                cellularSleeping = false;
            }
            presentationDirty = true;
        }

        if (settings.HydraulicPressure && liquidMatter && waterPressureRoutesDirty &&
            resources.IsSimulationAllocated)
        {
            resources.Context.ClearUnorderedAccessView(
                resources.WaterPressureRoutes.UnorderedView,
                new RawInt4(0, 0, 0, 0));
            resources.Context.ClearUnorderedAccessView(
                resources.WaterPressureRouteScratch.UnorderedView,
                new RawInt4(0, 0, 0, 0));
            waterPressureRoutesDirty = false;
        }

        if (!settings.Paused && cellularMatter && !cellularSleeping)
        {
            // Expand the active bounding box to account for gravity and horizontal flow
            if (activeRegionValid)
            {
                activeMaxY = Math.Min(settings.Height - 1, activeMaxY + 38);
                activeMinY = Math.Max(0, activeMinY - 12);
                int dx = fluidMatter ? 256 : 4;
                activeMinX = Math.Max(0, activeMinX - dx);
                activeMaxX = Math.Min(settings.Width - 1, activeMaxX + dx);
            }
            else
            {
                activeMinX = 0;
                activeMinY = 0;
                activeMaxX = settings.Width - 1;
                activeMaxY = settings.Height - 1;
                activeRegionValid = true;
            }

            // At medium/native resolutions one complete cellular step already
            // saturates the GPU. The second step only doubles memory traffic;
            // alternating the wide spans preserves their total reach over two
            // rendered frames without freezing the UI on mid-range hardware.
            bool useOptimizedSchedule = settings.Scale >= 0.5f;
            // Quarter-resolution has enough headroom for a full second ordinary-
            // water step. Larger grids retain the single-step schedule so the
            // same scene remains practical on mid-range GPUs.
            bool boostOrdinaryWater = !settings.HydraulicPressure && !gasMatter;
            bool runSecondaryStep = fluidMatter && !useOptimizedSchedule &&
                (settings.HydraulicPressure || gasMatter || boostOrdinaryWater);
            bool runLongRangeFluid = fluidMatter;
            DispatchCellularAutomata(
                resources,
                ref constants,
                cellMaterialsDirty,
                primaryStep: true,
                runPressureRoutes: liquidMatter && settings.HydraulicPressure,
                runLongRangeFluid,
                updateRestState: !runSecondaryStep,
                useOptimizedSchedule);
            cellMaterialsDirty = false;
            if (runSecondaryStep)
            {
                DispatchCellularAutomata(
                    resources,
                    ref constants,
                    rebuildCellMaterials: false,
                    primaryStep: false,
                    runPressureRoutes: false,
                    runLongRangeFluid,
                    updateRestState: true,
                    useOptimizedSchedule: false);
            }
            hydraulicWarmupFrames = settings.HydraulicPressure
                ? Math.Max(0, hydraulicWarmupFrames - 1)
                : 0;
            fastSettleFrames = settings.HydraulicPressure
                ? 0
                : Math.Max(0, fastSettleFrames - 1);
            fastMaximumAwakeFrames = settings.HydraulicPressure
                ? 0
                : Math.Max(0, fastMaximumAwakeFrames - 1);
            presentationDirty = true;
        }

        PollThermalTiming(resources);
        int thermalTicks = thermalScheduler.Advance(
            elapsedSeconds,
            settings.Paused,
            thermalActive && resources.IsSimulationAllocated);
        for (int tick = 0; tick < thermalTicks; tick++)
        {
            bool measure = thermalScheduler.TotalTicks >= 40 && !thermalTimingPending;
            DispatchThermalDiffusion(resources, measure);
        }

        int phaseDispatchCount = PhaseTransitionDispatchPolicy.GetDispatchCount(
            materialRegistry.RegistryHasPhaseTransitions,
            resources.IsSimulationAllocated,
            thermalActive,
            settings.Paused,
            thermalTicks);
        if (phaseDispatchCount != 0)
        {
            bool measure = phaseDispatches >= 40 && !phaseTimingPending;
            PhaseSummaryReadbackScheduleResult readbackResult =
                DispatchPhaseTransitions(resources, measure);
            phaseDispatches++;
            maximumPhaseDispatchesPerFrame = Math.Max(maximumPhaseDispatchesPerFrame, phaseDispatchCount);
            lastPhaseDispatchFrame = frameIndex;
            presentationDirty = true;
            // A queued summary may become readable after the next cellular
            // schedule. Invalidate the material map now so an already-awake
            // cellular pass never interprets a transitioned cell as its old kind.
            cellMaterialsDirty = true;
            if (readbackResult == PhaseSummaryReadbackScheduleResult.NoFreeSlot)
            {
                phaseWakeUpGate.Request();
                ApplyPendingPhaseFallback(resources);
            }
        }

        constants.SolidGravity = settings.SolidGravity ? 1u : 0u;
        if (presentationDirty && resources.IsSimulationAllocated)
        {
            DispatchComposition(resources, ref constants);
            lastCompositionFrame = frameIndex;
            presentationDirty = false;
        }
        frameIndex++;
        return resources;
    }

    public void ClearCurrentWorld(SimulationSettings settings)
    {
        GpuSimulationResources resources = lifecycleManager.CreateOrResize(settings, false);
        Clear(resources);
        boundResources = resources;
        ResetActivity(resources.IsSimulationAllocated);
    }

    public void RestoreWorldActivity(
        GpuSimulationResources resources,
        bool containsMatter,
        bool hydraulicPressure)
    {
        resources.Context.ClearUnorderedAccessView(
            resources.PathBlockerMasks.UnorderedView,
            new RawInt4(0, 0, 0, 0));
        boundResources = resources;
        worldHasMatter = containsMatter;
        thermalActive = containsMatter;
        thermalScheduler.Reset();
        ResetThermalTiming();
        ResetPhaseRuntime();
        cellularMatter = containsMatter;
        fluidMatter = containsMatter;
        liquidMatter = containsMatter;
        gasMatter = containsMatter;
        solidMatter = containsMatter;
        cellularSleeping = false;
        solidSleeping = false;
        topologyDirty = true;
        waterPressureRoutesDirty = true;
        previousHydraulicPressure = hydraulicPressure;
        solidMotionNeedsCellular = true;
        cellMaterialsDirty = containsMatter;
        settledObservations = 0;
        hydraulicWarmupFrames = hydraulicPressure ? 128 : 0;
        fastSettleFrames = hydraulicPressure ? 0 : 300;
        fastMaximumAwakeFrames = hydraulicPressure ? 0 : 3600;
        presentationDirty = resources.IsSimulationAllocated;

        if (containsMatter)
        {
            activeMinX = 0;
            activeMinY = 0;
            activeMaxX = resources.Width - 1;
            activeMaxY = resources.Height - 1;
            activeRegionValid = true;
        }
        else
        {
            ResetActiveRegion();
        }
    }

    public void ObserveStatistics(SimulationStatistics statistics)
    {
        if (statistics.FrameIndex == 0 || statistics.FrameIndex <= lastObservedStatisticsFrame)
        {
            return;
        }
        lastObservedStatisticsFrame = statistics.FrameIndex;
        uint cellularCells = statistics.LiquidCells + statistics.GranularCells + statistics.GasCells;
        fluidMatter = statistics.LiquidCells > 0 || statistics.GasCells > 0;
        liquidMatter = statistics.LiquidCells > 0;
        gasMatter = statistics.GasCells > 0;
        uint movingCellularCells = statistics.MovingCells > statistics.MovingSolidCells
            ? statistics.MovingCells - statistics.MovingSolidCells
            : 0;
        bool minorSolidMotion = statistics.MovingSolidCells is > 0 and <= 64;
        solidMotionNeedsCellular = statistics.MovingSolidCells > 64;
        uint residualTolerance = statistics.GasCells > 0
            ? Math.Max(64u, cellularCells / 10u)
            : minorSolidMotion
                ? Math.Max(256u, statistics.MovingSolidCells * 64u)
                : statistics.LiquidCells >= 10000
                    // Large, level pools can retain a handful of parity swaps
                    // after pressure and every visible surface have settled.
                    ? 8u
                    : 1u;
        bool settleDelayElapsed = fastSettleFrames == 0 && hydraulicWarmupFrames == 0;
        bool fastSafetyTimeout = !previousHydraulicPressure &&
            fastMaximumAwakeFrames == 0 && statistics.GasCells == 0;
        bool settled = statistics.ActiveCells > 0 && !solidMotionNeedsCellular &&
            (fastSafetyTimeout ||
                (settleDelayElapsed &&
                    (!previousHydraulicPressure || statistics.PressureMoves == 0) &&
                    movingCellularCells <= residualTolerance));
        settledObservations = settled ? settledObservations + 1 : 0;
        int observationsRequired = fastSafetyTimeout
            ? 2
            : !minorSolidMotion && (statistics.GasCells > 0 || movingCellularCells > 0)
                ? 24
                : 2;
        if (!settled)
        {
            cellularSleeping = false;
            solidSleeping = false;
        }
        else if (settledObservations >= observationsRequired)
        {
            finalizeCellularRest = cellularMatter && !cellularSleeping;
            cellularSleeping = true;
            solidSleeping = !previousSolidGravity || statistics.MovingSolidCells == 0;
            ResetActiveRegion();
        }
        if (statistics.ActiveCells == 0)
        {
            ResetActivity(false, resetThermal: false);
        }
    }

    private void DispatchBrush(GpuSimulationResources resources, ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.BrushShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Commands.View, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.ReadUnorderedView);
        int diameter = Math.Max(1, (int)constants.MaximumBrushDiameter);
        context.Dispatch(DivideRoundUp(diameter, 16), DivideRoundUp(diameter, 16), (int)constants.CommandCount);
        Unbind(context, 2, 1);
    }

    private void DispatchComponentLabeling(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        int cells = resources.Width * resources.Height;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.ComponentInitializeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            null,
            resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.ComponentParents.UnorderedView);
        context.Dispatch(DivideRoundUp(cells, 256), 1, 1);
        Unbind(context, 3, 1);

        for (int iteration = 0; iteration < 24; iteration++)
        {
            context.ComputeShader.Set(resources.ComponentUnionShader);
            context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
            context.ComputeShader.SetShaderResources(
                0,
                resources.Grid.ReadView,
                null,
                resources.Materials.View);
            context.ComputeShader.SetUnorderedAccessView(0, resources.ComponentParents.UnorderedView);
            context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
            Unbind(context, 3, 1);

            context.ComputeShader.Set(resources.ComponentCompressShader);
            context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
            context.ComputeShader.SetUnorderedAccessView(0, resources.ComponentParents.UnorderedView);
            context.Dispatch(DivideRoundUp(cells, 256), 1, 1);
            Unbind(context, 0, 1);
        }

        context.ComputeShader.Set(resources.ComponentFinalizeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            null,
            resources.ComponentParents.View,
            resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.ReadUnorderedView);
        context.Dispatch(DivideRoundUp(cells, 256), 1, 1);
        Unbind(context, 3, 2);
    }

    private static void DispatchSolidGeometry(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        context.ClearUnorderedAccessView(
            resources.SolidBodyGeometry.UnorderedView,
            new RawInt4(0, 0, 0, 0));
        context.ClearUnorderedAccessView(
            resources.SolidBodyMass.UnorderedView,
            new RawInt4(0, 0, 0, 0));
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.SolidGeometryAnalyzeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            null,
            null,
            null,
            null,
            resources.Materials.View,
            null);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            null,
            null,
            null,
            null,
            resources.SolidBodyGeometry.UnorderedView,
            resources.SolidBodyMass.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        Unbind(context, 7, 6);
    }

    private void DispatchSolidPass(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        uint pass)
    {
        DeviceContext context = resources.Context;
        constants.SolidPass = pass;
        context.ClearUnorderedAccessView(resources.BodyFlags.UnorderedView, new RawInt4(0, 0, 0, 0));
        // CellMaterials is rebuilt by cellular phase 32. During the solid pass
        // it stores the current water depth for each cached rigid body.
        context.ClearUnorderedAccessView(resources.CellMaterials.UnorderedView, new RawInt4(0, 0, 0, 0));
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.SolidAnalyzeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            null,
            null,
            null,
            resources.SolidBodyGeometry.View,
            resources.Materials.View,
            resources.SolidBodyMass.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.BodyFlags.UnorderedView,
            null,
            resources.CellMaterials.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 7, 3);

        // ComponentParents is only labeling scratch after body IDs have been
        // copied into the cells. Reuse it for one-frame displacement targets;
        // hydraulic routing clears/rebuilds it immediately after solid motion.
        context.ClearUnorderedAccessView(resources.ComponentParents.UnorderedView, new RawInt4(0, 0, 0, 0));
        context.ComputeShader.Set(resources.SolidDisplacementPlanShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.BodyFlags.View,
            resources.CellMaterials.View,
            null,
            resources.SolidBodyGeometry.View,
            resources.Materials.View,
            resources.SolidBodyMass.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            null,
            null,
            null,
            resources.ComponentParents.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 7, 4);

        context.ComputeShader.Set(resources.SolidMoveShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.BodyFlags.View,
            resources.CellMaterials.View,
            resources.ComponentParents.View,
            resources.SolidBodyGeometry.View,
            resources.Materials.View,
            resources.SolidBodyMass.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 7, 2);

        context.ComputeShader.Set(resources.SolidDisplacementApplyShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.BodyFlags.View,
            resources.CellMaterials.View,
            resources.ComponentParents.View,
            resources.SolidBodyGeometry.View,
            resources.Materials.View,
            resources.SolidBodyMass.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 7, 2);
        resources.Grid.Swap();
        waterPressureRoutesDirty = true;
    }

    private void DispatchCellularAutomata(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        bool rebuildCellMaterials,
        bool primaryStep,
        bool runPressureRoutes,
        bool runLongRangeFluid,
        bool updateRestState,
        bool useOptimizedSchedule)
    {
        DeviceContext context = resources.Context;
        // Resolve gravity and ordinary lateral flow before consulting the
        // hydraulic caches. Phase 13 balances adjacent columns only; phase 29
        // applies that local plan before the pressure-only passes run.
        ReadOnlySpan<uint> phases = useOptimizedSchedule
            ? (frameIndex & 1) == 0 ? OptimizedEvenPhases : OptimizedOddPhases
            : primaryStep
                ? (frameIndex & 1) == 0 ? PrimaryEvenPhases : PrimaryOddPhases
            : (frameIndex & 1) == 0 ? SecondaryEvenPhases : SecondaryOddPhases;

        // Bind common compute states once before the loop
        context.ComputeShader.Set(resources.CellularAutomataShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResource(0, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.BodyFlags.UnorderedView,
            resources.PathBlockerMasks.UnorderedView,
            resources.CellMaterials.UnorderedView,
            resources.WaterPressureRoutes.UnorderedView,
            resources.WaterPressureRouteScratch.UnorderedView);

        foreach (uint phase in phases)
        {
            if (phase == 32 && !rebuildCellMaterials)
            {
                continue;
            }
            if (phase == 4 && !updateRestState)
            {
                continue;
            }
            // Adjacent-column leveling is ordinary liquid behavior. Only the
            // pressure-route phases belong to communicating vessels.
            bool pressureRoutePhase = phase == 34 || phase == 35 || phase == 36 ||
                phase == 37 || phase == 38 || phase == 39 || phase == 70 || phase == 71;
            if (pressureRoutePhase && !runPressureRoutes)
            {
                continue;
            }
            bool ordinaryFlowPhase = phase is >= 48 and <= 57;
            if (ordinaryFlowPhase && runPressureRoutes)
            {
                continue;
            }
            bool longRangeFluidPhase = phase is >= 40 and <= 47;
            if (longRangeFluidPhase && !runLongRangeFluid)
            {
                continue;
            }

            constants.SimulationPhase = phase;

            bool buildPathBlockers = phase == 31;
            bool buildCellMaterials = phase == 32;
            bool waterColumnPhase = phase == 33 || phase == 56 || phase == 57 ||
                phase == 29 || phase == 37 ||
                phase == 39 || phase == 70 || phase == 71 || phase == 13;
            int startX = buildPathBlockers || buildCellMaterials || waterColumnPhase
                ? 0
                : activeRegionValid ? activeMinX : 0;
            int startY = buildPathBlockers || buildCellMaterials || waterColumnPhase
                ? 0
                : activeRegionValid ? activeMinY : 0;
            int endX = buildPathBlockers
                ? DivideRoundUp(resources.Width, 32) - 1
                : buildCellMaterials
                    ? resources.Width - 1
                    : waterColumnPhase
                        ? resources.Width - 1
                        : activeRegionValid ? activeMaxX : resources.Width - 1;
            int endY = buildPathBlockers
                ? resources.Height - 1
                : buildCellMaterials
                    ? resources.Height - 1
                    : waterColumnPhase
                        ? 0
                        : activeRegionValid ? activeMaxY : resources.Height - 1;

            int dispatchW = endX - startX + 1;
            int dispatchH = endY - startY + 1;
            if (phase <= 1)
            {
                int parity = (int)phase;
                startY += (startY & 1) == parity ? 0 : 1;
                dispatchH = startY > endY ? 0 : (endY - startY) / 2 + 1;
            }
            else if (phase is >= 2 and <= 3)
            {
                int parity = (int)phase - 2;
                startX += (startX & 1) == parity ? 0 : 1;
                dispatchW = startX > endX ? 0 : (endX - startX) / 2 + 1;
            }
            else if (phase is >= 5 and <= 12)
            {
                int diagonalPhase = (int)phase - 5;
                int xParity = (diagonalPhase >> 1) & 1;
                int yParity = (diagonalPhase >> 2) & 1;
                startX += (startX & 1) == xParity ? 0 : 1;
                startY += (startY & 1) == yParity ? 0 : 1;
                dispatchW = startX > endX ? 0 : (endX - startX) / 2 + 1;
                dispatchH = startY > endY ? 0 : (endY - startY) / 2 + 1;
            }

            if (dispatchW <= 0 || dispatchH <= 0)
            {
                continue;
            }

            constants.DispatchOffsetX = (uint)startX;
            constants.DispatchOffsetY = (uint)startY;
            constants.DispatchExtentX = (uint)dispatchW;
            constants.DispatchExtentY = (uint)dispatchH;

            UpdateConstants(context, resources, ref constants);
            context.Dispatch(DivideRoundUp(dispatchW, 16), DivideRoundUp(dispatchH, 16), 1);
        }

        // Unbind once at the end
        Unbind(context, 1, 6);
        constants.SimulationPhase = 0;
    }

    private void DispatchCellularRest(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        constants.SimulationPhase = 30;

        int startX = activeRegionValid ? activeMinX : 0;
        int startY = activeRegionValid ? activeMinY : 0;
        int endX = activeRegionValid ? activeMaxX : resources.Width - 1;
        int endY = activeRegionValid ? activeMaxY : resources.Height - 1;

        constants.DispatchOffsetX = (uint)startX;
        constants.DispatchOffsetY = (uint)startY;

        int dispatchW = Math.Max(1, endX - startX + 1);
        int dispatchH = Math.Max(1, endY - startY + 1);
        constants.DispatchExtentX = (uint)dispatchW;
        constants.DispatchExtentY = (uint)dispatchH;

        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.CellularAutomataShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResource(0, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.BodyFlags.UnorderedView,
            resources.PathBlockerMasks.UnorderedView,
            resources.CellMaterials.UnorderedView);

        context.Dispatch(DivideRoundUp(dispatchW, 16), DivideRoundUp(dispatchH, 16), 1);
        Unbind(context, 1, 4);
        constants.SimulationPhase = 0;
    }

    private void DispatchComposition(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        bool collect = frameIndex % 10 == 0;
        if (collect)
        {
            context.ClearUnorderedAccessView(resources.Statistics.WriteUnorderedView, new RawInt4(0, 0, 0, 0));
        }
        constants.SimulationPhase = collect ? 1u : 0u;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.CompositionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.Materials.View,
            resources.BodyFlags.View,
            resources.PathBlockerMasks.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.CompositionTargets.WriteView,
            resources.Statistics.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 4, 2);
        if (collect)
        {
            resources.Statistics.Swap();
        }
        constants.SimulationPhase = 0;
        context.CopyResource(resources.CompositionTargets.WriteTexture, resources.NativePresentationTexture);
        resources.PresentationIndex = 1 - resources.PresentationIndex;
        resources.CompositionTargets.Swap();
    }

    private static void Clear(GpuSimulationResources resources)
    {
        RawInt4 zero = new(0, 0, 0, 0);
        foreach (UnorderedAccessView view in resources.Grid.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }
        // Component labels, rigid-body geometry, and hydraulic routes are large
        // scratch buffers. Their owning passes fully initialize them before use,
        // so clearing them here only stalls the first brush stroke.
        resources.Context.ClearUnorderedAccessView(resources.BodyFlags.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.PathBlockerMasks.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.CellMaterials.UnorderedView, zero);
        foreach (UnorderedAccessView view in resources.Statistics.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }
    }

    private SimulationFrameConstants CreateConstants(
        SimulationSettings settings,
        ReadOnlySpan<BrushDrawCommand> commands)
    {
        float maximumRadius = settings.BrushRadius;
        foreach (BrushDrawCommand command in commands)
        {
            maximumRadius = Math.Max(maximumRadius, command.Radius);
        }
        return new SimulationFrameConstants
        {
            DeltaTime = 1f / 60f,
            Gravity = settings.Gravity,
            Width = (uint)settings.Width,
            Height = (uint)settings.Height,
            FrameIndex = frameIndex,
            CommandCount = (uint)commands.Length,
            MaximumBrushDiameter = (uint)(MathF.Ceiling(maximumRadius) * 2 + 1),
            MaximumVelocity = 2200,
            SolidGravity = settings.SolidGravity ? 1u : 0u,
            HydraulicPressure = settings.HydraulicPressure ? 1u : 0u
        };
    }

    private void RegisterActivity(
        ReadOnlySpan<BrushDrawCommand> commands,
        int width,
        int height,
        bool hydraulicPressure)
    {
        if (ContainsGridTopologyCommand(commands))
        {
            waterPressureRoutesDirty = true;
            hydraulicWarmupFrames = hydraulicPressure ? 128 : 0;
            fastSettleFrames = hydraulicPressure ? 0 : 300;
            fastMaximumAwakeFrames = hydraulicPressure ? 0 : 3600;
        }
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode == BrushCommandMode.SetTemperature)
            {
                thermalActive |= worldHasMatter;
                continue;
            }
            if (command.Mode != BrushCommandMode.Material &&
                command.Mode != BrushCommandMode.Erase)
            {
                continue;
            }
            ExpandActiveRegion(command.X, command.Y, (int)MathF.Ceiling(command.Radius), width, height);

            if (command.Mode == BrushCommandMode.Erase)
            {
                topologyDirty = true;
                cellularSleeping = false;
                finalizeCellularRest = false;
                solidSleeping = false;
                solidMotionNeedsCellular = true;
                settledObservations = 0;
                continue;
            }
            thermalActive = true;
            MaterialSimulationKind kind = (MaterialSimulationKind)materialRegistry[command.MaterialIndex]
                .Properties.SimulationKind;
            if (kind == MaterialSimulationKind.Solid)
            {
                solidMatter = true;
                topologyDirty = true;
                solidSleeping = false;
                solidMotionNeedsCellular = true;
            }
            else if (kind is MaterialSimulationKind.Granular or MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas)
            {
                cellularMatter = true;
                fluidMatter |= kind is MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas;
                liquidMatter |= kind == MaterialSimulationKind.Liquid;
                gasMatter |= kind == MaterialSimulationKind.Gas;
                cellularSleeping = false;
                finalizeCellularRest = false;
            }
            settledObservations = 0;
        }
    }

    private void ResetActivity(bool dirtyPresentation, bool resetThermal = true)
    {
        worldHasMatter = false;
        cellularMatter = false;
        fluidMatter = false;
        liquidMatter = false;
        gasMatter = false;
        solidMatter = false;
        cellularSleeping = false;
        solidSleeping = false;
        topologyDirty = false;
        settledObservations = 0;
        presentationDirty = dirtyPresentation;
        finalizeCellularRest = false;
        waterPressureRoutesDirty = true;
        solidMotionNeedsCellular = true;
        fastSettleFrames = 0;
        fastMaximumAwakeFrames = 0;
        cellMaterialsDirty = false;
        if (resetThermal)
        {
            thermalActive = false;
            thermalScheduler.Reset();
            ResetThermalTiming();
        }
        ResetPhaseRuntime();
        ResetActiveRegion();
    }

    private void DispatchThermalDiffusion(GpuSimulationResources resources, bool measure)
    {
        ThermalSimulationConstants constants = new()
        {
            DeltaTime = FixedThermalStep,
            ExchangeRate = ThermalExchangeRate,
            Width = (uint)resources.Width,
            Height = (uint)resources.Height
        };
        DeviceContext context = resources.Context;
        if (measure)
        {
            context.Begin(resources.ThermalTimestampDisjointQuery);
            context.End(resources.ThermalTimestampStartQuery);
        }
        context.UpdateSubresource(ref constants, resources.ThermalConstants);
        context.ComputeShader.Set(resources.ThermalDiffusionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.ThermalConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.WriteUnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        if (measure)
        {
            context.End(resources.ThermalTimestampEndQuery);
            context.End(resources.ThermalTimestampDisjointQuery);
            thermalTimingPending = true;
        }
        Unbind(context, 2, 1);
        resources.Grid.Swap();
    }

    private void PollThermalTiming(GpuSimulationResources resources)
    {
        if (!thermalTimingPending)
        {
            return;
        }
        DeviceContext context = resources.Context;
        bool disjointReady = context.GetData(
            resources.ThermalTimestampDisjointQuery,
            AsynchronousFlags.DoNotFlush,
            out QueryDataTimestampDisjoint disjoint);
        bool startReady = context.GetData(
            resources.ThermalTimestampStartQuery,
            AsynchronousFlags.DoNotFlush,
            out long start);
        bool endReady = context.GetData(
            resources.ThermalTimestampEndQuery,
            AsynchronousFlags.DoNotFlush,
            out long end);
        if (!disjointReady || !startReady || !endReady)
        {
            return;
        }
        thermalTimingPending = false;
        if (disjoint.Disjoint || disjoint.Frequency <= 0 || end < start)
        {
            return;
        }
        double milliseconds = (end - start) * 1000d / disjoint.Frequency;
        thermalTimingSamples++;
        thermalTimingTotalMilliseconds += milliseconds;
        thermalTimingMinimumMilliseconds = Math.Min(thermalTimingMinimumMilliseconds, milliseconds);
        thermalTimingMaximumMilliseconds = Math.Max(thermalTimingMaximumMilliseconds, milliseconds);
    }

    private void ResetThermalTiming()
    {
        thermalTimingPending = false;
        thermalTimingSamples = 0;
        thermalTimingTotalMilliseconds = 0;
        thermalTimingMinimumMilliseconds = double.PositiveInfinity;
        thermalTimingMaximumMilliseconds = 0;
    }

    private PhaseSummaryReadbackScheduleResult DispatchPhaseTransitions(
        GpuSimulationResources resources,
        bool measure)
    {
        PhaseTransitionConstants constants = new()
        {
            Width = (uint)resources.Width,
            Height = (uint)resources.Height,
            MaterialCount = (uint)materialRegistry.Count
        };
        DeviceContext context = resources.Context;
        context.ClearUnorderedAccessView(resources.PhaseSummary.UnorderedView, new RawInt4(0, 0, 0, 0));
        if (measure)
        {
            context.Begin(resources.PhaseTimestampDisjointQuery);
            context.End(resources.PhaseTimestampStartQuery);
        }
        context.UpdateSubresource(ref constants, resources.PhaseConstants);
        context.ComputeShader.Set(resources.PhaseTransitionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.PhaseConstants);
        context.ComputeShader.SetShaderResource(0, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.PhaseSummary.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        if (measure)
        {
            context.End(resources.PhaseTimestampEndQuery);
            context.End(resources.PhaseTimestampDisjointQuery);
            phaseTimingPending = true;
        }
        Unbind(context, 1, 2);

        Span<bool> pendingSlots = stackalloc bool[resources.PhaseSummaryReadbackSlots.Length];
        for (int index = 0; index < pendingSlots.Length; index++)
        {
            pendingSlots[index] = resources.PhaseSummaryReadbackSlots[index].Pending;
        }
        PhaseSummaryReadbackScheduleResult result = PhaseSummaryReadbackPolicy.SelectSlot(
            pendingSlots,
            out int slotIndex);
        if (result == PhaseSummaryReadbackScheduleResult.NoFreeSlot)
        {
            return result;
        }
        GpuPhaseSummaryReadbackSlot slot = resources.PhaseSummaryReadbackSlots[slotIndex];
        context.CopyResource(resources.PhaseSummary.Buffer, slot.Staging);
        context.End(slot.Query);
        slot.Pending = true;
        slot.Generation = phaseReadbackGeneration;
        return result;
    }

    private void PollPhaseSummary(GpuSimulationResources resources)
    {
        DeviceContext context = resources.Context;
        foreach (GpuPhaseSummaryReadbackSlot slot in resources.PhaseSummaryReadbackSlots)
        {
            if (!slot.Pending ||
                !context.GetData(slot.Query, AsynchronousFlags.DoNotFlush, out RawBool complete) ||
                !complete)
            {
                continue;
            }
            DataBox mapping = context.MapSubresource(slot.Staging, 0, MapMode.Read, MapFlags.None);
            uint rawFlags = unchecked((uint)Marshal.ReadInt32(mapping.DataPointer));
            context.UnmapSubresource(slot.Staging, 0);
            slot.Pending = false;
            if (slot.Generation != phaseReadbackGeneration)
            {
                continue;
            }
            PhaseTransitionSummaryFlags flags = (PhaseTransitionSummaryFlags)rawFlags;
            if ((flags & PhaseTransitionSummaryFlags.PhaseOccurred) != 0)
            {
                lastPhaseSummary = flags;
                ApplyPhaseSummary(resources, flags);
            }
        }
    }

    private void PollPhaseTiming(GpuSimulationResources resources)
    {
        if (!phaseTimingPending)
        {
            return;
        }
        DeviceContext context = resources.Context;
        bool disjointReady = context.GetData(
            resources.PhaseTimestampDisjointQuery,
            AsynchronousFlags.DoNotFlush,
            out QueryDataTimestampDisjoint disjoint);
        bool startReady = context.GetData(
            resources.PhaseTimestampStartQuery,
            AsynchronousFlags.DoNotFlush,
            out long start);
        bool endReady = context.GetData(
            resources.PhaseTimestampEndQuery,
            AsynchronousFlags.DoNotFlush,
            out long end);
        if (!disjointReady || !startReady || !endReady)
        {
            return;
        }
        phaseTimingPending = false;
        if (disjoint.Disjoint || disjoint.Frequency <= 0 || end < start)
        {
            return;
        }
        double milliseconds = (end - start) * 1000d / disjoint.Frequency;
        phaseTimingSamples++;
        phaseTimingTotalMilliseconds += milliseconds;
        phaseTimingMinimumMilliseconds = Math.Min(phaseTimingMinimumMilliseconds, milliseconds);
        phaseTimingMaximumMilliseconds = Math.Max(phaseTimingMaximumMilliseconds, milliseconds);
    }

    private void ApplyPendingPhaseFallback(GpuSimulationResources resources)
    {
        if (!phaseWakeUpGate.Consume())
        {
            return;
        }
        phaseFallbackWakeUps++;
        ApplyPhaseSummary(resources, materialRegistry.PhaseTransitionGraphFlags);
    }

    private void ApplyPhaseSummary(
        GpuSimulationResources resources,
        PhaseTransitionSummaryFlags flags)
    {
        if ((flags & PhaseTransitionSummaryFlags.PhaseOccurred) == 0)
        {
            return;
        }
        presentationDirty = true;
        cellMaterialsDirty = true;
        finalizeCellularRest = false;
        settledObservations = 0;
        thermalActive = true;

        if ((flags & PhaseTransitionSummaryFlags.TargetCellular) != 0)
        {
            cellularMatter = true;
            cellularSleeping = false;
            ActivateFullRegion(resources);
        }
        if ((flags & PhaseTransitionSummaryFlags.TargetLiquid) != 0)
        {
            fluidMatter = true;
            liquidMatter = true;
        }
        if ((flags & PhaseTransitionSummaryFlags.TargetGas) != 0)
        {
            fluidMatter = true;
            gasMatter = true;
        }
        if ((flags & PhaseTransitionSummaryFlags.TouchesLiquid) != 0)
        {
            waterPressureRoutesDirty = true;
            hydraulicWarmupFrames = previousHydraulicPressure ? 128 : 0;
            fastSettleFrames = previousHydraulicPressure ? 0 : 300;
            fastMaximumAwakeFrames = previousHydraulicPressure ? 0 : 3600;
            ActivateFullRegion(resources);
        }
        if ((flags & PhaseTransitionSummaryFlags.TouchesSolid) != 0)
        {
            solidMatter = true;
            solidSleeping = false;
            topologyDirty = true;
            solidMotionNeedsCellular = true;
            ActivateFullRegion(resources);
        }
        if ((flags & PhaseTransitionSummaryFlags.TargetMovableSolid) != 0)
        {
            topologyDirty = true;
        }
    }

    private void ActivateFullRegion(GpuSimulationResources resources)
    {
        if (!resources.IsSimulationAllocated)
        {
            return;
        }
        activeMinX = 0;
        activeMinY = 0;
        activeMaxX = resources.Width - 1;
        activeMaxY = resources.Height - 1;
        activeRegionValid = true;
    }

    private void ResetPhaseRuntime()
    {
        phaseReadbackGeneration++;
        phaseWakeUpGate.Reset();
        phaseFallbackWakeUps = 0;
        phaseTimingPending = false;
        phaseTimingSamples = 0;
        phaseTimingTotalMilliseconds = 0;
        phaseTimingMinimumMilliseconds = double.PositiveInfinity;
        phaseTimingMaximumMilliseconds = 0;
        phaseDispatches = 0;
        maximumPhaseDispatchesPerFrame = 0;
        lastPhaseSummary = PhaseTransitionSummaryFlags.None;
        lastPhaseDispatchFrame = 0;
        lastCompositionFrame = 0;
    }

    private static bool ContainsMaterialCommand(ReadOnlySpan<BrushDrawCommand> commands)
    {
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode == BrushCommandMode.Material)
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsGridTopologyCommand(ReadOnlySpan<BrushDrawCommand> commands)
    {
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode is BrushCommandMode.Material or BrushCommandMode.Erase)
            {
                return true;
            }
        }
        return false;
    }

    private static void UpdateConstants(
        DeviceContext context,
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        context.UpdateSubresource(ref constants, resources.FrameConstants);
    }

    private static void Unbind(DeviceContext context, int resources, int unordered)
    {
        if (resources > 0)
        {
            context.ComputeShader.SetShaderResources(0, new ShaderResourceView[resources]);
        }
        if (unordered > 0)
        {
            context.ComputeShader.SetUnorderedAccessViews(0, new UnorderedAccessView[unordered]);
        }
        context.ComputeShader.Set(null);
    }

    private static int DivideRoundUp(int value, int divisor)
    {
        return (value + divisor - 1) / divisor;
    }
}
