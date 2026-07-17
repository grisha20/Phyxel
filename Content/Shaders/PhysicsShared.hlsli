struct GridCell
{
    uint MaterialIndex;
    float Mass;
    float VelocityX;
    float VelocityY;
    float Pressure;
    uint IsActive;
    uint BodyId;
    uint RestFrames;
    float Temperature;
};

struct MaterialProperties
{
    uint Flags;
    uint SimulationKind;
    float Density;
    float Friction;
    float FlowRate;
    float ColorR;
    float ColorG;
    float ColorB;
    float ColorA;
    float InitialTemperature;
    float ThermalConductivity;
    float HeatCapacity;
};

static const uint SimulationKindNone = 0;
static const uint SimulationKindGranular = 1;
static const uint SimulationKindSolid = 2;
static const uint SimulationKindTool = 3;
static const uint SimulationKindLiquid = 4;
static const uint SimulationKindGas = 5;
static const uint MaterialFlagMovableSolid = 1u << 0;
static const float MaximumMaterialDensity = 100.0;

struct BrushDrawCommand
{
    int X;
    int Y;
    uint MaterialIndex;
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
    uint LiquidCells;
    uint GranularCells;
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
    return simulationKind == SimulationKindGranular ||
        simulationKind == SimulationKindLiquid ||
        simulationKind == SimulationKindGas;
}

bool IsFluidMaterial(uint simulationKind)
{
    return simulationKind == SimulationKindLiquid || simulationKind == SimulationKindGas;
}

bool IsSolidMaterial(MaterialProperties material)
{
    return material.SimulationKind == SimulationKindSolid;
}

bool IsMovableSolidMaterial(MaterialProperties material)
{
    return IsSolidMaterial(material) && (material.Flags & MaterialFlagMovableSolid) != 0;
}

float ValidatedMaterialDensity(MaterialProperties material)
{
    return clamp(material.Density, 0.0, MaximumMaterialDensity);
}

GridCell CreateEmptyCell()
{
    return (GridCell)0;
}
