using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class CorePhaseAcceptanceVerifier
{
    public static int Validate(
        AcceptanceScenarioMode mode,
        MaterialRegistry materials,
        SimulationWorldSnapshot finalSnapshot,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        VerifyRegistry(materials, errors);
        return mode switch
        {
            AcceptanceScenarioMode.WaterIceSteam =>
                ValidateChain(materials, checkpoints, errors),
            AcceptanceScenarioMode.WaterIceSteamMotion =>
                ValidateMotion(materials, checkpoints, errors),
            AcceptanceScenarioMode.WaterIceSteamPause =>
                ValidatePause(materials, checkpoints, errors),
            AcceptanceScenarioMode.WaterIceSteamV5RoundTrip =>
                ValidateRoundTrip(materials, finalSnapshot, checkpoints, errors),
            _ => 0
        };
    }

    private static void VerifyRegistry(MaterialRegistry materials, List<string> errors)
    {
        Require(materials.RegistryHasPhaseTransitions,
            "actual core registry does not report phase transitions", errors);
        Require(materials[CoreMaterialIds.Empty].RuntimeIndex == 0,
            "core:empty runtime index is not zero", errors);
        bool hasIce = materials.TryGet(CoreMaterialIds.Ice, out MaterialDefinition ice);
        bool hasSteam = materials.TryGet(CoreMaterialIds.Steam, out MaterialDefinition steam);
        Require(hasIce && hasSteam, "core:ice/core:steam are missing", errors);
        if (!hasIce || !hasSteam)
        {
            return;
        }
        MaterialDefinition water = materials[CoreMaterialIds.Water];
        Require(water.Properties.TransitionBelowMaterialIndex == ice.RuntimeIndex &&
            water.Properties.TransitionBelowTemperature == 0,
            "core:water freeze rule is not 0 -> core:ice", errors);
        Require(water.Properties.TransitionAboveMaterialIndex == steam.RuntimeIndex &&
            water.Properties.TransitionAboveTemperature == 100,
            "core:water boil rule is not 100 -> core:steam", errors);
        Require(ice.Properties.TransitionAboveMaterialIndex == water.RuntimeIndex &&
            ice.Properties.TransitionAboveTemperature == 2,
            "core:ice melt rule is not 2 -> core:water", errors);
        Require(steam.Properties.TransitionBelowMaterialIndex == water.RuntimeIndex &&
            steam.Properties.TransitionBelowTemperature == 98,
            "core:steam condensation rule is not 98 -> core:water", errors);
        Console.WriteLine(
            $"PHYXEL_CORE_RUNTIME_INDICES empty={materials[CoreMaterialIds.Empty].RuntimeIndex} " +
            $"water={water.RuntimeIndex} ice={ice.RuntimeIndex} steam={steam.RuntimeIndex} " +
            $"gas={materials[CoreMaterialIds.Gas].RuntimeIndex}");
    }

    private static int ValidateChain(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 3,
            $"water_ice_steam checkpoints expected=3 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 3)
        {
            return 0;
        }
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.WaterIceSteam,
            checkpoints[0].Snapshot, materials);
        (int X, int Y, string Id)[] stable =
        [
            (180, 130, CoreMaterialIds.Water),
            (200, 130, CoreMaterialIds.Water),
            (220, 130, CoreMaterialIds.Ice),
            (240, 130, CoreMaterialIds.Steam),
            (260, 130, CoreMaterialIds.Water)
        ];
        foreach ((int x, int y, string id) in stable)
        {
            GridCell before = Cell(initial, x, y);
            GridCell afterOne = Cell(checkpoints[0].Snapshot, x, y);
            GridCell afterFive = Cell(checkpoints[2].Snapshot, x, y);
            Require(SamePhaseState(before, afterOne) && SamePhaseState(afterOne, afterFive),
                $"strict threshold/hysteresis changed stable {id} at {x},{y}", errors);
            Require(afterFive.MaterialIndex == materials.GetRequiredRuntimeIndex(id),
                $"stable phase ID changed at {x},{y}", errors);
        }

        (int X, string Target)[] firstTransitions =
        [
            (180, CoreMaterialIds.Ice),
            (200, CoreMaterialIds.Water),
            (220, CoreMaterialIds.Steam),
            (240, CoreMaterialIds.Water),
            (260, CoreMaterialIds.Water)
        ];
        foreach ((int x, string target) in firstTransitions)
        {
            ValidateNormalized(Cell(initial, x, 160), Cell(checkpoints[0].Snapshot, x, 160),
                materials, target, $"first phase {x},160", errors);
        }

        GridCell oneTransition = Cell(checkpoints[0].Snapshot, 260, 160);
        GridCell twoTransitions = Cell(checkpoints[1].Snapshot, 260, 160);
        Require(oneTransition.MaterialIndex == materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            "ice at 110 C skipped water in the first dispatch", errors);
        ValidateNormalized(oneTransition, twoTransitions, materials, CoreMaterialIds.Steam,
            "second dispatch water -> steam", errors);

        int[] stableAfterFirst = [180, 200, 220, 240];
        foreach (int x in stableAfterFirst)
        {
            Require(SamePhaseState(Cell(checkpoints[0].Snapshot, x, 160),
                    Cell(checkpoints[2].Snapshot, x, 160)),
                $"completed transition oscillated through five passes at {x},160", errors);
        }
        Require(SamePhaseState(twoTransitions, Cell(checkpoints[2].Snapshot, 260, 160)),
            "one-transition chain did not stabilize after the second dispatch", errors);
        Require(checkpoints[0].PhaseDispatches == 1 && checkpoints[1].PhaseDispatches == 2 &&
            checkpoints[2].PhaseDispatches >= 5,
            "phase checkpoint dispatch sequence is not 1/2/5", errors);
        return 6;
    }

    private static int ValidateMotion(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 2,
            $"water_ice_steam_motion checkpoints expected=2 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 2)
        {
            return 0;
        }
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.WaterIceSteamMotion,
            checkpoints[0].Snapshot, materials);
        SimulationWorldSnapshot immediate = checkpoints[0].Snapshot;
        SimulationWorldSnapshot moved = checkpoints[1].Snapshot;
        ValidateRegionNormalization(initial, immediate, materials,
            150, 80, 157, 87, CoreMaterialIds.Water, "melt", errors);
        ValidateRegionNormalization(initial, immediate, materials,
            230, 180, 237, 187, CoreMaterialIds.Steam, "boil", errors);
        ValidateRegionNormalization(initial, immediate, materials,
            310, 80, 317, 87, CoreMaterialIds.Water, "condense", errors);
        ValidateRegionUnchanged(initial, immediate, 70, 160, 85, 163,
            "stable ice changed in phase pass", errors);
        ValidateRegionUnchanged(initial, immediate, 238, 180, 245, 187,
            "core:gas changed beside steam", errors);

        uint ice = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Ice);
        uint water = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water);
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        uint gas = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Gas);
        MaterialMetrics iceMetrics = Measure(moved, ice);
        MaterialMetrics fixedIceMetrics = MeasureRegion(moved, ice, 70, 160, 85, 163);
        MaterialMetrics waterMetrics = Measure(moved, water);
        MaterialMetrics steamMetrics = Measure(moved, steam);
        MaterialMetrics gasMetrics = Measure(moved, gas);
        Console.WriteLine(
            $"PHYXEL_CORE_MOTION steam={steamMetrics} gas={gasMetrics}");
        Require(fixedIceMetrics.Cells == 64 &&
            Math.Abs(fixedIceMetrics.Mass - 64) <= 0.001 &&
            fixedIceMetrics.MinimumX == 70 && fixedIceMetrics.MaximumX == 85 &&
            fixedIceMetrics.MinimumY == 160 && fixedIceMetrics.MaximumY == 163,
            $"fixed ice moved or changed={fixedIceMetrics}", errors);
        double newlyFrozenMass = Math.Max(0, iceMetrics.Mass - fixedIceMetrics.Mass);
        double phaseFamilyMass = waterMetrics.Mass + steamMetrics.Mass + newlyFrozenMass;
        Require(waterMetrics.Cells > 0 && Math.Abs(phaseFamilyMass - 192) <= 0.05 &&
            waterMetrics.AverageY > 95,
            $"water/steam family did not fall or conserve mass water={waterMetrics} " +
            $"steam={steamMetrics} newIceMass={newlyFrozenMass:F3}", errors);
        bool steamStillPresent = steamMetrics.Mass >= 1;
        Require(!steamStillPresent || steamMetrics.AverageY < 178,
            $"remaining boiled steam did not rise={steamMetrics}", errors);
        Require(!steamStillPresent ||
            (steamMetrics.RestingCells * 4 < steamMetrics.Cells * 3 &&
                steamMetrics.MaximumX - steamMetrics.MinimumX >= 16),
            $"remaining boiled steam stopped diffusing={steamMetrics}", errors);
        Require(waterMetrics.Mass >= 120 && steamMetrics.Mass >= 1,
            $"boiled steam neither remained nor condensed into water={waterMetrics}", errors);
        Require(gasMetrics.Cells > 0 && gasMetrics.Mass >= 60 && gasMetrics.AverageY < 178,
            $"core:gas beside steam did not remain a separate moving gas={gasMetrics}", errors);
        Require(steam != gas, "core:steam and core:gas share a runtime index", errors);
        Require(CountMaterialInRegion(moved, water, 150, 80, 157, 87) < 64 &&
            CountMaterialInRegion(moved, water, 310, 80, 317, 87) < 64,
            "melted or condensed water remained frozen in its source region", errors);
        Require(checkpoints[0].PhaseDispatches == 1,
            "motion fixture was not captured immediately after one phase dispatch", errors);
        return 192;
    }

    private static int ValidatePause(
        MaterialRegistry materials,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 2,
            $"water_ice_steam_pause checkpoints expected=2 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 2)
        {
            return 0;
        }
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.WaterIceSteamPause,
            checkpoints[0].Snapshot, materials);
        GridCell coldBefore = Cell(initial, 220, 135);
        coldBefore.Temperature = -10;
        GridCell hotBefore = Cell(initial, 260, 135);
        hotBefore.Temperature = 110;
        Require(CellEquals(coldBefore, Cell(checkpoints[0].Snapshot, 220, 135)) &&
            CellEquals(hotBefore, Cell(checkpoints[0].Snapshot, 260, 135)),
            "temperature brush changed fields other than Temperature while paused", errors);
        Require(checkpoints[0].ThermalTicks == 0 && checkpoints[0].PhaseDispatches == 0,
            "Pause accumulated thermal ticks or dispatched phase", errors);
        ValidateNormalized(coldBefore, Cell(checkpoints[1].Snapshot, 220, 135),
            materials, CoreMaterialIds.Ice, "Pause cold Continue", errors);
        ValidateNormalized(hotBefore, Cell(checkpoints[1].Snapshot, 260, 135),
            materials, CoreMaterialIds.Steam, "Pause hot Continue", errors);
        Require(checkpoints[1].ThermalTicks == 1 && checkpoints[1].PhaseDispatches == 1,
            $"Continue expected one total thermal tick/phase pass actual={checkpoints[1].ThermalTicks}/" +
            $"{checkpoints[1].PhaseDispatches}", errors);
        return 2;
    }

    private static int ValidateRoundTrip(
        MaterialRegistry materials,
        SimulationWorldSnapshot finalSnapshot,
        IReadOnlyList<PhaseAcceptanceCheckpoint> checkpoints,
        List<string> errors)
    {
        Require(checkpoints.Count == 3,
            $"water_ice_steam_v5 checkpoints expected=3 actual={checkpoints.Count}", errors);
        if (checkpoints.Count < 3)
        {
            return 0;
        }
        SimulationWorldSnapshot initial = Initial(AcceptanceScenarioMode.WaterIceSteamV5RoundTrip,
            checkpoints[0].Snapshot, materials);
        string[] firstTargets =
        [
            CoreMaterialIds.Ice,
            CoreMaterialIds.Water,
            CoreMaterialIds.Steam,
            CoreMaterialIds.Water,
            CoreMaterialIds.Water
        ];
        for (int index = 0; index < firstTargets.Length; index++)
        {
            int x = 200 + index * 20;
            ValidateNormalized(Cell(initial, x, 135), Cell(checkpoints[0].Snapshot, x, 135),
                materials, firstTargets[index], $"v5 normalized {x},135", errors);
        }
        Require(SnapshotsEqual(checkpoints[0].Snapshot, checkpoints[1].Snapshot),
            "v5 save/clear/load changed the actual-core snapshot", errors);
        Require(checkpoints[1].ThermalTicks == 0 && checkpoints[1].PhaseDispatches == 0,
            "paused loaded v5 scene executed thermal or phase work", errors);
        for (int index = 0; index < firstTargets.Length - 1; index++)
        {
            int x = 200 + index * 20;
            Require(SamePhaseState(Cell(checkpoints[1].Snapshot, x, 135),
                    Cell(checkpoints[2].Snapshot, x, 135)),
                $"loaded stable v5 cell changed at {x},135", errors);
        }
        GridCell loadedExtreme = Cell(checkpoints[1].Snapshot, 280, 135);
        ValidateNormalized(loadedExtreme, Cell(checkpoints[2].Snapshot, 280, 135),
            materials, CoreMaterialIds.Steam, "loaded threshold cell", errors);
        Require(SnapshotsEqual(checkpoints[2].Snapshot, finalSnapshot),
            "final v5 snapshot changed after post-load phase checkpoint", errors);
        foreach ((int x, string expectedId) in new[]
        {
            (200, CoreMaterialIds.Ice), (220, CoreMaterialIds.Water),
            (240, CoreMaterialIds.Steam), (260, CoreMaterialIds.Water),
            (280, CoreMaterialIds.Steam)
        })
        {
            GridCell cell = Cell(finalSnapshot, x, 135);
            Require(materials[cell.MaterialIndex].Id == expectedId,
                $"v5 runtime remap expected={expectedId} actual={materials[cell.MaterialIndex].Id}", errors);
        }
        return 6;
    }

    private static void ValidateRegionNormalization(
        SimulationWorldSnapshot source,
        SimulationWorldSnapshot target,
        MaterialRegistry materials,
        int left,
        int top,
        int right,
        int bottom,
        string targetId,
        string label,
        List<string> errors)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                ValidateNormalized(Cell(source, x, y), Cell(target, x, y), materials,
                    targetId, $"{label} {x},{y}", errors);
            }
        }
    }

    private static void ValidateRegionUnchanged(
        SimulationWorldSnapshot source,
        SimulationWorldSnapshot target,
        int left,
        int top,
        int right,
        int bottom,
        string label,
        List<string> errors)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                Require(CellEquals(Cell(source, x, y), Cell(target, x, y)),
                    $"{label} at {x},{y}", errors);
            }
        }
    }

    private static void ValidateNormalized(
        GridCell source,
        GridCell actual,
        MaterialRegistry materials,
        string targetId,
        string label,
        List<string> errors)
    {
        uint target = materials.GetRequiredRuntimeIndex(targetId);
        MaterialProperties sourceProperties = materials[source.MaterialIndex].Properties;
        GridCell sourceAtPhasePass = source;
        bool hasAmbientCooling = sourceProperties.AmbientCoolingRate > 0;
        if (hasAmbientCooling)
        {
            float factor = 1 - MathF.Exp(
                -sourceProperties.AmbientCoolingRate * SimulationDispatchCoordinator.FixedThermalStep);
            sourceAtPhasePass.Temperature +=
                (sourceProperties.AmbientTemperature - sourceAtPhasePass.Temperature) * factor;
        }
        GridCell expected = PhaseTransitionRuntime.Normalize(
            sourceAtPhasePass,
            sourceProperties,
            materials[target].Properties,
            target);
        // GPU ambient cooling can be reduced by local material shelter. The
        // phase contract still requires every non-temperature field and the
        // locally cooled temperature to be preserved by normalization.
        const float ambientTemperatureTolerance = 0.05f;
        bool fieldsMatch = hasAmbientCooling
            ? CellEqualsExceptTemperature(expected, actual) &&
                Math.Abs(expected.Temperature - actual.Temperature) <= ambientTemperatureTolerance
            : CellEquals(expected, actual);
        Require(fieldsMatch,
            $"{label} fields expected={Describe(expected)} actual={Describe(actual)}", errors);
        bool latentBoil = sourceProperties.TransitionAboveLatentHeat > 0 &&
            materials[target].Properties.SimulationKind == (uint)MaterialSimulationKind.Gas;
        Require(SameBits(source.Mass, actual.Mass) &&
            (latentBoil
                ? SameBits(actual.Temperature,
                    sourceProperties.TransitionAboveTemperature)
                : hasAmbientCooling
                    ? Math.Abs(sourceAtPhasePass.Temperature - actual.Temperature) <=
                        ambientTemperatureTolerance
                    : SameBits(source.Temperature, actual.Temperature)),
            $"{label} did not preserve Mass/Temperature", errors);
    }

    private static bool CellEqualsExceptTemperature(GridCell left, GridCell right) =>
        left.MaterialIndex == right.MaterialIndex &&
        SameBits(left.Mass, right.Mass) &&
        SameBits(left.VelocityX, right.VelocityX) &&
        SameBits(left.VelocityY, right.VelocityY) &&
        SameBits(left.Pressure, right.Pressure) &&
        left.IsActive == right.IsActive &&
        left.BodyId == right.BodyId &&
        left.RestFrames == right.RestFrames &&
        SameBits(left.Lifetime, right.Lifetime);

    private static SimulationWorldSnapshot Initial(
        AcceptanceScenarioMode mode,
        SimulationWorldSnapshot dimensions,
        MaterialRegistry materials) =>
        PhaseAcceptanceScenario.Create(mode, dimensions.Width, dimensions.Height, materials) ??
        throw new InvalidOperationException($"Missing core phase fixture for {mode}.");

    private static MaterialMetrics Measure(SimulationWorldSnapshot snapshot, uint material)
    {
        return MeasureRegion(
            snapshot, material, 0, 0, snapshot.Width - 1, snapshot.Height - 1);
    }

    private static MaterialMetrics MeasureRegion(
        SimulationWorldSnapshot snapshot,
        uint material,
        int left,
        int top,
        int right,
        int bottom)
    {
        int count = 0;
        int resting = 0;
        double mass = 0;
        double weightedY = 0;
        int minimumX = snapshot.Width;
        int maximumX = -1;
        int minimumY = snapshot.Height;
        int maximumY = -1;
        ReadOnlySpan<GridCell> cells = Cells(snapshot);
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != material)
                {
                    continue;
                }
                count++;
                if (cell.RestFrames >= 60)
                {
                    resting++;
                }
                mass += cell.Mass;
                weightedY += y * cell.Mass;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
            }
        }
        return new MaterialMetrics(count, resting, mass, mass > 0 ? weightedY / mass : 0,
            minimumX, maximumX, minimumY, maximumY);
    }

    private static int CountMaterialInRegion(
        SimulationWorldSnapshot snapshot,
        uint material,
        int left,
        int top,
        int right,
        int bottom)
    {
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = Cell(snapshot, x, y);
                count += cell.IsActive != 0 && cell.MaterialIndex == material ? 1 : 0;
            }
        }
        return count;
    }

    private static GridCell Cell(SimulationWorldSnapshot snapshot, int x, int y) =>
        Cells(snapshot)[y * snapshot.Width + x];

    private static ReadOnlySpan<GridCell> Cells(SimulationWorldSnapshot snapshot) =>
        MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);

    private static bool SnapshotsEqual(SimulationWorldSnapshot left, SimulationWorldSnapshot right) =>
        left.Width == right.Width && left.Height == right.Height && left.Grid.AsSpan().SequenceEqual(right.Grid);

    private static bool CellEquals(GridCell left, GridCell right) =>
        left.MaterialIndex == right.MaterialIndex &&
        SameBits(left.Mass, right.Mass) &&
        SameBits(left.VelocityX, right.VelocityX) &&
        SameBits(left.VelocityY, right.VelocityY) &&
        SameBits(left.Pressure, right.Pressure) &&
        left.IsActive == right.IsActive &&
        left.BodyId == right.BodyId &&
        left.RestFrames == right.RestFrames &&
        SameBits(left.Temperature, right.Temperature);

    private static bool SamePhaseState(GridCell left, GridCell right) =>
        left.MaterialIndex == right.MaterialIndex &&
        SameBits(left.Mass, right.Mass) &&
        left.IsActive == right.IsActive;

    private static bool SameBits(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static string Describe(GridCell cell) =>
        $"{cell.MaterialIndex}/{cell.Mass:R}/{cell.Temperature:R}/" +
        $"{cell.VelocityX:R}/{cell.VelocityY:R}/{cell.Pressure:R}/" +
        $"{cell.IsActive}/{cell.BodyId}/{cell.RestFrames}";

    private static void Require(bool condition, string error, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(error);
        }
    }

    private readonly record struct MaterialMetrics(
        int Cells,
        int RestingCells,
        double Mass,
        double AverageY,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY);
}
