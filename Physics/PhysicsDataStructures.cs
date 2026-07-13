using System.Runtime.InteropServices;

namespace Phyxel.Physics;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LatticeParticle
{
    public float PositionX;
    public float PositionY;
    public float VelocityX;
    public float VelocityY;
    public float Mass;
    public uint MaterialId;
    public uint IsActive;
    public float Stress;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LatticeBond
{
    public uint ParticleA;
    public uint ParticleB;
    public float RestLength;
    public float ElasticLimit;
    public float PlasticLimit;
    public float CurrentLength;
    public uint IsActive;
    public float AccumulatedStrain;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GridCell
{
    public uint MaterialId;
    public float Mass;
    public float VelocityX;
    public float VelocityY;
    public float Pressure;
    public uint IsActive;
    public uint LatticeParticleIndex;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MaterialProperties
{
    public uint MaterialId;
    public uint SimulationKind;
    public float Density;
    public float ElasticLimit;
    public float PlasticLimit;
    public float Friction;
    public float Restitution;
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
    public uint SolverIteration;
    public uint ParticleCount;
    public uint BondCount;
    public uint StressView;
    public uint Reserved0;
    public float InverseWidth;
    public float InverseHeight;
    public float MaximumVelocity;
    public float Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SimulationStatistics
{
    public uint ActiveParticles;
    public uint ActiveBonds;
    public uint ActiveCells;
    public uint FrameIndex;
}
