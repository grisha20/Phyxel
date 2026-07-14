using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Graphics;

public sealed class GpuStructuredBuffer<T> : IDisposable where T : struct
{
    public GpuStructuredBuffer(Device device, int count)
    {
        int stride = Marshal.SizeOf<T>();
        Buffer = new Buffer(device, new BufferDescription
        {
            SizeInBytes = checked(stride * count),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = stride
        });
        View = new ShaderResourceView(device, Buffer);
        UnorderedView = new UnorderedAccessView(device, Buffer);
    }

    public Buffer Buffer { get; }
    public ShaderResourceView View { get; }
    public UnorderedAccessView UnorderedView { get; }

    public void Dispose()
    {
        UnorderedView.Dispose();
        View.Dispose();
        Buffer.Dispose();
    }
}
