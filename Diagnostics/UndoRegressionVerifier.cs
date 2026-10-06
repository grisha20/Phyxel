using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using Phyxel.UI;

namespace Phyxel.Diagnostics;

internal static class UndoRegressionVerifier
{
    internal static void Run(PhyxelGame game, SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir = Path.GetFullPath(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/undo");
        Directory.CreateDirectory(dir);
        int checks = 0;
        void Check(bool ok, string label)
        {
            checks++;
            if (!ok) throw new InvalidDataException(label);
            Console.WriteLine("UNDO_PASS " + label);
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Get<T>(string name) => (T)typeof(PhyxelGame).GetField(name, flags)!.GetValue(game)!;
        void Set(string name, object? value) => typeof(PhyxelGame).GetField(name, flags)!.SetValue(game, value);
        var settings = Get<SimulationSettings>("settings");
        settings.Width = 480; settings.Height = 270; settings.Paused = true;
        settings.SpawnDensity = 1; settings.BrushRadius = 3; settings.Mode = SimulationMode.Simulation;
        var history = Get<WorldEditHistory>("editHistory");
        var serializer = new SimulationStateSerializer();
        var r = coordinator.DispatchFrame(settings, [Command(50, 50, registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal))], 0);
        Set("currentResources", r);

        SimulationWorldSnapshot Capture()
        {
            var timer = Stopwatch.StartNew();
            serializer.BeginWorldCapture(r);
            SimulationWorldSnapshot? result;
            while (!serializer.TryCompleteWorldCapture(r, out result))
            {
                if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("GPU snapshot");
                System.Threading.Thread.Yield();
            }
            return result!;
        }
        byte[]?[] Fields(SimulationWorldSnapshot s) => [s.Grid, s.Air, s.GasMotion, s.Oxidizer,
            s.AirThermal, s.ReactionPending, s.ReactionPulse, s.Filters];
        void Equal(SimulationWorldSnapshot a, SimulationWorldSnapshot b, string label)
        {
            var left = Fields(a); var right = Fields(b);
            for (int i = 0; i < left.Length; i++)
                Check((left[i] ?? []).AsSpan().SequenceEqual(right[i] ?? []), label + " field " + i);
        }
        void Edit(BrushDrawCommand c, bool start = true, float elapsed = 0)
        { r = game.DispatchEditorFrame([c], start, elapsed); Set("currentResources", r); }
        void Restore(bool forward, bool expected = true)
        {
            Check(game.RestoreEditHistory(forward) == expected, forward ? "redo result" : "undo result");
            r = Get<GpuSimulationResources>("currentResources");
        }

        // Use recognizable nonzero physical auxiliary fields; all must survive
        // undo, including fields that a grid-only implementation would lose.
        var initial = Capture();
        MemoryMarshal.Cast<byte, AirCell>(initial.Air!)[0] = new() { Pressure = .25f, VelocityX = .3f };
        MemoryMarshal.Cast<byte, GasMotionState>(initial.GasMotion!)[0] = new() { VelocityX = .3f, VelocityY = -.1f };
        MemoryMarshal.Cast<byte, float>(initial.Oxidizer!)[0] = 2.5f;
        MemoryMarshal.Cast<byte, System.Numerics.Vector2>(initial.AirThermal!)[0] = new(300, 1);
        MemoryMarshal.Cast<byte, System.Numerics.Vector4>(initial.ReactionPending!)[0] = new(.1f, 300, 1, 0);
        MemoryMarshal.Cast<byte, System.Numerics.Vector4>(initial.ReactionPulse!)[0] = new(.1f, .2f, -.3f, .4f);
        uint[] filter = new uint[r.Width * r.Height]; filter[100 * r.Width + 100] = FilterRules.Closed;
        initial = initial with { Filters = MemoryMarshal.AsBytes(filter.AsSpan()).ToArray() };
        serializer.ApplyWorldSnapshot(r, initial);
        coordinator.RestoreWorldActivity(r, true, false, false, true);

        var brush = Get<CanvasBrushController>("brushController");
        Rectangle canvas = new(0, 0, 480, 270);
        ushort metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal);
        void Gesture(RawInputSnapshot input)
        {
            var commands = brush.CreateCommands(input, canvas, settings, metal, false, false, 20, false);
            r = game.DispatchEditorFrame(new GpuCommandEncoder().Encode(commands), brush.CommandsStartStroke, 0);
            Set("currentResources", r);
        }
        Gesture(default(RawInputSnapshot) with { MousePosition = new(200, 100), LeftDown = true, LeftPressed = true });
        Check(brush.CommandsStartStroke, "free stroke begins once");
        Gesture(default(RawInputSnapshot) with { MousePosition = new(240, 110), LeftDown = true });
        Check(!brush.CommandsStartStroke, "held stroke grouped across frames");
        var painted = Capture();
        Check(!painted.Grid.AsSpan().SequenceEqual(initial.Grid), "stroke changes actual GPU cells");
        Restore(false); Equal(initial, Capture(), "whole stroke undo");
        Check(settings.Paused, "undo pauses scene");
        Gesture(default(RawInputSnapshot) with { MousePosition = new(260, 120), LeftDown = true });
        Equal(initial, Capture(), "held mouse suppressed after undo");
        Gesture(default);
        Restore(true); Equal(painted, Capture(), "whole stroke redo");

        foreach (var mode in new[] { BrushCommandMode.Erase, BrushCommandMode.SetTemperature,
            BrushCommandMode.Filter, BrushCommandMode.ThermalDevice })
        {
            var before = Capture();
            var command = mode == BrushCommandMode.ThermalDevice
                ? Command(420, 190, registry.GetRequiredRuntimeIndex("core:heater"))
                : mode == BrushCommandMode.SetTemperature ? Command(50, 50, metal)
                : Command(mode == BrushCommandMode.Filter ? 320 : 220, 105, metal);
            command.Mode = mode; command.TargetTemperature = 400;
            command.Reserved = mode == BrushCommandMode.Filter ? FilterRules.Closed :
                mode == BrushCommandMode.ThermalDevice ? BitConverter.SingleToUInt32Bits(600) : 1;
            Edit(command);
            var after = Capture();
            Check(Fields(before).Zip(Fields(after)).Any(p => !(p.First ?? []).AsSpan().SequenceEqual(p.Second ?? [])),
                mode + " modifies GPU state");
            Restore(false); Equal(before, Capture(), mode + " undo");
            Restore(true); Equal(after, Capture(), mode + " redo");
        }

        Gesture(default);
        var lineBefore = Capture();
        Gesture(default(RawInputSnapshot) with { MousePosition = new(340, 130), ShiftDown = true, LeftDown = true, LeftPressed = true });
        Check(brush.LinePreview is not null && !brush.CommandsStartStroke, "line preview has no history edit");
        Gesture(default(RawInputSnapshot) with { MousePosition = new(400, 170), LeftReleased = true });
        Check(brush.CommandsStartStroke, "line commit has one history entry");
        var lineAfter = Capture(); Restore(false); Equal(lineBefore, Capture(), "line undo");
        Restore(true); Equal(lineAfter, Capture(), "line redo");
        Restore(false);
        Edit(Command(350, 200, metal));
        Check(!history.CanRedo, "new edit truncates redo branch");

        var beforeClear = Capture();
        typeof(PhyxelGame).GetMethod("ProcessUiActions", flags)!.Invoke(game,
            [new UiFrameActions(false, true, false, false, false, false, false, false, false)]);
        Restore(false); Equal(beforeClear, Capture(), "reset undo");
        typeof(PhyxelGame).GetMethod("ProcessUiActions", flags)!.Invoke(game,
            [new UiFrameActions(true, false, false, false, false, false, false, false, false)]);
        r = game.DispatchEditorFrame([], false, 0); Set("currentResources", r);
        Check(!SimulationStateSerializer.ContainsMatter(Capture()), "clear removes matter");
        Restore(false); Equal(beforeClear, Capture(), "clear undo");

        Set("pendingWorldCapture", true);
        Check(!game.RestoreEditHistory(false), "save transfer blocks history restore");
        Set("pendingWorldCapture", false);

        // Real physics evolves between a stroke and undo. Redo must restore the
        // evolved state captured at undo, not just replay a brush command.
        settings.Paused = false;
        var runningBefore = Capture();
        Edit(Command(380, 70, registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water)), elapsed: 1f / 60);
        for (int i = 0; i < 12; i++) r = game.DispatchEditorFrame([], false, 1f / 60);
        var evolved = Capture();
        Restore(false); Equal(runningBefore, Capture(), "running physics undo");
        Restore(true); Equal(evolved, Capture(), "running physics redo");
        for (int i = 0; i < 10; i++) r = game.DispatchEditorFrame([], false, 1f / 30);
        Equal(evolved, Capture(), "restored pause stable");
        settings.Paused = false;
        for (int i = 0; i < 5; i++) r = game.DispatchEditorFrame([], false, 1f / 60);
        Check(!Capture().Grid.AsSpan().SequenceEqual(evolved.Grid), "restored scene resumes physics");
        settings.Paused = true;
        var saved = Capture();
        string path = Path.Combine(dir, "undo-save.json");
        Task.Run(() => serializer.SaveAsync(path, settings, metal, saved, registry)).GetAwaiter().GetResult();
        var loaded = Task.Run(() => serializer.LoadAsync(path, registry)).GetAwaiter().GetResult()!;
        // Scene files deliberately canonicalize inactive GPU scratch cells.
        // Undo itself is byte-exact; disk comparisons use the existing format.
        byte[] canonical = (byte[])saved.Grid.Clone();
        var canonicalCells = MemoryMarshal.Cast<byte, GridCell>(canonical);
        for (int i = 0; i < canonicalCells.Length; i++)
            if (canonicalCells[i].IsActive == 0) canonicalCells[i] = default;
        Equal(saved with { Grid = canonical }, loaded.World!, "restored scene disk roundtrip");
        Edit(Command(430, 200, metal));
        Set("pendingLoad", Task.FromResult<LoadedSimulationScene?>(loaded));
        typeof(PhyxelGame).GetMethod("ProcessSerializationCompletion", flags)!.Invoke(game, null);
        r = Get<GpuSimulationResources>("currentResources");
        Check(!history.CanUndo && !history.CanRedo, "successful load resets history");
        Edit(Command(435, 200, metal));
        typeof(PhyxelGame).GetMethod("ProcessUiActions", flags)!.Invoke(game,
            [new UiFrameActions(false, false, false, false, false, true, false, false, false)]);
        Check(!history.CanUndo, "scale change resets history");
        history.Clear(); Restore(false, false); Restore(true, false);

        // First edit of an unallocated empty world and redo after empty undo.
        coordinator.ClearCurrentWorld(settings);
        r = game.DispatchEditorFrame([], false, 0); Set("currentResources", r);
        Edit(Command(100, 100, metal));
        var firstStroke = Capture();
        Restore(false);
        Check(!SimulationStateSerializer.ContainsMatter(Capture()), "first stroke undo returns empty world");
        Restore(true); Equal(firstStroke, Capture(), "first stroke redo");
        history.Clear();
        settings.Paused = false;
        ushort steam = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        Edit(Command(300, 80, steam));
        Restore(false);
        settings.Paused = false;
        for (int i = 0; i < 10; i++) r = game.DispatchEditorFrame([], false, 1f / 60);
        Check(MemoryMarshal.Cast<byte, GridCell>(Capture().Grid).ToArray()
            .All(c => c.IsActive == 0 || c.MaterialIndex != steam), "undo clears queued gas brush emissions");
        settings.Paused = true;

        if (Environment.GetEnvironmentVariable("PHYXEL_UNDO_SCENE") is { Length: > 0 } scene)
        {
            var source = Task.Run(() => serializer.LoadAsync(scene, registry)).GetAwaiter().GetResult()!;
            Set("pendingLoad", Task.FromResult<LoadedSimulationScene?>(source));
            typeof(PhyxelGame).GetMethod("ProcessSerializationCompletion", flags)!.Invoke(game, null);
            r = Get<GpuSimulationResources>("currentResources");
            settings.Paused = true;
            var furnaceBefore = Capture();
            var timer = Stopwatch.StartNew();
            Edit(Command(15, 15, metal));
            double recordMs = timer.Elapsed.TotalMilliseconds;
            var furnaceAfter = Capture();
            Check(!furnaceBefore.Grid.AsSpan().SequenceEqual(furnaceAfter.Grid), "user furnace stroke actually changes cells");
            timer.Restart(); Restore(false); double undoMs = timer.Elapsed.TotalMilliseconds;
            Equal(furnaceBefore, Capture(), "user furnace undo");
            Restore(true); Equal(furnaceAfter, Capture(), "user furnace redo");
            File.WriteAllText(Path.Combine(dir, "furnace-capture.txt"),
                $"world={r.Width}x{r.Height}\nbytes={WorldEditHistory.Size(furnaceBefore)}\nrecordMs={recordMs:F3}\nundoMs={undoMs:F3}\n");
        }

        TestLimits(Check);
        foreach (Keys ctrl in new[] { Keys.LeftControl, Keys.RightControl })
        {
            var z = new KeyboardState(ctrl, Keys.Z);
            Check(RawInputSampler.HistoryKeys(z, default) == (true, false), "Ctrl+Z edge " + ctrl);
            Check(RawInputSampler.HistoryKeys(z, z) == (false, false), "held shortcut does not repeat");
            Check(RawInputSampler.HistoryKeys(new(ctrl, Keys.Y), default) == (false, true), "Ctrl+Y edge");
            Check(RawInputSampler.HistoryKeys(new(ctrl, Keys.RightShift, Keys.Z), default) == (false, true), "Ctrl+Shift+Z edge");
        }
        Check(RawInputSampler.HistoryKeys(new(Keys.Z), default) == (false, false), "plain Z ignored");
        Console.WriteLine($"PHYXEL_UNDO_SUCCESS checks={checks} storedBytes={history.StoredBytes}");
        File.WriteAllText(Path.Combine(dir, "result.txt"), $"PASS checks={checks}\n");
    }

    private static BrushDrawCommand Command(int x, int y, ushort material) => new()
    { X = x, Y = y, EndX = x, EndY = y, Radius = 3, Density = 1,
        Mode = BrushCommandMode.Material, MaterialIndex = material, Reserved = 1 };

    private static void TestLimits(Action<bool, string> check)
    {
        var h = new WorldEditHistory(24, 3);
        SimulationWorldSnapshot S(int n) => new(1, 1, new byte[n]);
        h.Record(S(8)); h.Record(S(8)); h.Record(S(8)); h.Record(S(8));
        check(h.StoredBytes == 24, "oldest entry evicted at count and byte budget");
        for (int i = 0; i < 3; i++) check(h.Restore(false, S(8), out _), "bounded history undo " + i);
        check(!h.CanUndo && h.CanRedo, "only retained entries undoable");
        h.Record(S(8)); check(!h.CanRedo && h.StoredBytes == 8, "branch releases redo memory");
        check(!h.Record(S(25)) && !h.CanUndo && h.StoredBytes == 0, "oversized edit invalidates older history");
        h.Record(S(0)); h.Record(S(0)); h.Record(S(0)); h.Record(S(0));
        for (int i = 0; i < 3; i++) check(h.Restore(false, S(0), out _), "zero-byte entries still bounded " + i);
        check(!h.CanUndo, "zero-byte entry cap");
    }
}
