using System;
using System.Collections.Generic;
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
    private readonly GpuCommandEncoder commandEncoder = new();
    private readonly SimulationStateSerializer stateSerializer = new();
    private readonly GpuDebugProbe debugProbe = new();
    private readonly string scenePath;
    private readonly bool automatedVerification;
    private readonly bool physicsRegressionVerification;
    private readonly bool criticalRegressionVerification;
    private readonly bool startupPerformanceVerification;
    private readonly bool solidSpawnRegressionVerification;
    private readonly bool solidSpawnPerformanceVerification;
    private readonly bool hydrodynamicsRegressionVerification;
    private readonly bool sandWaterRegressionVerification;
    private readonly bool hydrodynamicsPerformanceVerification;
    private readonly SpecificationRegressionHarness specificationRegression;
    private SpriteBatch? spriteBatch;
    private MaterialRegistry? materialRegistry;
    private GpuResourceLifecycleManager? resourceManager;
    private SimulationDispatchCoordinator? dispatchCoordinator;
    private SandboxUiCoordinator? userInterface;
    private GpuSimulationResources? currentResources;
    private Task? pendingSave;
    private Task<LoadedSimulationScene?>? pendingLoad;
    private bool pendingWorldCapture;
    private MaterialId capturedMaterial;
    private string transientStatus = string.Empty;
    private float transientStatusRemaining;
    private double frameRateAccumulator;
    private int accumulatedFrames;
    private double displayedFrameRate = 60;
    private uint frameIndex;
    private int automatedVerificationStage;
    private RawInputSnapshot latestInput;
    private StartupPerformanceVerifier? startupPerformanceVerifier;
    private StartupPerformanceVerifier? solidSpawnPerformanceVerifier;
    private StartupPerformanceVerifier? hydrodynamicsPerformanceVerifier;
    private SimulationStatistics hydrodynamicsInitialStatistics;
    private double hydrodynamicsInitialFramesPerSecond;
    private bool hydrodynamicsInitialPanelCaptured;

    public PhyxelGame()
    {
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = SimulationSettings.NativeWidth,
            PreferredBackBufferHeight = SimulationSettings.NativeHeight,
            GraphicsProfile = GraphicsProfile.HiDef,
            SynchronizeWithVerticalRetrace = true,
            PreferMultiSampling = false,
            IsFullScreen = true,
            HardwareModeSwitch = false
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1d / 60d);
        Window.AllowUserResizing = true;
        string? verificationPath = Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SCENE_PATH");
        automatedVerification = !string.IsNullOrWhiteSpace(verificationPath);
        physicsRegressionVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_PHYSICS_REGRESSION"),
            "1",
            StringComparison.Ordinal);
        criticalRegressionVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_CRITICAL_REGRESSION"),
            "1",
            StringComparison.Ordinal);
        startupPerformanceVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_PERFORMANCE_REGRESSION"),
            "1",
            StringComparison.Ordinal);
        solidSpawnRegressionVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_SOLID_SPAWN_REGRESSION"),
            "1",
            StringComparison.Ordinal);
        solidSpawnPerformanceVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_SOLID_SPAWN_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);
        hydrodynamicsRegressionVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_HYDRO_REGRESSION"),
            "1",
            StringComparison.Ordinal);
        sandWaterRegressionVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_SAND_WATER_REGRESSION"),
            "1",
            StringComparison.Ordinal);
        hydrodynamicsPerformanceVerification = string.Equals(
            Environment.GetEnvironmentVariable("PHYXEL_HYDRO_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);
        specificationRegression = new SpecificationRegressionHarness();
        if (physicsRegressionVerification || criticalRegressionVerification || solidSpawnRegressionVerification ||
            hydrodynamicsRegressionVerification || sandWaterRegressionVerification ||
            specificationRegression.RequiresQuarterScale)
        {
            settings.ApplyScale(0.25f);
        }
        if (solidSpawnRegressionVerification || solidSpawnPerformanceVerification)
        {
            settings.Gravity = 0;
        }
        scenePath = automatedVerification
            ? verificationPath!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Phyxel",
                "scene.json");
    }

    protected override void LoadContent()
    {
        spriteBatch = new SpriteBatch(GraphicsDevice);
        SpriteFont font = Content.Load<SpriteFont>("Fonts/SandboxFont");
        materialRegistry = new MaterialRegistry();
        resourceManager = new GpuResourceLifecycleManager(GraphicsDevice, materialRegistry);
        resourceManager.PrepareSimulation(settings);
        dispatchCoordinator = new SimulationDispatchCoordinator(resourceManager, materialRegistry);
        userInterface = new SandboxUiCoordinator(materialRegistry, font, resourceManager);
        if (startupPerformanceVerification)
        {
            startupPerformanceVerifier = new StartupPerformanceVerifier();
        }
        if (solidSpawnPerformanceVerification)
        {
            solidSpawnPerformanceVerifier = new StartupPerformanceVerifier(
                4,
                55,
                "PHYXEL_SOLID_SPAWN_PERFORMANCE");
        }
        if (hydrodynamicsPerformanceVerification)
        {
            hydrodynamicsPerformanceVerifier = new StartupPerformanceVerifier(
                4,
                55,
                "PHYXEL_HYDRO_PERFORMANCE");
        }

        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        if (userInterface is null || dispatchCoordinator is null)
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
        UiFrameActions actions = userInterface.Update(input, GraphicsDevice.Viewport, settings);
        ProcessUiActions(actions);
        Rectangle worldBounds = FitWorldToCanvas(userInterface.CanvasBounds, settings.Width, settings.Height);
        IReadOnlyList<BrushDrawCommand> commands = specificationRegression.Active
            ? specificationRegression.CreateCommands(frameIndex, settings.Width, settings.Height)
            : hydrodynamicsRegressionVerification || hydrodynamicsPerformanceVerification
            ? HydrodynamicsRegressionScenario.CreateCommands(frameIndex)
            : sandWaterRegressionVerification
                ? SandWaterRegressionScenario.CreateCommands(frameIndex)
                : solidSpawnPerformanceVerification
            ? SolidSpawnPerformanceScenario.CreateCommands(frameIndex, settings.Width, settings.Height)
            : solidSpawnRegressionVerification
                ? SolidSpawnRegressionScenario.CreateCommands(frameIndex)
                : criticalRegressionVerification
            ? CriticalRegressionScenario.CreateCommands(frameIndex, settings.Width, settings.Height)
            : physicsRegressionVerification
                ? PhysicsRegressionScenario.CreateCommands(frameIndex, settings.Width, settings.Height)
                : brushController.CreateCommands(
                    input,
                    worldBounds,
                    settings,
                    userInterface.SelectedMaterial,
                    userInterface.PointerConsumed);
        try
        {
            currentResources = dispatchCoordinator.DispatchFrame(settings, commandEncoder.Encode(commands));
            specificationRegression.CaptureScreenshot(currentResources, frameIndex);
            debugProbe.Update(currentResources, frameIndex++);
            dispatchCoordinator.ObserveStatistics(debugProbe.Latest);
            BeginAutomatedVerificationWhenReady();
        }
        catch (SharpDXException exception) when (exception.ResultCode.Code == unchecked((int)0x8007000E) && settings.Scale > 0.25f)
        {
            settings.ApplyScale(settings.Scale - 0.25f);
            SetStatus("Видеопамять ограничена: масштаб снижен");
        }

        transientStatusRemaining = Math.Max(0f, transientStatusRemaining - input.DeltaSeconds);
        if (transientStatusRemaining == 0f)
        {
            transientStatus = string.Empty;
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(9, 11, 14));
        if (spriteBatch is null || userInterface is null || currentResources is null)
        {
            base.Draw(gameTime);
            return;
        }

        Rectangle worldBounds = FitWorldToCanvas(userInterface.CanvasBounds, currentResources.Width, currentResources.Height);
        spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.Opaque,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);
        spriteBatch.Draw(currentResources.PresentationTexture, worldBounds, Color.White);
        spriteBatch.End();
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
        userInterface.Draw(spriteBatch, settings, debugProbe.Latest, displayedFrameRate, transientStatus);
        spriteBatch.End();
        UpdateFrameRate(gameTime);
        if (hydrodynamicsPerformanceVerifier is not null && !hydrodynamicsInitialPanelCaptured && frameIndex >= 35)
        {
            hydrodynamicsInitialStatistics = debugProbe.Latest;
            hydrodynamicsInitialFramesPerSecond = displayedFrameRate;
            hydrodynamicsInitialPanelCaptured = true;
        }
        if (startupPerformanceVerifier is not null &&
            startupPerformanceVerifier.RecordFrame(out bool performancePassed, out string performanceReport))
        {
            Console.WriteLine(performanceReport);
            Console.WriteLine($"PHYXEL_STARTUP_DISPATCHES physics={dispatchCoordinator?.FullGridPhysicsDispatches ?? 0} composition={dispatchCoordinator?.CompositionDispatches ?? 0}");
            Console.WriteLine(performancePassed
                ? "PHYXEL_STARTUP_PERFORMANCE_SUCCESS"
                : "PHYXEL_STARTUP_PERFORMANCE_FAILED");
            Exit();
        }

        if (solidSpawnPerformanceVerifier is not null &&
            solidSpawnPerformanceVerifier.RecordFrame(out bool spawnPerformancePassed, out string spawnPerformanceReport))
        {
            Console.WriteLine(spawnPerformanceReport);
            Console.WriteLine($"PHYXEL_SOLID_SPAWN_DISPATCHES topology={dispatchCoordinator?.LocalTopologyDispatches ?? 0} physics={dispatchCoordinator?.FullGridPhysicsDispatches ?? 0} composition={dispatchCoordinator?.CompositionDispatches ?? 0}");
            Console.WriteLine(spawnPerformancePassed
                ? "PHYXEL_SOLID_SPAWN_PERFORMANCE_SUCCESS"
                : "PHYXEL_SOLID_SPAWN_PERFORMANCE_FAILED");
            Exit();
        }

        if (hydrodynamicsPerformanceVerifier is not null &&
            hydrodynamicsPerformanceVerifier.RecordFrame(out bool hydroPerformancePassed, out string hydroPerformanceReport))
        {
            Console.WriteLine(hydroPerformanceReport);
            Console.WriteLine($"PHYXEL_HYDRO_DISPATCHES physics={dispatchCoordinator?.FullGridPhysicsDispatches ?? 0} composition={dispatchCoordinator?.CompositionDispatches ?? 0}");
            Console.WriteLine($"PHYXEL_HYDRO_PANEL_BEGIN fps={hydrodynamicsInitialFramesPerSecond:0.0} activeBonds={hydrodynamicsInitialStatistics.ActiveBonds} activeParticles={hydrodynamicsInitialStatistics.ActiveParticles}");
            Console.WriteLine($"PHYXEL_HYDRO_PANEL_END fps={displayedFrameRate:0.0} activeBonds={debugProbe.Latest.ActiveBonds} activeParticles={debugProbe.Latest.ActiveParticles}");
            Console.WriteLine(hydroPerformancePassed
                ? "PHYXEL_HYDRO_PERFORMANCE_SUCCESS"
                : "PHYXEL_HYDRO_PERFORMANCE_FAILED");
            Exit();
        }

        if (specificationRegression.RecordPerformanceFrame(
            dispatchCoordinator!,
            out bool restingPassed,
            out string restingReport))
        {
            Console.WriteLine(restingReport);
            Console.WriteLine(restingPassed ? "PHYXEL_REST_PERFORMANCE_SUCCESS" : "PHYXEL_REST_PERFORMANCE_FAILED");
            Exit();
        }

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        resourceManager?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }

    private void ProcessUiActions(UiFrameActions actions)
    {
        if (userInterface is null || dispatchCoordinator is null)
        {
            return;
        }

        if (actions.ClearRequested)
        {
            dispatchCoordinator.ClearCurrentWorld(settings);
            SetStatus("Сцена очищена");
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
            pendingLoad = stateSerializer.LoadAsync(scenePath);
            SetStatus("Загрузка…");
        }
    }

    private void ProcessSerializationCompletion()
    {
        if (pendingWorldCapture && currentResources is not null &&
            stateSerializer.TryCompleteWorldCapture(currentResources, out SimulationWorldSnapshot? snapshot) &&
            snapshot is not null)
        {
            if (automatedVerification)
            {
                Console.WriteLine("PHYXEL_VERIFY_CAPTURE_COMPLETE");
            }

            if (physicsRegressionVerification)
            {
                bool physicsPassed = PhysicsRegressionVerifier.Validate(snapshot, out string report);
                Console.WriteLine(report);
                Console.WriteLine(physicsPassed
                    ? "PHYXEL_PHYSICS_REGRESSION_SUCCESS"
                    : "PHYXEL_PHYSICS_REGRESSION_FAILED");
                if (!physicsPassed)
                {
                    Exit();
                    return;
                }
            }

            if (criticalRegressionVerification)
            {
                bool criticalPassed = CriticalRegressionVerifier.Validate(snapshot, out string criticalReport);
                Console.WriteLine(criticalReport);
                Console.WriteLine(criticalPassed
                    ? "PHYXEL_CRITICAL_REGRESSION_SUCCESS"
                    : "PHYXEL_CRITICAL_REGRESSION_FAILED");
                if (!criticalPassed)
                {
                    Exit();
                    return;
                }
            }

            if (solidSpawnRegressionVerification)
            {
                bool spawnPassed = SolidSpawnRegressionVerifier.Validate(snapshot, out string spawnReport);
                Console.WriteLine(spawnReport);
                Console.WriteLine(spawnPassed
                    ? "PHYXEL_SOLID_SPAWN_REGRESSION_SUCCESS"
                    : "PHYXEL_SOLID_SPAWN_REGRESSION_FAILED");
                if (!spawnPassed)
                {
                    Exit();
                    return;
                }
            }

            if (hydrodynamicsRegressionVerification)
            {
                bool hydroPassed = HydrodynamicsRegressionVerifier.Validate(snapshot, out string hydroReport);
                Console.WriteLine(hydroReport);
                Console.WriteLine(hydroPassed
                    ? "PHYXEL_HYDRO_REGRESSION_SUCCESS"
                    : "PHYXEL_HYDRO_REGRESSION_FAILED");
                if (!hydroPassed)
                {
                    Exit();
                    return;
                }
            }

            if (sandWaterRegressionVerification)
            {
                bool sandWaterPassed = SandWaterRegressionVerifier.Validate(snapshot, out string sandWaterReport);
                Console.WriteLine(sandWaterReport);
                Console.WriteLine(sandWaterPassed
                    ? "PHYXEL_SAND_WATER_REGRESSION_SUCCESS"
                    : "PHYXEL_SAND_WATER_REGRESSION_FAILED");
                if (!sandWaterPassed)
                {
                    Exit();
                    return;
                }
            }

            if (specificationRegression.Active && dispatchCoordinator is not null)
            {
                bool specificationPassed = specificationRegression.Validate(
                    snapshot,
                    debugProbe.Latest,
                    dispatchCoordinator,
                    out _);
                if (!specificationPassed)
                {
                    Exit();
                    return;
                }
            }

            pendingWorldCapture = false;
            pendingSave = stateSerializer.SaveAsync(scenePath, settings, capturedMaterial, snapshot);
            SetStatus("Сохранение сцены…");
        }

        if (pendingSave is { IsCompleted: true })
        {
            bool succeeded = pendingSave.IsCompletedSuccessfully;
            SetStatus(succeeded ? "Сцена сохранена" : "Ошибка сохранения");
            if (automatedVerification && automatedVerificationStage == 1)
            {
                if (succeeded)
                {
                    pendingLoad = stateSerializer.LoadAsync(scenePath);
                    automatedVerificationStage = 2;
                }
                else
                {
                    Console.WriteLine("PHYXEL_VERIFY_SAVE_FAILED");
                    Exit();
                }
            }

            pendingSave = null;
        }

        if (pendingLoad is not { IsCompleted: true } || userInterface is null)
        {
            return;
        }

        if (pendingLoad.IsCompletedSuccessfully && pendingLoad.Result is { } loaded)
        {
            SimulationStateSerializer.Apply(loaded.State, settings);
            userInterface.SelectedMaterial = loaded.State.SelectedMaterial;
            if (loaded.World is not null && resourceManager is not null)
            {
                bool containsMatter = SimulationStateSerializer.ContainsMatter(loaded.World);
                currentResources = resourceManager.CreateOrResize(settings, containsMatter);
                stateSerializer.ApplyWorldSnapshot(currentResources, loaded.World);
                dispatchCoordinator?.RestoreWorldActivity(currentResources, containsMatter);
                SetStatus("Сцена и мир загружены");
                if (automatedVerification && automatedVerificationStage == 2)
                {
                    automatedVerificationStage = 3;
                    Console.WriteLine("PHYXEL_VERIFY_SUCCESS");
                    Exit();
                }
            }
            else
            {
                SetStatus("Параметры сцены загружены");
            }
        }
        else
        {
            SetStatus(File.Exists(scenePath) ? "Ошибка загрузки" : "Сохранённая сцена не найдена");
            if (automatedVerification)
            {
                Console.WriteLine("PHYXEL_VERIFY_LOAD_FAILED");
                Exit();
            }
        }

        pendingLoad = null;
    }

    private void BeginAutomatedVerificationWhenReady()
    {
        uint captureFrame = specificationRegression.Active
            ? specificationRegression.CaptureFrame
            : hydrodynamicsRegressionVerification
            ? 123u
            : sandWaterRegressionVerification
                ? 302u
                : solidSpawnRegressionVerification
                    ? 20u
            : criticalRegressionVerification
                ? 55u
                : physicsRegressionVerification
                    ? 180u
                    : 10u;
        if (!automatedVerification || automatedVerificationStage != 0 || frameIndex < captureFrame ||
            currentResources is null || userInterface is null)
        {
            return;
        }

        capturedMaterial = userInterface.SelectedMaterial;
        stateSerializer.BeginWorldCapture(currentResources);
        pendingWorldCapture = true;
        automatedVerificationStage = 1;
        Console.WriteLine("PHYXEL_VERIFY_CAPTURE_BEGIN");
    }

    private void SetStatus(string message)
    {
        transientStatus = message;
        transientStatusRemaining = 3f;
    }

    private void UpdateFrameRate(GameTime gameTime)
    {
        frameRateAccumulator += gameTime.ElapsedGameTime.TotalSeconds;
        accumulatedFrames++;
        if (frameRateAccumulator < 0.5d)
        {
            return;
        }

        displayedFrameRate = accumulatedFrames / frameRateAccumulator;
        frameRateAccumulator = 0d;
        accumulatedFrames = 0;
    }

    private static Rectangle FitWorldToCanvas(Rectangle canvas, int worldWidth, int worldHeight)
    {
        float scale = MathF.Min(canvas.Width / (float)worldWidth, canvas.Height / (float)worldHeight);
        int width = Math.Max(1, (int)MathF.Round(worldWidth * scale));
        int height = Math.Max(1, (int)MathF.Round(worldHeight * scale));
        return new Rectangle(
            canvas.X + (canvas.Width - width) / 2,
            canvas.Y + (canvas.Height - height) / 2,
            width,
            height);
    }
}
