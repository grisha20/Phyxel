struct GridCell
{
    uint MaterialId;
    float Mass;
    float VelocityX;
    float VelocityY;
    float Pressure;
    uint IsActive;
    uint LatticeParticleIndex;
    uint Reserved;
};

struct LatticeParticle
{
    float PositionX;
    float PositionY;
    float VelocityX;
    float VelocityY;
    float Mass;
    uint MaterialId;
    uint IsActive;
    float Stress;
};

struct LatticeBond
{
    uint ParticleA;
    uint ParticleB;
    float RestLength;
    float ElasticLimit;
    float PlasticLimit;
    float CurrentLength;
    uint IsActive;
    float AccumulatedStrain;
};

struct MaterialProperties
{
    uint MaterialId;
    uint SimulationKind;
    float Density;
    float ElasticLimit;
    float PlasticLimit;
    float Friction;
    float Restitution;
    float FlowRate;
};

struct BrushDrawCommand
{
    int X;
    int Y;
    uint MaterialId;
    float Radius;
    float Density;
    uint Mode;
    uint Seed;
    uint Reserved;
};

struct SimulationStatistics
{
    uint ActiveParticles;
    uint ActiveBonds;
    uint ActiveCells;
    uint FrameIndex;
};

cbuffer SimulationFrameConstants : register(b0)
{
    float DeltaTime;
    float Gravity;
    uint Width;
    uint Height;
    uint FrameIndex;
    uint CommandCount;
    uint MaximumBrushDiameter;
    uint SolverIteration;
    uint ParticleCount;
    uint BondCount;
    uint StressView;
    uint Reserved0;
    float InverseWidth;
    float InverseHeight;
    float MaximumVelocity;
    float Reserved1;
};

uint FlattenCoordinate(uint2 coordinate)
{
    return coordinate.y * Width + coordinate.x;
}

uint HashValue(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352d;
    value ^= value >> 15;
    value *= 0x846ca68b;
    value ^= value >> 16;
    return value;
}

float HashUnitFloat(uint value)
{
    return (HashValue(value) & 0x00ffffff) / 16777215.0;
}
