using System.Runtime.InteropServices;

namespace Phyxel.Physics;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GridCell
{
    public uint MaterialIndex;
    public float Mass;
    public float VelocityX;
    public float VelocityY;
    // Hydraulic pressure; fixed thermal devices use this as their setpoint.
    // Negative PM damage in historical movable-solid saves is retired by PB.
    public float Pressure;
    public uint IsActive;
    public uint BodyId;
    public uint RestFrames;
    public float Temperature;
    public float Lifetime;
    // Mass is dry material; water and its boiling/excess heat reservoir travel
    // with the cell. The reservoir is released when drying has an outlet.
    public float MoistureMass;
    public float MoistureEnergy;
    public float FuelMass; // Retained non-water liquid; legacy field name.
    public uint RetainedLiquidMaterialIndex; // 0: historical configured liquid; otherwise explicit species.

    // Tagged by PhaseEnthalpy/FusionEnthalpy; liquid freezing is negative.
    // World v11 preserves this progress without increasing the cell stride.
    public float PhaseProgress { readonly get => Lifetime; set => Lifetime = value; }
    // Fixed thermal devices tag unused solid pressure/lifetime slots.
    public float DeviceTargetTemperature { readonly get => Pressure; set => Pressure = value; }
    public float DeviceMaximumPower { readonly get => Lifetime; set => Lifetime = value; }
}

// Per-body GPU scratch; reconstructed from world cells, never serialized.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BodyBalanceData
{
    public uint WeightLo, WeightHi, XLo, XHi, YLo, YHi;
    public uint LiftLo, LiftHi, LiftXLo, LiftXHi;
    public uint LeftSupport, RightSupport, SupportY, Contacts;
    public uint FirstCell;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BodyRotationPlan
{
    public int OldAngle, NewAngle, OldX, OldY, NewX, NewY, Direction;
    public uint Padding;
}

/// <summary>
/// Одна клетка грубого поля воздуха. <c>Blocked</c> объявлен float, а не bool
/// или uint, чтобы структура оставалась 16 байт с естественным выравниванием
/// одинаково в HLSL и C#. Зеркало <c>AirCell</c> из PhysicsShared.hlsli —
/// менять только вместе с ним.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AirCell
{
    public float Pressure;
    public float VelocityX;
    public float VelocityY;
    public float Blocked;
}

/// <summary>
/// Per-particle motion state for the deterministic FIRE/SMKE carrier.
/// This deliberately lives outside <see cref="GridCell"/>: saved worlds retain
/// water/solid velocity fields remain unchanged; motion lives separately.
/// </summary>
public struct GasMotionState
{
    public float VelocityX;
    public float VelocityY;
    public float OffsetX;
    public float OffsetY;
}

/// <summary>
/// Fixed-point impulse written for every successful gas-cell step and consumed
/// by the coarse air solver on its following tick. One unit is an AirDrag of
/// 0.04; keeping it integral permits atomic writes from the cellular shader.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GasAirImpulse
{
    public int X;
    public int Y;
}

/// <summary>
/// Diagnostic-only counters for the obstacle fallback ladder during one fixed
/// gas tick. This buffer is not world state and is cleared before each tick.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GasObstacleBypassStatistics
{
    public uint Blocked;
    public uint XOnly;
    public uint YOnly;
    public uint Diagonal;
    public uint Stayed;
}

/// <summary>
/// Acceptance-only cumulative counters for vertical FIRE transport. They are
/// reset with the world and never feed back into the simulation.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GasVerticalMotionStatistics
{
    public uint FireCellFrames;
    public int FireVelocityYMillisteps;
    public uint FireOffsetYClampFrames;
    public uint FireUpwardCandidates;
    public uint FireUpwardSteps;
    public uint FireUpwardBlockedByGas;
    public uint FireUpwardBlockedCellFrames;
    public GasPipeBandMotionStatistics PipeLow;
    public GasPipeBandMotionStatistics PipeMid;
    public GasPipeBandMotionStatistics PipeHigh;
}

/// <summary>Diagnostic-only aggregate for one horizontal band of the metal chimney.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GasPipeBandMotionStatistics
{
    public uint GasCellFrames;
    public int GasVelocityYMillisteps;
    public uint GasOffsetYClampFrames;
    public uint GasUpwardSteps;
}

/// <summary>
/// Cumulative movement counters for the steam diagnostic. They reset with the
/// world, are written after a successful gas move only, and never participate
/// in a simulation decision. A "no Y" (or "no X") event is a successful move
/// whose displacement on that axis is zero.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamGasStepStatistics
{
    public uint UpwardSteps;
    public uint DownwardSteps;
    public uint NoYSteps;
    public uint LeftSteps;
    public uint RightSteps;
    public uint NoXSteps;
}

/// <summary>
/// Cumulative, read-only steam_jet lateral-motion counters for one 20-cell
/// band above the source. Rejected attempts are intents that survived their
/// horizontal pair phase while their destination was occupied.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetLateralBandStatistics
{
    public uint LeftSteps;
    public uint RightSteps;
    public uint RejectedLeft;
    public uint RejectedRight;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetBlockingSubstepStatistics
{
    public uint Rejected;
    public uint Successful;
    public uint FreedNextSubstep;
    public uint SameMaterial;
    public uint OtherMaterial;
    public uint Solid;
}

/// <summary>
/// Cumulative cell-frame counters for the two steam_jet diagnostic bands.
/// This is observer output only, never simulation state.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetBlockingFrameStatistics
{
    public uint SteamCellFrames;
    public uint StalledWithWholeOffset;
}

/// <summary>
/// One rejected horizontal intent, retained until the next gas substep checks
/// whether its target became empty. There are two marker slots per fine cell.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetBlockingMarker
{
    public uint TargetIndexPlusOne;
    public uint StatisticsSlotPlusOne;
    public uint OriginFrame;
    public uint OriginSubstep;
}

/// <summary>
/// One-frame, read-only comparison of the axial and diagonal destinations of
/// WTRV cells whose accumulated motion is ready to consume both axes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetDiagonalIntentStatistics
{
    public uint SteamCells;
    public uint DiagonalIntents;
    public uint ValidIntentTargets;
    public uint LateralTargetOccupied;
    public uint DiagonalTargetOccupied;
    public uint LeftOccupied;
    public uint RightOccupied;
    public uint UpOccupied;
    public uint DownOccupied;
    public uint UpLeftOccupied;
    public uint UpRightOccupied;
    public uint DownLeftOccupied;
    public uint DownRightOccupied;
}

/// <summary>
/// Cumulative number of WTRV cells actually created by the held-brush source
/// in steam_jet. This is an observer result, not simulation state.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetInjectionStatistics
{
    public uint CreatedSteamCells;
}

/// <summary>
/// Per-frame spatial distribution of cells created by the steam_jet brush.
/// This is observer data only; no solver pass reads it.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetInjectionDistributionFrame
{
    public uint CreatedSteamCells;
    public int SumOffsetX;
    public int SumOffsetXSquared;
    public uint Ring0;
    public uint Ring1;
    public uint Ring2;
    public uint Ring3;
    public uint Ring4;
    public uint Ring5OrMore;
}

/// <summary>
/// Read-only, pre-integration decomposition of a steam cell's vertical gas
/// motion. It is allocated only when PHYXEL_STEAM_JET_AIR_COUPLING_TRACE=1
/// and is never read by a simulation pass.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetGasMotionContribution
{
    public float PreviousVelocityY;
    public float RetainedVelocityY;
    public float AirAdvectionY;
    public float BuoyancyY;
    public float DiffusionY;
    public float UnclampedVelocityY;
    public float IntegratedVelocityY;
    public uint Flags;
}

/// <summary>
/// Read-only copy of one coarse Air cell immediately before CSInject consumes
/// its pending gas impulse. SteamMask identifies the fine cells occupied by
/// steam at that exact sampling point; it is not a simulation input.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SteamJetAirCouplingCell
{
    public int ImpulseX;
    public int ImpulseY;
    public float AirLossProduct;
    public uint GasCellCount;
    public uint SteamMask;
    public uint SteamCellCount;
}

/// <summary>
/// Layout of the diagnostic-only lateral gas transport buffer. Each path has
/// fourteen uint counters; this is not persisted simulation state.
/// </summary>
public static class GasLateralTransferStatisticsLayout
{
    public const int PathCount = 6;
    public const int FieldsPerPath = 14;
    public const int Count = PathCount * FieldsPerPath;
}

/// <summary>
/// Клетка накопительного светового поля огня. Зеркало <c>FireGlowCell</c>
/// из PhysicsShared.hlsli — менять только вместе с ним.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FireGlowCell
{
    public float Red;
    public float Green;
    public float Blue;
    // Separate SMKE coverage.  The field remains 16 bytes and mirrors the
    // HLSL FireGlowCell exactly; smoke is alpha-blended rather than added as
    // white fire light.
    public float Smoke;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FireGlowConstants
{
    public uint FireGlowWidth;
    public uint FireGlowHeight;
    public uint FireGlowGridWidth;
    public uint FireGlowGridHeight;
    public float FireGlowDeposit;
    public float FireGlowDecay;
    public float FireGlowEmberStrength;
    public uint FireGlowReserved0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AirSimulationConstants
{
    public uint AirWidth;
    public uint AirHeight;
    public uint AirGridWidth;
    public uint AirGridHeight;
    public float AirAmbientTemperature;
    public float AirHotScale;
    public uint AirTickIndex;
    public uint AirSandboxMode;
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
    // Mirrors The Powder Toy's per-element HotAir source. It is expressed in
    // the element's native units; AirSimulation applies the TPT factor of 4.
    public float HotAir;
    public float MotionAdvection;
    public float MotionAirDrag;
    public float MotionAirLoss;
    public float MotionLoss;
    public float MotionCollision;
    public float MotionReserved0;
    // Uses an existing slot in the material prefix.
    public float ReactionPressurePerMass { readonly get => MotionReserved0; set => MotionReserved0 = value; }
    public float MotionReserved1;
    public float ReactionFlameLifetimeMultiplier { readonly get => MotionReserved1; set => MotionReserved1 = value; }
    public float MotionReserved2;
    public float FlameExtinctionTemperature { readonly get => MotionReserved2; set => MotionReserved2 = value; }
    // Render-only density of the soft halo around a gas. It deliberately
    // occupies the first former tail-padding scalar so the GPU table remains
    // a stable prefix slot; no physics pass reads it.
    public float GasHazeStrength;
    public float ThermalDeviceTargetTemperature; // Fixed regulator target; gas oxidizer displacement.
    public float GasOxidizerDisplacement { readonly get => ThermalDeviceTargetTemperature; set => ThermalDeviceTargetTemperature = value; }
    public float ThermalDeviceMaximumPower;
    public uint MoistureLiquidMaterialIndex;
    public uint MoistureDryMaterialIndex;
    public uint MoistureWetMaterialIndex;
    public float MoistureCapacity;
    public float MoistureAbsorptionRate;
    public float MoistureDryingRate;
    public float MoistureReserved0;
    public float MoistureCapillaryRate { readonly get => MoistureReserved0; set => MoistureReserved0 = value; }
    public float MoistureReserved1; // ABI slot: game pressure strength for movable solids.
    public float PressureStrength { readonly get => MoistureReserved1; set => MoistureReserved1 = value; }
    public uint FuelLiquidMaterialIndex;
    public float FuelCapacity;
    public float FuelAbsorptionRate;
    public float FuelSaturatedDensity;
    public float LiquidFlowReferenceTemperature;
    public float LiquidFlowTemperatureSensitivity;
    public float LiquidFlowMinimumMobility;
    public float LiquidFlowMaximumMobility;
    public float ContactIgnitionTemperature;
    // Granular contact sources cannot be regulators. Reuse their unused
    // power slot as an exact index+1; zero retains legacy ANY-liquid rules.
    public uint ContactLiquidRequiredMaterialIndex
    {
        readonly get => ThermalDeviceMaximumPower == 0 ? uint.MaxValue : (uint)ThermalDeviceMaximumPower - 1;
        set => ThermalDeviceMaximumPower = value == uint.MaxValue ? 0 : value + 1;
    }
}

public static class MaterialPropertiesLayout
{
    // Contact ignition is separate from automatic ignition; GridCell is unchanged.
    public const int FieldCount = 61;
    public const int ByteSize = 244;
}

public enum BrushCommandMode : uint
{
    Material = 0,
    Erase = 1,
    SetTemperature = 2,
    ThermalDevice = 3,
    Filter = 4
}

public enum BrushCommandShape : uint
{
    Point = 0,
    Segment = 1
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
    public uint Reserved; // ThermalDevice: IEEE power bits; Filter: permeability rule; otherwise BodyId.
    public float TargetTemperature;
    public int EndX;
    public int EndY;
    public BrushCommandShape Shape;
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

    /// <summary>
    /// Диагностический режим отрисовки: 0 — сцена, 1 — поле воздуха.
    /// Три следующих поля добиты для выравнивания: константный буфер D3D11
    /// обязан быть кратен 16 байтам, иначе он просто не создастся.
    /// Зеркало cbuffer в PhysicsShared.hlsli — менять только вместе с ним.
    /// </summary>
    public uint DebugView;

    /// <summary>
    /// Открытые границы: всё, что доходит до левого, правого или верхнего края,
    /// исчезает, как в The Powder Toy. Пол остаётся сплошным. В acceptance-тестах
    /// выключено — их сцены опираются на замкнутый мир.
    /// </summary>
    public uint OpenBoundaries;

    /// <summary>
    /// Номер подшага движения газа. Обязан входить в seed случайности: без него
    /// все проходы за кадр получают одно и то же число, и восемь независимых
    /// попыток вырождаются в одну, повторённую восемь раз, — при том что
    /// вероятность уже поделена на восемь. Газ двигался в восемь раз медленнее.
    /// </summary>
    public uint GasSubStep;
    // Ordinary-gas physical tick. Reuses reserved space without changing layout.
    public uint DebugReserved2;
    // Tagged alias used only by the gas-motion kernels; no cbuffer expansion.
    public uint GasFiniteAirGeometry { readonly get => SolidPass; set => SolidPass = value; }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ThermalSimulationConstants
{
    public float DeltaTime;
    public float ExchangeRate;
    public uint Width;
    public uint Height;
    public uint ObserveEnergy;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ThermalEnergyLedgerCell
{
    public float DeviceHeat;
    public float AmbientHeat;
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
public struct OxidizerConstants
{
    public uint Width;
    public uint Height;
    public uint OpenEdges;
    public float DeltaTime;
    public uint UseAir;
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
    public uint FiniteOxidizer;
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
    // Former padding; runtime reaction budget, not SI stoichiometry.
    public float OxidizerPerMass;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EmissionRequest
{
    public uint DestinationIndex;
    public uint MaterialIndex;
    public float Mass;
    public float Temperature;
    public uint SourceIndex; // High bit marks flame emitted by self-oxidizing fuel.
    public float FlameLifetimeMultiplier;
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
    public float FuelFraction;
    public uint RetainedLiquidMaterialIndex;
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
    public uint FreeBodyCells;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WaterPressureRouteData
{
    public uint Route;
    public uint SourceIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AirThermalConstants
{
    public uint Width, Height, GridWidth, GridHeight;
    public float Ambient, Capacity, Exchange;
    public uint OpenBoundaries;
}
