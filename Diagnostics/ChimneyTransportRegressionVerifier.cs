using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

// Exercise the real GPU transport, with prescribed carrier so combustion and
// pressure feedback cannot hide a loss of axial velocity or a wall crossing.
internal static class ChimneyTransportRegressionVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var r = coordinator.DispatchFrame(new SimulationSettings { Paused = true },
            [new() { X = 40, EndX = 40, Y = 40, EndY = 40, Radius = 1, Density = 1,
                Mode = BrushCommandMode.Material, MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire) }], 0);
        int n = r.Width * r.Height, checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(name);
        }
        (GridCell[] Grid, GasMotionState[] Motion, byte[][] Raw) Run(
            GridCell[] grid, AirCell[] air, bool finite, int fps = 60, int ticks = 60)
        {
            r.Context.UpdateSubresource(grid, r.Grid.ReadBuffer);
            r.Context.UpdateSubresource(new GasMotionState[n], r.GasMotion.Buffer);
            r.Context.UpdateSubresource(grid.Select(c => c.IsActive == 0 ? 0u : c.MaterialIndex).ToArray(), r.CellMaterials.Buffer);
            r.Context.UpdateSubresource(air, r.Air.Buffer);
            uint completed = 0;
            for (int frame = 1; completed < ticks; frame++)
            {
                uint target = (uint)Math.Min(ticks, (int)Math.Floor(frame * 60.0 / fps + 1e-8));
                while (completed < target)
                {
                    completed++;
                    var c = new SimulationFrameConstants { Width = (uint)r.Width, Height = (uint)r.Height,
                        FrameIndex = (uint)frame, DebugReserved2 = completed, DeltaTime = 1f / 60, MaximumVelocity = 5000 };
                    coordinator.DispatchGasMotion(r, ref c, true, finite, true);
                }
            }
            byte[][] raw = [AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer),
                AirInventoryRegressionVerifier.Read(r, r.GasMotion.Buffer),
                AirInventoryRegressionVerifier.Read(r, r.CellMaterials.Buffer)];
            return (MemoryMarshal.Cast<byte, GridCell>(raw[0]).ToArray(),
                MemoryMarshal.Cast<byte, GasMotionState>(raw[1]).ToArray(), raw);
        }
        AirCell[] Carrier(float x, float y) => Enumerable.Repeat(new AirCell { VelocityX = x, VelocityY = y }, r.AirWidth * r.AirHeight).ToArray();
        GridCell Cell(string id) => new() { MaterialIndex = registry.GetRequiredRuntimeIndex(id), IsActive = 1,
            Mass = registry[id].Properties.Density, Temperature = 340, Lifetime = 20 };
        void Conserved(GridCell[] before, GridCell[] after, string gas)
        {
            uint id = registry.GetRequiredRuntimeIndex(gas);
            double mass0 = before.Where(c => c.IsActive != 0 && c.MaterialIndex == id).Sum(c => (double)c.Mass);
            double mass1 = after.Where(c => c.IsActive != 0 && c.MaterialIndex == id).Sum(c => (double)c.Mass);
            double heat0 = before.Where(c => c.IsActive != 0 && c.MaterialIndex == id).Sum(c => (double)c.Mass * c.Temperature);
            double heat1 = after.Where(c => c.IsActive != 0 && c.MaterialIndex == id).Sum(c => (double)c.Mass * c.Temperature);
            Check(Math.Abs(mass1 - mass0) <= mass0 * .0001 && Math.Abs(heat1 - heat0) <= Math.Abs(heat0) * .0001,
                $"Mass/heat changed: {gas}");
        }
        foreach (bool finite in new[] { false, true })
        foreach (string gas in new[] { CoreMaterialIds.Fire, CoreMaterialIds.Smoke, CoreMaterialIds.Co2, CoreMaterialIds.Steam })
        {
            var grid = new GridCell[n];
            // Diffusive gases need an ensemble, not a single narrow row at
            // one random instant. Keep sparse16-pixel spacing and the same
            // strong/weak threshold; axial FIRE response is deterministic.
            for (int y = 96; y <= r.Height - 50; y += 16)
            for (int x = 24; x < r.Width - 24; x += 16) grid[y * r.Width + x] = Cell(gas);
            double[] speeds = new double[2];
            for (int strength = 0; strength < 2; strength++)
            {
                float speed = strength == 0 ? .4f : .8f;
                var result = Run(grid, Carrier(0, -speed), finite);
                var packets = Enumerable.Range(0, n).Where(i => result.Grid[i].IsActive != 0).ToArray();
                speeds[strength] = packets.Average(i => -(double)result.Motion[i].VelocityY);
                Conserved(grid, result.Grid, gas);
                if (gas == CoreMaterialIds.Fire)
                {
                    // No axial diffusion for FIRE. Compare measured response to
                    // its existing steady carrier gain; a transverse model may
                    // not borrow speed from that component.
                    var m = registry[gas].Properties;
                    double gain = finite ? Math.Min(m.MotionAdvection, 1 - m.MotionLoss) : m.MotionAdvection;
                    double expected = (speed * gain - m.GasBuoyancy) / (1 - m.MotionLoss);
                    Check(Math.Abs(speeds[strength] / expected - 1) < .05, "Axial response reduced");
                    Check(packets.All(i => Math.Abs(result.Motion[i].VelocityX) <= .001f),
                        "Unconfined zero-diffusion flame received transverse dispersion");
                }
                if (strength == 1)
                    foreach (int fps in new[] { 30, 100 })
                    {
                        var other = Run(grid, Carrier(0, -speed), finite, fps);
                        Check(result.Raw.Zip(other.Raw).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
                            $"Transport depends on render FPS: {gas}/{finite}/{fps}");
                    }
            }
            Console.WriteLine($"PHYXEL_CHIMNEY_SPEED gas={gas} finite={finite} weak={speeds[0]:F5} strong={speeds[1]:F5}");
            Check(speeds[1] >= 1.5 * speeds[0], $"Stronger carrier does not accelerate {gas}");
        }
        // Boundary mixing must still work in a channel. The free-stream test
        // above cannot prove that a geometry gate has not disabled it entirely.
        foreach (bool finite in new[] { false, true })
        {
            var grid = new GridCell[n];
            for (int y = 0; y < r.Height; y++)
            {
                grid[y * r.Width + 200] = Cell(CoreMaterialIds.Metal);
                grid[y * r.Width + 232] = Cell(CoreMaterialIds.Metal);
            }
            for (int y = 180; y <= 236; y += 8) grid[y * r.Width + 216] = Cell(CoreMaterialIds.Fire);
            var result = Run(grid, Carrier(0, -.8f), finite);
            uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire);
            int[] packets = Enumerable.Range(0, n).Where(i => result.Grid[i].IsActive != 0 &&
                result.Grid[i].MaterialIndex == fire).ToArray();
            Check(packets.Length == 8 && packets.All(i => i % r.Width > 200 && i % r.Width < 232),
                $"Channel lost a packet or crossed a wall: {finite}");
            Check(packets.Average(i => Math.Abs(result.Motion[i].VelocityX)) > .1f &&
                packets.Max(i => i % r.Width) - packets.Min(i => i % r.Width) >= 5,
                $"Boundary dispersion disappeared: {finite}");
            Conserved(grid, result.Grid, CoreMaterialIds.Fire);
            foreach (int fps in new[] { 30, 100 })
            {
                var other = Run(grid, Carrier(0, -.8f), finite, fps);
                Check(result.Raw.Zip(other.Raw).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
                    $"Channel depends on render FPS: {finite}/{fps}");
            }
        }
        // A long ceiling: tangential movement must survive impact, instead of
        // being reset to zero or fixed .9 every time the gas reaches the roof.
        foreach (bool finite in new[] { false, true })
        {
            var grid = new GridCell[n];
            for (int x = 10; x < r.Width - 10; x++) grid[99 * r.Width + x] = Cell(CoreMaterialIds.Metal);
            for (int x = 40; x <= 264; x += 32) grid[100 * r.Width + x] = Cell(CoreMaterialIds.Fire);
            var result = Run(grid, Carrier(2, -.7f), finite, ticks: 50);
            int[] packets = Enumerable.Range(0, n).Where(i => result.Grid[i].IsActive != 0 &&
                result.Grid[i].MaterialIndex == registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire)).ToArray();
            Check(packets.Length == 8 && packets.All(i => i / r.Width >= 100) &&
                packets.Average(i => i % r.Width) > 222 && packets.Average(i => result.Motion[i].VelocityX) > 1.8,
                $"Ceiling killed tangent, finite={finite}");
            Conserved(grid, result.Grid, CoreMaterialIds.Fire);
        }
        // Shifted one-cell walls both orientations, adverse normal wind. No
        // packet may cross, even with interpolation and transverse dispersion.
        foreach (bool horizontal in new[] { false, true })
        foreach (int shift in new[] { 0, 1, 2, 3 })
        {
            int wall = 120 + shift;
            var grid = new GridCell[n];
            if (horizontal) for (int x = 0; x < r.Width; x++) grid[wall * r.Width + x] = Cell(CoreMaterialIds.Metal);
            else for (int y = 0; y < r.Height; y++) grid[y * r.Width + wall] = Cell(CoreMaterialIds.Metal);
            int source = horizontal ? (wall + 2) * r.Width + 70 : 180 * r.Width + wall + 2;
            grid[source] = Cell(CoreMaterialIds.Smoke);
            var result = Run(grid, horizontal ? Carrier(1.5f, -1) : Carrier(-1, -.5f), true, ticks: 40);
            uint smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
            Check(Enumerable.Range(0, n).Where(i => result.Grid[i].IsActive != 0 && result.Grid[i].MaterialIndex == smoke)
                .All(i => horizontal ? i / r.Width > wall : i % r.Width > wall), $"Crossed fine wall {horizontal}/{shift}");
            Conserved(grid, result.Grid, CoreMaterialIds.Smoke);
        }
        // The wall-jet escape must find a real opening. A sealed chamber
        // retains every packet and its heat even with a packed smoke layer.
        foreach (bool finite in new[] { false, true })
        {
            var grid = new GridCell[n];
            for (int x = 160; x <= 320; x++)
            {
                grid[180 * r.Width + x] = Cell(CoreMaterialIds.Metal);
                grid[215 * r.Width + x] = Cell(CoreMaterialIds.Metal);
            }
            for (int y = 181; y < 215; y++)
            {
                grid[y * r.Width + 160] = Cell(CoreMaterialIds.Metal);
                grid[y * r.Width + 320] = Cell(CoreMaterialIds.Metal);
            }
            for (int y = 181; y <= 192; y++)
            for (int x = 161; x < 320; x++) grid[y * r.Width + x] = Cell(CoreMaterialIds.Smoke);
            var result = Run(grid, Carrier(0, 0), finite, ticks: 180);
            uint smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke);
            Check(Enumerable.Range(0, n).Where(i => result.Grid[i].IsActive != 0 && result.Grid[i].MaterialIndex == smoke)
                .All(i => i % r.Width > 160 && i % r.Width < 320 && i / r.Width > 180 && i / r.Width < 215),
                $"Sealed smoke chamber leaked: {finite}");
            Conserved(grid, result.Grid, CoreMaterialIds.Smoke);
        }
        string root = Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/chimney-transport";
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "result.json"), $"{{\"passed\":true,\"checks\":{checks}}}");
        Console.WriteLine($"PHYXEL_CHIMNEY_TRANSPORT_SUCCESS checks={checks}");
    }
}
