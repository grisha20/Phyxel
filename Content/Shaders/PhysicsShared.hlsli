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
    float Lifetime;
};

// One cell of the coarse air field. Blocked is a float rather than a bool or a
// uint so the struct stays 16 bytes with natural alignment in both HLSL and C#.
struct AirCell
{
    float Pressure;
    float VelocityX;
    float VelocityY;
    float Blocked;
};

// Mirrors Physics.GasMotionState.  This is intentionally a separate GPU
// buffer, not an addition to GridCell: worlds are serialized with a fixed
// 40-byte GridCell layout and the water solvers own GridCell.VelocityX/Y.
struct GasMotionState
{
    float VelocityX;
    float VelocityY;
    float OffsetX;
    float OffsetY;
};

// Fixed-point, per-air-cell momentum produced by actual gas-cell steps during
// the previous gas tick. Values are pre-weighted by the moving material's
// motion.airDrag and use the fixed-point scale declared by the gas/air passes.
struct GasAirImpulse
{
    int X;
    int Y;
};

// Diagnostic-only observer records. Neither struct is bound by a physical
// simulation pass; they deliberately stay outside GridCell and materials.
struct SteamJetGasMotionContribution
{
    float PreviousVelocityY;
    float RetainedVelocityY;
    float AirAdvectionY;
    float BuoyancyY;
    float DiffusionY;
    float UnclampedVelocityY;
    float IntegratedVelocityY;
    uint Flags;
};

struct SteamJetAirCouplingCell
{
    int ImpulseX;
    int ImpulseY;
    float AirLossProduct;
    uint GasCellCount;
    uint SteamMask;
    uint SteamCellCount;
};

struct GasPipeBandMotionStatistics
{
    uint GasCellFrames;
    int GasVelocityYMillisteps;
    uint GasOffsetYClampFrames;
    uint GasUpwardSteps;
};

// Mirrors Physics.GasVerticalMotionStatistics. This is diagnostic-only state:
// it is reset with the world and is not read by any simulation decision.
struct GasVerticalMotionStatistics
{
    uint FireCellFrames;
    int FireVelocityYMillisteps;
    uint FireOffsetYClampFrames;
    uint FireUpwardCandidates;
    uint FireUpwardSteps;
    uint FireUpwardBlockedByGas;
    uint FireUpwardBlockedCellFrames;
    GasPipeBandMotionStatistics PipeLow;
    GasPipeBandMotionStatistics PipeMid;
    GasPipeBandMotionStatistics PipeHigh;
};

// One cell of the persistent fire light field, on the same coarse grid as the
// air. Kept at 16 bytes for the same alignment reasons as AirCell.
struct FireGlowCell
{
    float Red;
    float Green;
    float Blue;
    float Smoke;
};

// A gas cell can only ever step one cell per pass, but a Powder Toy particle
// with Advection 0.9 in a draught of five to ten travels four to nine cells in
// a single frame. Capping fire at one cell per frame meant new flame arrived at
// the source faster than old flame could leave, so a point source grew into a
// ball instead of streaming away as a narrow column. Running the gas passes
// several times per frame, each with the probability divided by the same
// number, restores the correct average speed and raises the ceiling.
static const uint GasMotionSubSteps = 8;

// Width in simulation cells of one air cell. The Powder Toy uses CELL = 4 for
// its pressure map for the same reason: a per-pixel pressure solve is both
// needlessly fine and far too slow.
static const uint AirCellSize = 4;

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
    float TransitionBelowTemperature;
    uint TransitionBelowMaterialIndex;
    float TransitionAboveTemperature;
    uint TransitionAboveMaterialIndex;
    float IgnitionTemperature;
    float BurnRate;
    float HeatPerMass;
    uint BurnedIntoMaterialIndex;
    float FlameSpreadRate;
    float MinimumLifetime;
    float MaximumLifetime;
    uint DecayIntoMaterialIndex;
    float MaximumCombustionTemperature;
    float TransitionAboveLatentHeat;
    float AmbientTemperature;
    float AmbientCoolingRate;
    uint ContactLiquidIntoMaterialIndex;
    float ContactLiquidRatePerSecond;
    float GasDiffusion;
    float GasBuoyancy;
    float MotionAdvection;
    float MotionAirDrag;
    float MotionAirLoss;
    float MotionLoss;
    float MotionCollision;
    float MotionReserved0;
    float MotionReserved1;
    float MotionReserved2;
};

// Mirrors Physics.MaterialPropertiesLayout.ByteSize. Forty scalar fields
// produce a 160-byte structured-buffer stride (a multiple of sixteen).
static const uint MaterialPropertiesByteSize = 160;

struct MaterialEmissionProperties
{
    uint SmokeIntoMaterialIndex;
    float SmokeRate;
    uint GasIntoMaterialIndex;
    float GasRate;
    uint FlameIntoMaterialIndex;
    float FlameRate;
    uint Reserved0;
    uint Reserved1;
};

struct EmissionRequest
{
    uint DestinationIndex;
    uint MaterialIndex;
    float Mass;
    float Temperature;
    uint SourceIndex;
};

static const uint SimulationKindNone = 0;
static const uint SimulationKindGranular = 1;
static const uint SimulationKindSolid = 2;
static const uint SimulationKindTool = 3;
static const uint SimulationKindLiquid = 4;
static const uint SimulationKindGas = 5;
static const uint MaterialFlagMovableSolid = 1u << 0;
static const uint MaterialFlagFlame = 1u << 1;
// Material carries its own oxidiser and burns without contact with air.
// Gunpowder is the reference case: it works inside a sealed cartridge, so a
// grain buried in the middle of a heap must still detonate.
static const uint MaterialFlagSelfOxidizing = 1u << 2;

// Airtight: blocks the coarse air field. Only structural walls carry this.
// The Powder Toy blocks its air map with special walls alone; ordinary solids
// such as METL let the field pass and are collided with by the particles.
static const uint MaterialFlagBlocksAir = 1u << 3;
static const uint MaterialFlagSmoke = 1u << 4;
static const uint PhaseSummaryPhaseOccurred = 1u << 0;
static const uint PhaseSummaryTargetCellular = 1u << 1;
static const uint PhaseSummaryTargetLiquid = 1u << 2;
static const uint PhaseSummaryTargetGas = 1u << 3;
static const uint PhaseSummaryTouchesLiquid = 1u << 4;
static const uint PhaseSummaryTouchesSolid = 1u << 5;
static const uint PhaseSummaryTargetMovableSolid = 1u << 6;
static const float MaximumMaterialDensity = 100.0;
// SMKE density is 0.04 and its material alpha is 0.69, so a fresh smoke cell
// presents only 0.0276 coverage.  A 0.035 threshold made every smoke arc
// disappear before the colour/FireGlow pass could show it.
static const float GasVisibleMassThreshold = 0.001;
static const uint BrushCommandModeMaterial = 0;
static const uint BrushCommandModeErase = 1;
static const uint BrushCommandModeSetTemperature = 2;
static const uint BrushCommandShapePoint = 0;
static const uint BrushCommandShapeSegment = 1;

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
    float TargetTemperature;
    int EndX;
    int EndY;
    uint Shape;
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
    uint DebugView;
    uint OpenBoundaries;

    // Номер подшага движения газа. Обязан входить в seed: без него все проходы
    // за кадр получают одно случайное число, и восемь попыток вырождаются в
    // одну, повторённую восемь раз с вероятностью, уже делённой на восемь.
    uint GasSubStep;
    uint DebugReserved2;
};

static const uint DebugViewNone = 0;
static const uint DebugViewAir = 1;

// Width of the strip that swallows anything reaching it. The Powder Toy kills
// every particle within CELL of the left, right and top edges; the floor is
// left solid here so a scene still has something to rest on.
static const uint OpenBoundaryMargin = 2;

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

float InitialMaterialLifetime(MaterialProperties material, uint seed)
{
    if (material.MaximumLifetime <= 0)
    {
        return 0;
    }
    return lerp(
        max(0, material.MinimumLifetime),
        max(material.MinimumLifetime, material.MaximumLifetime),
        HashUnitFloat(seed));
}

float ValidatedMaterialDensity(MaterialProperties material)
{
    return clamp(material.Density, 0.0, MaximumMaterialDensity);
}

GridCell CreateEmptyCell()
{
    return (GridCell)0;
}
