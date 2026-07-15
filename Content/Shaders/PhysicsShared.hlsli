struct GridCell
{
    uint MaterialId;
    float Mass;
    float VelocityX;
    float VelocityY;
    float Pressure;
    uint IsActive;
    uint BodyId;
    uint RestFrames;
};

struct MaterialProperties
{
    uint MaterialId;
    uint SimulationKind;
    float Density;
    float Friction;
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
    uint ActiveCells;
    uint RestingCells;
    uint MovingCells;
    uint SolidCells;
    uint FrameIndex;
    uint WaterCells;
    uint SandCells;
    uint GasCells;
    uint PressureMoves;
    uint MovingSolidCells;
    uint FarColumnMoves;
    uint PressurePlans;
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
    uint SimulationPhase;
    uint DispatchOffsetX;
    uint DispatchOffsetY;
    float MaximumVelocity;
    uint SolidGravity;
    uint SolidPass;
    uint DispatchExtentX;
    uint DispatchExtentY;
    uint HydraulicPressure;
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

bool IsCellularMaterial(uint simulationKind)
{
    return simulationKind == 1 || simulationKind == 4 || simulationKind == 5;
}

bool IsFluidMaterial(uint simulationKind)
{
    return simulationKind == 4 || simulationKind == 5;
}

bool IsFallingSolid(uint materialId)
{
    return materialId == 3 || materialId == 4;
}

GridCell CreateEmptyCell()
{
    return (GridCell)0;
}
