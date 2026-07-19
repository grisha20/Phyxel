using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class CoalTypesAcceptanceVerifier
{
    public static bool Validate(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints,
        out string report)
    {
        List<string> errors = [];
        Require(checkpoints.Count == 4,
            $"coal checkpoints expected=4 actual={checkpoints.Count}", errors);
        bool hasWet = materials.TryGet(CoalTypesAcceptanceScenario.WetCharcoalId, out MaterialDefinition wet);
        bool hasStone = materials.TryGet(CoalTypesAcceptanceScenario.StoneCoalId, out MaterialDefinition stone);
        bool hasLight = materials.TryGet(CoalTypesAcceptanceScenario.ExternalLightId, out MaterialDefinition light);
        bool hasHeavy = materials.TryGet(CoalTypesAcceptanceScenario.ExternalHeavyId, out MaterialDefinition heavy);
        Require(hasWet, "core:wet_charcoal is missing", errors);
        Require(hasStone, "core:stone_coal is missing", errors);
        Require(hasLight && hasHeavy, "acceptance light/heavy granular materials are missing", errors);

        if (checkpoints.Count > 0)
        {
            SimulationWorldSnapshot initial = CoalTypesAcceptanceScenario.CreateInitialWorld(
                AcceptanceScenarioMode.CoalTypes, snapshot.Width, snapshot.Height, materials) ??
                throw new InvalidOperationException("Missing coal_types fixture.");
            Require(initial.Grid.AsSpan().SequenceEqual(checkpoints[0].Snapshot.Grid),
                "Pause changed the initial coal fixture or accumulated catch-up", errors);
            Require(checkpoints[0].ThermalTicks == 0,
                $"Pause advanced thermal ticks={checkpoints[0].ThermalTicks}", errors);
        }

        uint dryIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        MaterialMetrics dryFinal = Measure(snapshot, dryIndex, 10, 95);
        MaterialMetrics wetFinal = hasWet
            ? Measure(snapshot, wet.RuntimeIndex, 10, 185)
            : default;
        MaterialMetrics stoneFinal = hasStone
            ? Measure(snapshot, stone.RuntimeIndex, 190, 275)
            : default;
        MaterialMetrics lightFinal = hasLight
            ? Measure(snapshot, light.RuntimeIndex, 280, 365)
            : default;
        MaterialMetrics heavyFinal = hasHeavy
            ? Measure(snapshot, heavy.RuntimeIndex, 370, 455)
            : default;

        if (checkpoints.Count >= 2)
        {
            SimulationWorldSnapshot early = checkpoints[1].Snapshot;
            MaterialMetrics earlyDry = Measure(early, dryIndex, 10, 95);
            MaterialMetrics earlyWet = hasWet ? Measure(early, wet.RuntimeIndex, 10, 95) : default;
            MaterialMetrics earlyLight = hasLight ? Measure(early, light.RuntimeIndex, 280, 365) : default;
            MaterialMetrics earlyCharcoal = Combine(earlyDry, earlyWet);
            Require(earlyDry.Cells > 60,
                $"dry charcoal soaked too quickly after one second={earlyDry}", errors);
            Require(earlyCharcoal.AverageY < 158 && earlyDry.HorizontalSpan >= 30 &&
                earlyDry.MaximumVerticalRun <= 3,
                $"dry charcoal did not rise and spread in a thin surface layer={earlyCharcoal}", errors);
            Require(!hasLight || earlyLight.AverageY < 158,
                $"external light granular did not rise={earlyLight}", errors);
        }

        if (hasWet)
        {
            MaterialMetrics wetFromDry = Measure(snapshot, wet.RuntimeIndex, 10, 95);
            Require(dryFinal.Cells < 60 && wetFromDry.Cells > 100,
                $"dry charcoal did not progressively become wet dry={dryFinal} wet={wetFromDry}", errors);
            Require(wetFromDry.AverageY > 195,
                $"wet charcoal converted from dry did not sink={wetFromDry}", errors);
            MaterialMetrics initiallyWet = Measure(snapshot, wet.RuntimeIndex, 100, 185);
            Require(initiallyWet.Cells == CoalTypesAcceptanceScenario.InitialGranularMass &&
                initiallyWet.AverageY > 205,
                $"initial wet charcoal did not reach the bottom={initiallyWet}", errors);
        }
        if (hasStone)
        {
            Require(stoneFinal.Cells == CoalTypesAcceptanceScenario.InitialGranularMass &&
                stoneFinal.AverageY > 205,
                $"stone coal did not sink to the bottom={stoneFinal}", errors);
        }
        if (hasLight)
        {
            Require(lightFinal.Cells == CoalTypesAcceptanceScenario.InitialGranularMass &&
                lightFinal.AverageY < 115 &&
                lightFinal.HorizontalSpan >= 30 && lightFinal.MaximumVerticalRun <= 3,
                $"external light granular did not float/spread={lightFinal}", errors);
        }
        if (hasHeavy)
        {
            Require(heavyFinal.Cells == CoalTypesAcceptanceScenario.InitialGranularMass &&
                heavyFinal.AverageY > 205,
                $"external heavy granular did not sink={heavyFinal}", errors);
        }

        double initialCharcoalMass = CoalTypesAcceptanceScenario.InitialGranularMass *
            (hasWet ? 2 : 1);
        double finalCharcoalMass = dryFinal.Mass + wetFinal.Mass;
        Require(Math.Abs(initialCharcoalMass - finalCharcoalMass) <= 0.001,
            $"charcoal family mass changed={initialCharcoalMass:F3}/{finalCharcoalMass:F3}", errors);
        Require(!hasStone || Math.Abs(stoneFinal.Mass -
            CoalTypesAcceptanceScenario.InitialGranularMass) <= 0.001,
            $"stone coal mass changed={stoneFinal.Mass:F3}", errors);
        Require(!hasLight || Math.Abs(lightFinal.Mass -
            CoalTypesAcceptanceScenario.InitialGranularMass) <= 0.001,
            $"external light mass changed={lightFinal.Mass:F3}", errors);
        Require(!hasHeavy || Math.Abs(heavyFinal.Mass -
            CoalTypesAcceptanceScenario.InitialGranularMass) <= 0.001,
            $"external heavy mass changed={heavyFinal.Mass:F3}", errors);
        bool roundTrip = hasWet && hasStone && VerifyV6RoundTrip(snapshot, materials);
        Require(roundTrip, "dry/wet/stone coal v6 string-palette round-trip failed", errors);

        report = $"PHYXEL_COAL_TYPES dry={dryFinal} wet={wetFinal} stone={stoneFinal} " +
            $"externalLight={lightFinal} externalHeavy={heavyFinal} " +
            $"charcoalMass={initialCharcoalMass:F3}/{finalCharcoalMass:F3} roundTrip={roundTrip}";
        if (errors.Count == 0)
        {
            return true;
        }
        WriteColumnProfile(snapshot, lightFinal.Cells > 0 ? light.RuntimeIndex : dryIndex,
            lightFinal.Cells > 0 ? 280 : 10,
            lightFinal.Cells > 0 ? 365 : 95);
        report += Environment.NewLine + "PHYXEL_COAL_TYPES_FAILURE " +
            string.Join("; ", errors.Take(12));
        return false;
    }

    private static MaterialMetrics Measure(
        SimulationWorldSnapshot snapshot,
        uint material,
        int minimumX,
        int maximumX)
    {
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        int count = 0;
        double mass = 0;
        double weightedY = 0;
        int minX = int.MaxValue;
        int maxX = int.MinValue;
        int maximumVerticalRun = 0;
        int[] currentRuns = new int[maximumX - minimumX + 1];
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = minimumX; x <= maximumX; x++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != material)
                {
                    currentRuns[x - minimumX] = 0;
                    continue;
                }
                count++;
                mass += cell.Mass;
                weightedY += y * cell.Mass;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                int run = ++currentRuns[x - minimumX];
                maximumVerticalRun = Math.Max(maximumVerticalRun, run);
            }
        }
        return new MaterialMetrics(
            count,
            mass,
            mass > 0 ? weightedY / mass : 0,
            count > 0 ? maxX - minX + 1 : 0,
            maximumVerticalRun);
    }

    private static MaterialMetrics Combine(MaterialMetrics first, MaterialMetrics second)
    {
        double mass = first.Mass + second.Mass;
        return new MaterialMetrics(
            first.Cells + second.Cells,
            mass,
            mass > 0 ?
                (first.AverageY * first.Mass + second.AverageY * second.Mass) / mass : 0,
            Math.Max(first.HorizontalSpan, second.HorizontalSpan),
            Math.Max(first.MaximumVerticalRun, second.MaximumVerticalRun));
    }

    private static bool VerifyV6RoundTrip(
        SimulationWorldSnapshot snapshot,
        MaterialRegistry materials)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"phyxel-coal-types-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] original = (byte[])snapshot.Grid.Clone();
            string scenePath = Path.Combine(directory, "coal-types.json");
            SimulationStateSerializer serializer = new();
            LoadedSimulationScene? loaded = Task.Run(async () =>
            {
                await serializer.SaveAsync(
                    scenePath,
                    new SimulationSettings(),
                    materials.GetRequiredRuntimeIndex(CoreMaterialIds.StoneCoal),
                    snapshot,
                    materials);
                return await serializer.LoadAsync(scenePath, materials);
            }).GetAwaiter().GetResult();
            if (!snapshot.Grid.AsSpan().SequenceEqual(original) ||
                loaded?.World is null ||
                !loaded.World.Grid.AsSpan().SequenceEqual(original))
            {
                return false;
            }
            string json = File.ReadAllText(scenePath);
            return json.Contains(CoreMaterialIds.Coal, StringComparison.Ordinal) &&
                json.Contains(CoreMaterialIds.WetCharcoal, StringComparison.Ordinal) &&
                json.Contains(CoreMaterialIds.StoneCoal, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void Require(bool condition, string error, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(error);
        }
    }

    private static void WriteColumnProfile(
        SimulationWorldSnapshot snapshot,
        uint material,
        int minimumX,
        int maximumX)
    {
        string? artifactDirectory = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(artifactDirectory))
        {
            return;
        }
        Directory.CreateDirectory(artifactDirectory);
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        List<string> lines = ["x,count,minY,maxY"];
        for (int x = minimumX; x <= maximumX; x++)
        {
            int count = 0;
            int minimumY = int.MaxValue;
            int maximumY = int.MinValue;
            for (int y = 0; y < snapshot.Height; y++)
            {
                GridCell cell = cells[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialIndex != material)
                {
                    continue;
                }
                count++;
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
            }
            lines.Add($"{x},{count},{(count == 0 ? -1 : minimumY)},{(count == 0 ? -1 : maximumY)}");
        }
        File.WriteAllLines(Path.Combine(artifactDirectory, "coal-column-profile.csv"), lines);
    }

    private readonly record struct MaterialMetrics(
        int Cells,
        double Mass,
        double AverageY,
        int HorizontalSpan,
        int MaximumVerticalRun)
    {
        public override string ToString() =>
            $"{Cells}/{Mass:F3}/y={AverageY:F1}/span={HorizontalSpan}/run={MaximumVerticalRun}";
    }
}
