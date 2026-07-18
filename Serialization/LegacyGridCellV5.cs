using System.Runtime.InteropServices;

namespace Phyxel.Serialization;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LegacyGridCellV5
{
    public uint MaterialIndex;
    public float Mass;
    public float VelocityX;
    public float VelocityY;
    public float Pressure;
    public uint IsActive;
    public uint BodyId;
    public uint RestFrames;
    public float Temperature;
}
