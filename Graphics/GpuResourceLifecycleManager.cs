using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using KniTexture2D = Microsoft.Xna.Framework.Graphics.Texture2D;

namespace Phyxel.Graphics;

public sealed class GpuResourceLifecycleManager : IDisposable
{
    private readonly GraphicsDevice graphicsDevice;
    private readonly MaterialRegistry materialRegistry;
    private GpuSimulationResources? preparedSimulationResources;
    private bool disposed;

    public GpuResourceLifecycleManager(GraphicsDevice graphicsDevice, MaterialRegistry materialRegistry)
    {
        this.graphicsDevice = graphicsDevice;
        this.materialRegistry = materialRegistry;
        Device = (Device)graphicsDevice.GetD3D11Device();
        using (SharpDX.DXGI.Device dxgiDevice = Device.QueryInterface<SharpDX.DXGI.Device>())
        using (SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter)
        {
            SharpDX.DXGI.AdapterDescription description = adapter.Description;
            Console.WriteLine($"PHYXEL_GPU name={description.Description.Trim()} vendor=0x{description.VendorId:X4} device=0x{description.DeviceId:X4}");
        }
        PixelTexture = CreatePixelTexture();
        CircleTexture = CreateCircleTexture(64);
        BrushOutlineTexture = CreateBrushOutlineTexture(128);
        graphicsDevice.DeviceResetting += HandleDeviceResetting;
        graphicsDevice.DeviceReset += HandleDeviceReset;
    }

    public Device Device { get; }
    public KniTexture2D PixelTexture { get; }
    public KniTexture2D CircleTexture { get; }
    public KniTexture2D BrushOutlineTexture { get; }
    public GpuSimulationResources? Resources { get; private set; }
    public bool RequiresRecreation { get; private set; }

    public void PrepareSimulation(SimulationSettings settings)
    {
        if (preparedSimulationResources is not null &&
            preparedSimulationResources.Width == settings.Width &&
            preparedSimulationResources.Height == settings.Height)
        {
            return;
        }

        preparedSimulationResources?.Dispose();
        preparedSimulationResources = CreateResources(settings.Width, settings.Height, true);
    }

    public GpuSimulationResources CreateOrResize(SimulationSettings settings, bool requireSimulation)
    {
        if (Resources is not null && Resources.Width == settings.Width && Resources.Height == settings.Height &&
            Resources.IsSimulationAllocated == requireSimulation && !RequiresRecreation)
        {
            return Resources;
        }

        if (preparedSimulationResources is not null &&
            (preparedSimulationResources.Width != settings.Width || preparedSimulationResources.Height != settings.Height))
        {
            preparedSimulationResources.Dispose();
            preparedSimulationResources = null;
        }

        if (requireSimulation && preparedSimulationResources is not null)
        {
            Resources?.Dispose();
            Resources = preparedSimulationResources;
            preparedSimulationResources = null;
            RequiresRecreation = false;
            return Resources;
        }

        if (!requireSimulation && Resources is { IsSimulationAllocated: true })
        {
            preparedSimulationResources?.Dispose();
            preparedSimulationResources = Resources;
            Resources = null;
        }

        Resources?.Dispose();
        Resources = CreateResources(settings.Width, settings.Height, requireSimulation);
        RequiresRecreation = false;
        return Resources;
    }

    private GpuSimulationResources CreateResources(int width, int height, bool allocateSimulation)
    {
        int cellCount = allocateSimulation ? checked(width * height) : 1;
        GpuBufferPair<GridCell> grid = new(Device, cellCount);
        GpuStructuredBuffer<uint> componentParents = new(Device, cellCount);
        // The cellular solver reuses the first 18 rows for its compact water
        // column cache, 16 pressure-transfer lanes, and activity counter.
        int bodyFlagCount = allocateSimulation ? Math.Max(cellCount, checked(width * 18 + 1)) : 19;
        GpuStructuredBuffer<uint> bodyFlags = new(Device, bodyFlagCount);
        GpuStructuredBuffer<uint> solidBodyGeometry = new(Device, cellCount);
        GpuStructuredBuffer<uint> solidBodyMass = new(Device, cellCount);
        // One extra element is a persistent diagnostic counter for forbidden
        // non-adjacent column transfers; blocker-mask rebuilds never touch it.
        int blockerMaskCount = allocateSimulation
            ? checked(((width + 31) / 32) * height + 2)
            : 3;
        GpuStructuredBuffer<uint> pathBlockerMasks = new(Device, blockerMaskCount);
        GpuStructuredBuffer<uint> cellMaterials = new(Device, cellCount);
        GpuStructuredBuffer<WaterPressureRouteData> waterPressureRoutes = new(Device, cellCount);
        GpuStructuredBuffer<WaterPressureRouteData> waterPressureRouteScratch = new(Device, cellCount);
        GpuBufferPair<SimulationStatistics> statistics = new(Device, 1);
        GpuUploadBuffer<BrushDrawCommand> commands = new(Device, SimulationSettings.MaximumBrushCommands);
        MaterialProperties[] materialTable = materialRegistry.CreateGpuTable();
        GpuUploadBuffer<MaterialProperties> materials = new(Device, materialTable.Length);
        materials.Upload(Device.ImmediateContext, materialTable);
        MaterialEmissionProperties[] emissionTable = materialRegistry.CreateEmissionGpuTable();
        GpuUploadBuffer<MaterialEmissionProperties> emissions = new(Device, emissionTable.Length);
        emissions.Upload(Device.ImmediateContext, emissionTable);
        Buffer constants = new(Device, new BufferDescription
        {
            SizeInBytes = Marshal.SizeOf<SimulationFrameConstants>(),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
        Buffer thermalConstants = CreateConstantBuffer<ThermalSimulationConstants>();
        // The air field is solved on a coarse grid: one air cell covers
        // AirCellSize x AirCellSize simulation cells, as CELL = 4 does in
        // The Powder Toy. DivideRoundUp so the right and bottom edges of a
        // world whose size is not a multiple of the cell size are still covered.
        int airWidth = allocateSimulation
            ? Math.Max(1, (width + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize)
            : 1;
        int airHeight = allocateSimulation
            ? Math.Max(1, (height + SimulationSettings.AirCellSize - 1) / SimulationSettings.AirCellSize)
            : 1;
        int airCellCount = checked(airWidth * airHeight);
        Buffer airConstants = CreateConstantBuffer<AirSimulationConstants>();
        GpuStructuredBuffer<AirCell> air = new(Device, airCellCount);
        GpuStructuredBuffer<AirCell> airScratch = new(Device, airCellCount);
        GpuStructuredBuffer<GasAirImpulse> gasAirImpulse = new(Device, airCellCount);
        Buffer airStaging = CreateStagingBuffer(air.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<GasMotionState> gasMotion = new(Device, cellCount);
        Buffer gasMotionStaging = CreateStagingBuffer(gasMotion.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<GasObstacleBypassStatistics> gasObstacleBypassStatistics = new(Device, 1);
        Buffer gasObstacleBypassStatisticsStaging =
            CreateStagingBuffer(gasObstacleBypassStatistics.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<GasVerticalMotionStatistics> gasVerticalMotionStatistics = new(Device, 1);
        Buffer gasVerticalMotionStatisticsStaging =
            CreateStagingBuffer(gasVerticalMotionStatistics.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<uint> gasVerticalBlockFrameMarkers = new(Device, cellCount);
        GpuStructuredBuffer<uint> gasLateralTransferStatistics = new(
            Device,
            GasLateralTransferStatisticsLayout.Count);
        Buffer gasLateralTransferStatisticsStaging =
            CreateStagingBuffer(gasLateralTransferStatistics.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<SteamGasStepStatistics> steamGasStepStatistics = new(Device, 1);
        Buffer steamGasStepStatisticsStaging =
            CreateStagingBuffer(steamGasStepStatistics.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<GridCell> steamGasStepPreviousGrid = new(Device, cellCount);
        GpuStructuredBuffer<GasMotionState> steamGasStepPreviousMotion = new(Device, cellCount);
        GpuStructuredBuffer<SteamJetInjectionStatistics> steamJetInjectionStatistics = new(Device, 1);
        Buffer steamJetInjectionStatisticsStaging =
            CreateStagingBuffer(steamJetInjectionStatistics.Buffer.Description.SizeInBytes);
        bool steamJetSourceDistributionTrace = allocateSimulation &&
            Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_SOURCE_DISTRIBUTION_TRACE") == "1";
        const int steamJetSourceDistributionFrameCount = 200;
        GpuStructuredBuffer<SteamJetInjectionDistributionFrame>? steamJetInjectionDistribution =
            steamJetSourceDistributionTrace ? new(Device, steamJetSourceDistributionFrameCount) : null;
        Buffer? steamJetInjectionDistributionStaging = steamJetInjectionDistribution is null ? null :
            CreateStagingBuffer(steamJetInjectionDistribution.Buffer.Description.SizeInBytes);
        bool steamJetLateralTrace = allocateSimulation &&
            Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_LATERAL_TRACE") == "1";
        int steamJetBandCount = (height + 19) / 20;
        GpuStructuredBuffer<SteamJetLateralBandStatistics>? steamJetLateralBands =
            steamJetLateralTrace ? new(Device, steamJetBandCount) : null;
        Buffer? steamJetLateralBandsStaging = steamJetLateralBands is null ? null :
            CreateStagingBuffer(steamJetLateralBands.Buffer.Description.SizeInBytes);
        bool steamJetBlockingTrace = allocateSimulation &&
            Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_BLOCKING_TRACE") == "1";
        const int steamJetBlockingGroupCount = 2;
        const int steamJetBlockingSubstepCount = 8;
        GpuStructuredBuffer<SteamJetBlockingSubstepStatistics>? steamJetBlockingSubsteps =
            steamJetBlockingTrace ? new(Device, steamJetBlockingGroupCount * steamJetBlockingSubstepCount) : null;
        Buffer? steamJetBlockingSubstepsStaging = steamJetBlockingSubsteps is null ? null :
            CreateStagingBuffer(steamJetBlockingSubsteps.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<SteamJetBlockingFrameStatistics>? steamJetBlockingFrames =
            steamJetBlockingTrace ? new(Device, steamJetBlockingGroupCount) : null;
        Buffer? steamJetBlockingFramesStaging = steamJetBlockingFrames is null ? null :
            CreateStagingBuffer(steamJetBlockingFrames.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<SteamJetBlockingMarker>? steamJetBlockingMarkers =
            steamJetBlockingTrace ? new(Device, checked(cellCount * 2)) : null;
        GpuStructuredBuffer<uint>? steamJetBlockingMovedFrames =
            steamJetBlockingTrace ? new(Device, cellCount) : null;
        bool steamJetDiagonalTrace = allocateSimulation &&
            Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_DIAGONAL_TRACE") == "1";
        GpuStructuredBuffer<SteamJetDiagonalIntentStatistics>? steamJetDiagonalIntents =
            steamJetDiagonalTrace ? new(Device, steamJetBlockingGroupCount) : null;
        Buffer? steamJetDiagonalIntentsStaging = steamJetDiagonalIntents is null ? null :
            CreateStagingBuffer(steamJetDiagonalIntents.Buffer.Description.SizeInBytes);
        // A full fine-grid pre-integration trace is sizeable at the 1920x1080
        // steam_jet diagnostic scale. Do not allocate it, or dispatch either
        // observer, unless the explicit environment flag requests it.
        bool steamJetAirCouplingTrace = allocateSimulation &&
            Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_AIR_COUPLING_TRACE") == "1";
        GpuStructuredBuffer<SteamJetGasMotionContribution>? steamJetMotionContributions =
            steamJetAirCouplingTrace ? new(Device, cellCount) : null;
        Buffer? steamJetMotionContributionsStaging = steamJetMotionContributions is null
            ? null
            : CreateStagingBuffer(steamJetMotionContributions.Buffer.Description.SizeInBytes);
        GpuStructuredBuffer<SteamJetAirCouplingCell>? steamJetAirCoupling =
            steamJetAirCouplingTrace ? new(Device, airCellCount) : null;
        Buffer? steamJetAirCouplingStaging = steamJetAirCoupling is null
            ? null
            : CreateStagingBuffer(steamJetAirCoupling.Buffer.Description.SizeInBytes);
        // The fire light field shares the air resolution: one coarse cell per
        // AirCellSize square, exactly like fire_r/g/b in The Powder Toy.
        Buffer fireGlowConstants = CreateConstantBuffer<FireGlowConstants>();
        GpuStructuredBuffer<FireGlowCell> fireGlow = new(Device, airCellCount);
        GpuStructuredBuffer<FireGlowCell> fireGlowScratch = new(Device, airCellCount);
        Buffer contactTransitionConstants = CreateConstantBuffer<ContactTransitionConstants>();
        Buffer phaseConstants = CreateConstantBuffer<PhaseTransitionConstants>();
        GpuStructuredBuffer<uint> phaseSummary = new(Device, 1);
        GpuPhaseSummaryReadbackSlot[] phaseSummaryReadbackSlots = new GpuPhaseSummaryReadbackSlot[3];
        for (int index = 0; index < phaseSummaryReadbackSlots.Length; index++)
        {
            phaseSummaryReadbackSlots[index] = new GpuPhaseSummaryReadbackSlot
            {
                Staging = CreateReadStagingBuffer(sizeof(uint)),
                Query = new Query(Device, new QueryDescription
                {
                    Type = QueryType.Event,
                    Flags = QueryFlags.None
                })
            };
        }
        Buffer combustionConstants = CreateConstantBuffer<CombustionConstants>();
        GpuStructuredBuffer<uint> combustionSummary = new(Device, 1);
        GpuStructuredBuffer<uint> emissionClaims = new(Device, cellCount);
        GpuStructuredBuffer<EmissionRequest> emissionRequests = new(Device, checked(cellCount * 3));
        Buffer emissionConstants = CreateConstantBuffer<EmissionConstants>();
        GpuPhaseSummaryReadbackSlot[] combustionSummaryReadbackSlots = new GpuPhaseSummaryReadbackSlot[3];
        for (int index = 0; index < combustionSummaryReadbackSlots.Length; index++)
        {
            combustionSummaryReadbackSlots[index] = new GpuPhaseSummaryReadbackSlot
            {
                Staging = CreateReadStagingBuffer(sizeof(uint)),
                Query = new Query(Device, new QueryDescription
                {
                    Type = QueryType.Event,
                    Flags = QueryFlags.None
                })
            };
        }
        Buffer temperatureProbeConstants = CreateConstantBuffer<TemperatureProbeConstants>();
        GpuStructuredBuffer<TemperatureProbeResult> temperatureProbeResult = new(Device, 1);
        Buffer temperatureProbeStaging = CreateReadStagingBuffer(Marshal.SizeOf<TemperatureProbeResult>());
        Query temperatureProbeQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Event,
            Flags = QueryFlags.None
        });
        Query thermalTimestampDisjointQuery = new(Device, new QueryDescription
        {
            Type = QueryType.TimestampDisjoint,
            Flags = QueryFlags.None
        });
        Query thermalTimestampStartQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query thermalTimestampEndQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query contactTimestampDisjointQuery = new(Device, new QueryDescription
        {
            Type = QueryType.TimestampDisjoint,
            Flags = QueryFlags.None
        });
        Query contactTimestampStartQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query contactTimestampEndQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query gasTimestampDisjointQuery = new(Device, new QueryDescription
        {
            Type = QueryType.TimestampDisjoint,
            Flags = QueryFlags.None
        });
        Query gasTimestampStartQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query gasTimestampEndQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query phaseTimestampDisjointQuery = new(Device, new QueryDescription
        {
            Type = QueryType.TimestampDisjoint,
            Flags = QueryFlags.None
        });
        Query phaseTimestampStartQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query phaseTimestampEndQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query combustionTimestampDisjointQuery = new(Device, new QueryDescription
        {
            Type = QueryType.TimestampDisjoint,
            Flags = QueryFlags.None
        });
        Query combustionTimestampStartQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query combustionTimestampEndQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query probeTimestampDisjointQuery = new(Device, new QueryDescription
        {
            Type = QueryType.TimestampDisjoint,
            Flags = QueryFlags.None
        });
        Query probeTimestampStartQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Query probeTimestampEndQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Timestamp,
            Flags = QueryFlags.None
        });
        Buffer statisticsStaging = new(Device, new BufferDescription
        {
            SizeInBytes = Marshal.SizeOf<SimulationStatistics>(),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
        Query statisticsQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Event,
            Flags = QueryFlags.None
        });
        Buffer gridStaging = CreateStagingBuffer(grid.ReadBuffer.Description.SizeInBytes);
        Query sceneTransferQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Event,
            Flags = QueryFlags.None
        });
        int textureWidth = allocateSimulation ? width : 1;
        int textureHeight = allocateSimulation ? height : 1;
        GpuRenderTexturePair targets = new(Device, textureWidth, textureHeight);
        KniTexture2D[] presentations =
        [
            new KniTexture2D(graphicsDevice, textureWidth, textureHeight, false, SurfaceFormat.Color),
            new KniTexture2D(graphicsDevice, textureWidth, textureHeight, false, SurfaceFormat.Color)
        ];
        if (!allocateSimulation)
        {
            Color[] clearColor = [new Color(9, 11, 14)];
            presentations[0].SetData(clearColor);
            presentations[1].SetData(clearColor);
        }

        SharpDX.Direct3D11.Texture2D[] nativePresentations =
        [
            (SharpDX.Direct3D11.Texture2D)presentations[0].GetD3D11Resource(),
            (SharpDX.Direct3D11.Texture2D)presentations[1].GetD3D11Resource()
        ];
        return new GpuSimulationResources
        {
            Device = Device,
            Context = Device.ImmediateContext,
            Width = width,
            Height = height,
            IsSimulationAllocated = allocateSimulation,
            Grid = grid,
            ComponentParents = componentParents,
            BodyFlags = bodyFlags,
            SolidBodyGeometry = solidBodyGeometry,
            SolidBodyMass = solidBodyMass,
            PathBlockerMasks = pathBlockerMasks,
            CellMaterials = cellMaterials,
            WaterPressureRoutes = waterPressureRoutes,
            WaterPressureRouteScratch = waterPressureRouteScratch,
            Statistics = statistics,
            Commands = commands,
            Materials = materials,
            Emissions = emissions,
            FrameConstants = constants,
            ThermalConstants = thermalConstants,
            AirWidth = airWidth,
            AirHeight = airHeight,
            AirConstants = airConstants,
            Air = air,
            AirScratch = airScratch,
            GasAirImpulse = gasAirImpulse,
            AirStaging = airStaging,
            GasMotion = gasMotion,
            GasMotionStaging = gasMotionStaging,
            GasObstacleBypassStatistics = gasObstacleBypassStatistics,
            GasObstacleBypassStatisticsStaging = gasObstacleBypassStatisticsStaging,
            GasVerticalMotionStatistics = gasVerticalMotionStatistics,
            GasVerticalMotionStatisticsStaging = gasVerticalMotionStatisticsStaging,
            GasVerticalBlockFrameMarkers = gasVerticalBlockFrameMarkers,
            GasLateralTransferStatistics = gasLateralTransferStatistics,
            GasLateralTransferStatisticsStaging = gasLateralTransferStatisticsStaging,
            SteamGasStepStatistics = steamGasStepStatistics,
            SteamGasStepStatisticsStaging = steamGasStepStatisticsStaging,
            SteamGasStepPreviousGrid = steamGasStepPreviousGrid,
            SteamGasStepPreviousMotion = steamGasStepPreviousMotion,
            SteamJetInjectionStatistics = steamJetInjectionStatistics,
            SteamJetInjectionStatisticsStaging = steamJetInjectionStatisticsStaging,
            SteamJetInjectionDistribution = steamJetInjectionDistribution,
            SteamJetInjectionDistributionStaging = steamJetInjectionDistributionStaging,
            SteamJetLateralBands = steamJetLateralBands,
            SteamJetLateralBandsStaging = steamJetLateralBandsStaging,
            SteamJetBlockingSubsteps = steamJetBlockingSubsteps,
            SteamJetBlockingSubstepsStaging = steamJetBlockingSubstepsStaging,
            SteamJetBlockingFrames = steamJetBlockingFrames,
            SteamJetBlockingFramesStaging = steamJetBlockingFramesStaging,
            SteamJetBlockingMarkers = steamJetBlockingMarkers,
            SteamJetBlockingMovedFrames = steamJetBlockingMovedFrames,
            SteamJetDiagonalIntents = steamJetDiagonalIntents,
            SteamJetDiagonalIntentsStaging = steamJetDiagonalIntentsStaging,
            SteamJetMotionContributions = steamJetMotionContributions,
            SteamJetMotionContributionsStaging = steamJetMotionContributionsStaging,
            SteamJetAirCoupling = steamJetAirCoupling,
            SteamJetAirCouplingStaging = steamJetAirCouplingStaging,
            FireGlowConstants = fireGlowConstants,
            FireGlow = fireGlow,
            FireGlowScratch = fireGlowScratch,
            ContactTransitionConstants = contactTransitionConstants,
            PhaseConstants = phaseConstants,
            PhaseSummary = phaseSummary,
            PhaseSummaryReadbackSlots = phaseSummaryReadbackSlots,
            CombustionConstants = combustionConstants,
            CombustionSummary = combustionSummary,
            EmissionClaims = emissionClaims,
            EmissionRequests = emissionRequests,
            EmissionConstants = emissionConstants,
            CombustionSummaryReadbackSlots = combustionSummaryReadbackSlots,
            TemperatureProbeConstants = temperatureProbeConstants,
            TemperatureProbeResult = temperatureProbeResult,
            TemperatureProbeStaging = temperatureProbeStaging,
            TemperatureProbeQuery = temperatureProbeQuery,
            ThermalTimestampDisjointQuery = thermalTimestampDisjointQuery,
            ThermalTimestampStartQuery = thermalTimestampStartQuery,
            ThermalTimestampEndQuery = thermalTimestampEndQuery,
            ContactTimestampDisjointQuery = contactTimestampDisjointQuery,
            ContactTimestampStartQuery = contactTimestampStartQuery,
            ContactTimestampEndQuery = contactTimestampEndQuery,
            GasTimestampDisjointQuery = gasTimestampDisjointQuery,
            GasTimestampStartQuery = gasTimestampStartQuery,
            GasTimestampEndQuery = gasTimestampEndQuery,
            PhaseTimestampDisjointQuery = phaseTimestampDisjointQuery,
            PhaseTimestampStartQuery = phaseTimestampStartQuery,
            PhaseTimestampEndQuery = phaseTimestampEndQuery,
            CombustionTimestampDisjointQuery = combustionTimestampDisjointQuery,
            CombustionTimestampStartQuery = combustionTimestampStartQuery,
            CombustionTimestampEndQuery = combustionTimestampEndQuery,
            ProbeTimestampDisjointQuery = probeTimestampDisjointQuery,
            ProbeTimestampStartQuery = probeTimestampStartQuery,
            ProbeTimestampEndQuery = probeTimestampEndQuery,
            StatisticsStaging = statisticsStaging,
            StatisticsQuery = statisticsQuery,
            GridStaging = gridStaging,
            SceneTransferQuery = sceneTransferQuery,
            CompositionTargets = targets,
            PresentationTextures = presentations,
            NativePresentationTextures = nativePresentations,
            BrushShader = allocateSimulation ? CompileShader("BrushApplication.hlsl") : null,
            CellularAutomataShader = allocateSimulation ? CompileShader("CellularAutomataSolver.hlsl") : null,
            GasRedistributionShader = allocateSimulation ? CompileShader("GasRedistribution.hlsl") : null,
            SteamGasStepObserverShader = allocateSimulation ? CompileShader("SteamGasStepObserver.hlsl") : null,
            SteamJetLateralObserverShader = allocateSimulation && steamJetLateralTrace
                ? CompileShader("SteamGasStepObserver.hlsl", "CSLateralBands") : null,
            SteamJetBlockingObserverShader = allocateSimulation && steamJetBlockingTrace
                ? CompileShader("SteamGasStepObserver.hlsl", "CSBlocking") : null,
            SteamJetBlockingFrameObserverShader = allocateSimulation && steamJetBlockingTrace
                ? CompileShader("SteamGasStepObserver.hlsl", "CSBlockingFrame") : null,
            SteamJetDiagonalObserverShader = allocateSimulation && steamJetDiagonalTrace
                ? CompileShader("SteamGasStepObserver.hlsl", "CSDiagonalIntent") : null,
            SteamJetInjectionObserverShader = allocateSimulation ? CompileShader("SteamJetInjectionObserver.hlsl") : null,
            SteamJetInjectionDistributionObserverShader = allocateSimulation && steamJetSourceDistributionTrace
                ? CompileShader("SteamJetInjectionObserver.hlsl", "CSDistribution") : null,
            SteamJetMotionObserverShader = allocateSimulation ? CompileShader("SteamJetMotionObserver.hlsl") : null,
            SteamJetAirCouplingObserverShader = allocateSimulation ? CompileShader("SteamJetAirCouplingObserver.hlsl") : null,
            ComponentInitializeShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "InitializeComponents") : null,
            ComponentUnionShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "UnionComponents") : null,
            ComponentCompressShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "CompressComponents") : null,
            ComponentFinalizeShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "FinalizeComponents") : null,
            SolidGeometryAnalyzeShader = allocateSimulation ? CompileShader("SolidBodySolver.hlsl", "AnalyzeSolidGeometry") : null,
            SolidAnalyzeShader = allocateSimulation ? CompileShader("SolidBodySolver.hlsl", "AnalyzeSolidBodies") : null,
            SolidDisplacementPlanShader = allocateSimulation ? CompileShader("SolidBodySolver.hlsl", "PlanHullWaterDisplacement") : null,
            SolidMoveShader = allocateSimulation ? CompileShader("SolidBodySolver.hlsl", "MoveSolidBodies") : null,
            SolidDisplacementApplyShader = allocateSimulation ? CompileShader("SolidBodySolver.hlsl", "ApplyHullWaterDisplacement") : null,
            CompositionShader = allocateSimulation ? CompileShader("RenderComposition.hlsl") : null,
            ThermalDiffusionShader = allocateSimulation ? CompileShader("ThermalDiffusion.hlsl") : null,
            AirInjectShader = allocateSimulation ? CompileShader("AirSimulation.hlsl", "CSInject") : null,
            AirPressureShader = allocateSimulation ? CompileShader("AirSimulation.hlsl", "CSPressure") : null,
            AirVelocityShader = allocateSimulation ? CompileShader("AirSimulation.hlsl", "CSVelocity") : null,
            AirAdvectShader = allocateSimulation ? CompileShader("AirSimulation.hlsl", "CSAdvect") : null,
            AirCommitShader = allocateSimulation ? CompileShader("AirSimulation.hlsl", "CSCommit") : null,
            AirClearShader = allocateSimulation ? CompileShader("AirSimulation.hlsl", "CSClear") : null,
            FireGlowDepositShader = allocateSimulation ? CompileShader("FireGlow.hlsl", "CSDeposit") : null,
            FireGlowDiffuseShader = allocateSimulation ? CompileShader("FireGlow.hlsl", "CSDiffuse") : null,
            FireGlowCommitShader = allocateSimulation ? CompileShader("FireGlow.hlsl", "CSCommitGlow") : null,
            FireGlowClearShader = allocateSimulation ? CompileShader("FireGlow.hlsl", "CSClearGlow") : null,
            ContactTransitionShader = allocateSimulation ? CompileShader("ContactTransitions.hlsl") : null,
            PhaseTransitionShader = allocateSimulation ? CompileShader("PhaseTransitions.hlsl") : null,
            CombustionShader = allocateSimulation ? CompileShader("Combustion.hlsl") : null,
            EmissionResolveShader = allocateSimulation ? CompileShader("EmissionResolve.hlsl") : null,
            TransientLifecycleShader = allocateSimulation ? CompileShader("TransientLifecycle.hlsl") : null,
            TemperatureProbeShader = allocateSimulation ? CompileShader("TemperatureProbe.hlsl") : null
        };
    }

    private Buffer CreateConstantBuffer<T>() where T : struct
    {
        int size = Marshal.SizeOf<T>();
        if (size % 16 != 0)
        {
            throw new InvalidOperationException($"Constant buffer {typeof(T).Name} size {size} is not a multiple of 16.");
        }
        return new Buffer(Device, new BufferDescription
        {
            SizeInBytes = size,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
    }

    private ComputeShader CompileShader(string fileName, string entryPoint = "CSMain")
    {
        string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders");
        string path = Path.Combine(shaderDirectory, fileName);
        string sharedStructures = File.ReadAllText(Path.Combine(shaderDirectory, "PhysicsShared.hlsli"));
        string shaderSource = File.ReadAllText(path).Replace(
            "#include \"PhysicsShared.hlsli\"",
            sharedStructures,
            StringComparison.Ordinal);
        string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"phyxel-compute-shader-v1\0{entryPoint}\0{shaderSource}")));
        string cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Phyxel",
            "ShaderCache",
            cacheKey + ".cso");

        try
        {
            if (File.Exists(cachePath))
            {
                using ShaderBytecode cachedBytecode = new(File.ReadAllBytes(cachePath));
                return new ComputeShader(Device, cachedBytecode);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SharpDX.SharpDXException)
        {
            // A stale or partially written cache entry must never prevent startup.
            TryDeleteShaderCacheEntry(cachePath);
        }

        using CompilationResult compilation = ShaderBytecode.Compile(
            shaderSource,
            entryPoint,
            "cs_5_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None,
            null,
            null,
            path);
        TryWriteShaderCacheEntry(cachePath, compilation.Bytecode.Data);
        return new ComputeShader(Device, compilation.Bytecode);
    }

    private static void TryWriteShaderCacheEntry(string cachePath, byte[] bytecode)
    {
        try
        {
            string? cacheDirectory = Path.GetDirectoryName(cachePath);
            if (cacheDirectory is null)
            {
                return;
            }

            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllBytes(cachePath, bytecode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Caching is an optimization; compiled bytecode remains usable this run.
        }
    }

    private static void TryDeleteShaderCacheEntry(string cachePath)
    {
        try
        {
            File.Delete(cachePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private Buffer CreateStagingBuffer(int sizeInBytes)
    {
        return new Buffer(Device, new BufferDescription
        {
            SizeInBytes = sizeInBytes,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read | CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
    }

    private Buffer CreateReadStagingBuffer(int sizeInBytes)
    {
        return new Buffer(Device, new BufferDescription
        {
            SizeInBytes = sizeInBytes,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
    }

    private KniTexture2D CreatePixelTexture()
    {
        KniTexture2D texture = new(graphicsDevice, 1, 1);
        texture.SetData([Color.White]);
        return texture;
    }

    private KniTexture2D CreateCircleTexture(int size)
    {
        KniTexture2D texture = new(graphicsDevice, size, size);
        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float radiusSquared = center * center;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float offsetX = x - center;
                float offsetY = y - center;
                pixels[y * size + x] = offsetX * offsetX + offsetY * offsetY <= radiusSquared
                    ? Color.White
                    : Color.Transparent;
            }
        }

        texture.SetData(pixels);
        return texture;
    }

    private KniTexture2D CreateBrushOutlineTexture(int size)
    {
        KniTexture2D texture = new(graphicsDevice, size, size);
        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float outerRadius = center;
        float innerRadius = center - 4;
        float outerSquared = outerRadius * outerRadius;
        float innerSquared = innerRadius * innerRadius;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float offsetX = x - center;
                float offsetY = y - center;
                float distanceSquared = offsetX * offsetX + offsetY * offsetY;
                pixels[y * size + x] = distanceSquared <= outerSquared && distanceSquared >= innerSquared
                    ? Color.White
                    : Color.Transparent;
            }
        }

        texture.SetData(pixels);
        return texture;
    }

    private void HandleDeviceResetting(object? sender, EventArgs eventArgs)
    {
        RequiresRecreation = true;
        Resources?.Dispose();
        Resources = null;
        preparedSimulationResources?.Dispose();
        preparedSimulationResources = null;
    }

    private void HandleDeviceReset(object? sender, EventArgs eventArgs)
    {
        RequiresRecreation = true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        graphicsDevice.DeviceResetting -= HandleDeviceResetting;
        graphicsDevice.DeviceReset -= HandleDeviceReset;
        Resources?.Dispose();
        preparedSimulationResources?.Dispose();
        BrushOutlineTexture.Dispose();
        CircleTexture.Dispose();
        PixelTexture.Dispose();
    }
}
