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
    private bool waterPressureRoutesDirty = true;
    private int settledObservations;

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
            settledObservations = 0;
            presentationDirty = true;
            return;
        }
        solidSleeping = true;
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
            ResetActivity(resources.IsSimulationAllocated);
        }

        SimulationFrameConstants constants = CreateConstants(settings, commands);
        if (commands.Length > 0)
        {
            resources.Commands.Upload(resources.Context, commands);
            DispatchBrush(resources, ref constants);
            RegisterActivity(commands, settings.Width, settings.Height);
            worldHasMatter |= ContainsMatterCommand(commands);
            presentationDirty = true;
        }

        if (settings.SolidGravity && !previousSolidGravity)
        {
            solidMatter |= worldHasMatter;
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
            DispatchSolidPass(resources, ref constants, 0);
            cellularSleeping = false;
            presentationDirty = true;
        }

        if (waterPressureRoutesDirty && resources.IsSimulationAllocated)
        {
            resources.Context.ClearUnorderedAccessView(
                resources.ComponentParents.UnorderedView,
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

            DispatchCellularAutomata(resources, ref constants, true);
            if (fluidMatter)
            {
                DispatchCellularAutomata(resources, ref constants, false);
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
        waterPressureRoutesDirty = true;
        settledObservations = 0;
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
        uint cellularCells = statistics.WaterCells + statistics.SandCells + statistics.GasCells;
        uint residualTolerance = statistics.GasCells > 0
            ? Math.Max(64u, cellularCells / 10u)
            : Math.Max(64u, cellularCells / 50u);
        bool settled = statistics.ActiveCells > 0 && statistics.PressureMoves == 0 &&
            statistics.MovingCells <= residualTolerance;
        settledObservations = settled ? settledObservations + 1 : 0;
        if (settledObservations >= 8)
        {
            finalizeCellularRest = cellularMatter && !cellularSleeping;
            cellularSleeping = true;
            solidSleeping = !previousSolidGravity || statistics.MovingSolidCells == 0;
            ResetActiveRegion();
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

    private static void DispatchSolidGeometry(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        context.ClearUnorderedAccessView(
            resources.SolidBodyGeometry.UnorderedView,
            new RawInt4(0, 0, 0, 0));
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.SolidGeometryAnalyzeShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResource(0, resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            null,
            null,
            null,
            null,
            resources.SolidBodyGeometry.UnorderedView);
        context.Dispatch(
            DivideRoundUp(resources.Width, 16),
            DivideRoundUp(resources.Height, 16),
            1);
        Unbind(context, 1, 5);
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
        context.ComputeShader.SetShaderResource(0, resources.Grid.ReadView);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.BodyFlags.UnorderedView,
            null,
            resources.CellMaterials.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 1, 3);

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
            resources.SolidBodyGeometry.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            null,
            null,
            null,
            resources.ComponentParents.UnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 4);

        context.ComputeShader.Set(resources.SolidMoveShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.BodyFlags.View,
            resources.CellMaterials.View,
            resources.ComponentParents.View,
            resources.SolidBodyGeometry.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 2);

        context.ComputeShader.Set(resources.SolidDisplacementApplyShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(
            0,
            resources.Grid.ReadView,
            resources.BodyFlags.View,
            resources.CellMaterials.View,
            resources.ComponentParents.View,
            resources.SolidBodyGeometry.View);
        context.ComputeShader.SetUnorderedAccessViews(0, null, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 5, 2);
        resources.Grid.Swap();
        waterPressureRoutesDirty = true;
    }

    private void DispatchCellularAutomata(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        bool rebuildCellMaterials)
    {
        DeviceContext context = resources.Context;
        // 33 caches each water column once. Phases 13..23 plan bounded
        // power-of-two hydraulic transfers and phase 29 applies each plan.
        ReadOnlySpan<uint> phases = (frameIndex & 1) == 0
            ? [32, 0, 1, 0, 1, 0, 1, 0, 1, 5, 6, 7, 8, 9, 10, 11, 12,
                33,
                13, 29, 14, 29, 15, 29, 16, 29, 17, 29, 18, 29,
                19, 29, 20, 29, 21, 29, 22, 29, 23, 29,
                34, 34, 34, 38, 39, 36, 37,
                40, 41, 42, 43, 44, 45, 46, 47,
                2, 3, 2, 3, 2, 3, 2, 3, 4]
            : [32, 1, 0, 1, 0, 1, 0, 1, 0, 6, 5, 8, 7, 10, 9, 12, 11,
                33,
                23, 29, 22, 29, 21, 29, 20, 29, 19, 29, 18, 29,
                17, 29, 16, 29, 15, 29, 14, 29, 13, 29,
                34, 34, 34, 38, 39, 36, 37,
                47, 46, 45, 44, 43, 42, 41, 40,
                3, 2, 3, 2, 3, 2, 3, 2, 4];

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
            resources.ComponentParents.UnorderedView);

        foreach (uint phase in phases)
        {
            if (phase == 32 && !rebuildCellMaterials)
            {
                continue;
            }
            bool hydraulicPhase = phase == 33 || phase == 29 || phase == 34 ||
                phase == 36 || phase == 37 || phase == 38 || phase == 39 ||
                phase is >= 13 and <= 23;
            if (hydraulicPhase && !rebuildCellMaterials)
            {
                continue;
            }

            constants.SimulationPhase = phase;

            bool buildPathBlockers = phase == 31;
            bool buildCellMaterials = phase == 32;
            bool waterColumnPhase = phase == 33 || phase == 29 || phase == 37 ||
                phase == 39 ||
                phase is >= 13 and <= 23;
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
        Unbind(context, 1, 5);
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
        bool collect = frameIndex % 30 == 0;
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
            resources.BodyFlags.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.CompositionTargets.WriteView,
            resources.Statistics.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 3, 2);
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
        resources.Context.ClearUnorderedAccessView(resources.ComponentParents.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.BodyFlags.UnorderedView, zero);
        resources.Context.ClearUnorderedAccessView(resources.SolidBodyGeometry.UnorderedView, zero);
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
            SolidGravity = settings.SolidGravity ? 1u : 0u
        };
    }

    private void RegisterActivity(ReadOnlySpan<BrushDrawCommand> commands, int width, int height)
    {
        if (commands.Length > 0)
        {
            waterPressureRoutesDirty = true;
        }
        foreach (BrushDrawCommand command in commands)
        {
            ExpandActiveRegion(command.X, command.Y, (int)MathF.Ceiling(command.Radius), width, height);

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
        waterPressureRoutesDirty = true;
        ResetActiveRegion();
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
