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
