using System;
using System.Collections.Generic;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal readonly record struct PhaseAcceptanceCheckpoint(
    uint Frame,
    ulong ThermalTicks,
    int ThermalTicksInFrame,
    ulong PhaseDispatches,
    ulong SummaryReadbacks,
    ulong FallbackWakeUps,
    int MaximumDispatchesPerFrame,
    PhaseTransitionSummaryFlags Summary,
    SimulationWorldSnapshot Snapshot);

internal sealed class PhaseAcceptanceController
{
    private enum ReadbackStage
    {
        Normal,
        HoldFirst,
        DrainFirst,
        HoldSecond,
        DrainSecond,
        VerifyNormal,
        Complete
    }

    private enum RoundTripStage
    {
        None,
        Saving,
        Loading,
        Loaded
    }

    private readonly AcceptanceScenarioMode mode;
    private readonly List<PhaseAcceptanceCheckpoint> checkpoints = [];
    private PhaseAcceptanceCheckpoint pendingCheckpoint;
    private bool checkpointPending;
    private ReadbackStage readbackStage;
    private RoundTripStage roundTripStage;
    private ulong readbackBaseline;
    private ulong verifyDispatchBaseline;
    private ulong verifyReadbackBaseline;
    private uint roundTripLoadedFrame;

    public PhaseAcceptanceController(AcceptanceScenarioMode mode)
    {
        this.mode = mode;
    }

    public IReadOnlyList<PhaseAcceptanceCheckpoint> Checkpoints => checkpoints;
    public bool IsPhaseMode => PhaseAcceptanceScenario.IsPhaseMode(mode);
    public bool IsRoundTripSaving => roundTripStage == RoundTripStage.Saving;
    public bool IsRoundTripLoading => roundTripStage == RoundTripStage.Loading;

    public float AdjustElapsedSeconds(float elapsedSeconds)
    {
        if (mode == AcceptanceScenarioMode.PhaseSingleTransition)
        {
            return checkpoints.Count == 0 ? 0.2f : 0.05f;
        }
        if (mode is AcceptanceScenarioMode.PhaseThresholds or
            AcceptanceScenarioMode.PhaseHysteresis or
            AcceptanceScenarioMode.PhaseNormalizationMatrix or
            AcceptanceScenarioMode.PhaseSummaryLiquidGas or
            AcceptanceScenarioMode.PhaseSummarySolidLiquid or
            AcceptanceScenarioMode.PhaseSummaryGasMovable or
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
            AcceptanceScenarioMode.PhaseWakeGas or
            AcceptanceScenarioMode.PhaseWakeLiquid or
            AcceptanceScenarioMode.PhaseReadbackFallback or
            AcceptanceScenarioMode.PhaseExternalReorder or
            AcceptanceScenarioMode.PhaseEnergyContract or
            AcceptanceScenarioMode.PhaseV5RoundTrip)
        {
            return 0.05f;
        }
        return elapsedSeconds;
    }

    public void ApplyRuntimeControls(
        uint frame,
        SimulationSettings settings,
        SimulationDispatchCoordinator coordinator)
    {
        if (!IsPhaseMode)
        {
            return;
        }

        bool suppressReadback = false;

        settings.SolidGravity = false;
        settings.HydraulicPressure = false;
        settings.Paused = mode switch
        {
            AcceptanceScenarioMode.PhaseThresholds => coordinator.PhaseDispatches >= 1,
            AcceptanceScenarioMode.PhaseHysteresis => coordinator.PhaseDispatches >= 5,
            AcceptanceScenarioMode.PhaseSingleTransition => PauseSingleTransition(coordinator),
            AcceptanceScenarioMode.PhaseNormalizationMatrix or
            AcceptanceScenarioMode.PhaseSummaryLiquidGas or
            AcceptanceScenarioMode.PhaseSummarySolidLiquid or
            AcceptanceScenarioMode.PhaseSummaryGasMovable or
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
            AcceptanceScenarioMode.PhaseExternalReorder or
            AcceptanceScenarioMode.PhaseEnergyContract => coordinator.PhaseDispatches >= 1,
            AcceptanceScenarioMode.PhaseV5RoundTrip =>
                roundTripStage != RoundTripStage.None || coordinator.PhaseDispatches >= 1,
            AcceptanceScenarioMode.PhasePauseContinue => frame < 10 || coordinator.PhaseDispatches >= 1,
            AcceptanceScenarioMode.PhaseWakeGas or AcceptanceScenarioMode.PhaseWakeLiquid => frame < 3,
            _ => false
        };

        if (mode == AcceptanceScenarioMode.PhaseReadbackFallback)
        {
            UpdateReadbackState(settings, coordinator, out suppressReadback);
        }
        coordinator.ConfigurePhaseAcceptanceDiagnostics(suppressReadback);
    }

    public bool ShouldCaptureCheckpoint(uint frame, SimulationDispatchCoordinator coordinator)
    {
        if (checkpointPending)
        {
            return false;
        }
        bool ready = mode switch
        {
            AcceptanceScenarioMode.PhaseThresholds => checkpoints.Count == 0 && coordinator.PhaseDispatches >= 1,
            AcceptanceScenarioMode.PhaseHysteresis => checkpoints.Count switch
            {
                0 => coordinator.PhaseDispatches >= 1,
                1 => coordinator.PhaseDispatches >= 5,
                _ => false
            },
            AcceptanceScenarioMode.PhaseSingleTransition => checkpoints.Count switch
            {
                0 => coordinator.PhaseDispatches >= 1,
                1 => coordinator.PhaseDispatches >= 2,
                _ => false
            },
            AcceptanceScenarioMode.PhaseNormalizationMatrix or
            AcceptanceScenarioMode.PhaseSummaryLiquidGas or
            AcceptanceScenarioMode.PhaseSummarySolidLiquid or
            AcceptanceScenarioMode.PhaseSummaryGasMovable or
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
            AcceptanceScenarioMode.PhaseExternalReorder or
            AcceptanceScenarioMode.PhaseEnergyContract or
            AcceptanceScenarioMode.PhaseV5RoundTrip =>
                checkpoints.Count == 0 && coordinator.PhaseDispatches >= 1,
            AcceptanceScenarioMode.PhasePauseContinue => checkpoints.Count switch
            {
                0 => frame >= 3 && coordinator.PhaseDispatches == 0,
                1 => coordinator.PhaseDispatches >= 1,
                _ => false
            },
            AcceptanceScenarioMode.PhaseWakeGas or AcceptanceScenarioMode.PhaseWakeLiquid => checkpoints.Count switch
            {
                0 => frame >= 1 && coordinator.PhaseDispatches == 0,
                1 => coordinator.PhaseDispatches >= 1,
                2 => frame >= checkpoints[1].Frame + 30,
                _ => false
            },
            _ => false
        };
        if (ready)
        {
            pendingCheckpoint = new PhaseAcceptanceCheckpoint(
                frame,
                coordinator.ThermalTicks,
                coordinator.LastThermalTicksPerFrame,
                coordinator.PhaseDispatches,
                coordinator.PhaseSummaryReadbacks,
                coordinator.PhaseFallbackWakeUps,
                coordinator.MaximumPhaseDispatchesPerFrame,
                coordinator.LastPhaseSummary,
                null!);
            checkpointPending = true;
        }
        return ready;
    }

    public void RecordCheckpoint(uint frame, SimulationDispatchCoordinator coordinator, SimulationWorldSnapshot snapshot)
    {
        if (!checkpointPending)
        {
            throw new InvalidOperationException("Phase checkpoint metrics were not captured.");
        }
        checkpoints.Add(pendingCheckpoint with
        {
            SummaryReadbacks = coordinator.PhaseSummaryReadbacks,
            Summary = coordinator.LastPhaseSummary,
            Snapshot = snapshot
        });
        checkpointPending = false;
    }

    public bool CanBeginFinalCapture(uint frame, SimulationDispatchCoordinator coordinator)
    {
        return mode switch
        {
            AcceptanceScenarioMode.PhaseThresholds => checkpoints.Count >= 1,
            AcceptanceScenarioMode.PhaseHysteresis => checkpoints.Count >= 2,
            AcceptanceScenarioMode.PhaseSingleTransition => checkpoints.Count >= 2,
            AcceptanceScenarioMode.PhaseNormalizationMatrix or
            AcceptanceScenarioMode.PhaseSummaryLiquidGas or
            AcceptanceScenarioMode.PhaseSummarySolidLiquid or
            AcceptanceScenarioMode.PhaseSummaryGasMovable or
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
            AcceptanceScenarioMode.PhaseExternalReorder or
            AcceptanceScenarioMode.PhaseEnergyContract => checkpoints.Count >= 1,
            AcceptanceScenarioMode.PhasePauseContinue => checkpoints.Count >= 2,
            AcceptanceScenarioMode.PhaseWakeGas or AcceptanceScenarioMode.PhaseWakeLiquid => checkpoints.Count >= 3,
            AcceptanceScenarioMode.PhaseReadbackFallback => readbackStage == ReadbackStage.Complete,
            AcceptanceScenarioMode.PhaseDisabledRegistry => frame >= 120,
            AcceptanceScenarioMode.PhaseV5RoundTrip =>
                roundTripStage == RoundTripStage.Loaded && frame >= roundTripLoadedFrame + 5,
            AcceptanceScenarioMode.PhasePerformanceSteady or AcceptanceScenarioMode.PhasePerformanceBurst =>
                frame >= 240 && coordinator.PhaseGpuTiming.Samples > 0 && coordinator.ThermalGpuTiming.Samples > 0,
            _ => false
        };
    }

    public bool TryBeginRoundTripSave(out SimulationWorldSnapshot? snapshot)
    {
        snapshot = null;
        if (mode != AcceptanceScenarioMode.PhaseV5RoundTrip ||
            roundTripStage != RoundTripStage.None || checkpoints.Count == 0)
        {
            return false;
        }
        snapshot = checkpoints[0].Snapshot;
        roundTripStage = RoundTripStage.Saving;
        return true;
    }

    public void MarkRoundTripLoading(SimulationDispatchCoordinator coordinator)
    {
        if (roundTripStage != RoundTripStage.Saving)
        {
            throw new InvalidOperationException("Phase round-trip save was not pending.");
        }
        if (checkpoints.Count > 0)
        {
            checkpoints[0] = checkpoints[0] with
            {
                SummaryReadbacks = coordinator.PhaseSummaryReadbacks,
                Summary = coordinator.LastPhaseSummary
            };
        }
        roundTripStage = RoundTripStage.Loading;
    }

    public void MarkRoundTripLoaded(uint frame)
    {
        if (roundTripStage != RoundTripStage.Loading)
        {
            throw new InvalidOperationException("Phase round-trip load was not pending.");
        }
        roundTripStage = RoundTripStage.Loaded;
        roundTripLoadedFrame = frame;
    }

    private bool PauseSingleTransition(SimulationDispatchCoordinator coordinator)
    {
        if (coordinator.PhaseDispatches == 0)
        {
            return false;
        }
        if (checkpoints.Count == 0)
        {
            return true;
        }
        return coordinator.PhaseDispatches >= 2;
    }

    private void UpdateReadbackState(
        SimulationSettings settings,
        SimulationDispatchCoordinator coordinator,
        out bool suppressReadback)
    {
        suppressReadback = false;
        switch (readbackStage)
        {
            case ReadbackStage.Normal:
                if (coordinator.PhaseSummaryReadbacks >= 1)
                {
                    readbackBaseline = coordinator.PhaseSummaryReadbacks;
                    readbackStage = ReadbackStage.HoldFirst;
                    suppressReadback = true;
                }
                break;
            case ReadbackStage.HoldFirst:
                suppressReadback = true;
                if (coordinator.PhaseFallbackWakeUps >= 1)
                {
                    readbackStage = ReadbackStage.DrainFirst;
                    suppressReadback = false;
                    settings.Paused = true;
                }
                break;
            case ReadbackStage.DrainFirst:
                settings.Paused = true;
                if (coordinator.PendingPhaseReadbackSlots == 0 &&
                    coordinator.PhaseSummaryReadbacks > readbackBaseline)
                {
                    readbackBaseline = coordinator.PhaseSummaryReadbacks;
                    readbackStage = ReadbackStage.HoldSecond;
                    settings.Paused = false;
                    suppressReadback = true;
                }
                break;
            case ReadbackStage.HoldSecond:
                suppressReadback = true;
                if (coordinator.PhaseFallbackWakeUps >= 2)
                {
                    readbackStage = ReadbackStage.DrainSecond;
                    suppressReadback = false;
                    settings.Paused = true;
                }
                break;
            case ReadbackStage.DrainSecond:
                settings.Paused = true;
                if (coordinator.PendingPhaseReadbackSlots == 0 &&
                    coordinator.PhaseSummaryReadbacks > readbackBaseline)
                {
                    verifyDispatchBaseline = coordinator.PhaseDispatches;
                    verifyReadbackBaseline = coordinator.PhaseSummaryReadbacks;
                    readbackStage = ReadbackStage.VerifyNormal;
                    settings.Paused = false;
                }
                break;
            case ReadbackStage.VerifyNormal:
                if (coordinator.PhaseDispatches == verifyDispatchBaseline)
                {
                    settings.Paused = false;
                }
                else
                {
                    settings.Paused = true;
                    if (coordinator.PhaseSummaryReadbacks > verifyReadbackBaseline &&
                        coordinator.PendingPhaseReadbackSlots == 0 &&
                        coordinator.PhaseFallbackWakeUps == 2)
                    {
                        readbackStage = ReadbackStage.Complete;
                    }
                }
                break;
            case ReadbackStage.Complete:
                settings.Paused = true;
                break;
        }
    }
}
