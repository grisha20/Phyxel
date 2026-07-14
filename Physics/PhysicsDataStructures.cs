using System.Runtime.InteropServices;

namespace Phyxel.Physics;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GridCell
{
    public uint MaterialId;
    public float Mass;
    public float VelocityX;
    public float VelocityY;
    public float Pressure;
    public uint IsActive;
    public uint BodyId;
    public uint RestFrames;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MaterialProperties
{
    public uint MaterialId;
    public uint SimulationKind;
    public float Density;
    public float Friction;
    public float FlowRate;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BrushDrawCommand
{
    public int X;
    public int Y;
    public uint MaterialId;
    public float Radius;
    public float Density;
    public uint Mode;
    public uint Seed;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SimulationFrameConstants
{
    public float DeltaTime;
    public float Gravity;
    public uint Width;
    public uint Height;
    public uint FrameIndex;
    public uint CommandCount;
    public uint MaximumBrushDiameter;
    public uint SimulationPhase;
    public uint DispatchOffsetX;
    public uint DispatchOffsetY;
    public float MaximumVelocity;
    public uint SolidGravity;
    public uint SolidPass;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SimulationStatistics
{
    public uint ActiveCells;
    public uint RestingCells;
    public uint MovingCells;
    public uint SolidCells;
    public uint FrameIndex;
    public uint WaterCells;
    public uint SandCells;
    public uint GasCells;
}
