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
    public const double FixedGasStep = 1d / 120d;

    /// <summary>
    /// Поле воздуха идёт на фиксированных 60 Гц: константы решателя взяты из
    /// SimulationConfig.h The Powder Toy и заданы на тик, а не на секунду.
    /// </summary>
    public const double FixedAirStep = 1d / 60d;

    public const int MaximumAirTicksPerFrame = 4;
    private const float AirAmbientTemperature = 20f;

    /// <summary>
    /// Множитель перегрева перед нормировкой в шейдере. Единица означает
    /// «клетка на 1500 градусов выше комнатной считается полностью горячей».
    /// Крутить здесь, если тяга слишком слабая или слишком резкая.
    /// </summary>
    private const float AirHotScale = 1.0f;

    /// <summary>
    /// Вклад одной клетки пламени в световое поле за кадр. В The Powder Toy это
    /// <c>firea/8</c> от 255, то есть около 12% цвета частицы — но там вклад
    /// идёт от частицы, и шестнадцать частиц в одной грубой клетке одновременно
    /// оказываются редко. У нас клетка агрегирует до шестнадцати клеток пламени,
    /// и при 0.12 поле мгновенно упиралось в единицу по всей длине столба:
    /// пламя выходило белым от основания до конца, без перехода в жёлтый и
    /// красный. При 0.06 насыщается только плотное ядро, как и должно быть.
    /// </summary>
    // FIRE graphics leaves firea at 255; Renderer.cpp divides it by eight
    // with integer arithmetic before adding the flame-table colour.
    private const float FireGlowDeposit = 31f / 255f;

    /// <summary>
    /// Затухание за кадр: −4 из 255, как в Renderer::render_fire.
    /// Ускоренное затухание раньше маскировало отсутствие зажима поля; теперь
    /// поле ограничено единицей после каждого вклада, и компенсация не нужна.
    /// </summary>
    private const float FireGlowDecay = 0.0157f;

    /// <summary>Насколько ярко светятся раскалённые угли под пламенем.</summary>
    private const float FireGlowEmberStrength = 0.45f;

    /// <summary>
    /// Зеркало <c>GasMotionSubSteps</c> из PhysicsShared.hlsli — менять только
    /// вместе с ним, иначе средняя скорость газа разъедется с вероятностью,
    /// на которую шейдер делит свой шанс шага.
    /// </summary>
    private const int GasMotionSubSteps = 8;

    /// <summary>
    /// Порядок газовых фаз чередуется между подшагами.
    ///
    /// При постоянном порядке 82 → 83 газ на чётной координате мог сдвинуться
    /// вправо в первой фазе, оказаться на нечётной и сдвинуться ещё раз во
    /// второй — то есть две клетки за подшаг. Движению влево двойной ход
    /// доставался на противоположной чётности. У симметричного очага одна
    /// сторона попадала на «быструю полосу», другая на медленную, и струя
    /// сворачивалась крюком. Сдвиг сцены на клетку менял сторону крюка.
    /// </summary>
    private static readonly uint[][] GasMotionPhaseOrders =
    [
        [80, 81, 82, 83],
        [81, 80, 83, 82]
    ];
    // A larger fixed-step exchange keeps thermal fronts visible at gameplay
    // scale while remaining stable with the 0.05 s Jacobi step.
    public const float ThermalExchangeRate = 16f;
    public const int MaximumThermalTicksPerFrame = 4;
    public const int MaximumGasTicksPerFrame = 8;
    private static readonly uint[] PrimaryEvenPhases =
    [
        32, 0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
        2, 3, 2, 3, 2, 3, 2, 3,
        40, 41, 42, 43, 44, 45, 46, 47,
        48, 50, 52, 54,
        33, 56, 57, 58, 59, 13, 29,
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
        33, 56, 57, 58, 59, 13, 29,
        34, 35, 34, 35, 34, 35, 34, 35,
        36, 37, 71, 38, 39, 70,
        4
    ];

    private static readonly uint[] SecondaryEvenPhases =
    [
        0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
        2, 3, 2, 3, 2, 3, 2, 3,
        40, 41, 42, 43, 44, 45, 46, 47,
        33, 56, 57, 58, 59, 13, 29,
        4
    ];

    private static readonly uint[] SecondaryOddPhases =
    [
        1, 0, 1, 0, 1, 0, 1, 0, 6, 5, 8, 7, 10, 9, 12, 11,
        3, 2, 3, 2, 3, 2, 3, 2,
        47, 46, 45, 44, 43, 42, 41, 40,
        33, 56, 57, 58, 59, 13, 29,
        4
    ];

    private static readonly uint[] OptimizedEvenPhases =
    [
        32, 0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
        2, 3, 2, 3, 2, 3, 2, 3,
        40, 42, 44, 46,
        48, 52,
        33, 56, 57, 58, 59, 13, 29,
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
        33, 56, 57, 58, 59, 13, 29,
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
    private bool retainOxidizerField;
    private bool cellularMatter;
    private bool pressurePowderPotential;
    private bool fluidMatter;
    private bool liquidMatter;
    private double viscousSurfaceAccumulator;
    private int viscousSurfaceTicksThisFrame;
    private ulong viscousSurfaceTickIndex;
    private bool gasMatter;
    private readonly FixedStepGasScheduler gasScheduler = new();
    private bool gasTimingPending;
    private int gasTimingSamples;
    private double gasTimingTotalMilliseconds;
    private double gasTimingMinimumMilliseconds = double.PositiveInfinity;
    private double gasTimingMaximumMilliseconds;
    private bool solidMatter;
    private double densityBodyAccumulator;
    private double airborneBodyAccumulator;
    private bool cellularSleeping;
    private bool solidSleeping;
    private bool freeBodyMatter;
    private readonly bool bodyBalanceBaseline = Environment.GetEnvironmentVariable("PHYXEL_BODY_BALANCE_BASELINE") == "1";
    private bool topologyDirty;
    private bool previousSolidGravity;
    private bool previousHydraulicPressure;
    private SimulationMode previousAirMode = SimulationMode.Simulation;
    private bool presentationDirty = true;
    private bool gasVisualNeedsRebuild = true;
    private bool finalizeCellularRest;
    private bool waterPressureRoutesDirty = true;
    private bool solidMotionNeedsCellular = true;
    private bool cellMaterialsDirty;
    private bool thermalActive;
    private readonly FixedStepThermalScheduler thermalScheduler = new();
    private double gasMotionAccumulator;
    private ulong gasMotionTickIndex;
    private readonly GasBrushQueue gasBrushQueue = new();
    private readonly BrushDrawCommand[] immediateBrushCommands = new BrushDrawCommand[SimulationSettings.MaximumBrushCommands];
    // Renderer::render_fire in TPT advances together with its 60 Hz particle
    // simulation.  Keeping this accumulator separate from presentation avoids
    // fading FIRE/SMKE 100 times per second on a 100 FPS Phyxel window.
    private double fireGlowAccumulator;
    private ulong airTickIndex;
    private bool airFieldPopulated;
    private bool thermalTimingPending;
    private int thermalTimingSamples;
    private double thermalTimingTotalMilliseconds;
    private double thermalTimingMinimumMilliseconds = double.PositiveInfinity;
    private double thermalTimingMaximumMilliseconds;
    private bool contactTransitionPotential;
    private bool contactTimingPending;
    private int contactTimingSamples;
    private double contactTimingTotalMilliseconds;
    private double contactTimingMinimumMilliseconds = double.PositiveInfinity;
    private double contactTimingMaximumMilliseconds;
    private readonly PhaseTransitionWakeUpGate phaseWakeUpGate = new();
    private ulong phaseReadbackGeneration;
    private ulong phaseFallbackWakeUps;
    private bool phaseTimingPending;
    private int phaseTimingSamples;
    private double phaseTimingTotalMilliseconds;
    private double phaseTimingMinimumMilliseconds = double.PositiveInfinity;
    private double phaseTimingMaximumMilliseconds;
    private ulong phaseDispatches;
    private ulong phaseSummaryReadbacks;
    private int maximumPhaseDispatchesPerFrame;
    private PhaseTransitionSummaryFlags lastPhaseSummary;
    private uint lastPhaseDispatchFrame;
    private uint lastCompositionFrame;
    private ulong combustionDispatches;
    private ulong combustionSummaryReadbacks;
    private ulong combustionReadbackGeneration;
    private CombustionSummaryFlags lastCombustionSummary;
    private bool combustionTimingPending;
    private int combustionTimingSamples;
    private double combustionTimingTotalMilliseconds;
    private double combustionTimingMinimumMilliseconds = double.PositiveInfinity;
    private double combustionTimingMaximumMilliseconds;
    private int settledObservations;
    private int hydraulicWarmupFrames;
    private int fastSettleFrames;
    private int fastMaximumAwakeFrames;
    private int lastThermalTicksPerFrame;
    private bool diagnosticsSuppressPhaseReadbackPolling;

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
    public ulong GasTicks => gasScheduler.TotalTicks;
    public ulong AirTicks => airTickIndex;
    public ulong GasMotionTicks => gasMotionTickIndex;
    internal ThermalGpuTimingStatistics AirGpuTiming => boundResources?.AirTimer?.Statistics ?? default;
    internal ThermalGpuTimingStatistics AirHeatGpuTiming => boundResources?.AirHeatTimer?.Statistics ?? default;
    internal ThermalGpuTimingStatistics GasMotionGpuTiming => boundResources?.GasMotionTimer?.Statistics ?? default;
    public ThermalGpuTimingStatistics ThermalGpuTiming => new(
        thermalTimingSamples,
        thermalTimingSamples == 0 ? 0 : thermalTimingTotalMilliseconds / thermalTimingSamples,
        thermalTimingSamples == 0 ? 0 : thermalTimingMinimumMilliseconds,
        thermalTimingMaximumMilliseconds);
    public ThermalGpuTimingStatistics ContactTransitionGpuTiming => new(
        contactTimingSamples,
        contactTimingSamples == 0 ? 0 : contactTimingTotalMilliseconds / contactTimingSamples,
        contactTimingSamples == 0 ? 0 : contactTimingMinimumMilliseconds,
        contactTimingMaximumMilliseconds);
    public ThermalGpuTimingStatistics GasRedistributionGpuTiming => new(
        gasTimingSamples,
        gasTimingSamples == 0 ? 0 : gasTimingTotalMilliseconds / gasTimingSamples,
        gasTimingSamples == 0 ? 0 : gasTimingMinimumMilliseconds,
        gasTimingMaximumMilliseconds);
    public ThermalGpuTimingStatistics PhaseGpuTiming => new(
        phaseTimingSamples,
        phaseTimingSamples == 0 ? 0 : phaseTimingTotalMilliseconds / phaseTimingSamples,
        phaseTimingSamples == 0 ? 0 : phaseTimingMinimumMilliseconds,
        phaseTimingMaximumMilliseconds);
    public ulong PhaseDispatches => phaseDispatches;
    public ulong PhaseSummaryReadbacks => phaseSummaryReadbacks;
    public ulong PhaseFallbackWakeUps => phaseFallbackWakeUps;
    public int MaximumPhaseDispatchesPerFrame => maximumPhaseDispatchesPerFrame;
    public PhaseTransitionSummaryFlags LastPhaseSummary => lastPhaseSummary;
    public ulong CombustionDispatches => combustionDispatches;
    public ulong CombustionSummaryReadbacks => combustionSummaryReadbacks;
    public CombustionSummaryFlags LastCombustionSummary => lastCombustionSummary;
    public ThermalGpuTimingStatistics CombustionGpuTiming => new(
        combustionTimingSamples,
        combustionTimingSamples == 0 ? 0 : combustionTimingTotalMilliseconds / combustionTimingSamples,
        combustionTimingSamples == 0 ? 0 : combustionTimingMinimumMilliseconds,
        combustionTimingMaximumMilliseconds);
    public bool PhasePresentationIsCurrent => phaseDispatches == 0 || lastCompositionFrame >= lastPhaseDispatchFrame;
    public int LastThermalTicksPerFrame => lastThermalTicksPerFrame;
    public int PendingPhaseReadbackSlots
    {
        get
        {
            if (boundResources is null)
            {
                return 0;
            }
            int pending = 0;
            foreach (GpuPhaseSummaryReadbackSlot slot in boundResources.PhaseSummaryReadbackSlots)
            {
                pending += slot.Pending ? 1 : 0;
            }
            return pending;
        }
    }

    internal void ConfigurePhaseAcceptanceDiagnostics(
        bool suppressReadbackPolling)
    {
        diagnosticsSuppressPhaseReadbackPolling = suppressReadbackPolling;
    }

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
        // Free density bodies remain physical when construction gravity is off.
        solidSleeping = false;
        topologyDirty = worldHasMatter;
        cellularSleeping = false;
        settledObservations = 0;
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
            worldHasMatter || thermalActive || containsMaterialCommand || retainOxidizerField || boundResources?.FilterCount>0);
        if (!ReferenceEquals(resources, boundResources))
        {
            Clear(resources);
            boundResources = resources;
            ResetActivity(resources.IsSimulationAllocated);
        }

        if (!diagnosticsSuppressPhaseReadbackPolling)
        {
            PollPhaseSummary(resources);
            PollCombustionSummary(resources);
        }
        PollPhaseTiming(resources);
        PollCombustionTiming(resources);
        PollGasTiming(resources);
        ApplyPendingPhaseFallback(resources);

        if (settings.HydraulicPressure != previousHydraulicPressure)
        {
            ApplyHydraulicMode(resources, settings.HydraulicPressure);
            previousHydraulicPressure = settings.HydraulicPressure;
        }

        if (previousAirMode != settings.Mode)
        {
            // Geometry is identical in both modes. Preserve physical pressure
            // and momentum, so switching a lit furnace does not stop its draft.
            // Only the correction guess belongs to the old divergence policy.
            resources.Context.ClearUnorderedAccessView(resources.AirProjectionA.UnorderedView, new RawInt4());
            resources.Context.ClearUnorderedAccessView(resources.AirProjectionB.UnorderedView, new RawInt4());
            previousAirMode = settings.Mode;
        }
        resources.Context.ComputeShader.SetShaderResource(15,resources.Filters.View);
        ApplyFilters(resources,commands);
        SimulationFrameConstants constants = CreateConstants(settings, commands);
        if (commands.Length > 0 && resources.IsSimulationAllocated)
        {
            int immediateCount = 0;
            bool gasHeld = false;
            foreach (BrushDrawCommand command in commands)
            {
                bool scheduledGas = false;
                if (!settings.Paused && command.Mode == BrushCommandMode.Material)
                {
                    MaterialProperties material = materialRegistry[command.MaterialIndex].Properties;
                    scheduledGas = material.SimulationKind == (uint)MaterialSimulationKind.Gas &&
                        (material.Flags & (uint)(MaterialFlags.Flame | MaterialFlags.Smoke)) == 0;
                }
                if (scheduledGas)
                {
                    gasHeld = true;
                    if (!gasBrushQueue.Capture(command)) continue;
                }
                immediateBrushCommands[immediateCount++] = command;
            }
            if (!gasHeld) gasBrushQueue.Reset();
            DispatchBrush(resources, immediateBrushCommands.AsSpan(0, immediateCount), ref constants);
            bool topologyCommands = ContainsGridTopologyCommand(commands);
            cellMaterialsDirty |= topologyCommands;
            gasVisualNeedsRebuild |= topologyCommands;
            RegisterActivity(
                commands,
                settings.Width,
                settings.Height,
                settings.HydraulicPressure);
            worldHasMatter |= containsMaterialCommand;
            presentationDirty = true;
        }
        else
        {
            gasBrushQueue.Release();
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

        if (!settings.Paused && solidMatter && (settings.SolidGravity || (!bodyBalanceBaseline && freeBodyMatter)) && !solidSleeping)
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
            densityBodyAccumulator += Math.Min(Math.Max(elapsedSeconds, 0), 1f / 15);
            bool densityBodyStep = densityBodyAccumulator + 1e-8 >= 1.0 / 15;
            if (densityBodyStep) densityBodyAccumulator = Math.Max(0, densityBodyAccumulator - 1.0 / 15);
            airborneBodyAccumulator += Math.Min(Math.Max(elapsedSeconds, 0), 1f / 15);
            int airborneSteps = freeBodyMatter ? Math.Min(4, (int)((airborneBodyAccumulator + 1e-8) * 60)) : 0;
            airborneBodyAccumulator = freeBodyMatter ? Math.Max(0, airborneBodyAccumulator - airborneSteps / 60.0) : 0;
            DispatchSolidPass(resources, ref constants, (cellularSleeping ? 0u : 1u) |
                (densityBodyStep ? 2u : 0u) | (airborneSteps > 0 ? 4u : 0u));
            for (int step = 1; step < airborneSteps; step++)
                DispatchSolidPass(resources, ref constants, 4u | 8u);
            if (!bodyBalanceBaseline)
            {
                // Extra air passes leave their own flags in the constants.
                // Rotation still follows the liquid/body clock, including at
                // 30 FPS when two air passes occur before the balance pass.
                constants.SolidPass = densityBodyStep ? 2u : 0u;
                DispatchSolidBalance(resources, ref constants, densityBodyStep);
            }
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
            // Only the optional viscous-liquid gates read dt in this solver.
            // Existing water/granular movement keeps its established schedule.
            float cellularPreviousDelta = constants.DeltaTime;
            constants.DeltaTime = Math.Clamp(elapsedSeconds, 0, .1f);
            // Clock the short supported-liquid patches independently of render
            // FPS. Both cellular substeps used the same frame parity before,
            // so their shared seams advanced only 30 times/s at 30 FPS.
            viscousSurfaceAccumulator += constants.DeltaTime;
            viscousSurfaceTicksThisFrame = Math.Min(12,(int)((viscousSurfaceAccumulator+1e-6)*120));
            viscousSurfaceAccumulator = Math.Max(0,viscousSurfaceAccumulator-viscousSurfaceTicksThisFrame/120d);
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
            constants.DeltaTime = cellularPreviousDelta;
            Phyxel.Diagnostics.NonfiniteStateTrace.Observe(resources,frameIndex,"cellular");
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

        int gasTicks = gasScheduler.Advance(
            elapsedSeconds,
            settings.Paused,
            gasMatter && resources.IsSimulationAllocated);
        for (int tick = 0; tick < gasTicks; tick++)
        {
            ulong tickIndex = gasScheduler.TotalTicks - (ulong)gasTicks + (ulong)tick + 1;
            bool measureGas = gasScheduler.TotalTicks >= 120 && !gasTimingPending;
            DispatchGasRedistribution(resources, ref constants, tickIndex, measureGas);
            cellMaterialsDirty = false;
            cellularSleeping = false;
            presentationDirty = true;
        }

        if (settings.OpenBoundaries && resources.IsSimulationAllocated && !settings.Paused)
        {
            DispatchOpenBoundary(resources, ref constants);
            cellMaterialsDirty = false;
            presentationDirty = true;
        }

        if (!settings.AirSimulation && airFieldPopulated && resources.IsSimulationAllocated)
        {
            // Выключили на ходу — стереть поле, иначе застывший шлейф останется
            // висеть в режиме отображения давления.
            DispatchAirClear(resources);
            presentationDirty = true;
        }
        airFieldPopulated = settings.AirSimulation && resources.IsSimulationAllocated;

        bool combustionActive = (materialRegistry.RegistryHasCombustibleMaterials ||
            materialRegistry.RegistryHasTransientMaterials) &&
            resources.IsSimulationAllocated && (thermalActive || retainOxidizerField) && !settings.Paused;

        // Fixed 60 Hz deterministic gas carrier.  This deliberately follows
        // air: FIRE and SMKE are tracers of the just-solved field, as in TPT's
        // BeforeSim -> UpdateParticles order.
        if (resources.IsSimulationAllocated && !settings.Paused &&
            (gasMatter || settings.AirSimulation || combustionActive))
        {
            gasMotionAccumulator = Math.Min(
                gasMotionAccumulator + Math.Clamp(elapsedSeconds, 0, FixedAirStep * MaximumAirTicksPerFrame),
                FixedAirStep * MaximumAirTicksPerFrame);
            int gasMotionTicks = 0;
            // elapsedSeconds arrives as float. Its rounding at 100 FPS must
            // not lose the last 60 Hz tick at an otherwise exact checkpoint.
            while (gasMotionAccumulator + 1e-6 >= FixedAirStep && gasMotionTicks < MaximumAirTicksPerFrame)
            {
                gasMotionAccumulator -= FixedAirStep;
                gasMotionTicks++;
                gasMotionTickIndex++;
                ReadOnlySpan<BrushDrawCommand> gasCommands = gasBrushQueue.Tick();
                if (!gasCommands.IsEmpty)
                {
                    uint previousFrame = constants.FrameIndex;
                    constants.FrameIndex = unchecked((uint)gasMotionTickIndex);
                    DispatchBrush(resources, gasCommands, ref constants);
                    constants.FrameIndex = previousFrame;
                    cellMaterialsDirty = true;
                    RegisterActivity(gasCommands, settings.Width, settings.Height, settings.HydraulicPressure);
                    worldHasMatter = true;
                }
                gasBrushQueue.Consumed();
                if (pressurePowderPotential && cellularMatter && !cellularSleeping)
                {
                    uint previousFrame = constants.FrameIndex;
                    constants.FrameIndex = unchecked((uint)gasMotionTickIndex);
                    DispatchCellularAutomata(resources, ref constants, rebuildCellMaterials: true,
                        primaryStep: true, runPressureRoutes: false, runLongRangeFluid: false,
                        updateRestState: false, useOptimizedSchedule: false, pressurePowderOnly: true);
                    if (settings.Scale < 0.5f && fluidMatter)
                        DispatchCellularAutomata(resources, ref constants, rebuildCellMaterials: false,
                            primaryStep: false, runPressureRoutes: false, runLongRangeFluid: false,
                            updateRestState: false, useOptimizedSchedule: false, pressurePowderOnly: true);
                    constants.FrameIndex = previousFrame;
                    cellMaterialsDirty = true;
                }
                // Interleave each air tick and its particle tick. Batched air
                // updates at 30 FPS would otherwise consume stale gas impulses.
                if (settings.AirSimulation)
                {
                    airTickIndex++;
                    DispatchAirSimulation(resources, unchecked((uint)airTickIndex), settings.Mode == SimulationMode.Sandbox, settings.OpenBoundaries);
                    Phyxel.Diagnostics.NonfiniteStateTrace.Observe(resources,frameIndex,"air");
                }
                constants.DebugReserved2 = unchecked((uint)gasMotionTickIndex);
                if (gasMatter) DispatchGasMotion(resources, ref constants, settings.AirSimulation,
                    settings.Mode == SimulationMode.Simulation);
                // Each reaction tick belongs to one air/particle tick. Batching
                // two reactions after two air steps at 30 FPS changed when the
                // finite source reached the wave and excited different modes.
                // Keep this clock alive for hot fuel even with air switched off.
                if (combustionActive)
                {
                    bool measure = combustionDispatches >= 40 && !combustionTimingPending;
                    DispatchCombustion(resources, (float)FixedAirStep, measure, settings.OpenBoundaries,
                        settings.Mode == SimulationMode.Simulation, settings.AirSimulation);
                    combustionDispatches++;
                    Phyxel.Diagnostics.NonfiniteStateTrace.Observe(resources,frameIndex,"combustion");
                }
            }
            if (gasMotionTicks > 0)
            {
                cellMaterialsDirty = combustionActive;
                presentationDirty = true;
            }
        }

        PollThermalTiming(resources);
        PollContactTiming(resources);
        int thermalTicks = thermalScheduler.Advance(
            elapsedSeconds,
            settings.Paused,
            thermalActive && resources.IsSimulationAllocated);
        lastThermalTicksPerFrame = thermalTicks;
        for (int tick = 0; tick < thermalTicks; tick++)
        {
            uint thermalTick = unchecked((uint)(thermalScheduler.TotalTicks -
                (ulong)thermalTicks + (ulong)tick + 1));
            bool measure = thermalScheduler.TotalTicks >= 40 && !thermalTimingPending;
            DispatchThermalDiffusion(resources, measure, thermalTick, liquidMatter);
            Phyxel.Diagnostics.NonfiniteStateTrace.Observe(resources,frameIndex,"thermal");
            if (materialRegistry.RegistryHasContactTransitions && contactTransitionPotential)
            {
                uint tickIndex = unchecked((uint)(thermalScheduler.TotalTicks -
                    (ulong)thermalTicks + (ulong)tick + 1));
                bool measureContact = thermalScheduler.TotalTicks >= 40 && !contactTimingPending;
                DispatchContactTransitions(resources, tickIndex, measureContact);
                Phyxel.Diagnostics.NonfiniteStateTrace.Observe(resources,frameIndex,"contact");
                // A transition resets RestFrames on the GPU. Conservatively
                // schedule a cellular step after each fixed contact tick; this
                // avoids a blocking summary readback while keeping the 20 Hz
                // contact model independent of rendered FPS.
                cellularMatter = true;
                fluidMatter = true;
                cellularSleeping = false;
                finalizeCellularRest = false;
                presentationDirty = true;
            }
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
                DispatchPhaseTransitions(resources, thermalTicks, measure);
            phaseDispatches++;
            Phyxel.Diagnostics.NonfiniteStateTrace.Observe(resources,frameIndex,"phase");
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

        int fireGlowTicks = 0;
        if (resources.IsSimulationAllocated && !settings.Paused)
        {
            fireGlowAccumulator = Math.Min(
                fireGlowAccumulator + Math.Clamp(elapsedSeconds, 0, FixedAirStep * MaximumAirTicksPerFrame),
                FixedAirStep * MaximumAirTicksPerFrame);
            while (fireGlowAccumulator + 1e-9 >= FixedAirStep &&
                fireGlowTicks < MaximumAirTicksPerFrame)
            {
                fireGlowAccumulator -= FixedAirStep;
                fireGlowTicks++;
            }
            if (fireGlowTicks > 0)
            {
                presentationDirty = true;
            }
        }

        constants.SolidGravity = settings.SolidGravity ? 1u : 0u;
        if (presentationDirty && resources.IsSimulationAllocated)
        {
            if (settings.Paused && gasVisualNeedsRebuild)
            {
                // Editing on pause must be visible immediately, and erased
                // gas must not leave a frozen render-only trail behind it.
                RawInt4 zero = new(0, 0, 0, 0);
                resources.Context.ClearUnorderedAccessView(resources.GasVisual.UnorderedView, zero);
                resources.Context.ClearUnorderedAccessView(resources.GasVisualScratch.UnorderedView, zero);
                DispatchGasVisual(resources, decay: false);
                // Smoke/flame also use a derived field. Rebuild it once after
                // paused edits/load so smoke stays visible without raw pixels,
                // and erased sources do not leave a frozen glow behind.
                DispatchFireGlowClear(resources);
                DispatchFireGlowDeposit(resources);
                gasVisualNeedsRebuild = false;
            }
            DispatchComposition(resources, ref constants, fireGlowTicks);
            if (fireGlowTicks > 0) gasVisualNeedsRebuild = false;
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
        foreach(var view in resources.SolidOrigins.UnorderedAccessViews)
            resources.Context.ClearUnorderedAccessView(view,new RawInt4(0,0,0,0));
        ResetActivity(resources.IsSimulationAllocated);
    }

    public void ResetCurrentSimulation(SimulationSettings settings)
    {
        if (boundResources is null || !boundResources.IsSimulationAllocated)
        {
            ResetActivity(false);
            return;
        }

        bool containsMatter = worldHasMatter;
        bool containsContactSource = contactTransitionPotential;
        RestoreWorldActivity(
            boundResources,
            containsMatter,
            containsContactSource,
            settings.HydraulicPressure,
            retainOxidizerField);
        previousSolidGravity = settings.SolidGravity;
    }

    public void RestoreWorldActivity(
        GpuSimulationResources resources,
        bool containsMatter,
        bool containsContactTransitionSource,
        bool hydraulicPressure,
        bool preserveOxidizer = false)
    {
        resources.Context.ClearUnorderedAccessView(
            resources.PathBlockerMasks.UnorderedView,
            new RawInt4(0, 0, 0, 0));
        boundResources = resources;
        worldHasMatter = containsMatter;
        foreach(var view in resources.SolidOrigins.UnorderedAccessViews)
            resources.Context.ClearUnorderedAccessView(view,new RawInt4(0,0,0,0));
        resources.Context.ClearUnorderedAccessView(resources.ContactSummary.UnorderedView, new RawInt4());
        // The pressure correction is a solver guess for the old geometry,
        // not saved physical state. Never reuse it after loading/resetting a
        // world in an allocation with the same dimensions.
        resources.Context.ClearUnorderedAccessView(resources.AirProjectionA.UnorderedView, new RawInt4());
        resources.Context.ClearUnorderedAccessView(resources.AirProjectionB.UnorderedView, new RawInt4());
        retainOxidizerField = preserveOxidizer;
        thermalActive = containsMatter;
        contactTransitionPotential = containsContactTransitionSource;
        thermalScheduler.Reset();
        gasScheduler.Reset();
        viscousSurfaceAccumulator=0;
        viscousSurfaceTicksThisFrame=0;
        viscousSurfaceTickIndex=0;
        ResetThermalTiming();
        ResetGasTiming();
        ResetContactTiming();
        ResetPhaseRuntime();
        ResetCombustionRuntime();
        cellularMatter = containsMatter;
        pressurePowderPotential = containsMatter && materialRegistry.RegistryHasPressurePowders;
        fluidMatter = containsMatter;
        liquidMatter = containsMatter;
        gasMatter = containsMatter;
        solidMatter = containsMatter;
        freeBodyMatter = containsMatter; // Conservative until first GPU statistics.
        densityBodyAccumulator = 0;
        airborneBodyAccumulator = 0;
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
        freeBodyMatter = statistics.FreeBodyCells > 0;
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
            solidSleeping = statistics.MovingSolidCells == 0;
            ResetActiveRegion();
        }
        if (statistics.ActiveCells == 0 && (worldHasMatter || !retainOxidizerField))
        {
            // Exhausted air is still world state after the last particle is
            // erased. Particle sleep must not discard its resource or clock.
            bool preserveOxidizer = retainOxidizerField || boundResources is { IsSimulationAllocated: true };
            ResetActivity(false, resetThermal: false);
            retainOxidizerField = preserveOxidizer;
        }
    }

    private void DispatchBrush(
        GpuSimulationResources resources,
        ReadOnlySpan<BrushDrawCommand> commands,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        bool steamJetInjectionTrace = Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_INJECTION_TRACE") == "1";
        bool steamJetSourceDistributionTrace =
            Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_SOURCE_DISTRIBUTION_TRACE") == "1";
        for (int commandIndex = 0; commandIndex < commands.Length; commandIndex++)
        {
            ReadOnlySpan<BrushDrawCommand> command = commands.Slice(commandIndex, 1);
            if (steamJetInjectionTrace || steamJetSourceDistributionTrace)
            {
                context.CopyResource(resources.Grid.ReadBuffer, resources.SteamGasStepPreviousGrid.Buffer);
            }
            resources.Commands.Upload(context, command);
            constants.CommandCount = 1;
            constants.MaximumBrushDiameter =
                (uint)(MathF.Ceiling(command[0].Radius) * 2 + 1);
            int radius = (int)MathF.Ceiling(command[0].Radius);
            int endX = command[0].Shape == BrushCommandShape.Segment
                ? command[0].EndX
                : command[0].X;
            int endY = command[0].Shape == BrushCommandShape.Segment
                ? command[0].EndY
                : command[0].Y;
            constants.DispatchExtentX =
                (uint)(Math.Abs(endX - command[0].X) + radius * 2 + 1);
            constants.DispatchExtentY =
                (uint)(Math.Abs(endY - command[0].Y) + radius * 2 + 1);
            UpdateConstants(context, resources, ref constants);
            context.ComputeShader.Set(resources.BrushShader);
            context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
            context.ComputeShader.SetShaderResources(0, resources.Commands.View, resources.Materials.View);
            context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.ReadUnorderedView);
            context.Dispatch(
                DivideRoundUp((int)constants.DispatchExtentX, 16),
                DivideRoundUp((int)constants.DispatchExtentY, 16),
                1);
            Unbind(context, 2, 1);
            if (steamJetInjectionTrace)
            {
                DispatchSteamJetInjectionObserver(context, resources, ref constants);
            }
            if (steamJetSourceDistributionTrace)
            {
                DispatchSteamJetInjectionDistributionObserver(context, resources, ref constants);
            }
        }
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
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.ReadUnorderedView, resources.SolidOrigins.ReadUnorderedView);
        context.Dispatch(DivideRoundUp(cells, 256), 1, 1);
        Unbind(context, 3, 3);
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
        context.ClearUnorderedAccessView(resources.SolidRotationBlocked.UnorderedView,new RawInt4(0,0,0,0));
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
        context.ComputeShader.Set(resources.SolidOriginsInitializeShader);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView,resources.Materials.View,
            null,null,null,null,resources.SolidOrigins.ReadView);
        context.ComputeShader.SetUnorderedAccessViews(0,null,null,null,null,resources.SolidOrigins.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width*resources.Height,256),1,1);
        Unbind(context,9,7);
        resources.SolidOrigins.Swap();
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
        context.CopyResource(resources.GasMotion.Buffer,resources.SteamGasStepPreviousMotion.Buffer);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.BodyFlags.View,
            resources.CellMaterials.View,
            resources.ComponentParents.View,
            resources.SolidBodyGeometry.View,
            resources.Materials.View,
            resources.SolidBodyMass.View,
            resources.SolidOrigins.ReadView,resources.SteamGasStepPreviousMotion.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.WriteUnorderedView,
            null,null,null,null,resources.SolidOrigins.WriteUnorderedView,resources.GasMotion.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 9, 8);

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
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.WriteUnorderedView,
            null,null,null,null,null,resources.GasMotion.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 9, 8);
        resources.Grid.Swap();
        resources.SolidOrigins.Swap();
        waterPressureRoutesDirty = true;
    }

    internal void DispatchBodyOnlyAcceptanceStep(GpuSimulationResources resources,bool constructionGravity,bool initialize)
    {
        var constants=new SimulationFrameConstants{Width=(uint)resources.Width,Height=(uint)resources.Height,
            DeltaTime=1f/15,Gravity=980,
            SolidGravity=constructionGravity?1u:0u,FrameIndex=1,SolidPass=7};
        if(initialize){DispatchComponentLabeling(resources,ref constants);DispatchSolidGeometry(resources,ref constants);}
        DispatchSolidPass(resources,ref constants,7);
        if(!bodyBalanceBaseline)DispatchSolidBalance(resources,ref constants,true);
    }

    private static void DispatchSolidBalance(GpuSimulationResources resources, ref SimulationFrameConstants constants, bool step)
    {
        DeviceContext context = resources.Context;
        context.ClearUnorderedAccessView(resources.SolidBalance.UnorderedView, new RawInt4(0, 0, 0, 0));
        if(step)
        {
            context.ClearUnorderedAccessView(resources.SolidRotationTargets.UnorderedView, new RawInt4(0,0,0,0));
            context.ClearUnorderedAccessView(resources.SolidRotationBlocked.UnorderedView, new RawInt4(0,0,0,0));
        }
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.Set(resources.SolidBalanceShader);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Materials.View,
            null,null,null,resources.SolidBodyGeometry.View);
        context.ComputeShader.SetUnorderedAccessViews(0, resources.SolidBalance.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 9, 7);
        if(step)
        {
        context.ComputeShader.Set(resources.SolidRotationBuildShader);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView,resources.Materials.View,
            resources.SolidBalance.View,null,null,resources.SolidBodyGeometry.View,resources.SolidOrigins.ReadView);
        context.ComputeShader.SetUnorderedAccessViews(0,null,null,null,null,null,null,resources.SolidRotationPlans.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width*resources.Height,256),1,1);
        Unbind(context,9,7);
        context.ComputeShader.Set(resources.SolidRotationPlanShader);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Materials.View,
            resources.SolidBalance.View, null, null, resources.SolidBodyGeometry.View,
            resources.SolidOrigins.ReadView,null,resources.SolidRotationPlans.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.SolidRotationTargets.UnorderedView,
            resources.SolidRotationBlocked.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 9, 7);
        }
        context.ComputeShader.Set(resources.SolidRotationApplyShader);
        context.CopyResource(resources.GasMotion.Buffer,resources.SteamGasStepPreviousMotion.Buffer);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Materials.View,
            resources.SolidBalance.View, resources.SolidRotationTargets.View, resources.SolidRotationBlocked.View,
            resources.SolidBodyGeometry.View,resources.SolidOrigins.ReadView,resources.SteamGasStepPreviousMotion.View,resources.SolidRotationPlans.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, null, null, resources.Grid.WriteUnorderedView,resources.SolidOrigins.WriteUnorderedView,
            resources.GasMotion.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 9, 8);
        resources.Grid.Swap();
        resources.SolidOrigins.Swap();
    }

    private void DispatchCellularAutomata(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        bool rebuildCellMaterials,
        bool primaryStep,
        bool runPressureRoutes,
        bool runLongRangeFluid,
        bool updateRestState,
        bool useOptimizedSchedule,
        bool pressurePowderOnly = false)
    {
        DeviceContext context = resources.Context;
        // Resolve gravity and ordinary lateral flow before consulting the
        // hydraulic caches. Phase 13 balances adjacent columns only; phase 29
        // applies that local plan before the pressure-only passes run.
        ReadOnlySpan<uint> phases = pressurePowderOnly
            ? primaryStep
                ? (constants.FrameIndex & 1) == 0 ? PrimaryEvenPhases : PrimaryOddPhases
                : (constants.FrameIndex & 1) == 0 ? SecondaryEvenPhases : SecondaryOddPhases
            : useOptimizedSchedule
            ? (frameIndex & 1) == 0 ? OptimizedEvenPhases : OptimizedOddPhases
            : primaryStep
                ? (frameIndex & 1) == 0 ? PrimaryEvenPhases : PrimaryOddPhases
            : (frameIndex & 1) == 0 ? SecondaryEvenPhases : SecondaryOddPhases;

        // Bind common compute states once before the loop
        context.ComputeShader.Set(resources.CellularAutomataShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        // Air is bound read-only: flame cells take their drift from it.
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Air.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.BodyFlags.UnorderedView,
            resources.PathBlockerMasks.UnorderedView,
            resources.CellMaterials.UnorderedView,
            resources.WaterPressureRoutes.UnorderedView,
            resources.WaterPressureRouteScratch.UnorderedView,
            resources.GasMotion.UnorderedView);

        foreach (uint phase in phases)
        {
            if (pressurePowderOnly && phase != 32 && phase > 3 && (phase < 5 || phase > 12))
                continue;
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
            bool ordinaryFlowPhase = phase is >= 48 and <= 55;
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
            constants.GasSubStep = pressurePowderOnly ? 0x80000000u : 0u;

            bool buildPathBlockers = phase == 31;
            bool buildCellMaterials = phase == 32;
            bool waterColumnPhase = phase == 33 || phase == 56 || phase == 57 || phase == 58 || phase == 59 ||
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

            uint previousFrame=constants.FrameIndex;
            float previousDelta=constants.DeltaTime;
            int repeats=phase==58 ? (primaryStep ? viscousSurfaceTicksThisFrame : 0) : 1;
            for(int repeat=0;repeat<repeats;repeat++)
            {
                if(phase==58)
                {
                    constants.FrameIndex=unchecked((uint)++viscousSurfaceTickIndex);
                    constants.DeltaTime=1f/120;
                }
                UpdateConstants(context, resources, ref constants);
                context.Dispatch(DivideRoundUp(dispatchW, 16), DivideRoundUp(dispatchH, 16), 1);
            }
            constants.FrameIndex=previousFrame;
            constants.DeltaTime=previousDelta;
            if(primaryStep && (phase==33||phase==58))Phyxel.Diagnostics.NonfiniteStateTrace.Surface(resources,previousFrame,phase);
        }

        // Unbind once at the end
        Unbind(context, 2, 7);
        constants.SimulationPhase = 0;
        constants.GasSubStep = 0;
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
        // Air is bound read-only: flame cells take their drift from it.
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Air.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.BodyFlags.UnorderedView,
            resources.PathBlockerMasks.UnorderedView,
            resources.CellMaterials.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(6, resources.GasMotion.UnorderedView);

        context.Dispatch(DivideRoundUp(dispatchW, 16), DivideRoundUp(dispatchH, 16), 1);
        Unbind(context, 2, 7);
        constants.SimulationPhase = 0;
    }

    private void DispatchComposition(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        int fireGlowTicks)
    {
        DeviceContext context = resources.Context;
        bool collect = frameIndex % 10 == 0;
        if (collect)
        {
            context.ClearUnorderedAccessView(resources.Statistics.WriteUnorderedView, new RawInt4(0, 0, 0, 0));
        }
        // Renderer::render_fire displays the freshly deposited field, then
        // applies neighbour bleed and the -4 fade. Keep that ordering here.
        for (int tick = 0; tick + 1 < fireGlowTicks; tick++)
        {
            DispatchFireGlowDeposit(resources);
            DispatchFireGlowDiffuse(resources);
            DispatchGasVisual(resources, decay: true);
            DispatchGasVisual(resources, decay: false);
        }
        if (fireGlowTicks > 0)
        {
            DispatchFireGlowDeposit(resources);
            // Keep the gas field in the displayed phase between fixed ticks.
            // Decaying it after presentation made 100-FPS intermediate frames
            // alternate bright/depleted CO2 even with a stationary physical gas.
            DispatchGasVisual(resources, decay: true);
            DispatchGasVisual(resources, decay: false);
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
            resources.PathBlockerMasks.View,
            resources.FireGlow.View,
            resources.Air.View,
            resources.GasVisual.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.CompositionTargets.WriteView,
            resources.Statistics.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 7, 2);
        if (collect)
        {
            resources.Statistics.Swap();
        }
        constants.SimulationPhase = 0;
        context.CopyResource(resources.CompositionTargets.WriteTexture, resources.NativePresentationTexture);
        resources.PresentationIndex = 1 - resources.PresentationIndex;
        resources.CompositionTargets.Swap();
        if (fireGlowTicks > 0)
        {
            DispatchFireGlowDiffuse(resources);
        }
    }

    private static void Clear(GpuSimulationResources resources)
    {
        RawInt4 zero = new(0, 0, 0, 0);
        Array.Clear(resources.FilterMap); resources.FilterCount=0;
        resources.Context.ClearUnorderedAccessView(resources.Filters.UnorderedView,zero);
        resources.Context.ClearUnorderedAccessView(resources.ContactSummary.UnorderedView, zero);
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
        resources.Context.ClearUnorderedAccessView(resources.GasMotion.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.ReactionPending.UnorderedView,zero);
        foreach(var view in resources.ReactionPulse.UnorderedAccessViews) resources.Context.ClearUnorderedAccessView(view,zero);
        resources.Context.ClearUnorderedAccessView(resources.GasAirImpulse.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.SteamGasStepStatistics.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.SteamJetInjectionStatistics.UnorderedView, zero);
        if (resources.SteamJetInjectionDistribution is not null)
        {
            resources.Context.ClearUnorderedAccessView(resources.SteamJetInjectionDistribution.UnorderedView, zero);
        }
        if (resources.SteamJetLateralBands is not null)
        {
            resources.Context.ClearUnorderedAccessView(resources.SteamJetLateralBands.UnorderedView, zero);
        }
        if (resources.SteamJetBlockingSubsteps is not null)
        {
            resources.Context.ClearUnorderedAccessView(resources.SteamJetBlockingSubsteps.UnorderedView, zero);
            resources.Context.ClearUnorderedAccessView(resources.SteamJetBlockingFrames!.UnorderedView, zero);
            resources.Context.ClearUnorderedAccessView(resources.SteamJetBlockingMarkers!.UnorderedView, zero);
            resources.Context.ClearUnorderedAccessView(resources.SteamJetBlockingMovedFrames!.UnorderedView, zero);
        }
        resources.Context.ClearUnorderedAccessView(resources.GasVerticalMotionStatistics.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.GasVerticalBlockFrameMarkers.UnorderedView, zero);
        foreach (UnorderedAccessView view in resources.Statistics.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }
        resources.Context.ClearUnorderedAccessView(resources.CombustionSummary.UnorderedView, zero);
        // Свет огня и поле воздуха переживают кадры по построению, поэтому при
        // сбросе мира их надо гасить явно. Иначе от прошлой сцены остаётся
        // висеть зарево, а новый мазок огня попадает в остаточный воздушный
        // вихрь и разлетается ещё до того, как создаст собственный поток.
        DispatchFireGlowClear(resources);
        resources.Context.ClearUnorderedAccessView(resources.GasVisual.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.GasVisualScratch.UnorderedView, zero);
        DispatchAirClear(resources);
        float[] freshAir = new float[resources.Oxidizer.Count];
        Array.Fill(freshAir, 1f);
        foreach (var buffer in resources.Oxidizer.Buffers) resources.Context.UpdateSubresource(freshAir, buffer);
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
            HydraulicPressure = settings.HydraulicPressure ? 1u : 0u,
            DebugView = settings.RenderWithoutEffects
                ? 2u
                : settings.ShowAirField ? 1u : 0u,
            OpenBoundaries = settings.OpenBoundaries ? 1u : 0u
        };
    }

    private void ApplyFilters(GpuSimulationResources resources, ReadOnlySpan<BrushDrawCommand> commands)
    {
        bool changed = false;
        foreach (var command in commands)
        {
            if (command.Mode is not (BrushCommandMode.Filter or BrushCommandMode.Erase)) continue;
            uint rule = command.Mode == BrushCommandMode.Filter ? command.Reserved : 0;
            FilterRules.Validate(rule, materialRegistry.Count);
            int endX = command.Shape == BrushCommandShape.Segment ? command.EndX : command.X;
            int endY = command.Shape == BrushCommandShape.Segment ? command.EndY : command.Y;
            int radius = (int)MathF.Ceiling(command.Radius);
            float dx = endX - command.X, dy = endY - command.Y, length = dx * dx + dy * dy;
            for (int y = Math.Max(0, Math.Min(command.Y, endY) - radius);
                 y <= Math.Min(resources.Height - 1, Math.Max(command.Y, endY) + radius); y++)
            for (int x = Math.Max(0, Math.Min(command.X, endX) - radius);
                 x <= Math.Min(resources.Width - 1, Math.Max(command.X, endX) + radius); x++)
            {
                float t = length > 0 ? Math.Clamp(((x - command.X) * dx + (y - command.Y) * dy) / length, 0, 1) : 0;
                float rx = x - command.X - t * dx, ry = y - command.Y - t * dy;
                if (rx * rx + ry * ry > command.Radius * command.Radius) continue;
                int index = y * resources.Width + x;
                uint previous = resources.FilterMap[index];
                if (previous == rule) continue;
                resources.FilterCount += (rule != 0 ? 1 : 0) - (previous != 0 ? 1 : 0);
                resources.FilterMap[index] = rule;
                changed = true;
            }
        }
        if (changed) resources.UploadFilters();
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
            if(command.Mode==BrushCommandMode.Filter){
                topologyDirty=true;cellularSleeping=false;solidSleeping=false;solidMotionNeedsCellular=true;
                contactTransitionPotential|=worldHasMatter;finalizeCellularRest=false;settledObservations=0;
                activeMinX=0;activeMinY=0;activeMaxX=width-1;activeMaxY=height-1;activeRegionValid=true;
                continue;
            }
            if (command.Mode == BrushCommandMode.SetTemperature)
            {
                thermalActive |= worldHasMatter;
                continue;
            }
            if (command.Mode != BrushCommandMode.Material && command.Mode != BrushCommandMode.ThermalDevice &&
                command.Mode != BrushCommandMode.Erase)
            {
                continue;
            }
            ExpandActiveRegion(command.X, command.Y, (int)MathF.Ceiling(command.Radius), width, height);
            if (command.Shape == BrushCommandShape.Segment)
            {
                ExpandActiveRegion(
                    command.EndX,
                    command.EndY,
                    (int)MathF.Ceiling(command.Radius),
                    width,
                    height);
            }

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
            if (materialRegistry[command.MaterialIndex].Properties.ReactionPressurePerMass > 0 &&
                materialRegistry[command.MaterialIndex].Properties.SimulationKind ==
                    (uint)MaterialSimulationKind.Granular)
            {
                pressurePowderPotential = true;
                // An older empty-world readback must not put newly painted
                // powder to sleep. Future summaries use this generation.
                combustionReadbackGeneration++;
            }
            MaterialSimulationKind kind = (MaterialSimulationKind)materialRegistry[command.MaterialIndex]
                .Properties.SimulationKind;
            contactTransitionPotential |=
                materialRegistry[command.MaterialIndex].LiquidContactTransition is not null ||
                materialRegistry[command.MaterialIndex].Moisture is not null;
            if (kind == MaterialSimulationKind.Solid)
            {
                solidMatter = true;
                freeBodyMatter |= (materialRegistry[command.MaterialIndex].Properties.Flags & (uint)MaterialFlags.DensityBody) != 0;
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
        retainOxidizerField = false;
        cellularMatter = false;
        pressurePowderPotential = false;
        fluidMatter = false;
        liquidMatter = false;
        gasMatter = false;
        gasScheduler.Reset();
        ResetGasTiming();
        gasMotionAccumulator = 0;
        gasMotionTickIndex = 0;
        viscousSurfaceAccumulator=0;
        viscousSurfaceTicksThisFrame=0;
        viscousSurfaceTickIndex=0;
        gasBrushQueue.Reset();
        fireGlowAccumulator = 0;
        airFieldPopulated = false;
        solidMatter = false;
        freeBodyMatter = false;
        densityBodyAccumulator = 0;
        cellularSleeping = false;
        airborneBodyAccumulator = 0;
        solidSleeping = false;
        topologyDirty = false;
        settledObservations = 0;
        presentationDirty = dirtyPresentation;
        gasVisualNeedsRebuild = dirtyPresentation;
        finalizeCellularRest = false;
        waterPressureRoutesDirty = true;
        solidMotionNeedsCellular = true;
        fastSettleFrames = 0;
        fastMaximumAwakeFrames = 0;
        cellMaterialsDirty = false;
        contactTransitionPotential = false;
        if (resetThermal)
        {
            thermalActive = false;
            thermalScheduler.Reset();
            ResetThermalTiming();
            ResetContactTiming();
        }
        ResetPhaseRuntime();
        ResetCombustionRuntime();
        ResetActiveRegion();
    }

    internal void DispatchThermalDiffusion(GpuSimulationResources resources, bool measure,
        uint tickIndex, bool convectWater)
    {
        ThermalSimulationConstants constants = new()
        {
            DeltaTime = FixedThermalStep,
            ExchangeRate = ThermalExchangeRate,
            Width = (uint)resources.Width,
            Height = (uint)resources.Height,
            ObserveEnergy = resources.ThermalEnergyLedger is null ? 0u : 1u
        };
        DeviceContext context = resources.Context;
        if (measure)
        {
            context.Begin(resources.ThermalTimestampDisjointQuery);
            context.End(resources.ThermalTimestampStartQuery);
        }
        // Independent of cellular sleep, hydraulic mode and render FPS.
        // Same-material liquid rotations preserve occupancy and path maps.
        if (convectWater) DispatchWaterConvection(resources, tickIndex);
        context.ClearUnorderedAccessView(resources.Grid.WriteUnorderedView, new RawInt4());
        context.UpdateSubresource(ref constants, resources.ThermalConstants);
        context.ComputeShader.Set(resources.ThermalDiffusionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.ThermalConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.WriteUnorderedView);
        if (resources.ThermalEnergyLedger is not null)
            context.ComputeShader.SetUnorderedAccessView(1, resources.ThermalEnergyLedger.UnorderedView);
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
        Unbind(context, 2, resources.ThermalEnergyLedger is null ? 1 : 2);
        resources.Grid.Swap();
    }

    private void DispatchWaterConvection(GpuSimulationResources resources, uint tickIndex)
    {
        DeviceContext context = resources.Context;
        SimulationFrameConstants constants = new()
        {
            Width = (uint)resources.Width, Height = (uint)resources.Height,
            FrameIndex = tickIndex
        };
        context.ComputeShader.Set(resources.WaterConvectionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResource(0, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.ReadUnorderedView);
        for (uint pass = 0; pass < 4; pass++)
        {
            uint parity = (tickIndex & 1) == 0 ? pass : 3 - pass;
            constants.SimulationPhase = parity;
            constants.DispatchOffsetX = parity & 1;
            constants.DispatchOffsetY = parity >> 1;
            context.UpdateSubresource(ref constants, resources.FrameConstants);
            context.Dispatch(DivideRoundUp((resources.Width + 1) / 2, 16),
                DivideRoundUp((resources.Height + 1) / 2, 16), 1);
        }
        // One local same-height exchange per tick spreads bottom heat laterally
        // after it rises, without globally averaging the vessel or overturning
        // a stable warm upper layer.
        for (uint mixPass = 0; mixPass < 2; mixPass++)
        {
            uint side = mixPass == 0 ? 4u : 8u;
            constants.GasSubStep = side;
            constants.SimulationPhase = 4 + mixPass;
            uint span = side * 2;
            constants.DispatchOffsetX = (tickIndex / 2 + mixPass * 3) % span;
            constants.DispatchOffsetY = 0;
            context.UpdateSubresource(ref constants, resources.FrameConstants);
            context.Dispatch(DivideRoundUp((resources.Width + (int)span - 1) / (int)span, 16),
                DivideRoundUp(resources.Height, 16),1);
        }
        Unbind(context, 1, 1);
    }

    /// <summary>
    /// Один тик поля воздуха. Пять проходов подряд, каждый читает один буфер и
    /// пишет в другой: чтение и запись одного буфера были бы гонкой, потому что
    /// на GPU соседняя клетка может быть уже обновлена, а может и нет.
    /// Порядок повторяет Air::update_air из The Powder Toy.
    /// </summary>
    internal void DispatchAirSimulation(
        GpuSimulationResources resources,
        uint tickIndex,
        bool sandbox, bool openBoundaries)
    {
        AirSimulationConstants constants = new()
        {
            AirWidth = (uint)resources.AirWidth,
            AirHeight = (uint)resources.AirHeight,
            AirGridWidth = (uint)resources.Width,
            AirGridHeight = (uint)resources.Height,
            AirAmbientTemperature = AirAmbientTemperature,
            AirHotScale = AirHotScale,
            AirTickIndex = tickIndex,
            AirSandboxMode = sandbox ? 1u : 0u
        };
        DeviceContext context = resources.Context;
        // This is deliberately before CSInject: the observer sees the same
        // pending GasAirImpulse and Grid that CSInject will consume. It writes
        // only its own optional buffer; no physical resource is delayed.
        if (ShouldCaptureSteamJetAirCouplingTrace())
        {
            DispatchSteamJetAirCouplingObserver(context, resources);
        }
        context.UpdateSubresource(ref constants, resources.AirConstants);
        context.ComputeShader.SetConstantBuffer(0, resources.AirConstants);
        resources.AirTimer?.Begin(context);
        context.ComputeShader.Set(resources.AirFineMaterialsShader);
        context.ComputeShader.SetShaderResource(1, resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessView(6, resources.CellMaterials.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        context.ComputeShader.SetUnorderedAccessView(6, null);
        cellMaterialsDirty = false;
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Grid.ReadView,
            resources.GasMotion.View, resources.AirThermal.View,resources.ReactionPulse.ReadView,
            resources.CellMaterials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Air.UnorderedView,
            resources.AirScratch.UnorderedView,
            resources.GasAirImpulse.UnorderedView,
            resources.AirFlowLinks.UnorderedView,
            resources.AirProjectionA.UnorderedView,
            resources.AirProjectionB.UnorderedView);

        int groupsX = DivideRoundUp(resources.AirWidth, 8);
        int groupsY = DivideRoundUp(resources.AirHeight, 8);

        RunAirPass(context, resources.AirInjectShader, groupsX, groupsY);
        // Fine links are now baked. Reaction volume must enter BEFORE the
        // projection, while its compressible wave is attached afterwards.
        Unbind(context,6,6);
        DispatchReactionGather(resources);
        context.ComputeShader.SetShaderResources(0,resources.Materials.View,resources.Grid.ReadView,
            resources.GasMotion.View,resources.AirThermal.View,resources.ReactionPulse.ReadView,
            resources.CellMaterials.View);
        context.ComputeShader.SetUnorderedAccessViews(0,resources.Air.UnorderedView,resources.AirScratch.UnorderedView,
            resources.GasAirImpulse.UnorderedView,resources.AirFlowLinks.UnorderedView,resources.AirProjectionA.UnorderedView,resources.AirProjectionB.UnorderedView);
        RunAirPass(context, resources.AirPressureShader, groupsX, groupsY);
        RunAirPass(context, resources.AirVelocityShader, groupsX, groupsY);
        RunAirPass(context, resources.AirAdvectShader, groupsX, groupsY);
        RunAirPass(context, resources.AirCommitShader, groupsX, groupsY);
        // Simulation closes the air circuit. Sandbox admits a bounded HotAir
        // volume source, allowing outlet flow without a required lower inlet.
        RunAirPass(context, resources.AirFacesShader, groupsX, groupsY);
        RunAirPass(context, resources.AirDivergenceShader, groupsX, groupsY);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            RunAirPass(context, resources.AirJacobiABShader, groupsX, groupsY);
            RunAirPass(context, resources.AirJacobiBAShader, groupsX, groupsY);
        }
        RunAirPass(context, resources.AirProjectShader, groupsX, groupsY);
        resources.AirTimer?.End(context);

        Unbind(context, 6, 6);
        DispatchReactionPulse(resources);
        DispatchAirHeat(resources, openBoundaries, tickIndex);
    }

    // Consumes every issued reaction packet once; keeps pressure waves outside
    // the ordinary incompressible draft projection. No extra simulation tick.
    internal static void DispatchReactionPulse(GpuSimulationResources r)
    {
        var c=r.Context;
        c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Grid.ReadView,r.ReactionPulse.ReadView,r.AirFlowLinks.View);
        c.ComputeShader.SetUnorderedAccessViews(0,r.ReactionPending.UnorderedView,r.ReactionPulse.WriteUnorderedView,
            r.ReactionPulseScratch.UnorderedView,r.Air.UnorderedView,r.AirThermal.UnorderedView);
        int x=DivideRoundUp(r.AirWidth,8),y=DivideRoundUp(r.AirHeight,8);
        RunAirPass(c,r.ReactionFacesShader,x,y);
        RunAirPass(c,r.ReactionCommitShader,x,y);
        Unbind(c,4,5);r.ReactionPulse.Swap();
    }

    private static void DispatchReactionGather(GpuSimulationResources r)
    {
        var c=r.Context;
        c.ComputeShader.SetShaderResources(0,r.Materials.View,r.Grid.ReadView,r.ReactionPulse.ReadView,r.AirFlowLinks.View);
        c.ComputeShader.SetUnorderedAccessViews(0,r.ReactionPending.UnorderedView,r.ReactionPulse.WriteUnorderedView,
            r.ReactionPulseScratch.UnorderedView,r.Air.UnorderedView,r.AirThermal.UnorderedView,r.AirProjectionB.UnorderedView);
        RunAirPass(c,r.ReactionGatherShader,DivideRoundUp(r.AirWidth,8),DivideRoundUp(r.AirHeight,8));
        c.ComputeShader.Set(r.ReactionClearMappedShader);
        c.Dispatch(DivideRoundUp(r.Width,16),DivideRoundUp(r.Height,16),1);
        Unbind(c,4,6);
    }

    internal static void DispatchAirHeat(GpuSimulationResources resources, bool openBoundaries, uint surfacePhase = 0)
    {
        var context=resources.Context;
        resources.AirHeatTimer?.Begin(context);
        var constants=new AirThermalConstants { Width=(uint)resources.AirWidth, Height=(uint)resources.AirHeight,
            GridWidth=(uint)resources.Width,GridHeight=(uint)resources.Height, Ambient=20,Capacity=.016f,
            Exchange=.12f,OpenBoundaries=(openBoundaries?1u:0u)|((surfacePhase&3u)<<1) };
        context.UpdateSubresource(ref constants,resources.AirThermalConstants);
        context.ComputeShader.SetConstantBuffer(0,resources.AirThermalConstants);
        context.ComputeShader.SetShaderResources(0,resources.Air.View,resources.AirFlowLinks.View,resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(0,resources.AirThermal.UnorderedView,resources.Grid.ReadUnorderedView,resources.AirThermalFlux.UnorderedView);
        int x=DivideRoundUp(resources.AirWidth,8), y=DivideRoundUp(resources.AirHeight,8);
        RunAirPass(context,resources.AirHeatExchangeShader,x,y);
        RunAirPass(context,resources.AirHeatFluxShader,x,y);
        RunAirPass(context,resources.AirHeatTransportShader,x,y);
        resources.AirHeatTimer?.End(context);
        Unbind(context,3,3);
    }

    private static void RunAirPass(
        DeviceContext context,
        ComputeShader? shader,
        int groupsX,
        int groupsY)
    {
        if (shader is null)
        {
            return;
        }
        context.ComputeShader.Set(shader);
        context.Dispatch(groupsX, groupsY, 1);
    }

    /// <summary>
    /// Накопительное световое поле огня. Повторяет Renderer::render_fire из
    /// The Powder Toy: вклад, размытие по соседям, затухание. Идёт каждый кадр,
    /// а не по фиксированному расписанию, потому что это визуальный эффект и
    /// его сглаживание должно совпадать с частотой отрисовки.
    /// </summary>
    private static void DispatchFireGlowDeposit(GpuSimulationResources resources)
    {
        FireGlowConstants constants = new()
        {
            FireGlowWidth = (uint)resources.AirWidth,
            FireGlowHeight = (uint)resources.AirHeight,
            FireGlowGridWidth = (uint)resources.Width,
            FireGlowGridHeight = (uint)resources.Height,
            FireGlowDeposit = FireGlowDeposit,
            FireGlowDecay = FireGlowDecay,
            FireGlowEmberStrength = FireGlowEmberStrength,
            FireGlowReserved0 = 0
        };
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.FireGlowConstants);
        context.ComputeShader.SetConstantBuffer(0, resources.FireGlowConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.FireGlow.UnorderedView,
            resources.FireGlowScratch.UnorderedView);

        int groupsX = DivideRoundUp(resources.AirWidth, 8);
        int groupsY = DivideRoundUp(resources.AirHeight, 8);
        RunAirPass(context, resources.FireGlowDepositShader, groupsX, groupsY);
        Unbind(context, 2, 2);
    }

    private static void DispatchFireGlowDiffuse(GpuSimulationResources resources)
    {
        FireGlowConstants constants = new()
        {
            FireGlowWidth = (uint)resources.AirWidth,
            FireGlowHeight = (uint)resources.AirHeight,
            FireGlowGridWidth = (uint)resources.Width,
            FireGlowGridHeight = (uint)resources.Height,
            FireGlowDeposit = FireGlowDeposit,
            FireGlowDecay = FireGlowDecay,
            FireGlowEmberStrength = FireGlowEmberStrength,
            FireGlowReserved0 = 0
        };
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.FireGlowConstants);
        context.ComputeShader.SetConstantBuffer(0, resources.FireGlowConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.FireGlow.UnorderedView,
            resources.FireGlowScratch.UnorderedView);

        int groupsX = DivideRoundUp(resources.AirWidth, 8);
        int groupsY = DivideRoundUp(resources.AirHeight, 8);
        RunAirPass(context, resources.FireGlowDiffuseShader, groupsX, groupsY);
        RunAirPass(context, resources.FireGlowCommitShader, groupsX, groupsY);
        Unbind(context, 2, 2);
    }

    private static void DispatchGasVisual(GpuSimulationResources resources, bool decay)
    {
        DeviceContext context = resources.Context;
        FireGlowConstants constants = new()
        {
            FireGlowWidth = (uint)resources.AirWidth,
            FireGlowHeight = (uint)resources.AirHeight,
            FireGlowGridWidth = (uint)resources.Width,
            FireGlowGridHeight = (uint)resources.Height
        };
        context.UpdateSubresource(ref constants, resources.FireGlowConstants);
        context.ComputeShader.SetConstantBuffer(0, resources.FireGlowConstants);
        context.ComputeShader.SetShaderResources(0, resources.Materials.View, resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessViews(0,
            resources.GasVisual.UnorderedView, resources.GasVisualScratch.UnorderedView);
        int groupsX = DivideRoundUp(resources.AirWidth, 8);
        int groupsY = DivideRoundUp(resources.AirHeight, 8);
        RunAirPass(context, decay ? resources.GasVisualDiffuseShader : resources.GasVisualDepositShader, groupsX, groupsY);
        if (decay) RunAirPass(context, resources.GasVisualCommitShader, groupsX, groupsY);
        Unbind(context, 2, 2);
    }

    private static void DispatchFireGlowClear(GpuSimulationResources resources)
    {
        if (resources.FireGlowClearShader is null)
        {
            return;
        }
        FireGlowConstants constants = new()
        {
            FireGlowWidth = (uint)resources.AirWidth,
            FireGlowHeight = (uint)resources.AirHeight,
            FireGlowGridWidth = (uint)resources.Width,
            FireGlowGridHeight = (uint)resources.Height,
            FireGlowDeposit = FireGlowDeposit,
            FireGlowDecay = FireGlowDecay,
            FireGlowEmberStrength = FireGlowEmberStrength,
            FireGlowReserved0 = 0
        };
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.FireGlowConstants);
        context.ComputeShader.Set(resources.FireGlowClearShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FireGlowConstants);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.FireGlow.UnorderedView,
            resources.FireGlowScratch.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.AirWidth, 8),
            DivideRoundUp(resources.AirHeight, 8),
            1);
        Unbind(context, 0, 2);
    }

    /// <summary>
    /// Движение газов и пламени. Вынесено из общего клеточного прохода и идёт
    /// <see cref="GasMotionSubSteps"/> раз за кадр: одна клетка за проход — это
    /// потолок 54 клетки в секунду, а частица в The Powder Toy при Advection
    /// 0.9 и потоке 5-10 проходит 4-9 клеток за кадр. Из-за этого приток огня
    /// у источника опережал отток вверх, и точка разрасталась в шар вместо
    /// узкой струи. Вероятность внутри шейдера поделена на то же число, так
    /// что средняя скорость не меняется — растёт только достижимый максимум.
    /// </summary>
    /// <summary>
    /// Съедает всё, что дошло до левого, правого или верхнего края. Идёт каждый
    /// кадр отдельным проходом, потому что дойти до края может что угодно и
    /// каким угодно путём — привязывать это к конкретному солверу неверно.
    /// </summary>
    private static void DispatchOpenBoundary(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        uint previousPhase = constants.SimulationPhase;
        constants.SimulationPhase = 84;
        constants.DispatchOffsetX = 0;
        constants.DispatchOffsetY = 0;
        constants.DispatchExtentX = (uint)resources.Width;
        constants.DispatchExtentY = (uint)resources.Height;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.CellularAutomataShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Air.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.BodyFlags.UnorderedView,
            resources.PathBlockerMasks.UnorderedView,
            resources.CellMaterials.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(6, resources.GasMotion.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        Unbind(context, 2, 7);
        constants.SimulationPhase = previousPhase;
    }

    internal void DispatchGasMotion(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        bool airSimulationEnabled,
        bool finiteAirGeometry,
        bool fullScanForVerification = false)
    {
        uint previousSolidPass = constants.SolidPass;
        constants.GasFiniteAirGeometry = finiteAirGeometry ? 1u : 0u;
        DeviceContext context = resources.Context;
        resources.GasMotionTimer?.Begin(context);
        // Classify once, on the current grid. Empty far-away tiles can skip
        // repeated pair/bypass passes without a CPU readback or a reduced clock.
        Unbind(context, 3, 12);
        context.ClearUnorderedAccessView(resources.GasActiveTiles.UnorderedView,
            fullScanForVerification ? new RawInt4(1,1,1,1) : new RawInt4());
        context.ComputeShader.Set(resources.GasActiveTilesShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.GasActiveTiles.UnorderedView);
        UpdateConstants(context, resources, ref constants);
        if (!fullScanForVerification)
            context.Dispatch(DivideRoundUp(resources.Width,16), DivideRoundUp(resources.Height,16),1);
        Unbind(context, 3, 1);
        bool steamGasStepTrace = Environment.GetEnvironmentVariable("PHYXEL_STEAM_GAS_STEP_TRACE") == "1";
        bool steamJetBlockingTrace = Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_BLOCKING_TRACE") == "1";
        bool steamJetDiagonalTrace = Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_DIAGONAL_TRACE") == "1";
        bool steamObserverTrace = steamGasStepTrace || steamJetBlockingTrace;
        BindGasMotionSolver(context, resources);
        context.ClearUnorderedAccessView(
            resources.GasObstacleBypassStatistics.UnorderedView,
            new RawInt4(0, 0, 0, 0));
        if (!airSimulationEnabled)
        {
            context.ClearUnorderedAccessView(resources.GasAirImpulse.UnorderedView, new RawInt4(0, 0, 0, 0));
        }

        uint previousPhase = constants.SimulationPhase;
        // TPT updates vx/vy once per frame, then its fractional position is
        // consumed by MovementPhase.  Do the equivalent once before the eight
        // checkerboard passes; repeating it per pass would multiply velocity.
        constants.SimulationPhase = 89;
        constants.GasSubStep = 0;
        constants.DispatchOffsetX = 0;
        constants.DispatchOffsetY = 0;
        constants.DispatchExtentX = (uint)resources.Width;
        constants.DispatchExtentY = (uint)resources.Height;
        UpdateConstants(context, resources, ref constants);
        // This observer runs at the exact pre-integration point, with the
        // same Grid, Air and GasMotion inputs phase 89 consumes next. Changing
        // shader bindings is sufficient in D3D11; no new physical barrier or
        // synchronization is introduced.
        if (ShouldCaptureSteamJetAirCouplingTrace())
        {
            Unbind(context, 2, 12);
            DispatchSteamJetMotionObserver(context, resources);
            BindGasMotionSolver(context, resources);
        }
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        if (steamJetDiagonalTrace && resources.SteamJetDiagonalIntents is not null)
        {
            // A self-contained observer pass after the physical integration
            // reads the intent field but neither changes nor orders the
            // following checkerboard passes.
            Unbind(context, 2, 12);
            context.ClearUnorderedAccessView(resources.SteamJetDiagonalIntents.UnorderedView, new RawInt4(0, 0, 0, 0));
            DispatchSteamJetDiagonalObserver(context, resources);
            BindGasMotionSolver(context, resources);
        }

        for (int step = 0; step < GasMotionSubSteps; step++)
        {
            // Номер подшага уходит в шейдер и входит в seed случайности.
            constants.GasSubStep = unchecked((uint)step) + 1u;
            uint[] order = GasMotionPhaseOrders[(step + (int)((gasMotionTickIndex - 1) & 1u)) % GasMotionPhaseOrders.Length];
            foreach (uint phase in order)
            {
                constants.SimulationPhase = phase;
                // Vertical pairs halve the row count, horizontal pairs the
                // columns, matching the parity layout of phases 0 to 3.
                bool vertical = phase is 80 or 81;
                bool obstacleBypass = phase is >= 85 and <= 88;
                int parity = (int)(phase & 1);
                int startX = obstacleBypass
                    ? (int)((phase - 85) & 1u)
                    : vertical ? 0 : parity;
                int startY = obstacleBypass
                    ? (int)((phase - 85) >> 1)
                    : vertical ? parity : 0;
                int dispatchW = vertical
                    ? resources.Width
                    : DivideRoundUp(Math.Max(0, resources.Width - startX), 2);
                int dispatchH = vertical
                    ? DivideRoundUp(Math.Max(0, resources.Height - startY), 2)
                    : obstacleBypass
                        ? DivideRoundUp(Math.Max(0, resources.Height - startY), 2)
                        : resources.Height;
                if (dispatchW <= 0 || dispatchH <= 0)
                {
                    continue;
                }
                constants.DispatchOffsetX = (uint)startX;
                constants.DispatchOffsetY = (uint)startY;
                constants.DispatchExtentX = (uint)dispatchW;
                constants.DispatchExtentY = (uint)dispatchH;
                if (steamObserverTrace)
                {
                    context.CopyResource(resources.Grid.ReadBuffer, resources.SteamGasStepPreviousGrid.Buffer);
                    context.CopyResource(resources.GasMotion.Buffer, resources.SteamGasStepPreviousMotion.Buffer);
                }
                BindGasMotionSolver(context, resources);
                UpdateConstants(context, resources, ref constants);
                context.Dispatch(DivideRoundUp(dispatchW, 16), DivideRoundUp(dispatchH, 16), 1);
                if (steamObserverTrace)
                {
                    Unbind(context, 2, 12);
                    if (steamGasStepTrace)
                    {
                        DispatchSteamGasStepObserver(context, resources);
                        DispatchSteamJetLateralObserver(context, resources);
                    }
                    if (steamJetBlockingTrace)
                    {
                        DispatchSteamJetBlockingObserver(context, resources);
                    }
                }
            }
        }
        // TPT evaluates collision fallback once in MovementPhase after the
        // particle has consumed its velocity for the frame. Its sub-pixel
        // movement may cross several cells, but it does not retry the rotated
        // obstacle branch eight times. Doing so here made FIRE teleport along
        // the whole plate and left only a thin, old red shelf.
        constants.GasSubStep = 0;
        uint[] collisionOrder = ((gasMotionTickIndex - 1) & 1u) == 0
            ? [85, 86, 87, 88]
            : [88, 87, 86, 85];
        foreach (uint phase in collisionOrder)
        {
            constants.SimulationPhase = phase;
            int startX = (int)((phase - 85) & 1u);
            int startY = (int)((phase - 85) >> 1);
            int dispatchW = DivideRoundUp(Math.Max(0, resources.Width - startX), 2);
            int dispatchH = DivideRoundUp(Math.Max(0, resources.Height - startY), 2);
            if (dispatchW <= 0 || dispatchH <= 0)
            {
                continue;
            }
            constants.DispatchOffsetX = (uint)startX;
            constants.DispatchOffsetY = (uint)startY;
            constants.DispatchExtentX = (uint)dispatchW;
            constants.DispatchExtentY = (uint)dispatchH;
            if (steamObserverTrace)
            {
                context.CopyResource(resources.Grid.ReadBuffer, resources.SteamGasStepPreviousGrid.Buffer);
                context.CopyResource(resources.GasMotion.Buffer, resources.SteamGasStepPreviousMotion.Buffer);
            }
            BindGasMotionSolver(context, resources);
            UpdateConstants(context, resources, ref constants);
            context.Dispatch(DivideRoundUp(dispatchW, 16), DivideRoundUp(dispatchH, 16), 1);
            if (steamObserverTrace)
            {
                Unbind(context, 2, 12);
                if (steamGasStepTrace)
                {
                    DispatchSteamGasStepObserver(context, resources);
                    DispatchSteamJetLateralObserver(context, resources);
                }
                if (steamJetBlockingTrace)
                {
                    DispatchSteamJetBlockingObserver(context, resources);
                }
            }
        }
        if (steamJetBlockingTrace)
        {
            Unbind(context, 2, 12);
            DispatchSteamJetBlockingFrameObserver(context, resources);
        }
        // Ordinary gases resolve all blocked axes once, against the stable
        // post-movement grid. FIRE/SMKE retain their existing surface fallback.
        BindGasMotionSolver(context, resources);
        constants.SimulationPhase = 90;
        constants.DispatchOffsetX = 0;
        constants.DispatchOffsetY = 0;
        constants.DispatchExtentX = (uint)resources.Width;
        constants.DispatchExtentY = (uint)resources.Height;
        UpdateConstants(context, resources, ref constants);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        constants.SimulationPhase = previousPhase;
        constants.GasSubStep = 0;
        constants.SolidPass = previousSolidPass;
        Unbind(context, 3, 12);
        resources.GasMotionTimer?.End(context);
    }

    private static void BindGasMotionSolver(DeviceContext context, GpuSimulationResources resources)
    {
        context.ComputeShader.Set(resources.CellularAutomataShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Materials.View, resources.Air.View, resources.GasActiveTiles.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.BodyFlags.UnorderedView,
            resources.PathBlockerMasks.UnorderedView,
            resources.CellMaterials.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(6, resources.GasMotion.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(7, resources.GasObstacleBypassStatistics.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(8, resources.GasLateralTransferStatistics.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(9, resources.GasAirImpulse.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(10, resources.GasVerticalMotionStatistics.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(11, resources.GasVerticalBlockFrameMarkers.UnorderedView);
    }

    private static void DispatchSteamGasStepObserver(DeviceContext context, GpuSimulationResources resources)
    {
        context.ComputeShader.Set(resources.SteamGasStepObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.SteamGasStepPreviousGrid.View,
            resources.Grid.ReadView,
            resources.Materials.View,
            resources.SteamGasStepPreviousMotion.View,
            resources.GasMotion.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.SteamGasStepStatistics.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 1);
    }

    private static void DispatchSteamJetLateralObserver(DeviceContext context, GpuSimulationResources resources)
    {
        if (resources.SteamJetLateralObserverShader is null || resources.SteamJetLateralBands is null)
        {
            return;
        }
        context.ComputeShader.Set(resources.SteamJetLateralObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.SteamGasStepPreviousGrid.View,
            resources.Grid.ReadView, resources.Materials.View, resources.SteamGasStepPreviousMotion.View,
            resources.GasMotion.View);
        context.ComputeShader.SetUnorderedAccessView(1, resources.SteamJetLateralBands.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 2);
    }

    private static void DispatchSteamJetBlockingObserver(DeviceContext context, GpuSimulationResources resources)
    {
        if (resources.SteamJetBlockingObserverShader is null || resources.SteamJetBlockingSubsteps is null ||
            resources.SteamJetBlockingMarkers is null || resources.SteamJetBlockingMovedFrames is null)
        {
            return;
        }
        context.ComputeShader.Set(resources.SteamJetBlockingObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.SteamGasStepPreviousGrid.View,
            resources.Grid.ReadView, resources.Materials.View, resources.SteamGasStepPreviousMotion.View,
            resources.GasMotion.View);
        context.ComputeShader.SetUnorderedAccessView(2, resources.SteamJetBlockingSubsteps.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(3, resources.SteamJetBlockingMarkers.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(5, resources.SteamJetBlockingMovedFrames.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 6);
    }

    private static void DispatchSteamJetDiagonalObserver(DeviceContext context, GpuSimulationResources resources)
    {
        if (resources.SteamJetDiagonalObserverShader is null || resources.SteamJetDiagonalIntents is null)
        {
            return;
        }
        context.ComputeShader.Set(resources.SteamJetDiagonalObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Grid.ReadView,
            resources.Materials.View, resources.GasMotion.View, resources.GasMotion.View);
        context.ComputeShader.SetUnorderedAccessView(6, resources.SteamJetDiagonalIntents.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 7);
    }

    private static void DispatchSteamJetBlockingFrameObserver(DeviceContext context, GpuSimulationResources resources)
    {
        if (resources.SteamJetBlockingFrameObserverShader is null || resources.SteamJetBlockingFrames is null ||
            resources.SteamJetBlockingMovedFrames is null)
        {
            return;
        }
        context.ComputeShader.Set(resources.SteamJetBlockingFrameObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Grid.ReadView,
            resources.Materials.View, resources.GasMotion.View, resources.GasMotion.View);
        context.ComputeShader.SetUnorderedAccessView(4, resources.SteamJetBlockingFrames.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(5, resources.SteamJetBlockingMovedFrames.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 6);
    }
    private static void DispatchSteamJetInjectionObserver(
        DeviceContext context,
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        context.ComputeShader.Set(resources.SteamJetInjectionObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.SteamGasStepPreviousGrid.View,
            resources.Grid.ReadView,
            resources.Commands.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.SteamJetInjectionStatistics.UnorderedView);
        UpdateConstants(context, resources, ref constants);
        context.Dispatch(
            DivideRoundUp((int)constants.DispatchExtentX, 16),
            DivideRoundUp((int)constants.DispatchExtentY, 16),
            1);
        Unbind(context, 3, 1);
    }

    private static void DispatchSteamJetInjectionDistributionObserver(
        DeviceContext context,
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        if (resources.SteamJetInjectionDistributionObserverShader is null ||
            resources.SteamJetInjectionDistribution is null)
        {
            return;
        }
        context.ComputeShader.Set(resources.SteamJetInjectionDistributionObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.SteamGasStepPreviousGrid.View,
            resources.Grid.ReadView,
            resources.Commands.View);
        context.ComputeShader.SetUnorderedAccessView(1, resources.SteamJetInjectionDistribution.UnorderedView);
        UpdateConstants(context, resources, ref constants);
        context.Dispatch(
            DivideRoundUp((int)constants.DispatchExtentX, 16),
            DivideRoundUp((int)constants.DispatchExtentY, 16),
            1);
        Unbind(context, 3, 2);
    }

    private static void DispatchSteamJetMotionObserver(
        DeviceContext context,
        GpuSimulationResources resources)
    {
        if (resources.SteamJetMotionObserverShader is null ||
            resources.SteamJetMotionContributions is null)
        {
            return;
        }
        context.ComputeShader.Set(resources.SteamJetMotionObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Air.View,
            resources.Grid.ReadView,
            resources.GasMotion.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.SteamJetMotionContributions.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 4, 1);
    }

    private static void DispatchSteamJetAirCouplingObserver(
        DeviceContext context,
        GpuSimulationResources resources)
    {
        if (resources.SteamJetAirCouplingObserverShader is null ||
            resources.SteamJetAirCoupling is null)
        {
            return;
        }
        context.ComputeShader.Set(resources.SteamJetAirCouplingObserverShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Materials.View,
            resources.Grid.ReadView,
            resources.GasAirImpulse.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.SteamJetAirCoupling.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.AirWidth, 8), DivideRoundUp(resources.AirHeight, 8), 1);
        Unbind(context, 3, 1);
    }

    private bool ShouldCaptureSteamJetAirCouplingTrace()
    {
        return Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_AIR_COUPLING_TRACE") == "1" &&
            frameIndex is 60 or 120 or 200 or 300 or 450 or 599;
    }

    private static void DispatchAirClear(GpuSimulationResources resources)
    {
        if (resources.AirClearShader is null)
        {
            return;
        }
        AirSimulationConstants constants = new()
        {
            AirWidth = (uint)resources.AirWidth,
            AirHeight = (uint)resources.AirHeight,
            AirGridWidth = (uint)resources.Width,
            AirGridHeight = (uint)resources.Height,
            AirAmbientTemperature = AirAmbientTemperature,
            AirHotScale = AirHotScale,
            AirTickIndex = 0,
            AirSandboxMode = 0
        };
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.AirConstants);
        context.ComputeShader.Set(resources.AirClearShader);
        context.ComputeShader.SetConstantBuffer(0, resources.AirConstants);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Air.UnorderedView,
            resources.AirScratch.UnorderedView);
        var freshHeat = new System.Numerics.Vector2[resources.AirWidth * resources.AirHeight];
        Array.Fill(freshHeat,new System.Numerics.Vector2(293.15f*.016f,.016f));
        context.UpdateSubresource(freshHeat,resources.AirThermal.Buffer);
        context.ClearUnorderedAccessView(resources.ReactionPending.UnorderedView,new RawInt4());
        foreach(var view in resources.ReactionPulse.UnorderedAccessViews) context.ClearUnorderedAccessView(view,new RawInt4());
        context.ClearUnorderedAccessView(resources.GasAirImpulse.UnorderedView, new RawInt4(0, 0, 0, 0));
        context.ClearUnorderedAccessView(resources.AirProjectionA.UnorderedView, new RawInt4(0, 0, 0, 0));
        context.ClearUnorderedAccessView(resources.AirProjectionB.UnorderedView, new RawInt4(0, 0, 0, 0));
        context.Dispatch(
            DivideRoundUp(resources.AirWidth, 8),
            DivideRoundUp(resources.AirHeight, 8),
            1);
        Unbind(context, 0, 2);
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

    private void DispatchContactTransitions(
        GpuSimulationResources resources,
        uint tickIndex,
        bool measure)
    {
        ContactTransitionConstants constants = new()
        {
            DeltaTime = FixedThermalStep,
            Width = (uint)resources.Width,
            Height = (uint)resources.Height,
            TickIndex = tickIndex & 0x7fffffffu
        };
        DeviceContext context = resources.Context;
        if (measure)
        {
            context.Begin(resources.ContactTimestampDisjointQuery);
            context.End(resources.ContactTimestampStartQuery);
        }
        context.UpdateSubresource(ref constants, resources.ContactTransitionConstants);
        context.ComputeShader.Set(resources.ContactTransitionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.ContactTransitionConstants);
        context.ComputeShader.SetShaderResource(0, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.CellMaterials.UnorderedView,
            resources.GasMotion.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(3, resources.ContactSummary.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        context.ComputeShader.Set(resources.MoistureShader);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        // A disjoint longer path accelerates water inside connected porous solids.
        constants.TickIndex=(tickIndex&0x7fffffffu)|0x80000000u;
        context.UpdateSubresource(ref constants,resources.ContactTransitionConstants);
        context.Dispatch(DivideRoundUp(resources.Width,16),DivideRoundUp(resources.Height,16),1);
        if (measure)
        {
            context.End(resources.ContactTimestampEndQuery);
            context.End(resources.ContactTimestampDisjointQuery);
            contactTimingPending = true;
        }
        Unbind(context, 1, 4);
    }

    private void PollContactTiming(GpuSimulationResources resources)
    {
        if (!contactTimingPending)
        {
            return;
        }
        DeviceContext context = resources.Context;
        bool disjointReady = context.GetData(
            resources.ContactTimestampDisjointQuery,
            AsynchronousFlags.DoNotFlush,
            out QueryDataTimestampDisjoint disjoint);
        bool startReady = context.GetData(
            resources.ContactTimestampStartQuery,
            AsynchronousFlags.DoNotFlush,
            out long start);
        bool endReady = context.GetData(
            resources.ContactTimestampEndQuery,
            AsynchronousFlags.DoNotFlush,
            out long end);
        if (!disjointReady || !startReady || !endReady)
        {
            return;
        }
        contactTimingPending = false;
        if (disjoint.Disjoint || disjoint.Frequency <= 0 || end < start)
        {
            return;
        }
        double milliseconds = (end - start) * 1000d / disjoint.Frequency;
        contactTimingSamples++;
        contactTimingTotalMilliseconds += milliseconds;
        contactTimingMinimumMilliseconds = Math.Min(contactTimingMinimumMilliseconds, milliseconds);
        contactTimingMaximumMilliseconds = Math.Max(contactTimingMaximumMilliseconds, milliseconds);
    }

    private void ResetContactTiming()
    {
        contactTimingPending = false;
        contactTimingSamples = 0;
        contactTimingTotalMilliseconds = 0;
        contactTimingMinimumMilliseconds = double.PositiveInfinity;
        contactTimingMaximumMilliseconds = 0;
    }

    internal static void DispatchOxidizer(GpuSimulationResources resources, float dt, bool openEdges, bool consume, bool useAir = false)
    {
        OxidizerConstants constants = new()
        {
            Width = (uint)resources.Width,
            Height = (uint)resources.Height,
            DeltaTime = dt,
            OpenEdges = openEdges ? 1u : 0u,
            UseAir = useAir ? 1u : 0u
        };
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.OxidizerConstants);
        context.ComputeShader.Set(consume ? resources.OxidizerConsumeShader : resources.OxidizerTransportShader);
        context.ComputeShader.SetConstantBuffer(0, resources.OxidizerConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Materials.View,
            resources.Oxidizer.ReadView, resources.OxidizerDemand.View, resources.Air.View,
            resources.GasMotion.View, resources.OxidizerAvailable.View);
        context.ComputeShader.SetUnorderedAccessViews(0, resources.Oxidizer.WriteUnorderedView,
            resources.OxidizerFlux.UnorderedView);
        if (!consume)
        {
            context.ComputeShader.Set(resources.OxidizerFluxShader);
            context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
            // The availability output is distinct from its SRV while writing.
            context.ComputeShader.SetShaderResource(6, null);
            context.ComputeShader.SetUnorderedAccessView(2, resources.OxidizerAvailable.UnorderedView);
            context.ComputeShader.Set(resources.OxidizerTransportShader);
        }
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 7, 3);
        resources.Oxidizer.Swap();
    }

    private void DispatchCombustion(
        GpuSimulationResources resources,
        float elapsedSeconds,
        bool measure,
        bool openEdges,
        bool finiteOxidizer,
        bool useAir)
    {
        CombustionConstants constants = new()
        {
            DeltaTime = elapsedSeconds,
            Width = (uint)resources.Width,
            Height = (uint)resources.Height,
            MaterialCount = (uint)materialRegistry.Count,
            TickIndex = unchecked((uint)thermalScheduler.TotalTicks),
            FiniteOxidizer = finiteOxidizer ? 1u : 0u,
            Reserved1 = useAir ? 1u : 0u
        };
        DeviceContext context = resources.Context;
        context.ClearUnorderedAccessView(resources.CombustionSummary.UnorderedView, new RawInt4(0, 0, 0, 0));
        context.ClearUnorderedAccessView(resources.EmissionClaims.UnorderedView, new RawInt4(-1, -1, -1, -1));
        context.ClearUnorderedAccessView(resources.OxidizerDemand.UnorderedView, new RawInt4());
        if (measure)
        {
            context.Begin(resources.CombustionTimestampDisjointQuery);
            context.End(resources.CombustionTimestampStartQuery);
        }
        // Sandbox freezes the existing inventory rather than inventing oxygen
        // when switching back to Simulation or loading an exhausted scene.
        if (finiteOxidizer) DispatchOxidizer(resources, elapsedSeconds, openEdges, consume: false, useAir);
        context.UpdateSubresource(ref constants, resources.CombustionConstants);
        context.ComputeShader.Set(resources.CombustionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.CombustionConstants);
        context.ComputeShader.SetShaderResources(0, resources.Materials.View, resources.Emissions.View, resources.OxidizerAvailable.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.CombustionSummary.UnorderedView,
            resources.EmissionClaims.UnorderedView,
            resources.EmissionRequests.UnorderedView,
            resources.OxidizerDemand.UnorderedView,resources.ReactionPending.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        Unbind(context, 3, 6);

        DispatchEmissionResolve(resources);
        DispatchTransientLifecycle(resources, constants);
        if (finiteOxidizer) DispatchOxidizer(resources, elapsedSeconds, openEdges, consume: true);

        if (measure)
        {
            context.End(resources.CombustionTimestampEndQuery);
            context.End(resources.CombustionTimestampDisjointQuery);
            combustionTimingPending = true;
        }

        Span<bool> pendingSlots = stackalloc bool[resources.CombustionSummaryReadbackSlots.Length];
        for (int index = 0; index < pendingSlots.Length; index++)
        {
            pendingSlots[index] = resources.CombustionSummaryReadbackSlots[index].Pending;
        }
        if (PhaseSummaryReadbackPolicy.SelectSlot(pendingSlots, out int slotIndex) ==
            PhaseSummaryReadbackScheduleResult.NoFreeSlot)
        {
            return;
        }
        GpuPhaseSummaryReadbackSlot slot = resources.CombustionSummaryReadbackSlots[slotIndex];
        context.CopyResource(resources.CombustionSummary.Buffer, slot.Staging);
        context.End(slot.Query);
        slot.Pending = true;
        slot.Generation = combustionReadbackGeneration;
    }

    private void DispatchGasRedistribution(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        ulong tickIndex,
        bool measure)
    {
        DeviceContext context = resources.Context;
        if (measure)
        {
            context.Begin(resources.GasTimestampDisjointQuery);
            context.End(resources.GasTimestampStartQuery);
        }
        uint previousFrame = constants.FrameIndex;
        uint previousPhase = constants.SimulationPhase;
        constants.FrameIndex = unchecked((uint)tickIndex);
        context.ClearUnorderedAccessView(
            resources.GasLateralTransferStatistics.UnorderedView,
            new RawInt4(0, 0, 0, 0));
        context.ComputeShader.Set(resources.GasRedistributionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResource(0, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.CellMaterials.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(2, resources.GasMotion.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(3, resources.GasLateralTransferStatistics.UnorderedView);
        // Gas packets move only across disjoint adjacent pairs. Vertical edges
        // alternate parity; both horizontal parities and one diagonal pairing
        // run every fixed tick without long-range mass splitting.
        for (uint pass = 0; pass < 4; pass++)
        {
            uint phase = pass == 0 ? unchecked((uint)tickIndex) & 1u : pass + 1;
            constants.SimulationPhase = phase;
            UpdateConstants(context, resources, ref constants);
            int dispatchWidth = phase <= 1
                ? resources.Width
                : DivideRoundUp(resources.Width, 2);
            int dispatchHeight = phase <= 1 || phase == 4
                ? DivideRoundUp(resources.Height, 2)
                : resources.Height;
            context.Dispatch(
                DivideRoundUp(dispatchWidth, 16),
                DivideRoundUp(dispatchHeight, 16),
                1);
        }
        if (measure)
        {
            context.End(resources.GasTimestampEndQuery);
            context.End(resources.GasTimestampDisjointQuery);
            gasTimingPending = true;
        }
        Unbind(context, 1, 3);
        constants.FrameIndex = previousFrame;
        constants.SimulationPhase = previousPhase;
    }

    private void PollGasTiming(GpuSimulationResources resources)
    {
        if (!gasTimingPending)
        {
            return;
        }
        DeviceContext context = resources.Context;
        bool disjointReady = context.GetData(
            resources.GasTimestampDisjointQuery,
            AsynchronousFlags.DoNotFlush,
            out QueryDataTimestampDisjoint disjoint);
        bool startReady = context.GetData(
            resources.GasTimestampStartQuery,
            AsynchronousFlags.DoNotFlush,
            out long start);
        bool endReady = context.GetData(
            resources.GasTimestampEndQuery,
            AsynchronousFlags.DoNotFlush,
            out long end);
        if (!disjointReady || !startReady || !endReady)
        {
            return;
        }
        gasTimingPending = false;
        if (disjoint.Disjoint || disjoint.Frequency <= 0 || end < start)
        {
            return;
        }
        double milliseconds = (end - start) * 1000d / disjoint.Frequency;
        gasTimingSamples++;
        gasTimingTotalMilliseconds += milliseconds;
        gasTimingMinimumMilliseconds = Math.Min(gasTimingMinimumMilliseconds, milliseconds);
        gasTimingMaximumMilliseconds = Math.Max(gasTimingMaximumMilliseconds, milliseconds);
    }

    private void ResetGasTiming()
    {
        gasTimingPending = false;
        gasTimingSamples = 0;
        gasTimingTotalMilliseconds = 0;
        gasTimingMinimumMilliseconds = double.PositiveInfinity;
        gasTimingMaximumMilliseconds = 0;
    }

    private void DispatchEmissionResolve(GpuSimulationResources resources)
    {
        EmissionConstants constants = new()
        {
            Width = (uint)resources.Width,
            Height = (uint)resources.Height,
            MaterialCount = (uint)materialRegistry.Count,
            RequestCount = (uint)(resources.Width * resources.Height * 3)
        };
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.EmissionConstants);
        context.ComputeShader.Set(resources.EmissionResolveShader);
        context.ComputeShader.SetConstantBuffer(0, resources.EmissionConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.EmissionRequests.View,
            resources.EmissionClaims.View,
            resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.CombustionSummary.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        Unbind(context, 3, 2);
    }

    internal static void DispatchTransientLifecycle(
        GpuSimulationResources resources,
        CombustionConstants constants)
    {
        DeviceContext context = resources.Context;
        context.UpdateSubresource(ref constants, resources.CombustionConstants);
        context.ComputeShader.Set(resources.TransientLifecycleShader);
        context.ComputeShader.SetConstantBuffer(0, resources.CombustionConstants);
        context.ComputeShader.SetShaderResources(0, resources.Materials.View, resources.OxidizerAvailable.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.CombustionSummary.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        Unbind(context, 2, 2);
    }

    private void PollCombustionSummary(GpuSimulationResources resources)
    {
        DeviceContext context = resources.Context;
        foreach (GpuPhaseSummaryReadbackSlot slot in resources.CombustionSummaryReadbackSlots)
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
            if (slot.Generation != combustionReadbackGeneration)
            {
                continue;
            }
            combustionSummaryReadbacks++;
            lastCombustionSummary = (CombustionSummaryFlags)rawFlags;
            pressurePowderPotential =
                (lastCombustionSummary & CombustionSummaryFlags.PressurePowderPresent) != 0;
            if ((lastCombustionSummary & CombustionSummaryFlags.CombustionOccurred) != 0)
            {
                presentationDirty = true;
                thermalActive = true;
                contactTransitionPotential |= materialRegistry.RegistryHasContactTransitions;
                if ((lastCombustionSummary & CombustionSummaryFlags.TargetCellular) != 0)
                {
                    cellularMatter = true;
                    cellularSleeping = false;
                    ActivateFullRegion(resources);
                }
                if ((lastCombustionSummary & CombustionSummaryFlags.TouchesLiquid) != 0)
                {
                    liquidMatter = true;
                    fluidMatter = true;
                    waterPressureRoutesDirty = true;
                    ActivateFullRegion(resources);
                }
                if ((lastCombustionSummary & CombustionSummaryFlags.TouchesSolid) != 0)
                {
                    solidMatter = true;
                    solidSleeping = false;
                    topologyDirty = true;
                    solidMotionNeedsCellular = true;
                    ActivateFullRegion(resources);
                }
            }
        }
    }

    private void PollCombustionTiming(GpuSimulationResources resources)
    {
        if (!combustionTimingPending)
        {
            return;
        }
        DeviceContext context = resources.Context;
        bool disjointReady = context.GetData(
            resources.CombustionTimestampDisjointQuery,
            AsynchronousFlags.DoNotFlush,
            out QueryDataTimestampDisjoint disjoint);
        bool startReady = context.GetData(
            resources.CombustionTimestampStartQuery,
            AsynchronousFlags.DoNotFlush,
            out long start);
        bool endReady = context.GetData(
            resources.CombustionTimestampEndQuery,
            AsynchronousFlags.DoNotFlush,
            out long end);
        if (!disjointReady || !startReady || !endReady)
        {
            return;
        }
        combustionTimingPending = false;
        if (disjoint.Disjoint || disjoint.Frequency <= 0 || end < start)
        {
            return;
        }
        double milliseconds = (end - start) * 1000d / disjoint.Frequency;
        combustionTimingSamples++;
        combustionTimingTotalMilliseconds += milliseconds;
        combustionTimingMinimumMilliseconds = Math.Min(combustionTimingMinimumMilliseconds, milliseconds);
        combustionTimingMaximumMilliseconds = Math.Max(combustionTimingMaximumMilliseconds, milliseconds);
    }

    private void ResetCombustionRuntime()
    {
        combustionReadbackGeneration++;
        combustionTimingPending = false;
        combustionTimingSamples = 0;
        combustionTimingTotalMilliseconds = 0;
        combustionTimingMinimumMilliseconds = double.PositiveInfinity;
        combustionTimingMaximumMilliseconds = 0;
        combustionDispatches = 0;
        combustionSummaryReadbacks = 0;
        lastCombustionSummary = CombustionSummaryFlags.None;
    }

    private PhaseSummaryReadbackScheduleResult DispatchPhaseTransitions(
        GpuSimulationResources resources,
        int thermalTicks,
        bool measure)
    {
        PhaseTransitionConstants constants = new()
        {
            Width = (uint)resources.Width,
            Height = (uint)resources.Height,
            MaterialCount = (uint)materialRegistry.Count,
            TickIndex = unchecked((uint)thermalScheduler.TotalTicks),
            TickCount = (uint)Math.Max(1, thermalTicks),
            Reserved0 = resources.PhaseEventCounters is null ? 0u : 1u
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
        context.ComputeShader.SetShaderResources(0, resources.Materials.View, resources.ContactSummary.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.PhaseSummary.UnorderedView);
        context.ComputeShader.SetUnorderedAccessView(2, resources.PhaseEventCounters?.UnorderedView);
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
        Unbind(context, 2, 3);
        context.ClearUnorderedAccessView(resources.ContactSummary.UnorderedView, new RawInt4());

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
            phaseSummaryReadbacks++;
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
        contactTransitionPotential |= materialRegistry.RegistryHasContactTransitions;

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
            freeBodyMatter = true; // A freeze target may be a free body.
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
        phaseSummaryReadbacks = 0;
        maximumPhaseDispatchesPerFrame = 0;
        lastPhaseSummary = PhaseTransitionSummaryFlags.None;
        lastPhaseDispatchFrame = 0;
        lastCompositionFrame = 0;
        lastThermalTicksPerFrame = 0;
    }

    private static bool ContainsMaterialCommand(ReadOnlySpan<BrushDrawCommand> commands)
    {
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode is BrushCommandMode.Material or BrushCommandMode.ThermalDevice or BrushCommandMode.Filter)
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
            if (command.Mode is BrushCommandMode.Material or BrushCommandMode.Erase or BrushCommandMode.ThermalDevice or BrushCommandMode.Filter)
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
