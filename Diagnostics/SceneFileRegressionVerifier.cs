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
            // The live user's save failed on shrinking carriers retaining metal.
            grid[101 * r.Width + 100] = new() { IsActive = 1,
                MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Gunpowder),
                Mass = .0002f, Temperature = 1050, FuelMass = .04f,
                RetainedLiquidMaterialIndex = registry.GetRequiredRuntimeIndex("core:molten_metal") };
            grid[101 * r.Width + 101] = new() { IsActive = 1,
                MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal),
                Mass = .0002f, Temperature = 500, FuelMass = .05f,
                RetainedLiquidMaterialIndex = oil };
            uint[] filters=new uint[grid.Length];filters[120*r.Width+100]=(uint)registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam)+1;
            int coarse = ((r.Width + 3) / 4) * ((r.Height + 3) / 4);
            var oxygen = new float[grid.Length]; oxygen[0] = 2.5f;
            var heat = new System.Numerics.Vector2[coarse]; heat[0] = new(300, 1);
            var air = new AirCell[coarse]; air[0] = new() { Pressure = .25f, VelocityX = .3f };
            var motion = new System.Numerics.Vector4[grid.Length]; motion[0] = new(.3f, -.1f, 0, 0);
            var pending = new System.Numerics.Vector4[grid.Length]; pending[0] = new(.1f, 300, 1, 0);
            var pulse = new System.Numerics.Vector4[coarse]; pulse[0] = new(.1f, .2f, -.3f, .4f);
            serializer.ApplyWorldSnapshot(r, new(r.Width, r.Height, MemoryMarshal.AsBytes(grid.AsSpan()).ToArray(),
                Air: MemoryMarshal.AsBytes(air.AsSpan()).ToArray(), GasMotion: MemoryMarshal.AsBytes(motion.AsSpan()).ToArray(),
                Oxidizer: MemoryMarshal.AsBytes(oxygen.AsSpan()).ToArray(), AirThermal: MemoryMarshal.AsBytes(heat.AsSpan()).ToArray(),
                ReactionPending: MemoryMarshal.AsBytes(pending.AsSpan()).ToArray(), ReactionPulse: MemoryMarshal.AsBytes(pulse.AsSpan()).ToArray(),
                Filters: MemoryMarshal.AsBytes(filters.AsSpan()).ToArray()));
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
        var retainedWorld = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(first, registry))
            .GetAwaiter().GetResult()!.World!;
        var retainedCells = MemoryMarshal.Cast<byte, GridCell>(retainedWorld.Grid);
        Check(retainedCells[101 * r.Width + 100].FuelMass == .04f &&
            retainedCells[101 * r.Width + 100].Mass == .0002f &&
            retainedCells[101 * r.Width + 100].RetainedLiquidMaterialIndex == registry.GetRequiredRuntimeIndex("core:molten_metal"),
            "Shrinking carrier lost its retained metal on save/load.");
        Check(retainedCells[101 * r.Width + 101].FuelMass == .05f,
            "Shrinking coal lost its retained oil on save/load.");
        Check(MemoryMarshal.Cast<byte, float>(retainedWorld.Oxidizer!)[0] == 2.5f &&
            MemoryMarshal.Cast<byte, System.Numerics.Vector2>(retainedWorld.AirThermal!)[0] == new System.Numerics.Vector2(300, 1),
            "Save lost compressed oxygen or carrier heat.");
        Check(MemoryMarshal.Cast<byte, AirCell>(retainedWorld.Air!)[0].Pressure == .25f &&
            MemoryMarshal.Cast<byte, System.Numerics.Vector4>(retainedWorld.GasMotion!)[0].X == .3f &&
            MemoryMarshal.Cast<byte, System.Numerics.Vector4>(retainedWorld.ReactionPending!)[0].Y == 300 &&
            MemoryMarshal.Cast<byte, System.Numerics.Vector4>(retainedWorld.ReactionPulse!)[0].W == .4f,
            "Save lost air, motion or deferred reaction state.");
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
        Check(r.FilterCount==1 && r.FilterMap[120*r.Width+100]==(uint)registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam)+1,"Load lost the filter overlay.");
        Check(MemoryMarshal.Cast<byte, float>(AirInventoryRegressionVerifier.Read(r, r.Oxidizer.ReadBuffer))[0] == 2.5f &&
            MemoryMarshal.Cast<byte, System.Numerics.Vector4>(AirInventoryRegressionVerifier.Read(r, r.ReactionPending.Buffer))[0].Y == 300,
            "Load did not restore auxiliary fields to the GPU.");
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
        Check(r.FilterMap[(120+extraTop)*r.Width+100]==(uint)registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam)+1,"Canvas expansion changed the filter overlay.");
        next=Path.Combine(dir,"Расширенная сцена.json");Action(true,saveAs:true);Complete();
        int savedWidth=r.Width,savedHeight=r.Height;
        coordinator.ClearCurrentWorld(settings);Set("currentResources",coordinator.DispatchFrame(settings,[],0));
        Action(false,load:true);Complete();r=Get<GpuSimulationResources>("currentResources");
        Check(r.Width==savedWidth&&r.Height==savedHeight&&settings.Width==savedWidth&&settings.Height==savedHeight,"Load reverted expanded dimensions to 16:9.");
        cells=MemoryMarshal.Cast<byte,GridCell>(AirInventoryRegressionVerifier.Read(r,r.Grid.ReadBuffer));
        Check(cells[(100+extraTop)*r.Width+100].Mass==.7f,"Expanded scene load lost its cell.");
        Check(r.FilterCount==1&&r.FilterMap[(120+extraTop)*r.Width+100]==(uint)registry.GetRequiredRuntimeIndex(CoreMaterialIds.Steam)+1,"Expanded scene load lost the filter.");
        // New transport is bounded; old saved hot residues still load exactly.
        // The codec does not impose the particle ceiling on carrier fields.
        World(.7f);
        int node=10*r.AirWidth+10, count=r.AirWidth*r.AirHeight;
        var carrier=new AirCell[count];
        carrier[node].VelocityX=carrier[node+1].VelocityX=4;
        var links=new uint[count];links[node]=1u<<5;links[node+1]=1u<<3;
        var thermal=new System.Numerics.Vector2[count];
        thermal[node]=new(300,1);thermal[node+1]=new(5000,1);
        r.Context.UpdateSubresource(carrier,r.Air.Buffer);
        r.Context.UpdateSubresource(links,r.AirFlowLinks.Buffer);
        r.Context.UpdateSubresource(thermal,r.AirThermal.Buffer);
        SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        byte[] transported=AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer);
        var depleted=MemoryMarshal.Cast<byte,System.Numerics.Vector2>(transported)[node];
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_SCENE_FILES_HOT_AIR E={depleted.X:R} C={depleted.Y:R} K={depleted.X/depleted.Y:R}"));
        Check(depleted.X>0&&depleted.Y>0&&float.IsFinite(depleted.X/depleted.Y)&&depleted.X/depleted.Y<=5000.1f,
            "New transport still creates out-of-range carrier temperature.");
        // Preserve compatibility with scenes saved before the CFL repair.
        thermal[node]=new(117.50018f,1.013279e-6f);
        r.Context.UpdateSubresource(thermal,r.AirThermal.Buffer);
        transported=AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer);
        next=Path.Combine(dir,"Перегретый воздух.json");Action(true,saveAs:true);Complete();
        Check(Get<string>("transientStatus").StartsWith("Сохранено:"),"GPU hot-air save failed: "+Get<string>("transientStatus"));
        var hotWorld=System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(next,registry)).GetAwaiter().GetResult()!.World!;
        Check(hotWorld.AirThermal!.AsSpan().SequenceEqual(transported),"Hot-air disk round-trip changed energy/capacity.");
        coordinator.ClearCurrentWorld(settings);
        Action(false,load:true);Complete();r=Get<GpuSimulationResources>("currentResources");
        Check(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer).AsSpan().SequenceEqual(transported),"Hot-air load changed GPU energy/capacity.");
        Action(true);Complete();
        Check(Get<string>("transientStatus").StartsWith("Сохранено:"),"Hot-air quick save failed.");
        Check(System.Threading.Tasks.Task.Run(()=>serializer.LoadAsync(next,registry)).GetAwaiter().GetResult()!.World!.AirThermal!.AsSpan().SequenceEqual(transported),
            "Repeated hot-air save changed state.");
        Console.WriteLine($"PHYXEL_SCENE_FILES_SUCCESS checks={checks} choices={choices}");
    }
}
