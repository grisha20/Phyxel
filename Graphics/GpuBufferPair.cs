using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Graphics;

public sealed class GpuBufferPair<T> : IDisposable where T : struct
{
    private int readIndex;

    public GpuBufferPair(Device device, int count)
    {
        Count = count;
        int stride = Marshal.SizeOf<T>();
        BufferDescription description = new()
        {
            SizeInBytes = checked(stride * count),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = stride
        };
        Buffers = [new Buffer(device, description), new Buffer(device, description)];
        ShaderResourceViews =
        [
            new ShaderResourceView(device, Buffers[0]),
            new ShaderResourceView(device, Buffers[1])
        ];
        UnorderedAccessViews =
        [
            new UnorderedAccessView(device, Buffers[0]),
            new UnorderedAccessView(device, Buffers[1])
        ];
    }

    public int Count { get; }
    public Buffer[] Buffers { get; }
    public ShaderResourceView[] ShaderResourceViews { get; }
    public UnorderedAccessView[] UnorderedAccessViews { get; }
    public Buffer ReadBuffer => Buffers[readIndex];
    public Buffer WriteBuffer => Buffers[1 - readIndex];
    public ShaderResourceView ReadView => ShaderResourceViews[readIndex];
    public ShaderResourceView WriteView => ShaderResourceViews[1 - readIndex];
    public UnorderedAccessView ReadUnorderedView => UnorderedAccessViews[readIndex];
    public UnorderedAccessView WriteUnorderedView => UnorderedAccessViews[1 - readIndex];

    public void Swap()
    {
        readIndex = 1 - readIndex;
    }

    public void Dispose()
    {
        foreach (UnorderedAccessView view in UnorderedAccessViews)
        {
            view.Dispose();
        }

        foreach (ShaderResourceView view in ShaderResourceViews)
        {
            view.Dispose();
        }

        foreach (Buffer buffer in Buffers)
        {
            buffer.Dispose();
        }
    }
}
