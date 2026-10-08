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
    private readonly WorldEditHistory editHistory = new();
    private readonly SimulationStateSerializer historySerializer = new();
    private readonly CanvasCameraController cameraController = new();
    private readonly GpuCommandEncoder commandEncoder = new();
    private readonly SimulationStateSerializer stateSerializer = new();
    private readonly GpuDebugProbe debugProbe = new();
    private readonly GpuTemperatureProbe temperatureProbe = new();
    private readonly GpuTemperatureSensors temperatureSensors = new();
    private readonly AcceptanceRegressionHarness acceptance = new();
    private readonly SimulationClockTrace simulationClockTrace = new();
    private string scenePath;
    private bool hasChosenScenePath;
    private bool sceneDialogOpen;
    private string? pendingSavePath;
    private string? pendingLoadPath;
    private SimulationSettings? pendingSaveSettings;
    private Func<string, bool, IntPtr, string?> scenePathPicker =
        (path, save, owner) => SceneFileDialog.Select(path, save, owner);
    private readonly string? uiScreenshotPath;
    private readonly float? uiDpiOverride;
    private SpriteBatch? spriteBatch;
    private RasterizerState? canvasRasterizerState;
    private MaterialRegistry? materialRegistry;
    private GpuResourceLifecycleManager? resourceManager;
    private SimulationDispatchCoordinator? dispatchCoordinator;
    private SandboxUiCoordinator? userInterface;
    private GpuSimulationResources? currentResources;
    private IEnumerator<GpuSimulationResources>? oilSmokeVerification;
    private Task? pendingSave;
    private Task<LoadedSimulationScene?>? pendingLoad;
    private bool pendingWorldCapture;
    private readonly SimulationStateSerializer canvasCaptureSerializer = new();
    private bool canvasExpansionPending;
    private Point canvasExpansionSize;
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
    private string? diagnosticUiCapturePath;
    private int diagnosticFramesPerSecond;
    private long diagnosticFrameStart;
    private bool diagnosticTimerResolution;
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

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
            // Acceptance windows may be hidden/unfocused. Keep their fixed-
            // step benchmark from being throttled by the inactive-window sleep.
            InactiveSleepTime = TimeSpan.Zero;
            string? requestedScale = Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_SCALE");
            float acceptanceScale = float.TryParse(
                requestedScale,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsedScale)
                ? parsedScale
                : acceptance.RequiresNativeResolution ? 1f : 0.25f;
            settings.ApplyScale(acceptanceScale);
            ApplyAcceptanceWorldSizeOverride(settings);
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
        if((Environment.GetEnvironmentVariable("PHYXEL_VERIFY_HANDOFF")=="1" || Environment.GetEnvironmentVariable("PHYXEL_VERIFY_ABSORPTION")=="1" || Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FILTERS")=="1"))
        {
            graphics.SynchronizeWithVerticalRetrace=false;
            IsFixedTimeStep=false;
            diagnosticFramesPerSecond=100;
            // Windows' default 15.6ms sleep quantum would cap this fixture near
            // 64 FPS even with an empty grid. Release the request at shutdown.
            diagnosticTimerResolution=timeBeginPeriod(1)==0;
        }
        scenePath = Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SCENE_PATH") ??
            (acceptance.Active && !acceptance.RequiresSavedScene
                ? Path.Combine(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ??
                    Path.Combine(AppContext.BaseDirectory, "artifacts"), "roundtrip-scene.json")
                : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Phyxel",
                "scene.json"));
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
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_UNDO") == "1")
        {
            try { UndoRegressionVerifier.Run(this, dispatchCoordinator, materialRegistry); }
            catch (Exception e) { Console.WriteLine($"PHYXEL_UNDO_FAILED {e}"); Environment.ExitCode = 1; }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_BODY_SUITE") == "1")
        {
            string diagnosticsRoot=Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")??"artifacts/body-suite";
            try
            {
                Environment.SetEnvironmentVariable("PHYXEL_ARTIFACT_DIR",Path.Combine(diagnosticsRoot,"body"));
                BodyBalanceRegressionVerifier.Run(dispatchCoordinator,materialRegistry);
                Environment.SetEnvironmentVariable("PHYXEL_ARTIFACT_DIR",Path.Combine(diagnosticsRoot,"frozen"));
                FrozenBodyRegressionVerifier.Run(dispatchCoordinator,materialRegistry);
                UiLayoutRegressionTests.RunAllTests(materialRegistry,fonts,userInterface);
                Environment.SetEnvironmentVariable("PHYXEL_ARTIFACT_DIR",Path.Combine(diagnosticsRoot,"scenes"));
                SceneFileRegressionVerifier.Run(this,dispatchCoordinator,materialRegistry);
                Console.WriteLine("PHYXEL_BODY_SUITE_SUCCESS");
            }
            catch(Exception e){Console.WriteLine($"PHYXEL_BODY_SUITE_FAILED {e}");Environment.ExitCode=1;}
            finally{Environment.SetEnvironmentVariable("PHYXEL_ARTIFACT_DIR",diagnosticsRoot);}
            Exit();return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_BODY_BALANCE") == "1")
        {
            try { BodyBalanceRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch(Exception e) { Console.WriteLine($"PHYXEL_BODY_BALANCE_FAILED {e}");Environment.ExitCode=1; }
            Exit();return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FROZEN_BODIES") == "1")
        {
            try { FrozenBodyRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception e) { Console.WriteLine($"PHYXEL_FROZEN_BODY_FAILED {e}"); Environment.ExitCode = 1; }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_OIL_PHASES") == "1")
        {
            oilSmokeVerification = OilPhaseRegressionVerifier.Run(dispatchCoordinator, materialRegistry).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_LIQUID_TEMPERATURE") == "1")
        {
            oilSmokeVerification = LiquidTemperatureRegressionVerifier.Run(dispatchCoordinator, materialRegistry).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_LIQUID_FEED") == "1")
        {
            oilSmokeVerification = LiquidFeedRegressionVerifier.Run(dispatchCoordinator, materialRegistry).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_OIL_ABSORPTION") == "1")
        {
            try { OilAbsorptionRegressionVerifier.Run(dispatchCoordinator,materialRegistry); }
            catch (Exception e) { Console.WriteLine($"PHYXEL_OIL_ABSORPTION_FAILED {e}"); Environment.ExitCode=1; }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_OIL") == "1")
        {
            try { OilRegressionVerifier.Run(dispatchCoordinator,materialRegistry); }
            catch (Exception exception) { Console.WriteLine($"PHYXEL_OIL_FAILED {exception}"); Environment.ExitCode=1; }
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SUBMERGED_HEAPS") == "1")
        {
            oilSmokeVerification = SubmergedHeapRegressionVerifier.Run(dispatchCoordinator,materialRegistry).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_WOOD") == "1")
        {
            oilSmokeVerification = WoodCycleRegressionVerifier.Run(dispatchCoordinator, materialRegistry).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FUEL_MOISTURE") == "1")
        {
            oilSmokeVerification = FuelMoistureRegressionVerifier.Run(dispatchCoordinator,materialRegistry).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_WETTING_CONTACT") == "1")
        {
            try { WettingContactRegressionVerifier.Run(dispatchCoordinator,materialRegistry); }
            catch (Exception exception) { Console.WriteLine($"PHYXEL_WETTING_FAILED {exception}"); Environment.ExitCode=1; }
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_ALLOYS") == "1")
        {
            try { AlloyRegressionVerifier.Run(dispatchCoordinator, materialRegistry, GraphicsDevice); }
            catch (Exception e) { Console.WriteLine($"PHYXEL_ALLOY_FAILED {e}"); Environment.ExitCode=1; }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_POOL_LEVEL") == "1")
        {
            try { PoolLevelRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception e) { Console.WriteLine($"PHYXEL_POOL_FAILED {e}"); Environment.ExitCode=1; }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_METAL_FUSION") == "1")
        {
            try { MetalFusionRegressionVerifier.Run(dispatchCoordinator,materialRegistry); }
            catch (Exception exception) { Console.WriteLine($"PHYXEL_METAL_FUSION_FAILED {exception}"); Environment.ExitCode=1; }
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_ICE_FUSION") == "1")
        {
            try { IceFusionRegressionVerifier.Run(dispatchCoordinator,materialRegistry); }
            catch(Exception exception) { Console.WriteLine($"PHYXEL_ICE_FUSION_FAILED {exception}");Environment.ExitCode=1; }
            Exit();return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_WATER_CONTACT") == "1")
        {
            try { WaterContactRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_WATER_CONTACT_FAILED {exception}");
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_REACTION_PULSE") == "1")
        {
            try { ReactionPulseRegressionVerifier.Run(dispatchCoordinator,materialRegistry); }
            catch(Exception exception) { Console.WriteLine($"PHYXEL_REACTION_PULSE_FAILED {exception}");Environment.ExitCode=1; }
            Exit();return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_PRESSURE_SHELL") == "1")
        {
            oilSmokeVerification = PressureShellRegressionVerifier.Run(dispatchCoordinator, materialRegistry, settings).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_GUNPOWDER") == "1")
        {
            try { GunpowderRegressionVerifier.Run(dispatchCoordinator,materialRegistry); }
            catch(Exception exception) { Console.WriteLine($"PHYXEL_GUNPOWDER_FAILED {exception}");Environment.ExitCode=1; }
            Exit();return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_TRANSIENT_PACE") == "1")
        {
            try { TransientPaceRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_TRANSIENT_PACE_FAILED {exception}");
                Environment.ExitCode = 1;
            }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_GAS_TILES") == "1")
        {
            try { GasTileRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_GAS_TILES_FAILED {exception}");
                Environment.ExitCode = 1;
            }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_CHIMNEY_TRANSPORT") == "1")
        {
            try { ChimneyTransportRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_CHIMNEY_TRANSPORT_FAILED {exception}");
                Environment.ExitCode = 1;
            }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_WATER_LEVEL") == "1")
        {
            try { WaterLevelRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception) { Console.WriteLine($"PHYXEL_WATER_LEVEL_FAILED {exception}"); Environment.ExitCode=1; }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_OIL_LOCALITY") == "1")
        {
            oilSmokeVerification = OilLocalityRegressionVerifier.Run(dispatchCoordinator, materialRegistry).GetEnumerator();
            IsFixedTimeStep = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_OIL_SMOKE_FLOW") == "1")
        {
            oilSmokeVerification = OilSmokeFlowRegressionVerifier.Run(dispatchCoordinator, materialRegistry,
                settings, status => SetStatus("Автотест: " + status)).GetEnumerator();
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_LIQUID_LAYERS") == "1")
        {
            oilSmokeVerification = LiquidLayersRegressionVerifier.Run(dispatchCoordinator, materialRegistry,
                settings, status => SetStatus("Автотест: " + status)).GetEnumerator();
        }
        if(Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FILTERS")=="1"){
            InactiveSleepTime=TimeSpan.Zero;
            oilSmokeVerification=Environment.GetEnvironmentVariable("PHYXEL_SCENE_REPAIR_ONLY")=="1"
                ? SceneRepairRegressionVerifier.Run(dispatchCoordinator,materialRegistry,settings).GetEnumerator()
                : Environment.GetEnvironmentVariable("PHYXEL_FILTER_BRUSHES_ONLY")=="1"
                ? FilterBrushRegressionVerifier.Run(dispatchCoordinator,materialRegistry,settings).GetEnumerator()
                : FilterRegressionVerifier.Run(dispatchCoordinator,materialRegistry,settings,fps=>diagnosticFramesPerSecond=fps).GetEnumerator();
            IsFixedTimeStep=false; return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_ABSORPTION") == "1")
        {
            oilSmokeVerification=AbsorptionRegressionVerifier.Run(dispatchCoordinator,materialRegistry,fps=>diagnosticFramesPerSecond=fps).GetEnumerator();
            IsFixedTimeStep=false; return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_EXPLOSIVE_MATERIAL") == "1")
        {
            InactiveSleepTime = TimeSpan.Zero;
            oilSmokeVerification = ExplosiveMaterialRegressionVerifier.Run(dispatchCoordinator, materialRegistry).GetEnumerator();
            IsFixedTimeStep = false; return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_PRESSURE_RELIABILITY") == "1")
        {
            InactiveSleepTime=TimeSpan.Zero;
            oilSmokeVerification=PressureReliabilityRegressionVerifier.Run(dispatchCoordinator,materialRegistry).GetEnumerator();
            IsFixedTimeStep=false;return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FUSE") == "1")
        {
            InactiveSleepTime = TimeSpan.Zero;
            oilSmokeVerification = FuseRegressionVerifier.Run(dispatchCoordinator, materialRegistry).GetEnumerator();
            IsFixedTimeStep = false; return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FURNACE_SENSORS") == "1")
        {
            InactiveSleepTime = TimeSpan.Zero;
            if (Environment.GetEnvironmentVariable("PHYXEL_SENSOR_REALTIME") == "1")
                currentResources = FurnaceSensorRegressionVerifier.LoadFixture(dispatchCoordinator, materialRegistry, settings);
            else
            {
                oilSmokeVerification = FurnaceSensorRegressionVerifier.Run(dispatchCoordinator, materialRegistry, settings).GetEnumerator();
                IsFixedTimeStep = false; return;
            }
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_HANDOFF") == "1")
        {
            InactiveSleepTime = TimeSpan.Zero;
            oilSmokeVerification = Environment.GetEnvironmentVariable("PHYXEL_VERIFY_DRAFT")=="1"
                ? FurnaceDraftRegressionVerifier.Run(dispatchCoordinator,materialRegistry,settings,fps=>diagnosticFramesPerSecond=fps).GetEnumerator()
                : HandoffRegressionVerifier.Run(dispatchCoordinator, materialRegistry,
                settings, temperatureProbe, status => SetStatus(status.Length==0 ? string.Empty : "Автотест: " + status, status.Length==0 ? 0 : 3),
                path => diagnosticUiCapturePath = path,
                fps => diagnosticFramesPerSecond=fps).GetEnumerator();
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_MATERIAL_ENVIRONMENT") == "1")
        {
            try { MaterialEnvironmentRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception) { Console.WriteLine($"PHYXEL_ENVIRONMENT_FAILED {exception}"); Environment.ExitCode=1; }
            Exit(); return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_AIR_HEAT") == "1")
        {
            try { AirThermalRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_AIR_HEAT_FAILED {exception}");
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FURNACE_COMBUSTION") == "1")
        {
            try { FurnaceCombustionRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_FURNACE_COMBUSTION_FAILED {exception}");
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_AIR_INVENTORY") == "1")
        {
            try { AirInventoryRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_AIR_INVENTORY_FAILED {exception}");
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_AIR_MODE_SWITCH") == "1")
        {
            try { AirModeSwitchRegressionVerifier.Run(dispatchCoordinator, materialRegistry); }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_AIR_MODE_SWITCH_FAILED {exception.Message}");
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }
        if (!string.IsNullOrEmpty(uiScreenshotPath) &&
            Environment.GetEnvironmentVariable("PHYXEL_UI_PREVIEW_MATERIAL") is { } previewId)
        {
            MaterialDefinition preview = materialRegistry[previewId];
            userInterface.SelectedMaterial = preview.RuntimeIndex;
            userInterface.ActiveTool = PhyxelToolId.Brush;
            var layout = UiLayoutCalculator.Calculate(GraphicsDevice.Viewport, uiDpiOverride ?? 1);
            var tab = userInterface.CategoryPalette.GetCategoryTabBounds(layout.BottomPalette, MaterialCategoryResolver.Resolve(preview));
            var click = default(RawInputSnapshot) with { MousePosition = tab.Center, LeftDown = true, LeftPressed = true };
            userInterface.CategoryPalette.Update(click, layout.BottomPalette, preview.RuntimeIndex, false, out _);
        }
        if(Enum.TryParse<FilterSelection>(Environment.GetEnvironmentVariable("PHYXEL_UI_PREVIEW_FILTER"),out var previewFilter)){
            userInterface.ActiveTool=PhyxelToolId.Filter;settings.FilterSelection=previewFilter;
            userInterface.CategoryPalette.ShowFilters(previewFilter);
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_TEMPERATURE_SENSORS") == "1")
        {
            try
            {
                currentResources = TemperatureSensorsRegressionVerifier.Run(dispatchCoordinator, materialRegistry, settings);
                userInterface.ActiveTool = PhyxelToolId.Sensor;
            }
            catch (Exception e) { Console.WriteLine($"PHYXEL_TEMPERATURE_SENSORS_FAILED {e}"); Environment.ExitCode = 1; Exit(); return; }
            if (string.IsNullOrEmpty(uiScreenshotPath)) { Exit(); return; }
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_UI") == "1")
        {
            UiLayoutRegressionTests.RunAllTests(materialRegistry, fonts, userInterface);
            Console.WriteLine("PHYXEL_UI_REGRESSION_SUCCESS");
            Exit();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SCENE_FILES") == "1")
        {
            try { SceneFileRegressionVerifier.Run(this, dispatchCoordinator, materialRegistry); }
            catch (Exception e) { Console.WriteLine($"PHYXEL_SCENE_FILES_FAILED {e}"); Environment.ExitCode = 1; Exit(); return; }
            if (string.IsNullOrEmpty(uiScreenshotPath)) Exit();
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
                settings.HydraulicPressure,
                initialAcceptanceWorld.Oxidizer is { Length: > 0 });
            acceptance.InitializeDiagnosticFields(currentResources);
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
        if (canvasExpansionPending)
        {
            CompleteCanvasExpansion();
            return;
        }
        // Diagnostic wall-clock fixtures present exactly one Draw per update.
        // MonoGame fixed-step catch-up may run several updates per Draw, which
        // would make an update counter an invalid rendered-FPS measurement.
        if(diagnosticFramesPerSecond>0)
        {
            long now=System.Diagnostics.Stopwatch.GetTimestamp();
            double remaining=1d/diagnosticFramesPerSecond-
                (now-diagnosticFrameStart)/(double)System.Diagnostics.Stopwatch.Frequency;
            if(diagnosticFrameStart!=0 && remaining>0)
            {
                if(remaining>.002)System.Threading.Thread.Sleep((int)((remaining-.001)*1000));
                while((System.Diagnostics.Stopwatch.GetTimestamp()-diagnosticFrameStart)/
                    (double)System.Diagnostics.Stopwatch.Frequency<1d/diagnosticFramesPerSecond)
                    System.Threading.Thread.SpinWait(32);
            }
            diagnosticFrameStart=System.Diagnostics.Stopwatch.GetTimestamp();
        }
        RawInputSnapshot input = inputSampler.Sample(gameTime);
        // Reproducible screenshots use the same UI/input path as real gestures.
        if (!string.IsNullOrEmpty(uiScreenshotPath) && frameIndex >= 1 &&
            Environment.GetEnvironmentVariable("PHYXEL_UI_PREVIEW_INPUT") is { } editorPreview)
        {
            Rectangle canvas = userInterface.CanvasBounds;
            Point start = new(canvas.X + canvas.Width / 4, canvas.Y + canvas.Height / 3);
            Point end = new(canvas.X + canvas.Width * 3 / 4, canvas.Y + canvas.Height * 2 / 3);
            input = editorPreview switch
            {
                "line" => default(RawInputSnapshot) with { MousePosition = frameIndex == 1 ? start : end,
                    ShiftDown = true, LeftDown = true, LeftPressed = frameIndex == 1, DeltaSeconds = input.DeltaSeconds },
                "menu" or "exit" => default(RawInputSnapshot) with { EscapePressed = frameIndex == 1,
                    MousePosition = editorPreview == "exit" && frameIndex == 2 ? userInterface.PauseMenu.ExitBounds.Center : Point.Zero,
                    LeftPressed = editorPreview == "exit" && frameIndex == 2, DeltaSeconds = input.DeltaSeconds },
                _ => input
            };
        }
        if (sceneDialogOpen) return;
        latestInput = input;
        if (input.EscapePressed && (oilSmokeVerification is not null || acceptance.Active))
        {
            if (oilSmokeVerification is not null) Environment.ExitCode = 2;
            Exit();
            return;
        }
        if (oilSmokeVerification is not null)
        {
            transientStatusRemaining=Math.Max(0,transientStatusRemaining-input.DeltaSeconds);
            if(transientStatusRemaining==0)transientStatus=string.Empty;
            // Keep layout and the message pump active without letting input alter the fixture.
            userInterface.Update(default(RawInputSnapshot) with { MousePosition = input.MousePosition },
                GraphicsDevice.Viewport, uiDpiOverride ?? UiDisplayScale.GetDpiScale(Window.Handle), settings);
            try
            {
                if (oilSmokeVerification.MoveNext())
                {
                    currentResources = oilSmokeVerification.Current;
                    frameIndex++;
                }
                else
                {
                    oilSmokeVerification.Dispose();
                    oilSmokeVerification = null;
                    Exit();
                }
            }
            catch (Exception exception)
            {
                string test = Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FUEL_MOISTURE")=="1" ? "FUEL_MOISTURE" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_SUBMERGED_HEAPS")=="1" ? "SUBMERGED" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_ABSORPTION")=="1" ? "ABSORPTION" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_HANDOFF") == "1" ? "HANDOFF" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_LIQUID_LAYERS") == "1" ? "LL" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_LIQUID_TEMPERATURE") == "1" ? "LIQUID_TEMPERATURE" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_WOOD") == "1" ? "WOOD" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_LIQUID_FEED") == "1" ? "LIQUID_FEED" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_OIL_LOCALITY") == "1" ? "OIL_LOCALITY" :
                    Environment.GetEnvironmentVariable("PHYXEL_VERIFY_OIL_PHASES") == "1" ? "OIL_PHASES" : "OS";
                Console.WriteLine($"PHYXEL_{test}_FAILED {exception}");
                Environment.ExitCode = 1;
                Exit();
            }
            base.Update(gameTime);
            return;
        }
        ProcessSerializationCompletion();
        userInterface.CameraZoom = cameraController.Zoom;
        UiFrameActions actions = userInterface.Update(
            input,
            GraphicsDevice.Viewport,
            uiDpiOverride ?? UiDisplayScale.GetDpiScale(Window.Handle),
            settings,
            pendingSave is not null || pendingWorldCapture || pendingLoad is not null);
        ProcessUiActions(actions);
        if (actions.ExitRequested) return;
        if (!acceptance.Active && IsActive && !userInterface.PauseMenuOpen && (input.UndoPressed || input.RedoPressed))
        {
            RestoreEditHistory(input.RedoPressed);
            base.Update(gameTime);
            return;
        }
        if (!acceptance.Active && EnsureCanvasWorldFits(userInterface.CanvasBounds)) return;
        acceptance.ConfigureSettings(frameIndex, settings);
        acceptance.ApplyRuntimeControls(
            frameIndex,
            settings,
            dispatchCoordinator,
            temperatureProbe);
        Rectangle fittedWorldBounds = WorldCanvasBounds(
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
        if (!acceptance.Active && userInterface.SensorToolActive && !userInterface.PointerConsumed &&
            !FileOperationPending && IsActive && userInterface.CanvasBounds.Contains(input.MousePosition))
            if (TemperatureSensorOverlay.Edit(input, worldBounds, settings)) temperatureSensors.Reset();
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
                userInterface.BlocksBrushInput ||
                FileOperationPending ||
                !IsActive && (string.IsNullOrEmpty(uiScreenshotPath) ||
                    Environment.GetEnvironmentVariable("PHYXEL_UI_PREVIEW_INPUT") != "line") ||
                !userInterface.CanvasBounds.Contains(input.MousePosition),
                materialRegistry[userInterface.SelectedMaterial].ThermalRegulator is not null,
                userInterface.DeviceTargetTemperature,
                userInterface.DeviceMaximumPower,
                userInterface.FilterToolActive,FilterRules.Select(settings.FilterSelection,materialRegistry,userInterface.SelectedMaterial));
        try
        {
            uint acceptanceFrame = frameIndex;
            float physicalElapsedSeconds = acceptance.AdjustElapsedSeconds(input.DeltaSeconds);
            currentResources = DispatchEditorFrame(commandEncoder.Encode(commands),
                !acceptance.Active && brushController.CommandsStartStroke, physicalElapsedSeconds);
            simulationClockTrace.Observe(physicalElapsedSeconds, settings, dispatchCoordinator);
            if (simulationClockTrace.ExitRequested)
            {
                if (Environment.GetEnvironmentVariable("PHYXEL_SENSOR_REALTIME") == "1" &&
                    Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") is { Length: > 0 } sensorDir)
                {
                    diagnosticUiCapturePath = Path.Combine(sensorDir, "realtime-ui.png");
                    CaptureUiScreenshotIfRequested();
                }
                Exit();
            }
            acceptance.RecordAirPressureTrace(acceptanceFrame, currentResources);
            acceptance.RecordGasObstacleBypassTrace(acceptanceFrame, currentResources);
            acceptance.RecordGasLateralTransferTrace(acceptanceFrame, currentResources);
            acceptance.RecordGasVerticalMotionTrace(currentResources);
            acceptance.RecordSteamGasStepTrace(acceptanceFrame, currentResources);
            acceptance.RecordSteamJetInjectionTrace(acceptanceFrame, currentResources);
            acceptance.RecordSteamJetInjectionDistributionTrace(acceptanceFrame, currentResources);
            acceptance.RecordSteamJetLateralTrace(acceptanceFrame, currentResources);
            acceptance.RecordSteamJetBlockingTrace(acceptanceFrame, currentResources);
            acceptance.RecordSteamJetDiagonalTrace(acceptanceFrame, currentResources);
            acceptance.RecordSteamJetAirCouplingTrace(acceptanceFrame, currentResources);
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
            temperatureSensors.Update(currentResources, settings.TemperatureSensors, settings.AirSimulation, input.DeltaSeconds);
            acceptance.ObserveTemperatureProbe(acceptanceFrame, temperatureProbe.Latest);
            dispatchCoordinator.ObserveStatistics(debugProbe.Latest);
            BeginAcceptanceCheckpoint();
            BeginAcceptanceCapture();
        }
        catch (SharpDXException exception) when (
            exception.ResultCode.Code == unchecked((int)0x8007000E) && settings.Scale > 0.25f)
        {
            settings.ApplyScale(settings.Scale - 0.25f);
            editHistory.Clear();
            brushController.CancelStroke();
            temperatureProbe.Reset(); temperatureSensors.Reset();
            SetStatus("Видеопамять ограничена: масштаб снижен");
        }
        transientStatusRemaining = Math.Max(0, transientStatusRemaining - input.DeltaSeconds);
        if (transientStatusRemaining == 0)
        {
            transientStatus = string.Empty;
        }
        base.Update(gameTime);
    }

    internal GpuSimulationResources DispatchInteractiveFrame(ReadOnlySpan<BrushDrawCommand> commands, float elapsedSeconds)
    {
        bool wasPaused = settings.Paused;
        try
        {
            settings.Paused = wasPaused || userInterface!.PauseMenuOpen;
            return dispatchCoordinator!.DispatchFrame(settings, commands, elapsedSeconds);
        }
        finally { settings.Paused = wasPaused; }
    }

    private bool FileOperationPending => pendingSave is not null || pendingWorldCapture ||
        pendingLoad is not null || canvasExpansionPending || pendingAcceptanceCheckpoint;

    internal GpuSimulationResources DispatchEditorFrame(ReadOnlySpan<BrushDrawCommand> commands,
        bool startsStroke, float elapsedSeconds)
    {
        if (startsStroke && commands.Length > 0 && !RecordEdit()) commands = [];
        return DispatchInteractiveFrame(commands, elapsedSeconds);
    }

    private SimulationWorldSnapshot CaptureHistoryWorld()
    {
        currentResources ??= resourceManager!.CreateOrResize(settings, false);
        historySerializer.BeginWorldCapture(currentResources);
        // Wait only at the start of an edit, never once per painted frame. The
        // GPU copies are ordered before the edit; shared save staging is idle.
        SimulationWorldSnapshot? snapshot;
        while (!historySerializer.TryCompleteWorldCapture(currentResources, out snapshot))
            System.Threading.Thread.Yield();
        return snapshot!;
    }

    private bool RecordEdit()
    {
        if (FileOperationPending) { SetStatus("Дождитесь завершения сохранения или загрузки"); return false; }
        if (currentResources is { } r && (r.Width != settings.Width || r.Height != settings.Height))
        {
            editHistory.Clear();
            currentResources = resourceManager!.CreateOrResize(settings, false);
        }
        // Refuse a snapshot before allocating its CPU arrays if it exceeds the
        // history budget. Existing history cannot cross an unrecorded edit.
        var resources = currentResources;
        long estimate = resources is { IsSimulationAllocated: true }
            ? (long)resources.GridStaging.Description.SizeInBytes + resources.AirStaging.Description.SizeInBytes +
                resources.GasMotionStaging.Description.SizeInBytes + resources.OxidizerStaging.Description.SizeInBytes +
                resources.AirThermalStaging.Description.SizeInBytes + resources.ReactionPendingStaging.Description.SizeInBytes +
                resources.ReactionPulseStaging.Description.SizeInBytes + (resources.FilterCount > 0 ? (long)resources.FilterMap.Length * 4 : 0)
            : 0;
        if (estimate > editHistory.MaximumBytes)
        {
            editHistory.Clear();
            SetStatus("Большая сцена: этот штрих не поместится в историю отмены", 8);
            return true;
        }
        editHistory.Record(CaptureHistoryWorld());
        return true;
    }

    internal bool RestoreEditHistory(bool forward)
    {
        if (FileOperationPending) { SetStatus("Дождитесь завершения сохранения или загрузки"); return false; }
        brushController.CancelStroke();
        if (!(forward ? editHistory.CanRedo : editHistory.CanUndo))
        { SetStatus(forward ? "Нечего повторять" : "Нечего отменять"); return false; }
        var current = CaptureHistoryWorld();
        if (!editHistory.Restore(forward, current, out var restored) || restored is null) return false;
        if (restored.Width != settings.Width || restored.Height != settings.Height)
        { editHistory.Clear(); SetStatus("История сброшена после изменения размера мира"); return false; }
        bool matter = SimulationStateSerializer.ContainsMatter(restored);
        bool fields = restored.Oxidizer is { Length: > 0 } || restored.AirThermal is { Length: > 0 };
        currentResources = resourceManager!.CreateOrResize(settings, matter || fields || restored.Filters is { Length: > 0 });
        historySerializer.ApplyWorldSnapshot(currentResources, restored);
        dispatchCoordinator!.RestoreWorldActivity(currentResources, matter,
            SimulationStateSerializer.ContainsContactTransitionSource(restored, materialRegistry!),
            settings.HydraulicPressure, fields);
        settings.Paused = true;
        temperatureProbe.Reset(); temperatureSensors.Reset();
        currentResources = DispatchInteractiveFrame([], 0);
        ResetElapsedTime();
        SetStatus(forward ? "Действие восстановлено · пауза" : "Действие отменено · пауза");
        return true;
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

    private static void ApplyAcceptanceWorldSizeOverride(SimulationSettings settings)
    {
        bool hasWidth = int.TryParse(
            Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_WORLD_WIDTH"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int width);
        bool hasHeight = int.TryParse(
            Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_WORLD_HEIGHT"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int height);
        if (!hasWidth && !hasHeight)
        {
            return;
        }

        if (!hasWidth || !hasHeight || width < 320 || height < 180 ||
            width % SimulationSettings.AirCellSize != 0 || height % SimulationSettings.AirCellSize != 0)
        {
            throw new InvalidOperationException(
                "Acceptance world width and height must both be set, meet the minimum size, and align to AirCellSize.");
        }

        // This override is only for acceptance diagnostics that need to match
        // an external reference world's dimensions. Scale deliberately remains
        // unchanged so it cannot select a different simulation schedule.
        settings.Width = width;
        settings.Height = height;
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(9, 11, 14));
        if (spriteBatch is null || userInterface is null || currentResources is null)
        {
            base.Draw(gameTime);
            return;
        }
        Rectangle fittedWorldBounds = WorldCanvasBounds(
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
        userInterface.DrawTemperatureSensors(spriteBatch, worldBounds, settings, temperatureSensors.Readings);
        userInterface.DrawBrushIndicator(
            spriteBatch,
            latestInput.MousePosition,
            worldBounds,
            settings,
            latestInput.RightDown,
            brushController.LinePreview);
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
        if(diagnosticTimerResolution){timeEndPeriod(1);diagnosticTimerResolution=false;}
        oilSmokeVerification?.Dispose();
        simulationClockTrace.Dispose();
        userInterface?.Dispose();
        temperatureSensors.Dispose();
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
        if (actions.ExitRequested)
        {
            Exit();
            return;
        }
        if ((actions.ClearRequested || actions.ResetRequested) && !RecordEdit()) return;
        if (actions.ClearRequested)
        {
            dispatchCoordinator.ClearCurrentWorld(settings);
            settings.TemperatureSensors.Clear();
            temperatureProbe.Reset(); temperatureSensors.Reset();
            SetStatus("Сцена очищена");
        }
        if (actions.ResetRequested)
        {
            dispatchCoordinator.ResetCurrentSimulation(settings);
            temperatureProbe.Reset(); temperatureSensors.Reset();
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
            SetStatus(settings.SolidGravity ? "Гравитация построек включена" : "Постройки закреплены; свободные куски подвижны");
        }
        if (actions.ScaleChanged)
        {
            settings.TemperatureSensors.Clear();
            editHistory.Clear();
            brushController.CancelStroke();
            temperatureProbe.Reset(); temperatureSensors.Reset();
        }
        if (actions.HydraulicsChanged)
        {
            SetStatus(settings.HydraulicPressure
                ? "Гидравлика сосудов включена (медленнее)"
                : "Быстрая вода включена");
        }
        if (actions.ModeChanged)
        {
            SetStatus(settings.Mode == SimulationMode.Simulation
                ? "Симуляция: горению нужен кислород"
                : "Песочница: горение без обязательного поддува");
        }
        if ((actions.SaveRequested || actions.LoadRequested) &&
            (pendingSave is not null || pendingWorldCapture || pendingLoad is not null))
        {
            SetStatus("Дождитесь завершения сохранения или загрузки");
            return;
        }
        if (actions.SaveRequested || actions.LoadRequested) brushController.CancelStroke();
        if (actions.SaveRequested && currentResources is not null)
        {
            string? path = actions.SaveAsRequested || !hasChosenScenePath
                ? SelectScenePath(true) : scenePath;
            if (path is null) return;
            pendingSavePath = path;
            pendingSaveSettings = SnapshotSaveSettings(settings);
            stateSerializer.BeginWorldCapture(currentResources);
            pendingWorldCapture = true;
            capturedMaterial = userInterface.SelectedMaterial;
            SetStatus("Копирование мира с GPU…");
            return;
        }
        if (actions.LoadRequested)
        {
            string? path = SelectScenePath(false);
            if (path is null) return;
            pendingLoadPath = path;
            temperatureProbe.Reset(); temperatureSensors.Reset();
            pendingLoad = stateSerializer.LoadAsync(path, materialRegistry);
            SetStatus("Загрузка…");
        }
    }

    private string? SelectScenePath(bool save)
    {
        bool wasPaused = settings.Paused;
        sceneDialogOpen = true;
        settings.Paused = true;
        try
        {
            string? path = scenePathPicker(scenePath, save, Window.Handle);
            if (path is null) SetStatus(save ? "Сохранение отменено" : "Загрузка отменена");
            return path;
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            SetStatus(e is InvalidDataException ? e.Message : "Не удалось открыть выбор файла");
            return null;
        }
        finally
        {
            settings.Paused = wasPaused;
            sceneDialogOpen = false;
            inputSampler.ResetAfterDialog();
            ResetElapsedTime();
        }
    }

    private static SimulationSettings SnapshotSaveSettings(SimulationSettings source) => new()
    {
        Width = source.Width, Height = source.Height, Scale = source.Scale,
        Gravity = source.Gravity, BrushRadius = source.BrushRadius, SpawnDensity = source.SpawnDensity,
        Paused = source.Paused, FilterSelection = source.FilterSelection, SolidGravity = source.SolidGravity, HydraulicPressure = source.HydraulicPressure,
        PressureDestruction = source.PressureDestruction,
        TemperatureSensors = new(source.TemperatureSensors),
        OpenBoundaries = source.OpenBoundaries, Mode = source.Mode,
        AirSimulation = source.AirSimulation, ShowAirField = source.ShowAirField,
        RenderWithoutEffects = source.RenderWithoutEffects
    };

    private void CaptureUiScreenshotIfRequested()
    {
        bool diagnosticCapture=diagnosticUiCapturePath is not null;
        uint captureFrame = uint.TryParse(Environment.GetEnvironmentVariable("PHYXEL_UI_CAPTURE_FRAME"), out uint requestedFrame) ? requestedFrame : 1;
        if (!diagnosticCapture && (uiScreenshotCaptured || string.IsNullOrWhiteSpace(uiScreenshotPath) || frameIndex < captureFrame))
        {
            return;
        }

        int width = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        Color[] pixels = new Color[width * height];
        GraphicsDevice.GetBackBufferData(pixels);
        string fullPath = Path.GetFullPath(diagnosticUiCapturePath ?? uiScreenshotPath!);
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
        diagnosticUiCapturePath=null;
        if(!diagnosticCapture)uiScreenshotCaptured = true;
        Console.WriteLine($"PHYXEL_UI_SCREENSHOT {width}x{height} {fullPath}");
        if (!diagnosticCapture && oilSmokeVerification is null) Exit();
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
            pendingSave = stateSerializer.SaveAsync(pendingSavePath ?? scenePath,
                pendingSaveSettings ?? settings, capturedMaterial, snapshot, materialRegistry);
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
                settings.TemperatureSensors.Clear();
                temperatureProbe.Reset(); temperatureSensors.Reset();
                pendingLoad = stateSerializer.LoadAsync(scenePath, materialRegistry);
                pendingSave = null;
                return;
            }
            if (pendingSave.IsCompletedSuccessfully)
            {
                if (pendingSavePath is not null)
                {
                    scenePath = pendingSavePath;
                    hasChosenScenePath = true;
                }
                SetStatus($"Сохранено: {scenePath}", 12);
            }
            else
            {
                Exception? error = pendingSave.Exception?.GetBaseException();
                Console.Error.WriteLine($"PHYXEL_SAVE_FAILED path={pendingSavePath ?? scenePath}\n{error}");
                SetStatus($"Ошибка сохранения: {error?.Message ?? "операция отменена"}", 20);
            }
            pendingSavePath = null;
            pendingSaveSettings = null;
            pendingSave = null;
        }
        if (pendingLoad is not { IsCompleted: true } || userInterface is null)
        {
            return;
        }
        if (pendingLoad.IsCompletedSuccessfully && pendingLoad.Result is { } loaded)
        {
            editHistory.Clear();
            brushController.CancelStroke();
            temperatureProbe.Reset(); temperatureSensors.Reset();
            SimulationStateSerializer.Apply(loaded.State, settings);
            userInterface.SelectedMaterial = loaded.State.SelectedMaterial;
            if (loaded.World is not null && resourceManager is not null && materialRegistry is not null)
            {
                settings.Width=loaded.World.Width;
                settings.Height=loaded.World.Height;
                bool containsMatter = SimulationStateSerializer.ContainsMatter(loaded.World);
                bool preserveOxidizer = loaded.World.Oxidizer is { Length: > 0 } || loaded.World.AirThermal is { Length: > 0 };
                currentResources = resourceManager.CreateOrResize(settings, containsMatter || preserveOxidizer || loaded.World.Filters is {Length:>0});
                stateSerializer.ApplyWorldSnapshot(currentResources, loaded.World);
                dispatchCoordinator?.RestoreWorldActivity(
                    currentResources,
                    containsMatter,
                    SimulationStateSerializer.ContainsContactTransitionSource(
                        loaded.World,
                        materialRegistry),
                    settings.HydraulicPressure,
                    preserveOxidizer);
                if (acceptance.IsPhaseRoundTripLoading)
                {
                    acceptance.MarkPhaseRoundTripLoaded(frameIndex);
                }
                SetStatus(loaded.Warnings.Count == 0
                    ? $"Загружено: {pendingLoadPath ?? scenePath}"
                    : $"Сцена загружена с предупреждениями: {loaded.Warnings[0]}", 8);
                if (pendingLoadPath is not null)
                {
                    scenePath = pendingLoadPath;
                    hasChosenScenePath = true;
                }
            }
            else SetStatus("Загружены только настройки; в файле нет мира", 8);
        }
        else
        {
            SetStatus(File.Exists(pendingLoadPath ?? scenePath) ? "Ошибка загрузки" : "Сохранённая сцена не найдена");
        }
        pendingLoadPath = null;
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
        ThermalDeviceAcceptance.CaptureLedger(currentResources, dispatchCoordinator.ThermalTicks);
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

    private void SetStatus(string message, float seconds = 3)
    {
        transientStatus = message;
        transientStatusRemaining = seconds;
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

    private Rectangle WorldCanvasBounds(Rectangle canvas,int width,int height) => acceptance.Active
        ? FitWorldToCanvas(canvas,width,height) : CanvasWorldExpansion.CoverBounds(canvas,width,height);

    private bool EnsureCanvasWorldFits(Rectangle canvas)
    {
        if (pendingSave is not null || pendingWorldCapture || pendingLoad is not null || pendingAcceptanceCheckpoint)
            return false;
        Point size=CanvasWorldExpansion.RequiredSize(settings.Width,settings.Height,canvas);
        if(size.X==settings.Width&&size.Y==settings.Height)return false;
        // Guard pathological repeated resizes; camera still covers the panel.
        if(size.X>4096||size.Y>4096|| (long)size.X*size.Y>8388608)return false;
        editHistory.Clear();
        brushController.CancelStroke();
        if(currentResources is not {IsSimulationAllocated:true} ||
            currentResources.Width!=settings.Width || currentResources.Height!=settings.Height)
        {
            for (int i = 0; i < settings.TemperatureSensors.Count; i++)
                settings.TemperatureSensors[i] = settings.TemperatureSensors[i] with { Y = settings.TemperatureSensors[i].Y + size.Y - settings.Height };
            settings.Width=size.X;settings.Height=size.Y;
            cameraController.Reset();return false;
        }
        canvasExpansionSize=size;
        canvasCaptureSerializer.BeginWorldCapture(currentResources);
        canvasExpansionPending=true;
        return true;
    }

    private void CompleteCanvasExpansion()
    {
        if(currentResources is null||resourceManager is null||dispatchCoordinator is null||materialRegistry is null)return;
        if(!canvasCaptureSerializer.TryCompleteWorldCapture(currentResources,out SimulationWorldSnapshot? captured)||captured is null)return;
        SimulationWorldSnapshot expanded=CanvasWorldExpansion.Expand(captured,canvasExpansionSize);
        for (int i = 0; i < settings.TemperatureSensors.Count; i++)
            settings.TemperatureSensors[i] = settings.TemperatureSensors[i] with { Y = settings.TemperatureSensors[i].Y + expanded.Height - captured.Height };
        settings.Width=expanded.Width;settings.Height=expanded.Height;
        bool matter=SimulationStateSerializer.ContainsMatter(expanded);
        bool fields=expanded.Oxidizer is {Length:>0}||expanded.AirThermal is {Length:>0};
        currentResources=resourceManager.CreateOrResize(settings,matter||fields||expanded.Filters is {Length:>0});
        stateSerializer.ApplyWorldSnapshot(currentResources,expanded);
        dispatchCoordinator.RestoreWorldActivity(currentResources,matter,
            SimulationStateSerializer.ContainsContactTransitionSource(expanded,materialRegistry),settings.HydraulicPressure,fields);
        canvasExpansionPending=false;
        cameraController.Reset();temperatureProbe.Reset(); temperatureSensors.Reset();
        inputSampler.ResetAfterDialog();ResetElapsedTime();
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
