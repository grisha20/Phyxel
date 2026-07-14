using System;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
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
    private bool cellularMatter;
    private bool fluidMatter;
    private bool solidMatter;
    private bool cellularSleeping;
    private bool solidSleeping;
    private bool topologyDirty;
    private bool previousSolidGravity;
    private bool presentationDirty = true;
    private bool finalizeCellularRest;
    private int settledObservations;

    public SimulationDispatchCoordinator(
        GpuResourceLifecycleManager lifecycleManager,
        MaterialRegistry materialRegistry)
    {
        this.lifecycleManager = lifecycleManager;
        this.materialRegistry = materialRegistry;
    }

    public bool CellularSleeping => cellularSleeping;

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
            ResetActivity(resources.IsSimulationAllocated);
        }

        SimulationFrameConstants constants = CreateConstants(settings, commands);
        if (commands.Length > 0)
        {
            resources.Commands.Upload(resources.Context, commands);
            DispatchBrush(resources, ref constants);
            RegisterActivity(commands);
            worldHasMatter |= ContainsMatterCommand(commands);
            presentationDirty = true;
        }

        if (settings.SolidGravity && !previousSolidGravity)
        {
            topologyDirty = true;
            solidSleeping = false;
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
            if (topologyDirty)
            {
                DispatchComponentLabeling(resources, ref constants);
                topologyDirty = false;
            }
            DispatchSolidPass(resources, ref constants, 0);
            DispatchSolidPass(resources, ref constants, 1);
            cellularSleeping = false;
            presentationDirty = true;
        }

        if (!settings.Paused && cellularMatter && !cellularSleeping)
        {
            DispatchCellularAutomata(resources, ref constants);
            if (fluidMatter)
            {
                DispatchCellularAutomata(resources, ref constants);
            }
            presentationDirty = true;
        }

        constants.SolidGravity = settings.SolidGravity ? 1u : 0u;
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
        ResetActivity(resources.IsSimulationAllocated);
    }

    public void RestoreWorldActivity(GpuSimulationResources resources, bool containsMatter)
    {
        boundResources = resources;
        worldHasMatter = containsMatter;
        cellularMatter = containsMatter;
        fluidMatter = containsMatter;
        solidMatter = containsMatter;
        cellularSleeping = false;
        solidSleeping = false;
        topologyDirty = true;
        settledObservations = 0;
        presentationDirty = resources.IsSimulationAllocated;
    }

    public void ObserveStatistics(SimulationStatistics statistics)
    {
        if (statistics.FrameIndex == 0 || statistics.FrameIndex <= lastObservedStatisticsFrame)
        {
            return;
        }
        lastObservedStatisticsFrame = statistics.FrameIndex;
        uint cellularCells = statistics.WaterCells + statistics.SandCells + statistics.GasCells;
        uint tolerance = Math.Max(1u, cellularCells / (statistics.GasCells > 0 ? 10u : 50u));
        bool settled = statistics.ActiveCells > 0 && statistics.MovingCells <= tolerance;
        settledObservations = settled ? settledObservations + 1 : 0;
        if (settledObservations >= 2)
        {
            finalizeCellularRest = cellularMatter && !cellularSleeping;
            cellularSleeping = true;
            solidSleeping = true;
        }
        if (statistics.ActiveCells == 0)
        {
            ResetActivity(false);
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
        context.ComputeShader.SetShaderResource(0, resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessView(0, resources.ComponentParents.UnorderedView);
        context.Dispatch(DivideRoundUp(cells, 256), 1, 1);
        Unbind(context, 1, 1);

        for (int iteration = 0; iteration < 24; iteration++)
        {
            context.ComputeShader.Set(resources.ComponentUnionShader);
            context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
            context.ComputeShader.SetShaderResource(0, resources.Grid.ReadView);
            context.ComputeShader.SetUnorderedAccessView(0, resources.ComponentParents.UnorderedView);
            context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
            Unbind(context, 1, 1);

            context.ComputeShader.Set(resources.ComponentCompressShader);
            context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
            context.ComputeShader.SetUnorderedAccessView(0, resources.ComponentParents.UnorderedView);
            context.Dispatch(DivideRoundUp(cells, 256), 1, 1);
            Unbind(context, 0, 1);
        }

        context.ComputeShader.Set(resources.ComponentFinalizeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, null, resources.ComponentParents.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.ReadUnorderedView);
        context.Dispatch(DivideRoundUp(cells, 256), 1, 1);
        Unbind(context, 2, 2);
    }

    private void DispatchSolidPass(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        uint pass)
    {
        DeviceContext context = resources.Context;
        constants.SolidPass = pass;
        context.ClearUnorderedAccessView(resources.BodyFlags.UnorderedView, new RawInt4(0, 0, 0, 0));
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.SolidAnalyzeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResource(0, resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessView(0, resources.BodyFlags.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 1, 1);

        context.ComputeShader.Set(resources.SolidMoveShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.BodyFlags.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 2, 2);
        resources.Grid.Swap();
    }

    private void DispatchCellularAutomata(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        ReadOnlySpan<uint> phases = (frameIndex & 1) == 0
            ? [0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
                19, 29,
                21, 22, 23, 24, 25, 26, 27, 28,
                2, 3, 2, 3, 2, 3, 2, 3, 4]
            : [1, 0, 1, 0, 1, 0, 1, 0, 6, 5, 8, 7, 10, 9, 12, 11,
                28, 27, 26, 25, 24, 23, 22, 21,
                19, 29,
                3, 2, 3, 2, 3, 2, 3, 2, 4];
        foreach (uint phase in phases)
        {
            if (phase is >= 13 and <= 20)
            {
                context.ClearUnorderedAccessView(resources.BodyFlags.UnorderedView, new RawInt4(0, 0, 0, 0));
            }
            constants.SimulationPhase = phase;
            UpdateConstants(context, resources, ref constants);
            context.ComputeShader.Set(resources.CellularAutomataShader);
            context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
            context.ComputeShader.SetShaderResource(0, resources.Materials.View);
            context.ComputeShader.SetUnorderedAccessViews(
                0,
                resources.Grid.ReadUnorderedView,
                resources.BodyFlags.UnorderedView);
            context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
            Unbind(context, 1, 2);
        }
        constants.SimulationPhase = 0;
    }

    private static void DispatchCellularRest(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        constants.SimulationPhase = 30;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.CellularAutomataShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResource(0, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.ReadUnorderedView,
            resources.BodyFlags.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 1, 2);
        constants.SimulationPhase = 0;
    }

    private void DispatchComposition(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        bool collect = frameIndex % 30 == 0;
        if (collect)
        {
            context.ClearUnorderedAccessView(resources.Statistics.WriteUnorderedView, new RawInt4(0, 0, 0, 0));
        }
        constants.SimulationPhase = collect ? 1u : 0u;
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.CompositionShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.CompositionTargets.WriteView,
            resources.Statistics.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 2, 2);
        if (collect)
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
        resources.Context.ClearUnorderedAccessView(resources.ComponentParents.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.BodyFlags.UnorderedView, zero);
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
            SolidGravity = settings.SolidGravity ? 1u : 0u
        };
    }

    private void RegisterActivity(ReadOnlySpan<BrushDrawCommand> commands)
    {
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode != 0 || command.MaterialId == (uint)MaterialId.Eraser)
            {
                topologyDirty = true;
                cellularSleeping = false;
                finalizeCellularRest = false;
                solidSleeping = false;
                settledObservations = 0;
                continue;
            }
            MaterialSimulationKind kind = (MaterialSimulationKind)materialRegistry[(MaterialId)command.MaterialId]
                .Properties.SimulationKind;
            if (kind == MaterialSimulationKind.Solid)
            {
                solidMatter = true;
                topologyDirty = true;
                solidSleeping = false;
            }
            else if (kind is MaterialSimulationKind.Granular or MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas)
            {
                cellularMatter = true;
                fluidMatter |= kind is MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas;
                cellularSleeping = false;
                finalizeCellularRest = false;
            }
            settledObservations = 0;
        }
    }

    private void ResetActivity(bool dirtyPresentation)
    {
        worldHasMatter = false;
        cellularMatter = false;
        fluidMatter = false;
        solidMatter = false;
        cellularSleeping = false;
        solidSleeping = false;
        topologyDirty = false;
        settledObservations = 0;
        presentationDirty = dirtyPresentation;
        finalizeCellularRest = false;
    }

    private static bool ContainsMatterCommand(ReadOnlySpan<BrushDrawCommand> commands)
    {
        foreach (BrushDrawCommand command in commands)
        {
            if (command.Mode == 0 && command.MaterialId != (uint)MaterialId.Eraser)
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
