using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using Phyxel.UI;

namespace Phyxel.Diagnostics;

internal static class SceneFileRegressionVerifier
{
    internal static void Run(PhyxelGame game, SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        string dir = Path.GetFullPath(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/scene-files");
        Directory.CreateDirectory(dir);
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidDataException(label); }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(PhyxelGame).GetField(name, flags)!;
        void Set(string name, object? value) => Field(name).SetValue(game, value);
        T Get<T>(string name) => (T)Field(name).GetValue(game)!;
        void Action(bool save, bool load = false, bool saveAs = false) =>
            typeof(PhyxelGame).GetMethod("ProcessUiActions", flags)!.Invoke(game,
                [new UiFrameActions(false, false, false, save, load, false, false, false, false, saveAs)]);
        void Complete()
        {
            var started = DateTime.UtcNow;
            do
            {
                // The normal game loop pumps WinForms continuations. This
                // verification runs inside LoadContent and must pump them too.
                Application.DoEvents();
                Get<GpuSimulationResources>("currentResources").Context.Flush();
                typeof(PhyxelGame).GetMethod("ProcessSerializationCompletion", flags)!.Invoke(game, null);
                if (!Get<bool>("pendingWorldCapture") && Field("pendingSave").GetValue(game) is null &&
                    Field("pendingLoad").GetValue(game) is null) return;
                Thread.Sleep(5);
            } while ((DateTime.UtcNow - started).TotalSeconds < 20);
            throw new TimeoutException("Scene operation did not complete.");
        }
        string first = Path.Combine(dir, "Первая сцена.json"), second = Path.Combine(dir, "Вторая сцена.json");
        // Native dialog configuration is real, selection is injected to avoid
        // an unattended modal window. Save/load below use the actual GPU world.
        string? cancel = SceneFileDialog.Select(first, true, game.Window.Handle, (dialog, owner) =>
        {
            Check(dialog is SaveFileDialog { OverwritePrompt: true }, "Overwrite prompt missing.");
            Check(dialog.DefaultExt == "json" && dialog.AddExtension && dialog.RestoreDirectory, "Dialog configuration.");
            Check(owner.Handle == game.Window.Handle, "Owner mismatch.");
            return DialogResult.Cancel;
        });
        Check(cancel is null, "Cancel selected a filename.");
        string? selected = SceneFileDialog.Select(first, false, game.Window.Handle, (dialog, _) =>
        {
            Check(dialog is OpenFileDialog { CheckFileExists: true, Multiselect: false }, "Open dialog configuration.");
            dialog.FileName = second; return DialogResult.OK;
        });
        Check(selected == second, "Wrong filename.");
        try
        {
            SceneFileDialog.Select(first, true, game.Window.Handle, (dialog, _) =>
            { dialog.FileName = Path.ChangeExtension(first, ".world"); return DialogResult.OK; });
            throw new InvalidDataException(".world accepted as JSON.");
        }
        catch (InvalidDataException e) when (e.Message.Contains(".json")) { checks++; }
        var settings = Get<SimulationSettings>("settings");
        settings.Paused = false; settings.AirSimulation = false; settings.Mode = SimulationMode.Sandbox;
        uint oil = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Oil);
        var r = coordinator.DispatchFrame(settings, [new BrushDrawCommand { X = 100, Y = 100,
            EndX = 100, EndY = 100, Radius = 1, Density = 1,
            Mode = BrushCommandMode.Material, MaterialIndex = (ushort)oil }], 0);
        Set("currentResources", r);
        Get<SandboxUiCoordinator>("userInterface").SelectedMaterial = (ushort)oil;
        var serializer = new SimulationStateSerializer();
        void World(float mass)
        {
            var grid = new GridCell[r.Width * r.Height];
            grid[100 * r.Width + 100] = new() { IsActive = 1, MaterialIndex = oil, Mass = mass, Temperature = 330 };
            serializer.ApplyWorldSnapshot(r, new(r.Width, r.Height, MemoryMarshal.AsBytes(grid.AsSpan()).ToArray()));
        }
        GridCell Loaded(string path) => MemoryMarshal.Cast<byte, GridCell>(System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(path, registry))
            .GetAwaiter().GetResult()!.World!.Grid)[100 * r.Width + 100];
        int choices = 0; string? next = first;
        Set("scenePathPicker", new Func<string, bool, IntPtr, string?>((_, _, _) => { choices++; return next; }));
        World(.42f); Action(true, load: true, saveAs: true);
        Action(false, load: true);
        Check(choices == 1 && Get<bool>("pendingWorldCapture"), "Load interrupted pending save.");
        settings.Mode = SimulationMode.Simulation;
        Complete();
        Check(Loaded(first).Mass == .42f, "Save missed GPU world.");
        Check(System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(first, registry))
            .GetAwaiter().GetResult()!.State.Mode == SimulationMode.Sandbox, "Save mixed later settings into earlier snapshot.");
        Check(Get<string>("scenePath") == first && Get<bool>("hasChosenScenePath"), "Successful path not retained.");
        Check(!settings.Paused, "Dialog changed pause.");
        byte[] original = File.ReadAllBytes(Path.ChangeExtension(first, ".world"));
        next = null; World(.7f); Action(true, saveAs: true); Complete();
        Check(File.ReadAllBytes(Path.ChangeExtension(first, ".world")).AsSpan().SequenceEqual(original), "Cancel overwrote scene.");
        int beforeQuick = choices; Action(true); Complete();
        Check(choices == beforeQuick && Loaded(first).Mass == .7f, "Quick save chose another file or wrong state.");
        original = File.ReadAllBytes(Path.ChangeExtension(first, ".world"));
        next = second; World(.91f); Action(true, saveAs: true); Complete();
        Check(Loaded(second).Mass == .91f && File.ReadAllBytes(Path.ChangeExtension(first, ".world")).AsSpan().SequenceEqual(original), "Save As damaged first scene.");
        next = null; Action(false, load: true); Complete();
        Check(Get<string>("scenePath") == second, "Cancelled load changed path.");
        next = first; Action(false, load: true); Complete();
        r = Get<GpuSimulationResources>("currentResources");
        var cells = MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer));
        Check(cells[100 * r.Width + 100].Mass == .7f && cells[100 * r.Width + 100].Temperature == 330, "Load missed GPU state.");
        Check(Get<string>("scenePath") == first, "Load missed quick-save path.");
        next = Path.Combine(dir, "missing.json"); Action(false, load: true); Complete();
        Check(Get<string>("scenePath") == first, "Failed load changed path.");
        string blocked = Path.Combine(dir, "blocked-parent");
        File.WriteAllText(blocked, "not a directory");
        next = Path.Combine(blocked, "failed.json"); Action(true, saveAs: true); Complete();
        Check(Get<string>("scenePath") == first && !Get<string>("transientStatus").StartsWith("Сохранено:"), "Failed save selected a successful path.");
        settings.Paused = true; Action(true); Complete();
        Check(settings.Paused, "Save resumed paused simulation.");
        Check(Get<string>("transientStatus").StartsWith("Сохранено:"), "Status missing.");
        // Actual GPU capture -> padded world -> save/load with dimensions
        // that differ from the old 16:9 Scale-derived size.
        r=Get<GpuSimulationResources>("currentResources");int oldWidth=r.Width,oldHeight=r.Height;
        var canvas=new Microsoft.Xna.Framework.Rectangle(100,70,800,700);
        bool expanding=(bool)typeof(PhyxelGame).GetMethod("EnsureCanvasWorldFits",flags)!.Invoke(game,[canvas])!;
        Check(expanding,"Occupied scene did not start canvas expansion.");
        var expansionStart=DateTime.UtcNow;
        while(Get<bool>("canvasExpansionPending"))
        {
            Application.DoEvents();Get<GpuSimulationResources>("currentResources").Context.Flush();
            typeof(PhyxelGame).GetMethod("CompleteCanvasExpansion",flags)!.Invoke(game,null);
            if((DateTime.UtcNow-expansionStart).TotalSeconds>30)throw new TimeoutException("Canvas expansion did not finish.");
            Thread.Sleep(5);
        }
        r=Get<GpuSimulationResources>("currentResources");int extraTop=r.Height-oldHeight;
        cells=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer));
        Check(r.Width>=oldWidth&&extraTop>0,"Canvas did not expand the real grid.");
        Check(cells[(100+extraTop)*r.Width+100].Mass==.7f&&cells[(100+extraTop)*r.Width+100].Temperature==330,"Canvas expansion changed the existing cell.");
        next=Path.Combine(dir,"Расширенная сцена.json");Action(true,saveAs:true);Complete();
        int savedWidth=r.Width,savedHeight=r.Height;
        coordinator.ClearCurrentWorld(settings);Set("currentResources",coordinator.DispatchFrame(settings,[],0));
        Action(false,load:true);Complete();r=Get<GpuSimulationResources>("currentResources");
        Check(r.Width==savedWidth&&r.Height==savedHeight&&settings.Width==savedWidth&&settings.Height==savedHeight,"Load reverted expanded dimensions to 16:9.");
        cells=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer));
        Check(cells[(100+extraTop)*r.Width+100].Mass==.7f,"Expanded scene load lost its cell.");
        Console.WriteLine($"PHYXEL_SCENE_FILES_SUCCESS checks={checks} choices={choices}");
    }
}
