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
    private readonly GpuCommandEncoder commandEncoder = new();
    private readonly SimulationStateSerializer stateSerializer = new();
    private readonly GpuDebugProbe debugProbe = new();
    private readonly AcceptanceRegressionHarness acceptance = new();
    private readonly string scenePath;
    private SpriteBatch? spriteBatch;
    private MaterialRegistry? materialRegistry;
    private GpuResourceLifecycleManager? resourceManager;
    private SimulationDispatchCoordinator? dispatchCoordinator;
    private SandboxUiCoordinator? userInterface;
    private GpuSimulationResources? currentResources;
    private Task? pendingSave;
    private Task<LoadedSimulationScene?>? pendingLoad;
    private bool pendingWorldCapture;
    private bool acceptanceSuccess;
    private MaterialId capturedMaterial;
    private string transientStatus = string.Empty;
    private float transientStatusRemaining;
    private double frameRateAccumulator;
    private int accumulatedFrames;
    private double displayedFrameRate = 60;
    private uint frameIndex;
    private RawInputSnapshot latestInput;

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
        IsFixedTimeStep = false;
        TargetElapsedTime = TimeSpan.FromSeconds(1d / 60d);
        Window.AllowUserResizing = true;
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
        }
        scenePath = Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SCENE_PATH") ??
            Path.Combine(
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
        if (acceptance.RequiresSavedScene)
        {
            pendingLoad = stateSerializer.LoadAsync(scenePath);
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
        acceptance.ConfigureSettings(frameIndex, settings);
        Rectangle worldBounds = FitWorldToCanvas(userInterface.CanvasBounds, settings.Width, settings.Height);
        IReadOnlyList<BrushDrawCommand> commands = acceptance.Active
            ? acceptance.CreateCommands(frameIndex)
            : brushController.CreateCommands(
                input,
                worldBounds,
                settings,
                userInterface.SelectedMaterial,
                userInterface.PointerConsumed);
        try
        {
            currentResources = dispatchCoordinator.DispatchFrame(settings, commandEncoder.Encode(commands));
            acceptance.CaptureScreenshot(currentResources, frameIndex);
            debugProbe.Update(currentResources, frameIndex++);
            dispatchCoordinator.ObserveStatistics(debugProbe.Latest);
            BeginAcceptanceCapture();
        }
        catch (SharpDXException exception) when (
            exception.ResultCode.Code == unchecked((int)0x8007000E) && settings.Scale > 0.25f)
        {
            settings.ApplyScale(settings.Scale - 0.25f);
            SetStatus("Видеопамять ограничена: масштаб снижен");
        }
        transientStatusRemaining = Math.Max(0, transientStatusRemaining - input.DeltaSeconds);
        if (transientStatusRemaining == 0)
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
        Rectangle worldBounds = FitWorldToCanvas(
            userInterface.CanvasBounds,
            currentResources.Width,
            currentResources.Height);
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
        if (actions.GravityChanged)
        {
            dispatchCoordinator.SetSolidGravityEnabled(settings.SolidGravity);
            SetStatus(settings.SolidGravity ? "Гравитация включена" : "Гравитация выключена");
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
            pendingWorldCapture = false;
            if (acceptance.Active)
            {
                bool passed = acceptance.Validate(
                    snapshot,
                    debugProbe.Latest,
                    displayedFrameRate,
                    out _);
                Environment.ExitCode = passed ? 0 : 1;
                acceptanceSuccess = true;
                Exit();
                return;
            }
            pendingSave = stateSerializer.SaveAsync(scenePath, settings, capturedMaterial, snapshot);
            SetStatus("Сохранение сцены…");
        }
        if (pendingSave is { IsCompleted: true })
        {
            SetStatus(pendingSave.IsCompletedSuccessfully ? "Сцена сохранена" : "Ошибка сохранения");
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
                SetStatus("Сцена загружена");
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
        if (acceptanceSuccess || !acceptance.Active || pendingWorldCapture || frameIndex < acceptance.CaptureFrame ||
            currentResources is null || userInterface is null)
        {
            return;
        }
        capturedMaterial = userInterface.SelectedMaterial;
        stateSerializer.BeginWorldCapture(currentResources);
        pendingWorldCapture = true;
        Console.WriteLine("PHYXEL_ACCEPTANCE_CAPTURE_BEGIN");
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
