using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Graphics;

public sealed class GpuUploadBuffer<T> : IDisposable where T : struct
{
    private readonly T[] uploadArray;

    public GpuUploadBuffer(Device device, int capacity, bool allowUnorderedAccess = false)
    {
        Capacity = capacity;
        int stride = Marshal.SizeOf<T>();
        BufferDescription description = new()
        {
            SizeInBytes = checked(stride * capacity),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = stride
        };
        Buffer = new Buffer(device, description);
        View = new ShaderResourceView(device, Buffer);
        uploadArray = new T[capacity];
    }

    public int Capacity { get; }
    public Buffer Buffer { get; }
    public ShaderResourceView View { get; }

    public void Upload(DeviceContext context, ReadOnlySpan<T> values)
    {
        int count = Math.Min(values.Length, Capacity);
        values[..count].CopyTo(uploadArray);
        DataBox mapping = context.MapSubresource(Buffer, 0, MapMode.WriteDiscard, MapFlags.None);
        int stride = Marshal.SizeOf<T>();
        Utilities.Write(mapping.DataPointer, uploadArray, 0, count);
        if (count < Capacity)
        {
            Utilities.ClearMemory(mapping.DataPointer + count * stride, 0, (Capacity - count) * stride);
        }

        context.UnmapSubresource(Buffer, 0);
    }

    public void Dispose()
    {
        View.Dispose();
        Buffer.Dispose();
    }
}
