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
    private double displayedFrameRate;
    private uint frameIndex;
    private int automatedVerificationStage;

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
        if (physicsRegressionVerification)
        {
            settings.ApplyScale(0.25f);
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
        dispatchCoordinator = new SimulationDispatchCoordinator(resourceManager);
        userInterface = new SandboxUiCoordinator(materialRegistry, font, resourceManager);
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
        if (input.EscapePressed)
        {
            Exit();
            return;
        }

        ProcessSerializationCompletion();
        UiFrameActions actions = userInterface.Update(input, GraphicsDevice.Viewport, settings);
        ProcessUiActions(actions);
        Rectangle worldBounds = FitWorldToCanvas(userInterface.CanvasBounds, settings.Width, settings.Height);
        IReadOnlyList<BrushDrawCommand> commands = physicsRegressionVerification
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
            debugProbe.Update(currentResources, frameIndex++);
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
        userInterface.Draw(spriteBatch, settings, debugProbe.Latest, displayedFrameRate, transientStatus);
        spriteBatch.End();
        UpdateFrameRate(gameTime);
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
                currentResources = resourceManager.CreateOrResize(settings);
                stateSerializer.ApplyWorldSnapshot(currentResources, loaded.World);
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
        uint captureFrame = physicsRegressionVerification ? 180u : 10u;
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
