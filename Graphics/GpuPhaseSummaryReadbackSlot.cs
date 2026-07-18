using System;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Graphics;

public sealed class GpuPhaseSummaryReadbackSlot : IDisposable
{
    public required Buffer Staging { get; init; }
    public required Query Query { get; init; }
    public bool Pending { get; set; }
    public ulong Generation { get; set; }

    public void Dispose()
    {
        Query.Dispose();
        Staging.Dispose();
    }
}
