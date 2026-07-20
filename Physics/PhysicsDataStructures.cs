using System.Runtime.InteropServices;

namespace Phyxel.Physics;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GridCell
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
    public float Lifetime;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MaterialProperties
{
    public uint Flags;
    public uint SimulationKind;
    public float Density;
    public float Friction;
    public float FlowRate;
    public float ColorR;
    public float ColorG;
    public float ColorB;
    public float ColorA;
    public float InitialTemperature;
    public float ThermalConductivity;
    public float HeatCapacity;
    public float TransitionBelowTemperature;
    public uint TransitionBelowMaterialIndex;
    public float TransitionAboveTemperature;
    public uint TransitionAboveMaterialIndex;
    public float IgnitionTemperature;
    public float BurnRate;
    public float HeatPerMass;
    public uint BurnedIntoMaterialIndex;
    public float FlameSpreadRate;
    public float MinimumLifetime;
    public float MaximumLifetime;
    public uint DecayIntoMaterialIndex;
    public float MaximumCombustionTemperature;
    public float TransitionAboveLatentHeat;
    public float AmbientTemperature;
    public float AmbientCoolingRate;
    public uint ContactLiquidIntoMaterialIndex;
    public float ContactLiquidRatePerSecond;
    public float GasDiffusion;
    public float GasBuoyancy;
}

public enum BrushCommandMode : uint
{
    Material = 0,
    Erase = 1,
    SetTemperature = 2
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BrushDrawCommand
{
    public int X;
    public int Y;
    public uint MaterialIndex;
    public float Radius;
    public float Density;
    public BrushCommandMode Mode;
    public uint Seed;
    public uint Reserved;
    public float TargetTemperature;
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
    public uint DispatchExtentX;
    public uint DispatchExtentY;
    public uint HydraulicPressure;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ThermalSimulationConstants
{
    public float DeltaTime;
    public float ExchangeRate;
    public uint Width;
    public uint Height;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ContactTransitionConstants
{
    public float DeltaTime;
    public uint Width;
    public uint Height;
    public uint TickIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PhaseTransitionConstants
{
    public uint Width;
    public uint Height;
    public uint MaterialCount;
    public uint TickIndex;
    public uint TickCount;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CombustionConstants
{
    public float DeltaTime;
    public uint Width;
    public uint Height;
    public uint MaterialCount;
    public uint TickIndex;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MaterialEmissionProperties
{
    public uint SmokeIntoMaterialIndex;
    public float SmokeRate;
    public uint GasIntoMaterialIndex;
    public float GasRate;
    public uint FlameIntoMaterialIndex;
    public float FlameRate;
    public uint Reserved0;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EmissionRequest
{
    public uint DestinationIndex;
    public uint MaterialIndex;
    public float Mass;
    public float Temperature;
    public uint SourceIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EmissionConstants
{
    public uint Width;
    public uint Height;
    public uint MaterialCount;
    public uint RequestCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TemperatureProbeConstants
{
    public uint X;
    public uint Y;
    public uint Width;
    public uint Height;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TemperatureProbeResult
{
    public uint IsActive;
    public uint MaterialIndex;
    public float Temperature;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SimulationStatistics
{
    public uint ActiveCells;
    public uint RestingCells;
    public uint MovingCells;
    public uint SolidCells;
    public uint FrameIndex;
    public uint LiquidCells;
    public uint GranularCells;
    public uint GasCells;
    public uint PressureMoves;
    public uint MovingSolidCells;
    public uint FarColumnMoves;
    public uint PressurePlans;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WaterPressureRouteData
{
    public uint Route;
    public uint SourceIndex;
}
