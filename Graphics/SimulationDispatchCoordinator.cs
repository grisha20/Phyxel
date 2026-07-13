using System;
using Phyxel.Core;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace Phyxel.Graphics;

public sealed class SimulationDispatchCoordinator
{
    private readonly GpuResourceLifecycleManager lifecycleManager;
    private uint frameIndex;

    public SimulationDispatchCoordinator(GpuResourceLifecycleManager lifecycleManager)
    {
        this.lifecycleManager = lifecycleManager;
    }

    public GpuSimulationResources DispatchFrame(
        SimulationSettings settings,
        ReadOnlySpan<BrushDrawCommand> commands)
    {
        GpuSimulationResources resources = lifecycleManager.CreateOrResize(settings);
        resources.Commands.Upload(resources.Context, commands);
        if (frameIndex == 0)
        {
            Clear(resources);
        }

        SimulationFrameConstants constants = CreateConstants(settings, commands.Length, 0);
        if (commands.Length > 0)
        {
            DispatchBrush(resources, ref constants, commands);
        }

        if (!settings.Paused)
        {
            DispatchCellularAutomata(resources, ref constants);
            int iterations = Math.Clamp(settings.SolverIterations, 1, 8);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                constants.SolverIteration = (uint)iteration;
                DispatchLattice(resources, ref constants);
            }
        }

        constants.StressView = settings.StressView ? 1u : 0u;
        DispatchComposition(resources, ref constants);
        frameIndex++;
        return resources;
    }

    public void ClearCurrentWorld(SimulationSettings settings)
    {
        GpuSimulationResources resources = lifecycleManager.CreateOrResize(settings);
        Clear(resources);
    }

    private void DispatchBrush(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants,
        ReadOnlySpan<BrushDrawCommand> commands)
    {
        DeviceContext context = resources.Context;
        context.CopyResource(resources.Grid.ReadBuffer, resources.Grid.WriteBuffer);
        context.CopyResource(resources.Particles.ReadBuffer, resources.Particles.WriteBuffer);
        context.CopyResource(resources.Bonds.ReadBuffer, resources.Bonds.WriteBuffer);
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.BrushShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Commands.View, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessViews(
            0,
            resources.Grid.WriteUnorderedView,
            resources.Particles.WriteUnorderedView,
            resources.Bonds.WriteUnorderedView);
        int maximumDiameter = Math.Max(1, (int)constants.MaximumBrushDiameter);
        context.Dispatch(DivideRoundUp(maximumDiameter, 16), DivideRoundUp(maximumDiameter, 16), commands.Length);
        Unbind(context, 3, 3);
        resources.Grid.Swap();
        resources.Particles.Swap();
        resources.Bonds.Swap();
    }

    private void DispatchCellularAutomata(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        context.CopyResource(resources.Grid.ReadBuffer, resources.Grid.WriteBuffer);
        UpdateConstants(context, resources, ref constants);
        context.ComputeShader.Set(resources.CellularAutomataShader);
        context.ComputeShader.SetConstantBuffer(0, resources.FrameConstants);
        context.ComputeShader.SetShaderResources(0, resources.Grid.ReadView, resources.Particles.ReadView, resources.Materials.View);
        context.ComputeShader.SetUnorderedAccessView(0, resources.Grid.WriteUnorderedView);
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 3, 1);
        resources.Grid.Swap();
    }

    private void DispatchLattice(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
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
        context.Dispatch(DivideRoundUp(resources.Width, 16), DivideRoundUp(resources.Height, 16), 1);
        Unbind(context, 4, 2);
        resources.Particles.Swap();
        resources.Bonds.Swap();
    }

    private void DispatchComposition(
        GpuSimulationResources resources,
        ref SimulationFrameConstants constants)
    {
        DeviceContext context = resources.Context;
        context.ClearUnorderedAccessView(resources.Statistics.WriteUnorderedView, new RawInt4(0, 0, 0, 0));
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
        Unbind(context, 4, 2);
        resources.Statistics.Swap();
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
    }

    private SimulationFrameConstants CreateConstants(SimulationSettings settings, int commandCount, uint iteration)
    {
        return new SimulationFrameConstants
        {
            DeltaTime = 1f / 60f,
            Gravity = settings.Gravity,
            Width = (uint)settings.Width,
            Height = (uint)settings.Height,
            FrameIndex = frameIndex,
            CommandCount = (uint)commandCount,
            MaximumBrushDiameter = (uint)(settings.BrushRadius * 2 + 1),
            SolverIteration = iteration,
            ParticleCount = (uint)(settings.Width * settings.Height),
            BondCount = (uint)(settings.Width * settings.Height),
            InverseWidth = 1f / settings.Width,
            InverseHeight = 1f / settings.Height,
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
}
