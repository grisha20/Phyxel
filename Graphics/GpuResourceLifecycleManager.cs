using System;
using System.IO;
using System.Runtime.InteropServices;
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
        // The cellular solver reuses the first nine rows for its compact
        // water-column cache and pressure-transfer scratch space.
        int bodyFlagCount = allocateSimulation ? Math.Max(cellCount, checked(width * 9 + 1)) : 10;
        GpuStructuredBuffer<uint> bodyFlags = new(Device, bodyFlagCount);
        int blockerMaskCount = allocateSimulation ? checked(((width + 31) / 32) * height) : 1;
        GpuStructuredBuffer<uint> pathBlockerMasks = new(Device, blockerMaskCount);
        GpuStructuredBuffer<uint> cellMaterials = new(Device, cellCount);
        GpuBufferPair<SimulationStatistics> statistics = new(Device, 1);
        GpuUploadBuffer<BrushDrawCommand> commands = new(Device, SimulationSettings.MaximumBrushCommands);
        MaterialProperties[] materialTable = materialRegistry.CreateGpuTable();
        GpuUploadBuffer<MaterialProperties> materials = new(Device, materialTable.Length);
        materials.Upload(Device.ImmediateContext, materialTable);
        Buffer constants = new(Device, new BufferDescription
        {
            SizeInBytes = Marshal.SizeOf<SimulationFrameConstants>(),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
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
            PathBlockerMasks = pathBlockerMasks,
            CellMaterials = cellMaterials,
            Statistics = statistics,
            Commands = commands,
            Materials = materials,
            FrameConstants = constants,
            StatisticsStaging = statisticsStaging,
            StatisticsQuery = statisticsQuery,
            GridStaging = gridStaging,
            SceneTransferQuery = sceneTransferQuery,
            CompositionTargets = targets,
            PresentationTextures = presentations,
            NativePresentationTextures = nativePresentations,
            BrushShader = allocateSimulation ? CompileShader("BrushApplication.hlsl") : null,
            CellularAutomataShader = allocateSimulation ? CompileShader("CellularAutomataSolver.hlsl") : null,
            ComponentInitializeShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "InitializeComponents") : null,
            ComponentUnionShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "UnionComponents") : null,
            ComponentCompressShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "CompressComponents") : null,
            ComponentFinalizeShader = allocateSimulation ? CompileShader("SolidComponents.hlsl", "FinalizeComponents") : null,
            SolidAnalyzeShader = allocateSimulation ? CompileShader("SolidBodySolver.hlsl", "AnalyzeSolidBodies") : null,
            SolidMoveShader = allocateSimulation ? CompileShader("SolidBodySolver.hlsl", "MoveSolidBodies") : null,
            CompositionShader = allocateSimulation ? CompileShader("RenderComposition.hlsl") : null
        };
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
        using CompilationResult compilation = ShaderBytecode.Compile(
            shaderSource,
            entryPoint,
            "cs_5_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None,
            null,
            null,
            path);
        return new ComputeShader(Device, compilation.Bytecode);
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
