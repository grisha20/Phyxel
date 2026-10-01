using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class OxidizerAcceptance
{
    private static SimulationWorldSnapshot? initial;
    internal static bool EmptyLoad => Environment.GetEnvironmentVariable("PHYXEL_OXIDIZER_EMPTY_LOAD") == "1";
    private static bool EmptyOpen => Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES") == "1";
    internal static bool Restarting => EmptyLoad || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHYXEL_OXIDIZER_RESTART"));
    internal static int Fps => int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"), out int fps) ? fps : 60;
    internal static uint Frame(int seconds) => (uint)(seconds * Fps);
    private static (int X, int Y) Corner(int box) => (150 + box % 4 * 70, 65 + box / 4 * 90);

    internal static SimulationWorldSnapshot? Create(AcceptanceScenarioMode mode, int width, int height, MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.Oxidizer) return null;
        if (EmptyLoad)
        {
            float[] exhaustedAir = new float[width * height];
            Array.Fill(exhaustedAir, .37f);
            initial = new(width, height, new byte[width * height * 40],
                Oxidizer: MemoryMarshal.AsBytes(exhaustedAir.AsSpan()).ToArray());
            Task.Run(() => new SimulationStateSerializer().SaveAsync(
                Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SCENE_PATH")!,
                new SimulationSettings { Scale = .25f, OpenBoundaries = EmptyOpen },
                (ushort)materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2), initial, materials)).GetAwaiter().GetResult();
            // Load through the ordinary game path: an empty world still owns air.
            return null;
        }
        if (Restarting)
            return initial = Task.Run(async () => (await new SimulationStateSerializer().LoadAsync(
                Environment.GetEnvironmentVariable("PHYXEL_OXIDIZER_RESTART")!, materials))?.World
                ?? throw new InvalidDataException("Missing oxidizer restart world.")).GetAwaiter().GetResult();
        byte[] bytes = new byte[width * height * 40];
        float[] oxygen = new float[width * height];
        Array.Fill(oxygen, 1);
        void Put(int x, int y, string id, float temperature)
        {
            var material = materials[id];
            MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan())[y * width + x] = new GridCell
            { MaterialIndex = material.RuntimeIndex, Mass = material.Properties.Density, Temperature = temperature,
                IsActive = 1, Lifetime = material.Properties.MaximumLifetime };
            oxygen[y * width + x] = material.Properties.SimulationKind == (uint)MaterialSimulationKind.Gas &&
                (material.Properties.Flags & (uint)MaterialFlags.Flame) != 0 ? 1 : 0;
        }
        for (int box = 0; box < 8; box++)
        {
            var (x, y) = Corner(box);
            for (int yy = y; yy <= y + 8; yy++) for (int xx = x; xx <= x + 12; xx++)
                if (xx == x || xx == x + 12 || yy == y + 8 || (yy == y && box != 1))
                    Put(xx, yy, CoreMaterialIds.Fixture, 20);
            if (box is 3 or 4 or 5 or 6)
                for (int yy = y + 1; yy < y + 8; yy++) for (int xx = x + 1; xx < x + 12; xx++)
                    Put(xx, yy, box == 4 ? CoreMaterialIds.Steam : CoreMaterialIds.Co2, box == 4 ? 150 : 20);
            if (box < 7)
                for (int j = 0; j < 5; j++) Put(x + 2 + j * 2, y + 7,
                    box == 5 ? "test:self_oxygen_fuel" : box == 6 ? CoreMaterialIds.Gunpowder : "test:oxygen_fuel",
                    box == 6 ? (j == 0 ? 251 : 20) : 450);
            if (box is 3 or 4) Put(x + 6, y + 4, CoreMaterialIds.Fire, 420);
            if (box == 7)
                for (int yy = y + 1; yy < y + 8; yy++) for (int xx = x + 1; xx < x + 12; xx++)
                    oxygen[yy * width + xx] = .37f;
        }
        initial = new(width, height, bytes, Oxidizer: MemoryMarshal.AsBytes(oxygen.AsSpan()).ToArray());
        return initial;
    }

    internal static IReadOnlyList<BrushDrawCommand> Commands(uint frame)
    {
        if (Restarting || frame != Frame(40)) return [];
        var (x, y) = Corner(2);
        return [new() { X = x + 1, Y = y, EndX = x + 11, EndY = y, Radius = 0,
            Shape = BrushCommandShape.Segment, Density = 1, Mode = BrushCommandMode.Erase }];
    }

    private static (double Fuel, double Oxygen, double MinOxygen, double MaxOxygen, int Flames, int Powder) Measure(
        SimulationWorldSnapshot world, MaterialRegistry materials, int box)
    {
        var cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        var oxygen = MemoryMarshal.Cast<byte, float>(world.Oxidizer ?? throw new InvalidDataException("No oxygen readback."));
        var (x, y) = Corner(box);
        uint fuel = materials.GetRequiredRuntimeIndex(box == 5 ? "test:self_oxygen_fuel" : "test:oxygen_fuel");
        uint powder = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder);
        double mass = 0, amount = 0, min = 1, max = 0; int flames = 0, powders = 0;
        for (int yy = y + 1; yy < y + 8; yy++) for (int xx = x + 1; xx < x + 12; xx++)
        {
            int i = yy * world.Width + xx; var c = cells[i];
            amount += oxygen[i]; min = Math.Min(min, oxygen[i]); max = Math.Max(max, oxygen[i]);
            if (c.IsActive == 0) continue;
            if (c.MaterialIndex == fuel) mass += c.Mass;
            if (c.MaterialIndex == powder) powders++;
            if ((materials[c.MaterialIndex].Properties.Flags & (uint)MaterialFlags.Flame) != 0) flames++;
        }
        return (mass, amount, min, max, flames, powders);
    }

    internal static bool Validate(SimulationWorldSnapshot final, MaterialRegistry materials,
        IReadOnlyList<ThermalAcceptanceCheckpoint> checkpoints, string directory, out string report)
    {
        if (initial is null) throw new InvalidOperationException("Missing initial oxygen world.");
        if (EmptyLoad)
        {
            bool retained = final.Grid.AsSpan().SequenceEqual(initial.Grid) &&
                final.Oxidizer is not null && final.Oxidizer.AsSpan().SequenceEqual(initial.Oxidizer);
            if (EmptyOpen && final.Oxidizer is not null)
            {
                var oxygen = MemoryMarshal.Cast<byte, float>(final.Oxidizer);
                retained = final.Grid.AsSpan().SequenceEqual(initial.Grid) &&
                    oxygen[final.Width / 2] == 1 &&
                    oxygen[(final.Height / 2) * final.Width] == 1 &&
                    oxygen[(final.Height - 1) * final.Width + final.Width / 2] == .37f;
            }
            report = $"PHYXEL_OXIDIZER_EMPTY_LOAD retained={retained} openSides={EmptyOpen} cells={final.Width * final.Height}";
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "report.txt"), report);
            return retained;
        }
        var samples = checkpoints.Select(c => (c.Frame, c.Snapshot)).Append(((uint)(Restarting ? Frame(10) : Frame(60)), final)).ToArray();
        List<string> csv = ["frame,box,fuel,oxygen,minOxygen,maxOxygen,flames,powder"];
        foreach (var (frame, world) in samples)
            for (int box = 0; box < 8; box++)
            {
                var m = Measure(world, materials, box);
                csv.Add(FormattableString.Invariant($"{frame},{box},{m.Fuel:F6},{m.Oxygen:F6},{m.MinOxygen:F6},{m.MaxOxygen:F6},{m.Flames},{m.Powder}"));
            }
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "oxidizer-checkpoints.csv"), csv);
        var closed = Measure(final, materials, 0); var open = Measure(final, materials, 1);
        var vent = Measure(final, materials, 2); var co2 = Measure(final, materials, 3);
        var steam = Measure(final, materials, 4); var self = Measure(final, materials, 5);
        var powderFinal = Measure(final, materials, 6); var uniform = Measure(final, materials, 7);
        bool saved = Task.Run(async () => {
            var serializer = new SimulationStateSerializer();
            string path = Path.Combine(directory, "final.scene.json");
            await serializer.SaveAsync(path, new SimulationSettings { OpenBoundaries = false },
                (ushort)materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2), final, materials);
            var loaded = (await serializer.LoadAsync(path, materials))?.World;
            return loaded is not null && loaded.Grid.AsSpan().SequenceEqual(final.Grid) &&
                loaded.Oxidizer is not null && loaded.Oxidizer.AsSpan().SequenceEqual(final.Oxidizer);
        }).GetAwaiter().GetResult();
        bool pass = saved && co2.Fuel >= 24.999 && steam.Fuel >= 24.999 && co2.Flames == 0 && steam.Flames == 0 &&
            powderFinal.Powder == 0 && Math.Abs(uniform.Oxygen - .37 * 77) < .0001;
        var early = checkpoints[0].Snapshot;
        pass &= Measure(early, materials, 3).Flames == 0 && Measure(early, materials, 4).Flames == 0;
        // In a sealed, emission-free chamber, diffusion only redistributes
        // oxygen. Fuel consumption must account for its entire decrease.
        double oxygenResidual = closed.Oxygen - (72 - (25 - closed.Fuel) * 20);
        if (!Restarting) pass &= Math.Abs(oxygenResidual) < .003;
        double plateau = 0, resumed = 0;
        if (Restarting)
        {
            plateau = Math.Abs(Measure(initial, materials, 0).Fuel - closed.Fuel);
            pass &= plateau < .003 && initial.Oxidizer is not null;
        }
        else
        {
            var at30 = checkpoints.Last(c => c.Frame <= Frame(31)).Snapshot;
            var at40 = checkpoints.Last(c => c.Frame <= Frame(40)).Snapshot;
            plateau = Math.Abs(Measure(at30, materials, 0).Fuel - closed.Fuel);
            resumed = Measure(at40, materials, 2).Fuel - vent.Fuel;
            pass &= closed.Fuel < 24.8 && closed.Fuel > 20 && plateau < .003 && open.Fuel < closed.Fuel - 1 &&
                resumed > .1 && Math.Abs(self.Fuel - 10) < .04;
        }
        report = string.Create(CultureInfo.InvariantCulture,
            $"PHYXEL_OXIDIZER closedFuel={closed.Fuel:F6} openFuel={open.Fuel:F6} ventilatedFuel={vent.Fuel:F6} closedOxygen={closed.Oxygen:F6} oxygenResidual={oxygenResidual:F6} plateauLoss={plateau:F6} resumedBurn={resumed:F6} co2Fuel={co2.Fuel:F6} steamFuel={steam.Fuel:F6} selfFuel={self.Fuel:F6} powder={powderFinal.Powder} uniformOxygen={uniform.Oxygen:F6} saved={saved} restart={Restarting}");
        File.WriteAllText(Path.Combine(directory, "report.txt"), report);
        return pass;
    }
}
