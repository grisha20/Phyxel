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
    private bool disposed;

    public GpuResourceLifecycleManager(GraphicsDevice graphicsDevice, MaterialRegistry materialRegistry)
    {
        this.graphicsDevice = graphicsDevice;
        this.materialRegistry = materialRegistry;
        Device = (Device)graphicsDevice.GetD3D11Device();
        PixelTexture = CreatePixelTexture();
        CircleTexture = CreateCircleTexture(64);
        graphicsDevice.DeviceResetting += HandleDeviceResetting;
        graphicsDevice.DeviceReset += HandleDeviceReset;
    }

    public Device Device { get; }
    public KniTexture2D PixelTexture { get; }
    public KniTexture2D CircleTexture { get; }
    public GpuSimulationResources? Resources { get; private set; }
    public bool RequiresRecreation { get; private set; }

    public GpuSimulationResources CreateOrResize(SimulationSettings settings)
    {
        if (Resources is not null && Resources.Width == settings.Width && Resources.Height == settings.Height && !RequiresRecreation)
        {
            return Resources;
        }

        Resources?.Dispose();
        Resources = CreateResources(settings.Width, settings.Height);
        RequiresRecreation = false;
        return Resources;
    }

    private GpuSimulationResources CreateResources(int width, int height)
    {
        int cellCount = checked(width * height);
        GpuBufferPair<GridCell> grid = new(Device, cellCount);
        GpuBufferPair<LatticeParticle> particles = new(Device, cellCount);
        GpuBufferPair<LatticeBond> bonds = new(Device, cellCount);
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
        Buffer particlesStaging = CreateStagingBuffer(particles.ReadBuffer.Description.SizeInBytes);
        Buffer bondsStaging = CreateStagingBuffer(bonds.ReadBuffer.Description.SizeInBytes);
        Query sceneTransferQuery = new(Device, new QueryDescription
        {
            Type = QueryType.Event,
            Flags = QueryFlags.None
        });
        GpuRenderTexturePair targets = new(Device, width, height);
        KniTexture2D presentation = new(graphicsDevice, width, height, false, SurfaceFormat.Color);
        SharpDX.Direct3D11.Texture2D nativePresentation =
            (SharpDX.Direct3D11.Texture2D)presentation.GetD3D11Resource();
        return new GpuSimulationResources
        {
            Device = Device,
            Context = Device.ImmediateContext,
            Width = width,
            Height = height,
            Grid = grid,
            Particles = particles,
            Bonds = bonds,
            Statistics = statistics,
            Commands = commands,
            Materials = materials,
            FrameConstants = constants,
            StatisticsStaging = statisticsStaging,
            StatisticsQuery = statisticsQuery,
            GridStaging = gridStaging,
            ParticlesStaging = particlesStaging,
            BondsStaging = bondsStaging,
            SceneTransferQuery = sceneTransferQuery,
            CompositionTargets = targets,
            PresentationTexture = presentation,
            NativePresentationTexture = nativePresentation,
            BrushShader = CompileShader("BrushApplication.hlsl"),
            CellularAutomataShader = CompileShader("CellularAutomataSolver.hlsl"),
            LatticeShader = CompileShader("LatticePhysicsSolver.hlsl"),
            LatticeOccupancyClearShader = CompileShader("UnifiedOccupancyProjection.hlsl", "ClearLatticeOccupancy"),
            LatticeProjectionShader = CompileShader("UnifiedOccupancyProjection.hlsl", "ProjectLatticeOccupancy"),
            CompositionShader = CompileShader("RenderComposition.hlsl")
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

    private void HandleDeviceResetting(object? sender, EventArgs eventArgs)
    {
        RequiresRecreation = true;
        Resources?.Dispose();
        Resources = null;
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
        CircleTexture.Dispose();
        PixelTexture.Dispose();
    }
}
