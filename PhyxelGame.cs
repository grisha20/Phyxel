using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Diagnostics;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;
using Phyxel.UI;
using SharpDX;

namespace Phyxel;

public sealed class PhyxelGame : Game
{
    private readonly GraphicsDeviceManager graphics;
    private readonly SimulationSettings settings = new();
    private readonly RawInputSampler inputSampler = new();
    private readonly CanvasBrushController brushController = new();
    private readonly CanvasCameraController cameraController = new();
    private readonly GpuCommandEncoder commandEncoder = new();
    private readonly SimulationStateSerializer stateSerializer = new();
    private readonly GpuDebugProbe debugProbe = new();
    private readonly GpuTemperatureProbe temperatureProbe = new();
    private readonly AcceptanceRegressionHarness acceptance = new();
    private readonly string scenePath;
    private readonly string? uiScreenshotPath;
    private readonly float? uiDpiOverride;
    private SpriteBatch? spriteBatch;
    private RasterizerState? canvasRasterizerState;
    private MaterialRegistry? materialRegistry;
    private GpuResourceLifecycleManager? resourceManager;
    private SimulationDispatchCoordinator? dispatchCoordinator;
    private SandboxUiCoordinator? userInterface;
    private GpuSimulationResources? currentResources;
    private Task? pendingSave;
    private Task<LoadedSimulationScene?>? pendingLoad;
    private bool pendingWorldCapture;
    private bool pendingAcceptanceCheckpoint;
    private uint pendingAcceptanceCheckpointFrame;
    private ulong pendingAcceptanceCheckpointTick;
    private bool acceptanceSuccess;
    private ushort capturedMaterial;
    private string transientStatus = string.Empty;
    private float transientStatusRemaining;
    private double frameRateAccumulator;
    private int accumulatedFrames;
    private double displayedFrameRate = 60;
    private uint frameIndex;
    private RawInputSnapshot latestInput;
    private bool uiScreenshotCaptured;

    public PhyxelGame()
    {
        int requestedWidth = ReadWindowDimension("PHYXEL_WINDOW_WIDTH", SimulationSettings.NativeWidth);
        int requestedHeight = ReadWindowDimension("PHYXEL_WINDOW_HEIGHT", SimulationSettings.NativeHeight);
        bool windowed = Environment.GetEnvironmentVariable("PHYXEL_WINDOWED") == "1";
        if (float.TryParse(
            Environment.GetEnvironmentVariable("PHYXEL_UI_DPI_SCALE"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float parsedDpiScale) && parsedDpiScale is >= 1f and <= 2f)
        {
            uiDpiOverride = parsedDpiScale;
        }

        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = requestedWidth,
            PreferredBackBufferHeight = requestedHeight,
            GraphicsProfile = GraphicsProfile.HiDef,
            SynchronizeWithVerticalRetrace = true,
            PreferMultiSampling = false,
            IsFullScreen = !windowed,
            HardwareModeSwitch = false
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = false;
        TargetElapsedTime = TimeSpan.FromSeconds(1d / 60d);
        Window.AllowUserResizing = true;
        Window.Title = "Phyxel";
        if (acceptance.Active)
        {
            string? requestedScale = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_SCALE");
            float acceptanceScale = float.TryParse(
                requestedScale,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsedScale)
                ? parsedScale
                : acceptance.RequiresNativeResolution ? 1f : 0.25f;
            settings.ApplyScale(acceptanceScale);
            if (int.TryParse(
                Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int targetFramesPerSecond) && targetFramesPerSecond is >= 1 and <= 240)
            {
                graphics.SynchronizeWithVerticalRetrace = false;
                IsFixedTimeStep = true;
                TargetElapsedTime = TimeSpan.FromSeconds(1d / targetFramesPerSecond);
            }
        }
        scenePath = Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SCENE_PATH") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Phyxel",
                "scene.json");
        uiScreenshotPath = Environment.GetEnvironmentVariable("PHYXEL_UI_SCREENSHOT_PATH");
    }

    protected override void LoadContent()
    {
        spriteBatch = new SpriteBatch(GraphicsDevice);
        canvasRasterizerState = new RasterizerState
        {
            CullMode = CullMode.None,
            ScissorTestEnable = true
        };
        UiFontSet fonts = new(
            Content.Load<SpriteFont>("Fonts/SandboxFont"),
            Content.Load<SpriteFont>("Fonts/SandboxFontMedium"),
            Content.Load<SpriteFont>("Fonts/SandboxFontLarge"));
        materialRegistry = new MaterialRegistry();
        acceptance.ConfigureMaterials(materialRegistry);
        resourceManager = new GpuResourceLifecycleManager(GraphicsDevice, materialRegistry);
        resourceManager.PrepareSimulation(settings);
        dispatchCoordinator = new SimulationDispatchCoordinator(resourceManager, materialRegistry);
        userInterface = new SandboxUiCoordinator(materialRegistry, fonts, resourceManager);
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_UI") == "1")
        {
            UiLayoutRegressionTests.RunAllTests(materialRegistry, fonts, userInterface);
            Console.WriteLine("PHYXEL_UI_REGRESSION_SUCCESS");
            Exit();
            return;
        }
        SimulationWorldSnapshot? initialAcceptanceWorld = acceptance.CreateInitialWorld(
            settings.Width,
            settings.Height);
        if (initialAcceptanceWorld is not null)
        {
            currentResources = resourceManager.CreateOrResize(settings, true);
            stateSerializer.ApplyWorldSnapshot(currentResources, initialAcceptanceWorld);
            dispatchCoordinator.RestoreWorldActivity(
                currentResources,
                !acceptance.InitialWorldStartsDormant,
                SimulationStateSerializer.ContainsContactTransitionSource(
                    initialAcceptanceWorld,
                    materialRegistry),
                settings.HydraulicPressure);
        }
        if (acceptance.RequiresSavedScene)
        {
            pendingLoad = stateSerializer.LoadAsync(scenePath, materialRegistry);
        }
        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        if (userInterface is null || dispatchCoordinator is null || materialRegistry is null)
        {
            base.Update(gameTime);
            return;
        }
        RawInputSnapshot input = inputSampler.Sample(gameTime);
        latestInput = input;
        if (input.EscapePressed)
        {
            Exit();
            return;
        }
        ProcessSerializationCompletion();
        userInterface.CameraZoom = cameraController.Zoom;
        UiFrameActions actions = userInterface.Update(
            input,
            GraphicsDevice.Viewport,
            uiDpiOverride ?? UiDisplayScale.GetDpiScale(Window.Handle),
            settings);
        ProcessUiActions(actions);
        acceptance.ConfigureSettings(frameIndex, settings);
        acceptance.ApplyRuntimeControls(
            frameIndex,
            settings,
            dispatchCoordinator,
            temperatureProbe);
        Rectangle fittedWorldBounds = FitWorldToCanvas(
            userInterface.CanvasBounds,
            settings.Width,
            settings.Height);
        Rectangle worldBounds = acceptance.Active
            ? fittedWorldBounds
            : cameraController.Update(
                input,
                userInterface.CanvasBounds,
                fittedWorldBounds,
                userInterface.PanToolActive,
                userInterface.PointerConsumed);
        IReadOnlyList<BrushDrawCommand> commands = acceptance.Active
            ? acceptance.CreateCommands(frameIndex)
            : brushController.CreateCommands(
                input,
                worldBounds,
                settings,
                userInterface.SelectedMaterial,
                (MaterialSimulationKind)materialRegistry[userInterface.SelectedMaterial]
                    .Properties.SimulationKind == MaterialSimulationKind.Tool,
                userInterface.TemperatureToolActive,
                userInterface.TargetTemperature,
                userInterface.BlocksBrushInput);
        try
        {
            uint acceptanceFrame = frameIndex;
            currentResources = dispatchCoordinator.DispatchFrame(
                settings,
                commandEncoder.Encode(commands),
                acceptance.AdjustElapsedSeconds(input.DeltaSeconds));
            acceptance.CaptureScreenshot(currentResources, frameIndex);
            debugProbe.Update(currentResources, frameIndex++);
            Point? probeCoordinate = acceptance.OwnsTemperatureProbe
                ? acceptance.GetProbeCoordinate(acceptanceFrame)
                : !userInterface.CanvasBounds.Contains(input.MousePosition) || userInterface.PointerConsumed
                    ? null
                    : GpuTemperatureProbe.MapPointerToCell(
                        input.MousePosition,
                        worldBounds,
                        currentResources.Width,
                        currentResources.Height);
            temperatureProbe.Update(currentResources, probeCoordinate, input.DeltaSeconds);
            acceptance.ObserveTemperatureProbe(acceptanceFrame, temperatureProbe.Latest);
            dispatchCoordinator.ObserveStatistics(debugProbe.Latest);
            BeginAcceptanceCheckpoint();
            BeginAcceptanceCapture();
        }
        catch (SharpDXException exception) when (
            exception.ResultCode.Code == unchecked((int)0x8007000E) && settings.Scale > 0.25f)
        {
            settings.ApplyScale(settings.Scale - 0.25f);
            temperatureProbe.Reset();
            SetStatus("Видеопамять ограничена: масштаб снижен");
        }
        transientStatusRemaining = Math.Max(0, transientStatusRemaining - input.DeltaSeconds);
        if (transientStatusRemaining == 0)
        {
            transientStatus = string.Empty;
        }
        base.Update(gameTime);
    }

    private static int ReadWindowDimension(string variableName, int fallback)
    {
        return int.TryParse(
            Environment.GetEnvironmentVariable(variableName),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value) && value is >= 640 and <= 7680
            ? value
            : fallback;
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(9, 11, 14));
        if (spriteBatch is null || userInterface is null || currentResources is null)
        {
            base.Draw(gameTime);
            return;
        }
        Rectangle fittedWorldBounds = FitWorldToCanvas(
            userInterface.CanvasBounds,
            currentResources.Width,
            currentResources.Height);
        Rectangle worldBounds = cameraController.GetWorldBounds(fittedWorldBounds);
        spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.Opaque,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            canvasRasterizerState);
        GraphicsDevice.ScissorRectangle = userInterface.CanvasBounds;
        spriteBatch.Draw(currentResources.PresentationTexture, worldBounds, Color.White);
        spriteBatch.End();
        GraphicsDevice.ScissorRectangle = GraphicsDevice.Viewport.Bounds;
        spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);
        userInterface.DrawBrushIndicator(
            spriteBatch,
            latestInput.MousePosition,
            worldBounds,
            settings,
            latestInput.RightDown);
        userInterface.Draw(
            spriteBatch,
            settings,
            debugProbe.Latest,
            displayedFrameRate,
            transientStatus,
            temperatureProbe.Latest);
        spriteBatch.End();
        CaptureUiScreenshotIfRequested();
        UpdateFrameRate(gameTime);
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        userInterface?.Dispose();
        resourceManager?.Dispose();
        canvasRasterizerState?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }

    private void ProcessUiActions(UiFrameActions actions)
    {
        if (userInterface is null || dispatchCoordinator is null || materialRegistry is null)
        {
            return;
        }
        if (actions.ClearRequested)
        {
            dispatchCoordinator.ClearCurrentWorld(settings);
            temperatureProbe.Reset();
            SetStatus("Сцена очищена");
        }
        if (actions.ResetRequested)
        {
            dispatchCoordinator.ResetCurrentSimulation(settings);
            temperatureProbe.Reset();
            SetStatus("Симуляция перезапущена");
        }
        if (actions.ResetViewRequested)
        {
            cameraController.Reset();
            userInterface.CameraZoom = cameraController.Zoom;
            SetStatus("Вид камеры сброшен");
        }
        if (actions.GravityChanged)
        {
            dispatchCoordinator.SetSolidGravityEnabled(settings.SolidGravity);
            SetStatus(settings.SolidGravity ? "Гравитация включена" : "Гравитация выключена");
        }
        if (actions.ScaleChanged)
        {
            temperatureProbe.Reset();
        }
        if (actions.HydraulicsChanged)
        {
            SetStatus(settings.HydraulicPressure
                ? "Гидравлика сосудов включена (медленнее)"
                : "Быстрая вода включена");
        }
        if (actions.SaveRequested && pendingSave is null && !pendingWorldCapture && currentResources is not null)
        {
            stateSerializer.BeginWorldCapture(currentResources);
            pendingWorldCapture = true;
            capturedMaterial = userInterface.SelectedMaterial;
            SetStatus("Копирование мира с GPU…");
        }
        if (actions.LoadRequested && pendingLoad is null)
        {
            temperatureProbe.Reset();
            pendingLoad = stateSerializer.LoadAsync(scenePath, materialRegistry);
            SetStatus("Загрузка…");
        }
    }

    private void CaptureUiScreenshotIfRequested()
    {
        if (uiScreenshotCaptured || string.IsNullOrWhiteSpace(uiScreenshotPath))
        {
            return;
        }

        int width = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        Color[] pixels = new Color[width * height];
        GraphicsDevice.GetBackBufferData(pixels);
        string fullPath = Path.GetFullPath(uiScreenshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using System.Drawing.Bitmap capture = new(
            width,
            height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Drawing.Imaging.BitmapData data = capture.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        byte[] bgra = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index++)
        {
            Color color = pixels[index];
            int offset = index * 4;
            bgra[offset] = color.B;
            bgra[offset + 1] = color.G;
            bgra[offset + 2] = color.R;
            bgra[offset + 3] = color.A;
        }
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        capture.UnlockBits(data);
        capture.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
        uiScreenshotCaptured = true;
        Console.WriteLine($"PHYXEL_UI_SCREENSHOT {width}x{height} {fullPath}");
        Exit();
    }

    private void ProcessSerializationCompletion()
    {
        if (pendingAcceptanceCheckpoint && currentResources is not null &&
            dispatchCoordinator is not null &&
            stateSerializer.TryCompleteWorldCapture(
                currentResources,
                out SimulationWorldSnapshot? checkpointSnapshot) &&
            checkpointSnapshot is not null)
        {
            pendingAcceptanceCheckpoint = false;
            acceptance.RecordThermalCheckpoint(
                pendingAcceptanceCheckpointFrame,
                pendingAcceptanceCheckpointTick,
                checkpointSnapshot,
                dispatchCoordinator);
            if (materialRegistry is not null && userInterface is not null &&
                acceptance.TryBeginPhaseRoundTripSave(out SimulationWorldSnapshot? roundTripSnapshot) &&
                roundTripSnapshot is not null)
            {
                capturedMaterial = userInterface.SelectedMaterial;
                pendingSave = stateSerializer.SaveAsync(
                    scenePath,
                    settings,
                    capturedMaterial,
                    roundTripSnapshot,
                    materialRegistry);
            }
        }
        if (pendingWorldCapture && currentResources is not null && materialRegistry is not null &&
            stateSerializer.TryCompleteWorldCapture(currentResources, out SimulationWorldSnapshot? snapshot) &&
            snapshot is not null)
        {
            pendingWorldCapture = false;
            if (acceptance.Active)
            {
                bool passed = acceptance.Validate(
                    snapshot,
                    debugProbe.Latest,
                    displayedFrameRate,
                    dispatchCoordinator?.ThermalTicks ?? 0,
                    temperatureProbe.Latest,
                    dispatchCoordinator?.ThermalGpuTiming ?? default,
                    dispatchCoordinator?.ContactTransitionGpuTiming ?? default,
                    dispatchCoordinator?.GasRedistributionGpuTiming ?? default,
                    dispatchCoordinator?.PhaseGpuTiming ?? default,
                    dispatchCoordinator?.CombustionGpuTiming ?? default,
                    dispatchCoordinator?.CombustionDispatches ?? 0,
                    dispatchCoordinator?.CombustionSummaryReadbacks ?? 0,
                    temperatureProbe.GpuTiming,
                    dispatchCoordinator?.PhaseDispatches ?? 0,
                    dispatchCoordinator?.PhaseSummaryReadbacks ?? 0,
                    dispatchCoordinator?.PhaseFallbackWakeUps ?? 0,
                    dispatchCoordinator?.MaximumPhaseDispatchesPerFrame ?? 0,
                    dispatchCoordinator?.LastPhaseSummary ?? PhaseTransitionSummaryFlags.None,
                    dispatchCoordinator?.PhasePresentationIsCurrent ?? false,
                    out _);
                Environment.ExitCode = passed ? 0 : 1;
                acceptanceSuccess = true;
                Exit();
                return;
            }
            pendingSave = stateSerializer.SaveAsync(scenePath, settings, capturedMaterial, snapshot, materialRegistry);
            SetStatus("Сохранение сцены…");
        }
        if (pendingSave is { IsCompleted: true })
        {
            if (acceptance.IsPhaseRoundTripSaving && dispatchCoordinator is not null &&
                materialRegistry is not null)
            {
                if (!pendingSave.IsCompletedSuccessfully)
                {
                    Console.WriteLine("PHYXEL_ACCEPTANCE_FAILED phase_v5_roundtrip save failed");
                    Environment.ExitCode = 1;
                    Exit();
                    return;
                }
                acceptance.MarkPhaseRoundTripLoading(dispatchCoordinator);
                dispatchCoordinator.ClearCurrentWorld(settings);
                temperatureProbe.Reset();
                pendingLoad = stateSerializer.LoadAsync(scenePath, materialRegistry);
                pendingSave = null;
                return;
            }
            SetStatus(pendingSave.IsCompletedSuccessfully ? "Сцена сохранена" : "Ошибка сохранения");
            pendingSave = null;
        }
        if (pendingLoad is not { IsCompleted: true } || userInterface is null)
        {
            return;
        }
        if (pendingLoad.IsCompletedSuccessfully && pendingLoad.Result is { } loaded)
        {
            temperatureProbe.Reset();
            SimulationStateSerializer.Apply(loaded.State, settings);
            userInterface.SelectedMaterial = loaded.State.SelectedMaterial;
            if (loaded.World is not null && resourceManager is not null && materialRegistry is not null)
            {
                bool containsMatter = SimulationStateSerializer.ContainsMatter(loaded.World);
                currentResources = resourceManager.CreateOrResize(settings, containsMatter);
                stateSerializer.ApplyWorldSnapshot(currentResources, loaded.World);
                dispatchCoordinator?.RestoreWorldActivity(
                    currentResources,
                    containsMatter,
                    SimulationStateSerializer.ContainsContactTransitionSource(
                        loaded.World,
                        materialRegistry),
                    settings.HydraulicPressure);
                if (acceptance.IsPhaseRoundTripLoading)
                {
                    acceptance.MarkPhaseRoundTripLoaded(frameIndex);
                }
                SetStatus(loaded.Warnings.Count == 0
                    ? "Сцена загружена"
                    : $"Сцена загружена с предупреждениями: {loaded.Warnings[0]}");
            }
        }
        else
        {
            SetStatus(File.Exists(scenePath) ? "Ошибка загрузки" : "Сохранённая сцена не найдена");
        }
        pendingLoad = null;
    }

    private void BeginAcceptanceCapture()
    {
        if (acceptanceSuccess || !acceptance.Active || pendingWorldCapture ||
            pendingAcceptanceCheckpoint ||
            currentResources is null || userInterface is null || dispatchCoordinator is null)
        {
            return;
        }
        if (!acceptance.CanBeginFinalCapture(frameIndex, dispatchCoordinator))
        {
            return;
        }
        capturedMaterial = userInterface.SelectedMaterial;
        Console.WriteLine(
            $"PHYXEL_ACCEPTANCE_ACTIVITY cellularSleeping={dispatchCoordinator.CellularSleeping} " +
            $"solidSleeping={dispatchCoordinator.SolidSleeping} " +
            $"solidNeedsCellular={dispatchCoordinator.SolidMotionNeedsCellular} " +
            $"settledObservations={dispatchCoordinator.SettledObservations}");
        stateSerializer.BeginWorldCapture(currentResources);
        pendingWorldCapture = true;
        Console.WriteLine("PHYXEL_ACCEPTANCE_CAPTURE_BEGIN");
    }

    private void BeginAcceptanceCheckpoint()
    {
        if (!acceptance.Active || pendingWorldCapture || pendingAcceptanceCheckpoint ||
            currentResources is null || dispatchCoordinator is null ||
            !acceptance.TryBeginAcceptanceCheckpoint(
                frameIndex,
                dispatchCoordinator,
                out ulong checkpointTick))
        {
            return;
        }
        stateSerializer.BeginWorldCapture(currentResources);
        pendingAcceptanceCheckpoint = true;
        pendingAcceptanceCheckpointFrame = frameIndex;
        pendingAcceptanceCheckpointTick = checkpointTick;
        Console.WriteLine($"PHYXEL_THERMAL_CHECKPOINT_CAPTURE ticks={checkpointTick}");
    }

    private void SetStatus(string message)
    {
        transientStatus = message;
        transientStatusRemaining = 3;
    }

    private void UpdateFrameRate(GameTime gameTime)
    {
        frameRateAccumulator += gameTime.ElapsedGameTime.TotalSeconds;
        accumulatedFrames++;
        if (frameRateAccumulator < 0.5)
        {
            return;
        }
        displayedFrameRate = accumulatedFrames / frameRateAccumulator;
        frameRateAccumulator = 0;
        accumulatedFrames = 0;
    }

    internal static Rectangle FitWorldToCanvas(Rectangle canvas, int worldWidth, int worldHeight)
    {
        float scale = MathF.Min(canvas.Width / (float)worldWidth, canvas.Height / (float)worldHeight);
        int width = Math.Max(1, (int)MathF.Round(worldWidth * scale));
        int height = Math.Max(1, (int)MathF.Round(worldHeight * scale));
        return new Rectangle(
            canvas.X + (canvas.Width - width) / 2,
            canvas.Bottom - height,
            width,
            height);
    }
}
