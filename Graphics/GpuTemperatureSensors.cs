using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Physics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Graphics;

// One small asynchronous readback for every sensor; never transfers the world.
public sealed class GpuTemperatureSensors : IDisposable
{
    private GpuSimulationResources? bound;
    private GpuStructuredBuffer<TemperatureSensorPosition>? coordinates;
    private GpuStructuredBuffer<TemperatureSensorReading>? results;
    private Buffer? constants, staging;
    private Query? query;
    private TemperatureSensorPosition[] points = [], pendingPoints = [];
    private bool pending, airEnabled;
    private int generation, pendingGeneration;
    private double elapsed;
    private readonly Dictionary<TemperatureSensorPosition, TemperatureSensorReading> readings = [];
    public IReadOnlyDictionary<TemperatureSensorPosition, TemperatureSensorReading> Readings => readings;

    public void Reset() { readings.Clear(); generation++; elapsed = 0; }

    public void Update(GpuSimulationResources resources, IReadOnlyList<TemperatureSensorPosition> requested,
        bool airSimulation, float deltaSeconds)
    {
        if (!ReferenceEquals(bound, resources))
        {
            Dispose(); bound = resources; points = []; Reset();
        }
        int count = Math.Min(requested.Count, TemperatureSensorPosition.MaximumCount);
        bool changed = points.Length != count || airEnabled != airSimulation;
        for (int i = 0; i < count && !changed; i++) changed = points[i] != requested[i];
        if (changed)
        {
            points = new TemperatureSensorPosition[count];
            for (int i = 0; i < count; i++) points[i] = requested[i];
            airEnabled = airSimulation; Reset();
        }
        var context = resources.Context;
        if (pending && context.GetData(query!, AsynchronousFlags.DoNotFlush, out RawBool complete) && complete)
        {
            var data = context.MapSubresource(staging!, 0, MapMode.Read, MapFlags.None);
            try
            {
                if (pendingGeneration == generation)
                    for (int i = 0; i < pendingPoints.Length; i++)
                        readings[pendingPoints[i]] = Marshal.PtrToStructure<TemperatureSensorReading>(
                            IntPtr.Add(data.DataPointer, i * Marshal.SizeOf<TemperatureSensorReading>()));
            }
            finally { context.UnmapSubresource(staging!, 0); pending = false; }
        }
        if (!resources.IsSimulationAllocated)
            foreach (var point in points) readings[point] = new() { Pressure = float.NaN, ReactionPressure = float.NaN };
        elapsed = Math.Min(.1, elapsed + Math.Clamp((double)deltaSeconds, 0, .25));
        if (count == 0 || !resources.IsSimulationAllocated || pending || elapsed < .1) return;
        if (coordinates is null)
        {
            var device = context.Device; // Borrowed from the context; owned by the game.
            coordinates = new(device, TemperatureSensorPosition.MaximumCount);
            results = new(device, TemperatureSensorPosition.MaximumCount);
            constants = new(device, new BufferDescription(16, ResourceUsage.Default, BindFlags.ConstantBuffer,
                CpuAccessFlags.None, ResourceOptionFlags.None, 0));
            staging = new(device, new BufferDescription(Marshal.SizeOf<TemperatureSensorReading>() * TemperatureSensorPosition.MaximumCount,
                ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
            query = new(device, new QueryDescription { Type = QueryType.Event });
        }
        var upload = new TemperatureSensorPosition[TemperatureSensorPosition.MaximumCount];
        points.CopyTo(upload, 0);
        context.UpdateSubresource(upload, coordinates.Buffer);
        TemperatureProbeConstants parameters = new() { X = (uint)count, Y = airSimulation ? 1u : 0u,
            Width = (uint)resources.Width, Height = (uint)resources.Height };
        context.UpdateSubresource(ref parameters, constants!);
        context.ComputeShader.Set(resources.TemperatureSensorsShader);
        context.ComputeShader.SetConstantBuffer(0, constants!);
        context.ComputeShader.SetShaderResource(0, resources.Grid.ReadView);
        context.ComputeShader.SetShaderResource(1, resources.Materials.View);
        context.ComputeShader.SetShaderResource(2, coordinates.View);
        context.ComputeShader.SetShaderResource(3, resources.AirThermal.View);
        context.ComputeShader.SetShaderResource(4, resources.Air.View);
        context.ComputeShader.SetShaderResource(5, resources.ReactionPulse.ReadView);
        context.ComputeShader.SetUnorderedAccessView(0, results!.UnorderedView);
        context.Dispatch(1, 1, 1);
        for (int slot = 0; slot < 6; slot++) context.ComputeShader.SetShaderResource(slot, null);
        context.ComputeShader.SetUnorderedAccessView(0, null);
        context.ComputeShader.Set(null);
        context.CopyResource(results.Buffer, staging!);
        context.End(query!);
        pendingPoints = points; pendingGeneration = generation; pending = true; elapsed = 0;
    }

    public void Dispose()
    {
        query?.Dispose(); staging?.Dispose(); constants?.Dispose(); results?.Dispose(); coordinates?.Dispose();
        query = null; staging = constants = null; results = null; coordinates = null;
        pending = false; bound = null;
    }
}
