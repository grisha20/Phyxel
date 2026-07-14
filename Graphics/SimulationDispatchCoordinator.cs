using System;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Graphics;

public sealed class SimulationDispatchCoordinator
{
    private readonly GpuResourceLifecycleManager lifecycleManager;
    private readonly MaterialRegistry materialRegistry;
    private GpuSimulationResources? boundResources;
    private uint frameIndex;
    private uint lastObservedStatisticsFrame;
    private bool worldHasMatter;
    private bool presentationDirty = true;
    private bool lastStressView;
    private bool cellularMatter;
    private bool fluidCellularMatter;
    private bool cellularSleeping;
    private int settledCellularObservations;
    private bool staticLatticeMatter;
    private bool deformableLatticeMatter;
    private bool dynamicLatticeMatter;
    private bool latticeSleeping;
    private int settledLatticeObservations;
    private readonly SimulationDispatchRegion latticeRegion = new();
    private readonly SimulationDispatchRegion cellularRegion = new();

    public ulong FullGridPhysicsDispatches { get; private set; }
    public ulong LocalTopologyDispatches { get; private set; }
    public ulong CompositionDispatches { get; private set; }
    public bool CellularSleeping => cellularSleeping;

    public SimulationDispatchCoordinator(
        GpuResourceLifecycleManager lifecycleManager,
        MaterialRegistry materialRegistry)
    {
        this.lifecycleManager = lifecycleManager;
        this.materialRegistry = materialRegistry;
    }

    public GpuSimulationResources DispatchFrame(
        SimulationSettings settings,
        ReadOnlySpan<BrushDrawCommand> commands)
    {
        GpuSimulationResources resources = lifecycleManager.CreateOrResize(
            settings,
            worldHasMatter || commands.Length > 0);
        if (!ReferenceEquals(resources, boundResources))
        {
            Clear(resources);
            boundResources = resources;
            worldHasMatter = false;
            cellularMatter = false;
            fluidCellularMatter = false;
            cellularSleeping = false;
            settledCellularObservations = 0;
            staticLatticeMatter = false;
            deformableLatticeMatter = false;
            dynamicLatticeMatter = false;
            latticeSleeping = false;
            settledLatticeObservations = 0;
            latticeRegion.Reset();
            cellularRegion.Reset();
            presentationDirty = resources.IsSimulationAllocated;
        }

        SimulationFrameConstants constants = CreateConstants(settings, commands, 0);
        if (commands.Length > 0)
        {
            resources.Commands.Upload(resources.Context, commands);
            DispatchBrush(resources, ref constants, commands);
            worldHasMatter |= ContainsMatterCommand(commands);
            RegisterMaterialActivity(commands);
            presentationDirty = true;
        }

        if (!settings.Paused && dynamicLatticeMatter && !latticeSleeping)
        {
            int iterations = Math.Clamp(settings.SolverIterations, 1, 8);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                constants.SolverIteration = (uint)iteration;
                DispatchLattice(resources, ref constants);
            }

            DispatchUnifiedOccupancyProjection(resources, ref constants);
            presentationDirty = true;
        }

        if (!settings.Paused && cellularMatter && !cellularSleeping)
        {
            cellularRegion.Grow(
                resources.Width,
                resources.Height,
                fluidCellularMatter ? 1 : 0,
                fluidCellularMatter ? 1 : 0,
                fluidCellularMatter ? 1 : 2);
            DispatchCellularAutomata(resources, ref constants);
            if (fluidCellularMatter)
            {
                DispatchCellularAutomata(resources, ref constants);
            }
            presentationDirty |= fluidCellularMatter || frameIndex % 2 == 0;
        }

        presentationDirty |= resources.IsSimulationAllocated && lastStressView != settings.StressView;
        lastStressView = settings.StressView;
        constants.StressView = settings.StressView ? 1u : 0u;
        if (presentationDirty && resources.IsSimulationAllocated)
        {
            DispatchComposition(resources, ref constants);
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
        worldHasMatter = false;
        cellularMatter = false;
        fluidCellularMatter = false;
        cellularSleeping = false;
        settledCellularObservations = 0;
        staticLatticeMatter = false;
        deformableLatticeMatter = false;
        dynamicLatticeMatter = false;
        latticeSleeping = false;
        settledLatticeObservations = 0;
        latticeRegion.Reset();
        cellularRegion.Reset();
        presentationDirty = resources.IsSimulationAllocated;
    }

    public void RestoreWorldActivity(GpuSimulationResources resources, bool containsMatter)
    {
        boundResources = resources;
        worldHasMatter = containsMatter;
        cellularMatter = containsMatter;
        fluidCellularMatter = containsMatter;
        cellularSleeping = false;
        settledCellularObservations = 0;
        staticLatticeMatter = containsMatter;
        deformableLatticeMatter = containsMatter;
        dynamicLatticeMatter = containsMatter;
        latticeSleeping = false;
        settledLatticeObservations = 0;
        latticeRegion.SetFull(resources.Width, resources.Height);
        cellularRegion.SetFull(resources.Width, resources.Height);
        presentationDirty = resources.IsSimulationAllocated;
    }

    public void ObserveStatistics(SimulationStatistics statistics)
    {
        if (statistics.FrameIndex == 0 || statistics.FrameIndex <= lastObservedStatisticsFrame)
        {
            return;
        }

        lastObservedStatisticsFrame = statistics.FrameIndex;
        uint unsettledTolerance = Math.Max(1u, statistics.ActiveCells / 50000);
        bool cellularSettled = statistics.ActiveCells > 0 &&
            statistics.Reserved + unsettledTolerance >= statistics.ActiveCells;
        settledCellularObservations = cellularSettled ? settledCellularObservations + 1 : 0;
        if (cellularMatter && settledCellularObservations >= 2)
        {
            cellularSleeping = true;
        }

        bool latticeSettled = dynamicLatticeMatter && statistics.MovingParticles == 0 &&
            (!cellularMatter || cellularSleeping);
        settledLatticeObservations = latticeSettled ? settledLatticeObservations + 1 : 0;
        if (settledLatticeObservations >= 2)
        {
            latticeSleeping = true;
        }

        if (statistics.ActiveParticles == 0 && statistics.ActiveCells == 0)
        {
            worldHasMatter = false;
            cellularMatter = false;
            fluidCellularMatter = false;
            cellularSleeping = false;
            settledCellularObservations = 0;
            staticLatticeMatter = false;
            deformableLatticeMatter = false;
            dynamicLatticeMatter = false;
            latticeSleeping = false;
            settledLatticeObservations = 0;
            latticeRegion.Reset();
            cellularRegion.Reset();
        }
    }

    private void DispatchBrush(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        ReadOnlySpan<BrushDrawCommand> commands)
    {
        DeviceContext context = resources.Context;
        context.ClearUnorderedAccessView(resources.ActivatedBodyWords.WriteUnorderedView, new RawInt4(0, 0, 0, 0));
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.BrushShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Commands.View, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.Particles.ReadUnorderedView,
            resources.Bonds.ReadUnorderedView,
            resources.ActivatedBodyWords.WriteUnorderedView);
        int maximumDiameter = Math.Max(1, (int)constants.MaximumBrushDiameter);
        context.Dispatch(DivideRoundUp(maximumDiameter, 16), DivideRoundUp(maximumDiameter, 16), commands.Length);
        Unbind(context, 2, 4);
        DispatchLatticeTopology(resources, ref constants, commands);
    }

    private void DispatchLatticeTopology(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        ReadOnlySpan<BrushDrawCommand> commands)
    {
        DeviceContext context = resources.Context;
        CalculateTopologyRegion(commands, resources.Width, resources.Height, out int x, out int y, out int width, out int height);
        constants.DispatchOffsetX = (uint)x;
        constants.DispatchOffsetY = (uint)y;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.LatticeTopologyShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.ActivatedBodyWords.WriteView, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Particles.ReadUnorderedView,
            resources.Bonds.ReadUnorderedView);
        context.Dispatch(DivideRoundUp(width, 16), DivideRoundUp(height, 16), 1);
        LocalTopologyDispatches++;
        Unbind(context, 2, 2);
        constants.DispatchOffsetX = 0;
        constants.DispatchOffsetY = 0;
    }

    private void DispatchCellularAutomata(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        cellularRegion.Get(resources.Width, resources.Height, out int x, out int y, out int regionWidth, out int regionHeight);
        constants.DispatchOffsetX = (uint)x;
        constants.DispatchOffsetY = (uint)y;
        ReadOnlySpan<uint> phases = fluidCellularMatter && (frameIndex >= 32 || frameIndex % 4 != 1)
            ? [4, 5, 6, 7, 8, 9, 2, 3, 2, 3, 2, 3, 2, 3,
                0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1]
            : fluidCellularMatter
            ? [4, 2, 3, 2, 3, 0, 1, 0, 1]
            : [4, 2, 3, 0, 1];
        foreach (uint phase in phases)
        {
            constants.SimulationPhase = phase;
            UpdateConstants(context, resources, ref constants);
            context.ComputeShader.Set(resources.CellularAutomataShader);
            context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
            context.ComputeShader.SetShaderResource(0, resources.Materials.View);
            context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.ReadUnorderedView);
            context.Dispatch(DivideRoundUp(regionWidth, 16), DivideRoundUp(regionHeight, 16), 1);
            FullGridPhysicsDispatches++;
            Unbind(context, 1, 1);
        }

        constants.SimulationPhase = 0;
        constants.DispatchOffsetX = 0;
        constants.DispatchOffsetY = 0;
    }

    private void DispatchUnifiedOccupancyProjection(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        latticeRegion.Get(resources.Width, resources.Height, out int x, out int y, out int width, out int height);
        context.CopyResource(resources.Grid.ReadBuffer, resources.Grid.WriteBuffer);
        constants.DispatchOffsetX = (uint)x;
        constants.DispatchOffsetY = (uint)y;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.LatticeOccupancyClearShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, null!, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(width, 16), DivideRoundUp(height, 16), 1);
        FullGridPhysicsDispatches++;
        Unbind(context, 3, 1);
        resources.Grid.Swap();

        context.CopyResource(resources.Grid.ReadBuffer, resources.Grid.WriteBuffer);
        constants.DispatchOffsetX = 0;
        constants.DispatchOffsetY = 0;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.LatticeProjectionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, null!, resources.Particles.ReadView, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Particles.Count, 256), 1, 1);
        FullGridPhysicsDispatches++;
        Unbind(context, 3, 1);
        resources.Grid.Swap();
    }

    private void DispatchLattice(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        latticeRegion.Get(resources.Width, resources.Height, out int x, out int y, out int width, out int height);
        constants.DispatchOffsetX = (uint)x;
        constants.DispatchOffsetY = (uint)y;
        context.CopyResource(resources.Particles.ReadBuffer, resources.Particles.WriteBuffer);
        context.CopyResource(resources.Bonds.ReadBuffer, resources.Bonds.WriteBuffer);
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.LatticeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Particles.ReadView,
            resources.Bonds.ReadView,
            resources.Grid.ReadView,
            resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Particles.WriteUnorderedView,
            resources.Bonds.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(width, 16), DivideRoundUp(height, 16), 1);
        FullGridPhysicsDispatches++;
        Unbind(context, 4, 2);
        resources.Particles.Swap();
        resources.Bonds.Swap();
        constants.DispatchOffsetX = 0;
        constants.DispatchOffsetY = 0;
    }

    private void DispatchComposition(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        bool collectStatistics = frameIndex % 30 == 0;
        if (collectStatistics)
        {
            context.ClearUnorderedAccessView(resources.Statistics.WriteUnorderedView, new RawInt4(0, 0, 0, 0));
        }
        constants.SimulationPhase = collectStatistics ? 1u : 0u;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.CompositionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.Particles.ReadView,
            resources.Bonds.ReadView,
            resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.CompositionTargets.WriteView,
            resources.Statistics.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        CompositionDispatches++;
        Unbind(context, 4, 2);
        if (collectStatistics)
        {
            resources.Statistics.Swap();
        }
        constants.SimulationPhase = 0;
        context.CopyResource(resources.CompositionTargets.WriteTexture, resources.NativePresentationTexture);
        resources.CompositionTargets.Swap();
    }

    private static void Clear(GpuSimulationResources resources)
    {
        RawInt4 zero = new(0, 0, 0, 0);
        foreach (UnorderedAccessView view in resources.Grid.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }

        foreach (UnorderedAccessView view in resources.Particles.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }

        foreach (UnorderedAccessView view in resources.Bonds.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }

        foreach (UnorderedAccessView view in resources.Statistics.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }

        foreach (UnorderedAccessView view in resources.ActivatedBodyWords.UnorderedAccessViews)
        {
            resources.Context.ClearUnorderedAccessView(view, zero);
        }
    }

    private SimulationFrameConstants CreateConstants(
        SimulationSettings settings,
        ReadOnlySpan<BrushDrawCommand> commands,
        uint iteration)
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
            SolverIteration = iteration,
            ParticleCount = (uint)(settings.Width * settings.Height),
            BondCount = (uint)(settings.Width * settings.Height),
            MaximumVelocity = 2200f
        };
    }

    private static void UpdateConstants(
        DeviceContext context,
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        context.UpdateSubresource(ref constants, resources.FrameConstants);
    }

    private static void Unbind(DeviceContext context, int resourceCount, int unorderedAccessCount)
    {
        context.ComputeShader.SetShaderResources(0, new ShaderResourceView[resourceCount]);
        context.ComputeShader.SetUnorderedAccessViews(0, new UnorderedAccessView[unorderedAccessCount]);
        context.ComputeShader.Set(null);
    }

    private static int DivideRoundUp(int value, int divisor)
    {
        return (value + divisor - 1) / divisor;
    }

    private static bool ContainsMatterCommand(ReadOnlySpan<BrushDrawCommand> commands)
    {
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode == 0 && command.MaterialId != 5)
            {
                return true;
            }
        }

        return false;
    }

    private static void CalculateTopologyRegion(
        ReadOnlySpan<BrushDrawCommand> commands,
        int worldWidth,
        int worldHeight,
        out int x,
        out int y,
        out int width,
        out int height)
    {
        bool requiresBodyActivation = false;
        int minimumX = worldWidth;
        int minimumY = worldHeight;
        int maximumX = 0;
        int maximumY = 0;
        foreach (BrushDrawCommand command in commands)
        {
            requiresBodyActivation |= command.Mode == 1 || command.MaterialId == 5;
            int radius = (int)MathF.Ceiling(command.Radius) + 1;
            minimumX = Math.Min(minimumX, command.X - radius);
            minimumY = Math.Min(minimumY, command.Y - radius);
            maximumX = Math.Max(maximumX, command.X + radius + 1);
            maximumY = Math.Max(maximumY, command.Y + radius + 1);
        }

        if (requiresBodyActivation)
        {
            x = 0;
            y = 0;
            width = worldWidth;
            height = worldHeight;
            return;
        }

        x = Math.Clamp(minimumX, 0, worldWidth - 1);
        y = Math.Clamp(minimumY, 0, worldHeight - 1);
        int right = Math.Clamp(maximumX, x + 1, worldWidth);
        int bottom = Math.Clamp(maximumY, y + 1, worldHeight);
        width = right - x;
        height = bottom - y;
    }

    private void RegisterMaterialActivity(ReadOnlySpan<BrushDrawCommand> commands)
    {
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode == 2)
            {
                cellularSleeping = false;
                settledCellularObservations = 0;
                cellularRegion.Include(command, 2);
                dynamicLatticeMatter |= deformableLatticeMatter;
                latticeSleeping = false;
                settledLatticeObservations = 0;
                continue;
            }

            if (command.Mode != 0 || command.MaterialId == (uint)MaterialId.Eraser)
            {
                dynamicLatticeMatter |= deformableLatticeMatter;
                latticeSleeping = false;
                settledLatticeObservations = 0;
                latticeRegion.Reset();
                continue;
            }

            MaterialSimulationKind kind = (MaterialSimulationKind)materialRegistry[(MaterialId)command.MaterialId]
                .Properties.SimulationKind;
            if (kind == MaterialSimulationKind.Lattice)
            {
                staticLatticeMatter = true;
                deformableLatticeMatter |= materialRegistry[(MaterialId)command.MaterialId].Properties.FailureMode != 0;
                latticeRegion.Include(command, 16);
                if (cellularMatter && deformableLatticeMatter)
                {
                    dynamicLatticeMatter = true;
                    latticeSleeping = false;
                    settledLatticeObservations = 0;
                }
            }
            else if (kind is MaterialSimulationKind.Granular or MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas)
            {
                cellularMatter = true;
                fluidCellularMatter |= kind is MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas;
                cellularSleeping = false;
                settledCellularObservations = 0;
                cellularRegion.Include(command, 2);
                if (kind is MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas && staticLatticeMatter)
                {
                    cellularRegion.Include(latticeRegion);
                }
                if (kind == MaterialSimulationKind.Granular && deformableLatticeMatter)
                {
                    dynamicLatticeMatter = true;
                    latticeSleeping = false;
                    settledLatticeObservations = 0;
                }
            }
        }
    }

}
