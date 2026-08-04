using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public sealed class AcceptanceRegressionHarness
{
    private static readonly ulong[] ThermalContactCheckpointTicks = [20, 40, 60, 80];
    private static readonly ulong[] SteamCoolingCheckpointTicks = [0, 1, 20, 40, 60, 80];
    private static readonly ulong[] CoalCheckpointTicks = [0, 20, 120, 240];
    private static readonly ulong[] GasCheckpointTicks = [120];
    private static readonly ulong[] SteamDistributionCheckpointTicks = [20, 40, 80, 200];
    private static readonly uint[] SteamPuffCheckpointFrames = [1, 30, 60, 90, 120, 150, 300];
    // The jet can reach a short diagnostic world's ceiling before frame 600.
    // Keep a dense early history so front speed is fitted only from unclipped
    // observations rather than from a boundary-pinned final snapshot.
    private static readonly uint[] SteamJetCheckpointFrames = [60, 90, 120, 150, 200, 250, 300];
    private const ulong SteamDistributionFinalTick = 400;
    private static readonly ulong[] SteamCloudCheckpointTicks =
        [0, 20, 40, 80, 200, 400, 800, 1200, 1300, 1400, 1500];
    private const ulong SteamCloudFinalTick = 1500;
    private MaterialRegistry? materialRegistry;
    private readonly List<ThermalAcceptanceCheckpoint> thermalCheckpoints = [];
    private readonly TemperatureProbeAcceptanceTrace temperatureProbeTrace = new();
    private readonly AirPressureTimeSeries airPressureTrace = new();
    private readonly GasObstacleBypassTrace gasObstacleBypassTrace = new();
    private readonly GasLateralTransferTrace gasLateralTransferTrace = new();
    private readonly GasVerticalMotionTrace gasVerticalMotionTrace = new();
    private readonly SteamGasStepTrace steamGasStepTrace = new();
    private readonly SteamJetLateralTrace steamJetLateralTrace = new();
    private readonly SteamJetInjectionTrace steamJetInjectionTrace = new();
    private readonly SteamJetAirCouplingTrace steamJetAirCouplingTrace = new();
    private readonly PhaseAcceptanceController phaseAcceptance;
    private readonly uint scenarioSeed;

    public AcceptanceRegressionHarness()
    {
        string? requested = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_MODE") ??
            Environment.GetEnvironmentVariable("PHYXEL_SPEC_SCENARIO");
        Mode = requested?.Trim().ToLowerInvariant() switch
        {
            "bowl" or "acceptance_bowl" => AcceptanceScenarioMode.Bowl,
            "solid_gravity" or "acceptance_solid_gravity" => AcceptanceScenarioMode.SolidGravity,
            "sand" or "acceptance_sand" => AcceptanceScenarioMode.Sand,
            "hydro" or "acceptance_hydro" => AcceptanceScenarioMode.Hydro,
            "slope" or "acceptance_slope" => AcceptanceScenarioMode.Slope,
            "gas" or "acceptance_gas" => AcceptanceScenarioMode.Gas,
            "water_stress" or "stress_water" => AcceptanceScenarioMode.WaterStress,
            "flat_surface" or "surface" => AcceptanceScenarioMode.FlatSurface,
            "water_drain" or "drain" => AcceptanceScenarioMode.WaterDrain,
            "communicating_vessels" or "vessels" or "hydrostatic" => AcceptanceScenarioMode.CommunicatingVessels,
            "pressure_tube" or "tube" => AcceptanceScenarioMode.PressureTube,
            "saved_pressure" => AcceptanceScenarioMode.SavedPressure,
            "saved_isolation" or "isolation" => AcceptanceScenarioMode.SavedIsolation,
            "saved_gravity" => AcceptanceScenarioMode.SavedGravity,
            "buoyancy" or "float" => AcceptanceScenarioMode.Buoyancy,
            "saved_sand_water" or "sand_basin" => AcceptanceScenarioMode.SavedSandWater,
            "external_granular" => AcceptanceScenarioMode.ExternalGranular,
            "external_liquid" => AcceptanceScenarioMode.ExternalLiquid,
            "external_gas" => AcceptanceScenarioMode.ExternalGas,
            "external_solids" => AcceptanceScenarioMode.ExternalSolids,
            "underwater_granular" or "underwater_pile" => AcceptanceScenarioMode.UnderwaterGranularPile,
            "granular_displacement" or "soft_displacement" => AcceptanceScenarioMode.GranularWaterDisplacement,
            "granular_barrier" or "granular_barrier_off" => AcceptanceScenarioMode.GranularBarrier,
            "granular_barrier_hydraulic" or "granular_barrier_on" => AcceptanceScenarioMode.GranularBarrierHydraulic,
            "temperature_brush" => AcceptanceScenarioMode.TemperatureBrush,
            "temperature_tool" => AcceptanceScenarioMode.TemperatureTool,
            "thermal_uniform" => AcceptanceScenarioMode.ThermalUniform,
            "thermal_contact" => AcceptanceScenarioMode.ThermalContact,
            "thermal_capacity" => AcceptanceScenarioMode.ThermalCapacity,
            "conductivity_compare" or "thermal_conductivity_compare" =>
                AcceptanceScenarioMode.ThermalConductivityCompare,
            "thermal_fast" => AcceptanceScenarioMode.ThermalFast,
            "thermal_slow" => AcceptanceScenarioMode.ThermalSlow,
            "thermal_insulator" => AcceptanceScenarioMode.ThermalInsulator,
            "thermal_vacuum" => AcceptanceScenarioMode.ThermalVacuum,
            "thermal_gas" => AcceptanceScenarioMode.ThermalGas,
            "temperature_probe_gpu" or "thermal_probe" => AcceptanceScenarioMode.TemperatureProbeGpu,
            "phase_dispatch_smoke" => AcceptanceScenarioMode.PhaseDispatchSmoke,
            "phase_thresholds" => AcceptanceScenarioMode.PhaseThresholds,
            "phase_hysteresis" => AcceptanceScenarioMode.PhaseHysteresis,
            "phase_single_transition" => AcceptanceScenarioMode.PhaseSingleTransition,
            "phase_normalization_matrix" => AcceptanceScenarioMode.PhaseNormalizationMatrix,
            "phase_summary_liquid_gas" => AcceptanceScenarioMode.PhaseSummaryLiquidGas,
            "phase_summary_solid_liquid" => AcceptanceScenarioMode.PhaseSummarySolidLiquid,
            "phase_summary_gas_movable" => AcceptanceScenarioMode.PhaseSummaryGasMovable,
            "phase_summary_liquid_fixed" => AcceptanceScenarioMode.PhaseSummaryLiquidFixed,
            "phase_pause_continue" => AcceptanceScenarioMode.PhasePauseContinue,
            "phase_wake_gas" => AcceptanceScenarioMode.PhaseWakeGas,
            "phase_wake_liquid" => AcceptanceScenarioMode.PhaseWakeLiquid,
            "phase_readback_fallback" => AcceptanceScenarioMode.PhaseReadbackFallback,
            "phase_external_reorder" => AcceptanceScenarioMode.PhaseExternalReorder,
            "phase_disabled_registry" => AcceptanceScenarioMode.PhaseDisabledRegistry,
            "phase_energy_contract" => AcceptanceScenarioMode.PhaseEnergyContract,
            "phase_v5_roundtrip" => AcceptanceScenarioMode.PhaseV5RoundTrip,
            "phase_performance_steady" => AcceptanceScenarioMode.PhasePerformanceSteady,
            "phase_performance_burst" => AcceptanceScenarioMode.PhasePerformanceBurst,
            "water_ice_steam" => AcceptanceScenarioMode.WaterIceSteam,
            "water_ice_steam_motion" => AcceptanceScenarioMode.WaterIceSteamMotion,
            "water_ice_steam_pause" => AcceptanceScenarioMode.WaterIceSteamPause,
            "water_ice_steam_v5_roundtrip" => AcceptanceScenarioMode.WaterIceSteamV5RoundTrip,
            "combustion" or "combustion_chain" => AcceptanceScenarioMode.CombustionChain,
            "combustion_quench" or "water_quench" => AcceptanceScenarioMode.CombustionQuench,
            "fire_obstacle" or "fire_plate" => AcceptanceScenarioMode.FireObstacle,
            "fire_open" or "fire_no_plate" => AcceptanceScenarioMode.FireOpen,
            "furnace" or "fire_furnace" => AcceptanceScenarioMode.Furnace,
            "metal_chimney" or "furnace_metal_chimney" => AcceptanceScenarioMode.MetalChimney,
            "steam_puff" => AcceptanceScenarioMode.SteamPuff,
            "steam_jet" => AcceptanceScenarioMode.SteamJet,
            "steam_self_cooling" => AcceptanceScenarioMode.SteamSelfCooling,
            "brush_empty_only" => AcceptanceScenarioMode.BrushEmptyOnly,
            "continuous_brush_stroke" => AcceptanceScenarioMode.ContinuousBrushStroke,
            "coal_types" => AcceptanceScenarioMode.CoalTypes,
            "gas_uniform_distribution" or "gas_smooth_cloud" or
            "gas_visual_profile" or "gas_vertical_speed" =>
                AcceptanceScenarioMode.GasUniformDistribution,
            "steam_distribution_and_cooling" => AcceptanceScenarioMode.SteamDistributionAndCooling,
            "steam_cloud_temperature" or "steam_smooth_cloud" => AcceptanceScenarioMode.SteamCloudTemperature,
            _ => AcceptanceScenarioMode.None
        };
        scenarioSeed = uint.TryParse(Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_RUN_SEED"),
            out uint parsedScenarioSeed)
            ? parsedScenarioSeed
            : 0;
        phaseAcceptance = new PhaseAcceptanceController(Mode);
    }

    public AcceptanceScenarioMode Mode { get; }
    public bool Active => Mode != AcceptanceScenarioMode.None;
    public bool RequiresNativeResolution => Mode == AcceptanceScenarioMode.WaterStress;
    public bool RequiresSavedScene => Mode is
        AcceptanceScenarioMode.SavedPressure or AcceptanceScenarioMode.SavedIsolation or
        AcceptanceScenarioMode.SavedGravity or AcceptanceScenarioMode.SavedSandWater;
    public bool IsPhaseRoundTripSaving => phaseAcceptance.IsRoundTripSaving;
    public bool IsPhaseRoundTripLoading => phaseAcceptance.IsRoundTripLoading;
    public bool InitialWorldStartsDormant => Mode is
        AcceptanceScenarioMode.PhaseHysteresis or
        AcceptanceScenarioMode.PhaseNormalizationMatrix or
        AcceptanceScenarioMode.PhaseSummaryLiquidGas or
        AcceptanceScenarioMode.PhaseSummarySolidLiquid or
        AcceptanceScenarioMode.PhaseSummaryGasMovable or
        AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
        AcceptanceScenarioMode.PhaseReadbackFallback or
        AcceptanceScenarioMode.PhaseV5RoundTrip or
        AcceptanceScenarioMode.WaterIceSteam or
        AcceptanceScenarioMode.WaterIceSteamMotion or
        AcceptanceScenarioMode.WaterIceSteamPause or
        AcceptanceScenarioMode.WaterIceSteamV5RoundTrip;

    public void ConfigureMaterials(MaterialRegistry registry)
    {
        materialRegistry = registry;
    }
    public uint CaptureFrame
    {
        get
        {
            if (uint.TryParse(
                Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_CAPTURE_FRAME"),
                out uint requestedFrame) && requestedFrame > 0)
            {
                return requestedFrame;
            }
            return Mode switch
            {
                AcceptanceScenarioMode.Bowl => 1000,
                AcceptanceScenarioMode.SolidGravity => 360,
                AcceptanceScenarioMode.Sand => 190,
                AcceptanceScenarioMode.Hydro => 1200,
                AcceptanceScenarioMode.Slope => 600,
                AcceptanceScenarioMode.Gas => 900,
                AcceptanceScenarioMode.WaterStress => 180,
                AcceptanceScenarioMode.FlatSurface => 1200,
                AcceptanceScenarioMode.WaterDrain => 1800,
                AcceptanceScenarioMode.CommunicatingVessels => 3600,
                AcceptanceScenarioMode.PressureTube => 1800,
                AcceptanceScenarioMode.SavedPressure => 2000,
                AcceptanceScenarioMode.SavedIsolation => 1200,
                AcceptanceScenarioMode.SavedGravity => 400,
                AcceptanceScenarioMode.Buoyancy => 500,
                AcceptanceScenarioMode.SavedSandWater => 900,
                AcceptanceScenarioMode.ExternalGranular => 190,
                AcceptanceScenarioMode.ExternalLiquid => 600,
                AcceptanceScenarioMode.ExternalGas => 900,
                AcceptanceScenarioMode.ExternalSolids => 300,
                AcceptanceScenarioMode.UnderwaterGranularPile => 720,
                AcceptanceScenarioMode.GranularWaterDisplacement => 13,
                AcceptanceScenarioMode.GranularBarrier => 900,
                AcceptanceScenarioMode.GranularBarrierHydraulic => 900,
                AcceptanceScenarioMode.TemperatureBrush => 3,
                AcceptanceScenarioMode.TemperatureTool => 140,
                AcceptanceScenarioMode.ThermalGas => 120,
                // Four contact checkpoints are at 20/40/60/80 thermal ticks;
                // at 144 FPS the final capture must be after tick 80.
                AcceptanceScenarioMode.ThermalContact => 700,
                AcceptanceScenarioMode.ThermalCapacity => 1300,
                AcceptanceScenarioMode.TemperatureProbeGpu => 240,
                // Brush commands are serialized and the fixture is now dense
                // and deterministic. Capture while combustion is active rather
                // than after all transient flame/smoke has naturally expired.
                AcceptanceScenarioMode.CombustionChain => 900,
                AcceptanceScenarioMode.CombustionQuench => 900,
                AcceptanceScenarioMode.FireObstacle => 360,
                AcceptanceScenarioMode.FireOpen => 360,
                AcceptanceScenarioMode.Furnace => 600,
                AcceptanceScenarioMode.MetalChimney => 600,
                AcceptanceScenarioMode.SteamPuff => 600,
                AcceptanceScenarioMode.SteamJet => 600,
                AcceptanceScenarioMode.SteamSelfCooling => uint.MaxValue,
                AcceptanceScenarioMode.BrushEmptyOnly => 7,
                AcceptanceScenarioMode.ContinuousBrushStroke => 3,
                AcceptanceScenarioMode.CoalTypes => uint.MaxValue,
                AcceptanceScenarioMode.GasUniformDistribution => 600,
                AcceptanceScenarioMode.SteamDistributionAndCooling => uint.MaxValue,
                AcceptanceScenarioMode.SteamCloudTemperature => uint.MaxValue,
                AcceptanceScenarioMode.PhaseDispatchSmoke => 240,
                AcceptanceScenarioMode.ThermalUniform or
                AcceptanceScenarioMode.ThermalConductivityCompare or
                AcceptanceScenarioMode.ThermalFast or
                AcceptanceScenarioMode.ThermalSlow or
                AcceptanceScenarioMode.ThermalInsulator or
                AcceptanceScenarioMode.ThermalVacuum => 300,
                _ => uint.MaxValue
            };
        }
    }

    public IReadOnlyList<BrushDrawCommand> CreateCommands(uint frame)
    {
        return AcceptanceRegressionScenario.CreateCommands(Mode, frame, materialRegistry, scenarioSeed);
    }

    public SimulationWorldSnapshot? CreateInitialWorld(int width, int height) =>
        materialRegistry is null
            ? null
            : ThermalAcceptanceScenario.Create(Mode, width, height, materialRegistry) ??
                BrushEmptyOnlyAcceptanceScenario.CreateInitialWorld(Mode, width, height, materialRegistry) ??
                ContinuousBrushStrokeAcceptanceScenario.CreateInitialWorld(
                    Mode, width, height, materialRegistry) ??
                CoalTypesAcceptanceScenario.CreateInitialWorld(Mode, width, height, materialRegistry) ??
                GasUniformDistributionAcceptanceScenario.CreateInitialWorld(
                    Mode, width, height, materialRegistry) ??
                SteamDistributionAndCoolingAcceptanceScenario.CreateInitialWorld(
                    Mode, width, height, materialRegistry) ??
                SteamCloudTemperatureAcceptanceScenario.CreateInitialWorld(
                    Mode, width, height, materialRegistry);

    public Point? GetProbeCoordinate(uint frame) => Mode switch
    {
        AcceptanceScenarioMode.ThermalUniform => new Point(200, 120),
        AcceptanceScenarioMode.TemperatureTool => new Point(252, 135),
        AcceptanceScenarioMode.TemperatureProbeGpu when frame < 30 => new Point(60, 235),
        AcceptanceScenarioMode.TemperatureProbeGpu when frame < 60 => new Point(120, 235),
        AcceptanceScenarioMode.TemperatureProbeGpu when frame < 120 => new Point(239, 130),
        AcceptanceScenarioMode.TemperatureProbeGpu when frame < 180 => new Point(240, 130),
        AcceptanceScenarioMode.TemperatureProbeGpu when frame < 210 => new Point(350, 50),
        _ => null
    };
    public bool OwnsTemperatureProbe => Mode is
        AcceptanceScenarioMode.ThermalUniform or AcceptanceScenarioMode.TemperatureTool or
        AcceptanceScenarioMode.TemperatureProbeGpu;

    public void ApplyRuntimeControls(
        uint frame,
        SimulationSettings settings,
        SimulationDispatchCoordinator dispatchCoordinator,
        GpuTemperatureProbe temperatureProbe)
    {
        phaseAcceptance.ApplyRuntimeControls(frame, settings, dispatchCoordinator);
        if (Mode == AcceptanceScenarioMode.TemperatureTool)
        {
            if (frame == 130)
            {
                dispatchCoordinator.ClearCurrentWorld(settings);
                temperatureProbe.Reset();
            }
            return;
        }
        if (Mode != AcceptanceScenarioMode.TemperatureProbeGpu)
        {
            return;
        }
        if (frame == 210)
        {
            dispatchCoordinator.ClearCurrentWorld(settings);
            temperatureProbe.Reset();
        }
        else if (frame == 220)
        {
            settings.ApplyScale(0.35f);
            temperatureProbe.Reset();
        }
    }

    public void ObserveTemperatureProbe(uint frame, TemperatureProbeResult? result)
    {
        if (Mode == AcceptanceScenarioMode.TemperatureProbeGpu)
        {
            temperatureProbeTrace.Observe(frame, result);
        }
        else if (Mode == AcceptanceScenarioMode.TemperatureTool)
        {
            temperatureProbeTrace.ObserveTemperatureTool(frame, result);
        }
    }

    public void RecordAirPressureTrace(uint frame, GpuSimulationResources resources)
    {
        int airY = Mode switch
        {
            // Fine y=107 directly below the plate maps to coarse y=26.
            AcceptanceScenarioMode.FireObstacle => 26,
            // Source centre is y=170; coarse y=41 (fine 164..167) is the
            // cell immediately above its brush footprint.
            AcceptanceScenarioMode.FireOpen => 41,
            _ => -1
        };
        if (airY >= 0)
        {
            airPressureTrace.Record(frame, resources, airY);
        }
    }

    public void RecordGasObstacleBypassTrace(uint frame, GpuSimulationResources resources)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_GAS_BYPASS_TRACE") != "1" ||
            Mode is not (AcceptanceScenarioMode.FireObstacle or AcceptanceScenarioMode.FireOpen))
        {
            return;
        }
        gasObstacleBypassTrace.Record(frame, resources);
    }

    public void RecordGasLateralTransferTrace(uint frame, GpuSimulationResources resources)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_GAS_LATERAL_TRACE") != "1" ||
            Mode is not (AcceptanceScenarioMode.FireObstacle or AcceptanceScenarioMode.FireOpen))
        {
            return;
        }
        gasLateralTransferTrace.Record(frame, resources);
    }

    public void RecordGasVerticalMotionTrace(GpuSimulationResources resources)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_GAS_VERTICAL_TRACE") != "1" ||
            Mode is not (AcceptanceScenarioMode.FireOpen or AcceptanceScenarioMode.MetalChimney))
        {
            return;
        }
        gasVerticalMotionTrace.Record(resources);
    }

    public void RecordSteamGasStepTrace(uint frame, GpuSimulationResources resources)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_STEAM_GAS_STEP_TRACE") != "1" ||
            Mode is not (AcceptanceScenarioMode.SteamPuff or AcceptanceScenarioMode.SteamJet) ||
            frame is not (60 or 120 or 300 or 599))
        {
            return;
        }
        // The final acceptance world is captured after frame 599 and reported
        // as checkpoint 600, matching the existing steam-puff convention.
        steamGasStepTrace.Record(frame == 599 ? 600u : frame, resources);
    }

    public void RecordSteamJetInjectionTrace(uint frame, GpuSimulationResources resources)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_INJECTION_TRACE") != "1" ||
            Mode != AcceptanceScenarioMode.SteamJet ||
            frame is not (120 or 300 or 599))
        {
            return;
        }
        steamJetInjectionTrace.Record(frame == 599 ? 600u : frame, resources);
    }

    public void RecordSteamJetLateralTrace(uint frame, GpuSimulationResources resources)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_LATERAL_TRACE") != "1" ||
            Mode != AcceptanceScenarioMode.SteamJet || frame is not (120 or 300 or 599)) return;
        steamJetLateralTrace.Record(frame == 599 ? 600u : frame, resources);
    }

    public void RecordSteamJetAirCouplingTrace(uint frame, GpuSimulationResources resources)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_AIR_COUPLING_TRACE") != "1" ||
            Mode != AcceptanceScenarioMode.SteamJet || frame is not (120 or 300 or 599))
        {
            return;
        }
        steamJetAirCouplingTrace.Record(frame == 599 ? 600u : frame, resources);
    }

    public bool TryBeginAcceptanceCheckpoint(
        uint frame,
        SimulationDispatchCoordinator dispatchCoordinator,
        out ulong checkpointTick)
    {
        checkpointTick = 0;
        if (phaseAcceptance.ShouldCaptureCheckpoint(frame, dispatchCoordinator))
        {
            checkpointTick = dispatchCoordinator.ThermalTicks;
            return true;
        }
        if (Mode == AcceptanceScenarioMode.TemperatureTool)
        {
            bool ready = thermalCheckpoints.Count switch
            {
                0 => frame >= 3,
                1 => frame >= 120,
                _ => false
            };
            if (ready)
            {
                checkpointTick = dispatchCoordinator.ThermalTicks;
            }
            return ready;
        }
        if (Mode == AcceptanceScenarioMode.SteamSelfCooling)
        {
            if (thermalCheckpoints.Count >= SteamCoolingCheckpointTicks.Length)
            {
                return false;
            }
            ulong steamTarget = SteamCoolingCheckpointTicks[thermalCheckpoints.Count];
            bool ready = steamTarget == 0
                ? frame >= 10 && dispatchCoordinator.ThermalTicks == 0
                : dispatchCoordinator.ThermalTicks >= steamTarget;
            if (ready)
            {
                checkpointTick = dispatchCoordinator.ThermalTicks;
            }
            return ready;
        }
        if (Mode is AcceptanceScenarioMode.SteamPuff or AcceptanceScenarioMode.SteamJet)
        {
            uint[] checkpoints = Mode == AcceptanceScenarioMode.SteamJet
                ? SteamJetCheckpointFrames
                : SteamPuffCheckpointFrames;
            bool ready = thermalCheckpoints.Count < checkpoints.Length &&
                frame >= checkpoints[thermalCheckpoints.Count];
            if (ready)
            {
                checkpointTick = frame;
            }
            return ready;
        }
        if (Mode == AcceptanceScenarioMode.CoalTypes)
        {
            if (thermalCheckpoints.Count >= CoalCheckpointTicks.Length)
            {
                return false;
            }
            ulong coalTarget = CoalCheckpointTicks[thermalCheckpoints.Count];
            bool ready = coalTarget == 0
                ? frame >= 10 && dispatchCoordinator.ThermalTicks == 0
                : dispatchCoordinator.ThermalTicks >= coalTarget;
            if (ready)
            {
                checkpointTick = dispatchCoordinator.ThermalTicks;
            }
            return ready;
        }
        if (Mode == AcceptanceScenarioMode.GasUniformDistribution)
        {
            bool ready = thermalCheckpoints.Count < GasCheckpointTicks.Length &&
                dispatchCoordinator.GasTicks >= GasCheckpointTicks[thermalCheckpoints.Count];
            if (ready)
            {
                checkpointTick = dispatchCoordinator.GasTicks;
            }
            return ready;
        }
        if (Mode == AcceptanceScenarioMode.SteamDistributionAndCooling)
        {
            bool ready = thermalCheckpoints.Count < SteamDistributionCheckpointTicks.Length &&
                dispatchCoordinator.ThermalTicks >=
                    SteamDistributionCheckpointTicks[thermalCheckpoints.Count];
            if (ready)
            {
                checkpointTick = dispatchCoordinator.ThermalTicks;
            }
            return ready;
        }
        if (Mode == AcceptanceScenarioMode.SteamCloudTemperature)
        {
            if (thermalCheckpoints.Count >= SteamCloudCheckpointTicks.Length)
            {
                return false;
            }
            ulong steamTarget = SteamCloudCheckpointTicks[thermalCheckpoints.Count];
            bool ready = steamTarget == 0
                ? frame >= Math.Max(1u, SteamCloudTemperatureAcceptanceScenario.PauseFrames / 2) &&
                    dispatchCoordinator.ThermalTicks == 0
                : dispatchCoordinator.ThermalTicks >= steamTarget;
            if (ready)
            {
                checkpointTick = dispatchCoordinator.ThermalTicks;
            }
            return ready;
        }
        if (Mode != AcceptanceScenarioMode.ThermalContact ||
            thermalCheckpoints.Count >= ThermalContactCheckpointTicks.Length) return false;
        ulong target = ThermalContactCheckpointTicks[thermalCheckpoints.Count];
        if (dispatchCoordinator.ThermalTicks < target)
        {
            return false;
        }
        checkpointTick = dispatchCoordinator.ThermalTicks;
        return true;
    }

    public void RecordThermalCheckpoint(
        uint frame,
        ulong thermalTicks,
        SimulationWorldSnapshot snapshot,
        SimulationDispatchCoordinator dispatchCoordinator)
    {
        if (phaseAcceptance.IsPhaseMode)
        {
            phaseAcceptance.RecordCheckpoint(frame, dispatchCoordinator, snapshot);
            return;
        }
        thermalCheckpoints.Add(new ThermalAcceptanceCheckpoint(frame, thermalTicks, snapshot));
    }

    public float AdjustElapsedSeconds(float elapsedSeconds) =>
        phaseAcceptance.AdjustElapsedSeconds(elapsedSeconds);

    public bool CanBeginFinalCapture(uint frame, SimulationDispatchCoordinator dispatchCoordinator) =>
        Mode == AcceptanceScenarioMode.SteamSelfCooling
            ? thermalCheckpoints.Count >= SteamCoolingCheckpointTicks.Length &&
                dispatchCoordinator.ThermalTicks >= SteamCoolingCheckpointTicks[^1]
            : Mode == AcceptanceScenarioMode.CoalTypes
            ? thermalCheckpoints.Count >= CoalCheckpointTicks.Length &&
                dispatchCoordinator.ThermalTicks >= CoalCheckpointTicks[^1]
            : Mode == AcceptanceScenarioMode.GasUniformDistribution
            ? thermalCheckpoints.Count >= GasCheckpointTicks.Length &&
                dispatchCoordinator.GasTicks >= 600
            : Mode == AcceptanceScenarioMode.SteamDistributionAndCooling
            ? thermalCheckpoints.Count >= SteamDistributionCheckpointTicks.Length &&
                dispatchCoordinator.ThermalTicks >= SteamDistributionFinalTick
            : Mode == AcceptanceScenarioMode.SteamCloudTemperature
            ? thermalCheckpoints.Count >= SteamCloudCheckpointTicks.Length &&
                dispatchCoordinator.ThermalTicks >= SteamCloudFinalTick
            : phaseAcceptance.IsPhaseMode
            ? phaseAcceptance.CanBeginFinalCapture(frame, dispatchCoordinator)
            : frame >= CaptureFrame;

    public bool TryBeginPhaseRoundTripSave(out SimulationWorldSnapshot? snapshot) =>
        phaseAcceptance.TryBeginRoundTripSave(out snapshot);

    public void MarkPhaseRoundTripLoading(SimulationDispatchCoordinator dispatchCoordinator) =>
        phaseAcceptance.MarkRoundTripLoading(dispatchCoordinator);

    public void MarkPhaseRoundTripLoaded(uint frame) => phaseAcceptance.MarkRoundTripLoaded(frame);

    public void ConfigureSettings(uint frame, SimulationSettings settings)
    {
        if (!Active)
        {
            return;
        }

        bool scenarioHydraulics = Mode is
            AcceptanceScenarioMode.Hydro or
            AcceptanceScenarioMode.WaterDrain or
            AcceptanceScenarioMode.CommunicatingVessels or
            AcceptanceScenarioMode.PressureTube or
            AcceptanceScenarioMode.SavedPressure or
            AcceptanceScenarioMode.SavedIsolation or
            AcceptanceScenarioMode.GranularBarrierHydraulic;
        settings.HydraulicPressure = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_HYDRAULICS") switch
        {
            "0" => false,
            "1" => true,
            _ => scenarioHydraulics
        };
        // Позволяет изолировать поле воздуха при разборе регрессии: прогон с
        // PHYXEL_ACCEPTANCE_AIR=0 и без него отвечает на вопрос «виновато ли
        // поле» одной парой запусков вместо перебора коммитов.
        settings.AirSimulation = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_AIR") switch
        {
            "0" => false,
            "1" => true,
            _ => settings.AirSimulation
        };
        settings.ShowAirField = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_SHOW_AIR") == "1";
        // Сцены acceptance-набора построены в замкнутом мире: вода стоит в
        // сосудах, песок опирается на стенки. С открытыми границами всё это
        // вытечет за край, поэтому здесь границы всегда сплошные.
        settings.OpenBoundaries = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES") == "1";
        if (Mode is AcceptanceScenarioMode.SolidGravity or AcceptanceScenarioMode.Buoyancy or
            AcceptanceScenarioMode.ExternalSolids)
        {
            settings.SolidGravity = frame >= 60;
        }
        else if (Mode == AcceptanceScenarioMode.SavedGravity)
        {
            settings.SolidGravity = frame >= 30;
        }
        if (Mode == AcceptanceScenarioMode.TemperatureTool)
        {
            settings.Paused = frame < 20;
        }
        else if (Mode == AcceptanceScenarioMode.SteamSelfCooling)
        {
            settings.Paused = frame < 30;
        }
        else if (Mode is AcceptanceScenarioMode.BrushEmptyOnly or
            AcceptanceScenarioMode.ContinuousBrushStroke)
        {
            settings.Paused = true;
        }
        else if (Mode == AcceptanceScenarioMode.CoalTypes)
        {
            settings.Paused = frame < 30;
        }
        else if (Mode == AcceptanceScenarioMode.SteamCloudTemperature)
        {
            settings.Paused = frame < SteamCloudTemperatureAcceptanceScenario.PauseFrames;
        }
    }

    public void CaptureScreenshot(GpuSimulationResources resources, uint frame)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_RECORD") == "1" && frame % 4 == 0)
        {
            string frameDirectory = Path.Combine(
                ArtifactDirectory,
                "recording",
                Mode.ToString().ToLowerInvariant());
            Directory.CreateDirectory(frameDirectory);
            SimulationScreenshotWriter.Save(
                resources,
                Path.Combine(frameDirectory, $"{frame / 4:D4}.png"));
        }
        string? label = Mode switch
        {
            AcceptanceScenarioMode.Bowl when frame == 125 => "A_water_2s",
            AcceptanceScenarioMode.Bowl when frame == 999 => "A_water_sand",
            AcceptanceScenarioMode.SolidGravity when frame == 59 => "B_gravity_off",
            AcceptanceScenarioMode.SolidGravity when frame == 120 => "B_falling",
            AcceptanceScenarioMode.SolidGravity when frame == 190 => "B_landed",
            AcceptanceScenarioMode.SolidGravity when frame == 359 => "B_split_stone",
            AcceptanceScenarioMode.Sand when frame == 189 => "C_pile_3s",
            AcceptanceScenarioMode.Hydro when frame == 125 => "D_equal_2s",
            AcceptanceScenarioMode.Hydro when frame == 15 => "D_waterfall",
            AcceptanceScenarioMode.Hydro when frame == 1199 => "D_rest",
            AcceptanceScenarioMode.Slope when frame == 20 => "E_slope_fall",
            AcceptanceScenarioMode.Slope when frame == 599 => "E_slope_rest",
            AcceptanceScenarioMode.Gas when frame == 30 => "F_gas_rise",
            AcceptanceScenarioMode.Gas when frame == 899 => "F_gas_spread",
            AcceptanceScenarioMode.FlatSurface when frame == 15 => "O_flat_stream_early",
            AcceptanceScenarioMode.FlatSurface when frame == 100 => "O_flat_stream_mid1",
            AcceptanceScenarioMode.FlatSurface when frame == 200 => "O_flat_stream_mid2",
            AcceptanceScenarioMode.FlatSurface when frame == 299 => "O_flat_stream_late",
            AcceptanceScenarioMode.FlatSurface when frame + 1 == CaptureFrame => "O_flat_surface",
            AcceptanceScenarioMode.WaterDrain when frame == 1799 => "G_water_drain",
            AcceptanceScenarioMode.CommunicatingVessels when frame == 125 => "H_vessels_2s",
            AcceptanceScenarioMode.CommunicatingVessels when frame == 3599 => "H_vessels_rest",
            AcceptanceScenarioMode.PressureTube when frame == 300 => "I_pressure_tube_fill",
            AcceptanceScenarioMode.PressureTube when frame == 1799 => "I_pressure_tube_rest",
            AcceptanceScenarioMode.SavedPressure when frame == 1199 => "J_saved_pressure",
            AcceptanceScenarioMode.SavedIsolation when frame + 1 == CaptureFrame => "K_saved_isolation",
            AcceptanceScenarioMode.SavedGravity when frame == 399 => "L_saved_gravity",
            AcceptanceScenarioMode.Buoyancy when frame == 499 => "M_buoyancy",
            AcceptanceScenarioMode.SavedSandWater when frame == 899 => "N_saved_sand_water",
            AcceptanceScenarioMode.ExternalGranular when frame == 189 => "Q_external_granular",
            AcceptanceScenarioMode.ExternalLiquid when frame == 599 => "R_external_liquid",
            AcceptanceScenarioMode.ExternalGas when frame == 30 => "S_external_gas_rise",
            AcceptanceScenarioMode.ExternalGas when frame == 899 => "S_external_gas_spread",
            AcceptanceScenarioMode.ExternalSolids when frame == 299 => "T_external_solids",
            AcceptanceScenarioMode.UnderwaterGranularPile when frame == 719 => "U_underwater_granular",
            AcceptanceScenarioMode.GranularWaterDisplacement when frame == 12 => "V_granular_displacement",
            AcceptanceScenarioMode.GranularBarrier when frame == 899 => "W_granular_barrier_off",
            AcceptanceScenarioMode.GranularBarrierHydraulic when frame == 899 => "X_granular_barrier_on",
            AcceptanceScenarioMode.FireObstacle when frame == 359 => "Y_fire_obstacle",
            AcceptanceScenarioMode.FireOpen when frame == 359 => "Y_fire_open",
            AcceptanceScenarioMode.Furnace when frame == 599 => "Z_furnace",
            AcceptanceScenarioMode.MetalChimney when frame == 599 => "Z_metal_chimney",
            AcceptanceScenarioMode.SteamPuff when frame == 599 => "AA_steam_puff",
            AcceptanceScenarioMode.SteamJet when frame == 119 => "AA_steam_jet_120",
            AcceptanceScenarioMode.SteamJet when frame == 299 => "AA_steam_jet_300",
            AcceptanceScenarioMode.SteamJet when frame == 599 => "AA_steam_jet_600",
            _ => null
        };
        if (label is null)
        {
            return;
        }
        Directory.CreateDirectory(ArtifactDirectory);
        SimulationScreenshotWriter.Save(resources, Path.Combine(ArtifactDirectory, $"{label}.png"));
    }

    public bool Validate(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        ulong thermalTicks,
        TemperatureProbeResult? temperatureProbe,
        ThermalGpuTimingStatistics thermalGpuTiming,
        ThermalGpuTimingStatistics contactGpuTiming,
        ThermalGpuTimingStatistics gasGpuTiming,
        ThermalGpuTimingStatistics phaseGpuTiming,
        ThermalGpuTimingStatistics combustionGpuTiming,
        ulong combustionDispatches,
        ulong combustionSummaryReadbacks,
        ThermalGpuTimingStatistics probeGpuTiming,
        ulong phaseDispatches,
        ulong phaseSummaryReadbacks,
        ulong phaseFallbackWakeUps,
        int maximumPhaseDispatchesPerFrame,
        PhaseTransitionSummaryFlags phaseSummary,
        bool phasePresentationIsCurrent,
        out string report)
    {
        bool passed = AcceptanceRegressionVerifier.Validate(
            Mode,
            materialRegistry,
            snapshot,
            statistics,
            framesPerSecond,
            thermalTicks,
            temperatureProbe,
            thermalCheckpoints,
            temperatureProbeTrace,
            thermalGpuTiming,
            phaseGpuTiming,
            combustionGpuTiming,
            combustionDispatches,
            combustionSummaryReadbacks,
            phaseDispatches,
            phaseSummaryReadbacks,
            phaseFallbackWakeUps,
            maximumPhaseDispatchesPerFrame,
            phaseSummary,
            phasePresentationIsCurrent,
            phaseAcceptance.Checkpoints,
            ArtifactDirectory,
            out report);
        string? traceName = Mode switch
        {
            AcceptanceScenarioMode.FireObstacle => "fire-obstacle-pressure-trace.csv",
            AcceptanceScenarioMode.FireOpen => "fire-open-pressure-trace.csv",
            AcceptanceScenarioMode.MetalChimney => "metal-chimney-pressure-trace.csv",
            _ => null
        };
        if (traceName is not null)
        {
            string tracePath = airPressureTrace.WriteCsv(ArtifactDirectory, traceName);
            report += Environment.NewLine +
                $"PHYXEL_AIR_PRESSURE_TRACE samples=300 path={tracePath}";
        }
        if (gasObstacleBypassTrace.Count > 0)
        {
            string bypassTracePath = gasObstacleBypassTrace.WriteCsv(
                ArtifactDirectory,
                "gas-obstacle-bypass-trace.csv");
            GasObstacleBypassStatistics total = gasObstacleBypassTrace.Sum();
            report += Environment.NewLine +
                $"PHYXEL_GAS_OBSTACLE_BYPASS samples={gasObstacleBypassTrace.Count} " +
                $"blocked={total.Blocked} xOnly={total.XOnly} yOnly={total.YOnly} " +
                $"diagonal={total.Diagonal} stayed={total.Stayed} path={bypassTracePath}";
        }
        if (gasLateralTransferTrace.Count > 0)
        {
            string lateralTracePath = gasLateralTransferTrace.WriteCsv(
                ArtifactDirectory,
                "gas-lateral-transfer-trace.csv");
            foreach (GasLateralPathTotal total in gasLateralTransferTrace.Sum())
            {
                long bias = unchecked((long)total.Right - (long)total.Left);
                report += Environment.NewLine +
                    $"PHYXEL_GAS_LATERAL path={total.Path} left={total.Left} right={total.Right} bias={bias} " +
                    $"fromLeftLeft={total.FromLeftLeft} fromLeftRight={total.FromLeftRight} " +
                    $"fromRightLeft={total.FromRightLeft} fromRightRight={total.FromRightRight} " +
                    $"velocityMatch={total.VelocityMatch} velocityMismatch={total.VelocityMismatch} velocityZero={total.VelocityZero} " +
                    $"fireLeft={total.FireLeft} fireRight={total.FireRight} " +
                    $"fireVelocityMatch={total.FireVelocityMatch} fireVelocityMismatch={total.FireVelocityMismatch} " +
                    $"fireVelocityZero={total.FireVelocityZero} samples={gasLateralTransferTrace.Count} trace={lateralTracePath}";
            }
        }
        if (gasVerticalMotionTrace.Count > 0)
        {
            GasVerticalMotionStatistics vertical = gasVerticalMotionTrace.Latest;
            double fireCellFrames = Math.Max(1, vertical.FireCellFrames);
            double meanVelocityY = vertical.FireVelocityYMillisteps / (1000.0 * fireCellFrames);
            double actualRisePerFireFrame = vertical.FireUpwardSteps / fireCellFrames;
            double offsetYClampFraction = vertical.FireOffsetYClampFrames / fireCellFrames;
            double upwardBlockedByGasFraction = vertical.FireUpwardBlockedByGas /
                (double)Math.Max(1, vertical.FireUpwardCandidates);
            double upwardBlockedCellFrameFraction = vertical.FireUpwardBlockedCellFrames / fireCellFrames;
            string verticalTracePath = gasVerticalMotionTrace.WriteCsv(
                ArtifactDirectory,
                "gas-vertical-motion-trace.csv");
            report += Environment.NewLine +
                $"PHYXEL_GAS_VERTICAL fireCellFrames={vertical.FireCellFrames} " +
                $"meanVelocityY={meanVelocityY:0.000000} " +
                $"actualRisePerFireFrame={actualRisePerFireFrame:0.000000} " +
                $"offsetYClampFraction={offsetYClampFraction:0.000000} " +
                $"upwardCandidates={vertical.FireUpwardCandidates} " +
                $"upwardSteps={vertical.FireUpwardSteps} " +
                $"upwardBlockedByGas={vertical.FireUpwardBlockedByGas} " +
                $"upwardBlockedByGasFraction={upwardBlockedByGasFraction:0.000000} " +
                $"upwardBlockedCellFrames={vertical.FireUpwardBlockedCellFrames} " +
                $"upwardBlockedCellFrameFraction={upwardBlockedCellFrameFraction:0.000000} " +
                $"samples={gasVerticalMotionTrace.Count} trace={verticalTracePath}";
            if (Mode == AcceptanceScenarioMode.MetalChimney)
            {
                report += Environment.NewLine +
                    "PHYXEL_METAL_CHIMNEY_MOTION " +
                    GasVerticalMotionTrace.FormatMetalChimneyBands(vertical);
            }
        }
        if (Mode is AcceptanceScenarioMode.SteamPuff or AcceptanceScenarioMode.SteamJet)
        {
            string tracePath = steamGasStepTrace.WriteCsv(
                ArtifactDirectory,
                "steam-gas-step-trace.csv");
            foreach (uint frame in new uint[] { 60, 120, 300, 600 })
            {
                if (!steamGasStepTrace.TryGet(frame, out SteamGasStepStatistics steps))
                {
                    report += $" steamGasStepFrame{frame}Missing=1";
                    continue;
                }
                report +=
                    $" upwardSteps{frame}={steps.UpwardSteps}" +
                    $" downwardSteps{frame}={steps.DownwardSteps}" +
                    $" noYSteps{frame}={steps.NoYSteps}" +
                    $" leftSteps{frame}={steps.LeftSteps}" +
                    $" rightSteps{frame}={steps.RightSteps}" +
                    $" noXSteps{frame}={steps.NoXSteps}";
            }
            report += $" steamGasStepTrace={tracePath}";
        }
        if (Mode == AcceptanceScenarioMode.SteamJet)
        {
            if (Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_LATERAL_TRACE") == "1")
                report += $" steamJetLateralTrace={steamJetLateralTrace.WriteCsv(ArtifactDirectory)}";
            string tracePath = steamJetInjectionTrace.WriteCsv(
                ArtifactDirectory,
                "steam-jet-injection-trace.csv");
            if (steamJetInjectionTrace.TryGet(600, out SteamJetInjectionStatistics finalInjection))
            {
                report +=
                    $" steamJetCreatedCells600={finalInjection.CreatedSteamCells}" +
                    $" steamJetInflowPerFrame={finalInjection.CreatedSteamCells / 600.0:0.000000}";
            }
            else
            {
                report += " steamJetInjection600Missing=1";
            }
            report += $" steamJetInjectionTrace={tracePath}";
        }
        if (Mode == AcceptanceScenarioMode.SteamJet && steamJetAirCouplingTrace.Count > 0 && materialRegistry is not null)
        {
            Dictionary<uint, SimulationWorldSnapshot> snapshots = [];
            if (thermalCheckpoints.Count > 0) snapshots[120] = thermalCheckpoints[0].Snapshot;
            if (thermalCheckpoints.Count > 1) snapshots[300] = thermalCheckpoints[1].Snapshot;
            snapshots[600] = snapshot;
            string tracePaths = steamJetAirCouplingTrace.WriteArtifacts(
                ArtifactDirectory,
                snapshots,
                materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
                AcceptanceRegressionScenario.GetSteamJetSourceY());
            report += Environment.NewLine +
                $"PHYXEL_STEAM_JET_AIR_COUPLING samples={steamJetAirCouplingTrace.Count} paths={tracePaths}";
        }
        report += Environment.NewLine +
            $"PHYXEL_MATERIAL_PROPERTIES_LAYOUT csharpActual={Marshal.SizeOf<MaterialProperties>()} " +
            $"hlslDeclared={MaterialPropertiesLayout.ByteSize} fields={MaterialPropertiesLayout.FieldCount} " +
            $"match={(Marshal.SizeOf<MaterialProperties>() == MaterialPropertiesLayout.ByteSize ? 1 : 0)}";
        report += Environment.NewLine +
            $"PHYXEL_ACCEPTANCE_METRICS size={snapshot.Width}x{snapshot.Height} fps={framesPerSecond:0.0} " +
            $"thermalGpuMs={thermalGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{thermalGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{thermalGpuTiming.MaximumMilliseconds:0.0000} samples={thermalGpuTiming.Samples} " +
            $"contactGpuMs={contactGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{contactGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{contactGpuTiming.MaximumMilliseconds:0.0000} contactSamples={contactGpuTiming.Samples} " +
            $"gasGpuMs={gasGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{gasGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{gasGpuTiming.MaximumMilliseconds:0.0000} gasSamples={gasGpuTiming.Samples} " +
            $"phaseGpuMs={phaseGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{phaseGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{phaseGpuTiming.MaximumMilliseconds:0.0000} phaseSamples={phaseGpuTiming.Samples} " +
            $"phaseDispatches={phaseDispatches} phaseMaxPerFrame={maximumPhaseDispatchesPerFrame} " +
            $"phaseSummaryReadbacks={phaseSummaryReadbacks} " +
            $"phaseFallbackWakeUps={phaseFallbackWakeUps} " +
            $"combustionGpuMs={combustionGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{combustionGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{combustionGpuTiming.MaximumMilliseconds:0.0000} combustionSamples={combustionGpuTiming.Samples} " +
            $"combustionDispatches={combustionDispatches} combustionSummaryReadbacks={combustionSummaryReadbacks} " +
            $"probeGpuMs={probeGpuTiming.AverageMilliseconds:0.0000}/" +
            $"{probeGpuTiming.MinimumMilliseconds:0.0000}/" +
            $"{probeGpuTiming.MaximumMilliseconds:0.0000} probeSamples={probeGpuTiming.Samples}";
        Directory.CreateDirectory(ArtifactDirectory);
        File.WriteAllText(
            Path.Combine(ArtifactDirectory, "acceptance-report.txt"),
            report + Environment.NewLine +
            (passed ? "PHYXEL_ACCEPTANCE_SUCCESS" : "PHYXEL_ACCEPTANCE_FAILED") + Environment.NewLine);
        Console.WriteLine(report);
        Console.WriteLine(passed ? "PHYXEL_ACCEPTANCE_SUCCESS" : "PHYXEL_ACCEPTANCE_FAILED");
        return passed;
    }

    private static string ArtifactDirectory =>
        Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ??
        Path.Combine(Environment.CurrentDirectory, "artifacts", "powder-toy-acceptance");
}
