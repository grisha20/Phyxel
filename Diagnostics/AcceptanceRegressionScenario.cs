using System;
using System.Collections.Generic;
using System.Globalization;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

public enum AcceptanceScenarioMode
{
    None,
    Bowl,
    SolidGravity,
    Sand,
    Hydro,
    HydraulicSurface,
    HydraulicBalance,
    WaterConvection,
    WaterConvectionPause,
    WaterConvectionHeated,
    Slope,
    Gas,
    GasBrushFps,
    Co2Layer,
    SteamCycle,
    SteamSurface,
    SteamEnergy,
    ThermalDevices,
    SteamApparatus,
    Oxidizer,
    CoalFire,
    SavedFurnace,
    AirWall,
    Co2Thermal,
    TransientHeat,
    GasCoFlow,
    SmokeRender,
    WaterStress,
    FlatSurface,
    WaterDrain,
    CommunicatingVessels,
    PressureTube,
    SavedPressure,
    SavedIsolation,
    SavedGravity,
    Buoyancy,
    SavedSandWater,
    ExternalGranular,
    ExternalLiquid,
    ExternalGas,
    ExternalSolids,
    UnderwaterGranularPile,
    GranularWaterDisplacement,
    GranularBarrier,
    GranularBarrierHydraulic,
    TemperatureBrush,
    TemperatureTool,
    ThermalUniform,
    ThermalContact,
    ThermalCapacity,
    ThermalConductivityCompare,
    ThermalFast,
    ThermalSlow,
    ThermalInsulator,
    ThermalVacuum,
    ThermalGas,
    TemperatureProbeGpu,
    PhaseDispatchSmoke,
    PhaseThresholds,
    PhaseHysteresis,
    PhaseSingleTransition,
    PhaseNormalizationMatrix,
    PhaseSummaryLiquidGas,
    PhaseSummarySolidLiquid,
    PhaseSummaryGasMovable,
    PhaseSummaryLiquidFixed,
    PhasePauseContinue,
    PhaseWakeGas,
    PhaseWakeLiquid,
    PhaseReadbackFallback,
    PhaseExternalReorder,
    PhaseDisabledRegistry,
    PhaseEnergyContract,
    PhaseV5RoundTrip,
    PhasePerformanceSteady,
    PhasePerformanceBurst,
    WaterIceSteam,
    WaterIceSteamMotion,
    WaterIceSteamPause,
    WaterIceSteamV5RoundTrip,
    CombustionChain,
    CombustionQuench,
    FireObstacle,
    FireOpen,
    Furnace,
    MetalChimney,
    SteamPuff,
    SteamJet,
    SteamObstacle,
    SteamSelfCooling,
    BrushEmptyOnly,
    ContinuousBrushStroke,
    CoalTypes,
    GasUniformDistribution,
    SteamDistributionAndCooling,
    SteamCloudTemperature
}

public static class AcceptanceRegressionScenario
{
    public const int FireBrushRadius = 5;
    public const int FireBrushWidth = FireBrushRadius * 2 + 1;
    public const int SteamPuffSourceX = 240;
    // Previously y=135. A radius-10 puff reached the world ceiling before
    // frame 600, making its late-shape measurements unusable. Keeping the
    // source just above the lower boundary leaves the same physics intact
    // while retaining vertical room for the 600-frame diagnostic.
    public const int SteamPuffSourceY = 250;
    // Previous radius 2 produced a 13-cell sparse probe. Radius 10 and 82%
    // density mirror one UI brush command. The observed 347 particles in the
    // game can span several mouse-hold frames; a single disk command yields
    // about 249 and is the intended diagnostic source.
    public const int SteamPuffBrushRadius = 10;
    public const float SteamPuffSpawnDensity = 0.82f;
    // The TPT steam-jet reference was drawn with eight linear wheel steps.
    // Unlike steam_puff, this continuously held source is intentionally a
    // dense radius-8 brush, one command every frame.
    public const int SteamJetBrushRadius = 8;
    private const int FireObstacleSourceX = 240;
    private const int DefaultFireObstaclePlateWidth = 201;

    private static AcceptanceMaterialIndices materials = null!;

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        AcceptanceScenarioMode mode,
        uint frame,
        MaterialRegistry? materialRegistry = null,
        uint scenarioSeed = 0)
    {
        if (materialRegistry is null)
        {
            throw new InvalidOperationException("Acceptance-сценарий требует реестр материалов.");
        }
        materials = new AcceptanceMaterialIndices(materialRegistry);
        return mode switch
        {
            AcceptanceScenarioMode.Bowl => CreateBowl(frame),
            AcceptanceScenarioMode.SolidGravity => CreateSolidGravity(frame),
            AcceptanceScenarioMode.Sand => CreateSand(frame),
            AcceptanceScenarioMode.Hydro => CreateHydro(frame),
            AcceptanceScenarioMode.Slope => CreateSlope(frame),
            AcceptanceScenarioMode.Gas => CreateGas(frame, scenarioSeed),
            AcceptanceScenarioMode.GasBrushFps => CreateGasBrushFps(frame),
            AcceptanceScenarioMode.Co2Layer => [],
            AcceptanceScenarioMode.Oxidizer => OxidizerAcceptance.Commands(frame),
            AcceptanceScenarioMode.SteamSurface => [],
            AcceptanceScenarioMode.SteamEnergy => [],
            AcceptanceScenarioMode.SteamApparatus => [],
            AcceptanceScenarioMode.ThermalDevices => ThermalDeviceAcceptance.Commands(frame, materialRegistry),
            AcceptanceScenarioMode.SteamCycle => GasCycleAcceptanceScenario.CreateCommands(),
            AcceptanceScenarioMode.WaterStress => CreateWaterStress(frame),
            AcceptanceScenarioMode.FlatSurface => CreateFlatSurface(frame),
            AcceptanceScenarioMode.WaterDrain => CreateWaterDrain(frame),
            AcceptanceScenarioMode.CommunicatingVessels => CreateCommunicatingVessels(frame),
            AcceptanceScenarioMode.PressureTube => CreatePressureTube(frame),
            AcceptanceScenarioMode.SavedPressure => [],
            AcceptanceScenarioMode.SavedIsolation => CreateSavedIsolation(frame),
            AcceptanceScenarioMode.SavedGravity => [],
            AcceptanceScenarioMode.Buoyancy => CreateBuoyancy(frame),
            AcceptanceScenarioMode.SavedSandWater => [],
            AcceptanceScenarioMode.ExternalGranular => CreateExternalGranular(frame),
            AcceptanceScenarioMode.ExternalLiquid => CreateExternalLiquid(frame),
            AcceptanceScenarioMode.ExternalGas => CreateExternalGas(frame),
            AcceptanceScenarioMode.ExternalSolids => CreateExternalSolids(frame),
            AcceptanceScenarioMode.UnderwaterGranularPile => CreateUnderwaterGranular(frame),
            AcceptanceScenarioMode.GranularWaterDisplacement => CreateUnderwaterGranular(frame),
            AcceptanceScenarioMode.GranularBarrier => CreateGranularBarrier(frame),
            AcceptanceScenarioMode.GranularBarrierHydraulic => CreateGranularBarrier(frame),
            AcceptanceScenarioMode.TemperatureBrush => CreateTemperatureBrush(frame),
            AcceptanceScenarioMode.TemperatureTool => CreateTemperatureTool(frame),
            AcceptanceScenarioMode.ThermalUniform or
            AcceptanceScenarioMode.ThermalContact or
            AcceptanceScenarioMode.ThermalCapacity or
            AcceptanceScenarioMode.ThermalConductivityCompare or
            AcceptanceScenarioMode.ThermalFast or
            AcceptanceScenarioMode.ThermalSlow or
            AcceptanceScenarioMode.ThermalInsulator or
            AcceptanceScenarioMode.ThermalVacuum or
            AcceptanceScenarioMode.ThermalGas or
            AcceptanceScenarioMode.TemperatureProbeGpu => [],
            AcceptanceScenarioMode.CombustionChain => CreateCombustionChain(frame),
            AcceptanceScenarioMode.CoalFire => CoalFireAcceptance.Commands(frame, materialRegistry),
            AcceptanceScenarioMode.SavedFurnace => [],
            AcceptanceScenarioMode.AirWall or AcceptanceScenarioMode.Co2Thermal or AcceptanceScenarioMode.TransientHeat or AcceptanceScenarioMode.GasCoFlow => GasFlowAcceptance.Commands(mode, frame, materialRegistry),
            AcceptanceScenarioMode.CombustionQuench => CreateCombustionQuench(frame),
            AcceptanceScenarioMode.FireObstacle => CreateFireObstacle(frame, scenarioSeed),
            AcceptanceScenarioMode.FireOpen => CreateFireOpen(frame, scenarioSeed),
            AcceptanceScenarioMode.Furnace => CreateFurnace(frame),
            AcceptanceScenarioMode.MetalChimney => CreateMetalChimney(frame, scenarioSeed),
            AcceptanceScenarioMode.SteamPuff => CreateSteamPuff(frame, scenarioSeed),
            AcceptanceScenarioMode.SteamJet => CreateSteamJet(frame, scenarioSeed),
            AcceptanceScenarioMode.SteamObstacle => CreateSteamObstacle(frame, scenarioSeed),
            AcceptanceScenarioMode.SteamSelfCooling => [],
            AcceptanceScenarioMode.SteamCloudTemperature =>
                SteamCloudTemperatureAcceptanceScenario.CreateCommands(frame, materialRegistry),
            AcceptanceScenarioMode.BrushEmptyOnly => BrushEmptyOnlyAcceptanceScenario.CreateCommands(frame, materials),
            AcceptanceScenarioMode.ContinuousBrushStroke =>
                ContinuousBrushStrokeAcceptanceScenario.CreateCommands(frame, materials),
            AcceptanceScenarioMode.CoalTypes => [],
            AcceptanceScenarioMode.PhaseDispatchSmoke => [],
            AcceptanceScenarioMode.PhaseThresholds or
            AcceptanceScenarioMode.PhaseHysteresis or
            AcceptanceScenarioMode.PhaseSingleTransition or
            AcceptanceScenarioMode.PhaseNormalizationMatrix or
            AcceptanceScenarioMode.PhaseSummaryLiquidGas or
            AcceptanceScenarioMode.PhaseSummarySolidLiquid or
            AcceptanceScenarioMode.PhaseSummaryGasMovable or
            AcceptanceScenarioMode.PhaseSummaryLiquidFixed or
            AcceptanceScenarioMode.PhasePauseContinue or
            AcceptanceScenarioMode.PhaseWakeGas or
            AcceptanceScenarioMode.PhaseWakeLiquid or
            AcceptanceScenarioMode.PhaseReadbackFallback or
            AcceptanceScenarioMode.PhaseExternalReorder or
            AcceptanceScenarioMode.PhaseDisabledRegistry or
            AcceptanceScenarioMode.PhaseEnergyContract or
            AcceptanceScenarioMode.PhaseV5RoundTrip or
            AcceptanceScenarioMode.PhasePerformanceSteady or
            AcceptanceScenarioMode.PhasePerformanceBurst or
            AcceptanceScenarioMode.WaterIceSteam or
            AcceptanceScenarioMode.WaterIceSteamMotion or
            AcceptanceScenarioMode.WaterIceSteamPause or
            AcceptanceScenarioMode.WaterIceSteamV5RoundTrip =>
                PhaseAcceptanceScenario.CreateCommands(mode, frame, materials),
            _ => []
        };
    }

    private static IReadOnlyList<BrushDrawCommand> CreateCombustionChain(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            commands.AddRange(AddFill(150, 90, 230, 150, 7, 5, materials.Wood, 18001));
            commands.AddRange(AddFill(250, 90, 330, 150, 7, 5, materials.Wood, 18002));
            return commands;
        }
        if (frame == 1)
        {
            // The acceptance chain starts with a deterministic thermal brush;
            // the combustion pass then creates generic flame/smoke products.
            // A separate manual fire-tool check covers discrete ignition.
            return [CreateTemperature(190, 120, 46, materials.Wood, 500)];
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateCombustionQuench(uint frame)
    {
        if (frame == 0)
        {
            return AddFill(180, 138, 240, 152, 7, 5, materials.Wood, 18101);
        }
        if (frame == 1)
        {
            return [CreateTemperature(210, 145, 38, materials.Wood, 500)];
        }
        if (frame is 100 or 110 or 120)
        {
            return AddFill(175, 75, 245, 105, 8, 5, materials.Water, 0);
        }
        return [];
    }

    private static int FireDiagnosticFrames => 6 * (int.TryParse(
        Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"), out int fps) ? fps : 60);

    private static IReadOnlyList<BrushDrawCommand> CreateFireObstacle(uint frame, uint scenarioSeed)
    {
        List<BrushDrawCommand> commands = [];
        if (frame == 0)
        {
            // The manual reproduction: a broad metal plate with a centred
            // flame source below it. The plate is deliberately ordinary metal,
            // not an air-blocking fixture, matching the user scene and TPT.
            (int plateLeft, int plateRight) = GetFireObstaclePlateBounds();
            AddLine(commands, plateLeft, 100, plateRight, 100, 5, 6, materials.Metal, 19101);
        }
        if (frame < FireDiagnosticFrames)
        {
            // Holding the brush is part of the experiment. A one-frame puff can
            // expire before reaching the plate and does not test a furnace.
            BrushDrawCommand flame = Create(FireObstacleSourceX, 170, FireBrushRadius, materials.Fire, 0, 0);
            flame.Density = 0.82f;
            flame.Seed ^= scenarioSeed;
            commands.Add(flame);
        }
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateFireOpen(uint frame, uint scenarioSeed)
    {
        if (frame >= FireDiagnosticFrames && Environment.GetEnvironmentVariable("PHYXEL_FIRE_PERFORMANCE") != "1")
        {
            return [];
        }

        // Intentionally identical source to fire_obstacle, without any plate
        // or other solid.  This isolates the source/air feedback loop.
        bool benchmark = Environment.GetEnvironmentVariable("PHYXEL_FIRE_PERFORMANCE") == "1";
        int radius = benchmark && int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_FIRE_PERFORMANCE_RADIUS"), out int requestedRadius)
            ? Math.Clamp(requestedRadius,1,60) : FireBrushRadius;
        BrushDrawCommand flame = Create(benchmark ? 960 : FireObstacleSourceX,
            benchmark ? 810 : 170, radius, materials.Fire, 0, 0);
        flame.Density = 0.82f;
        flame.Seed ^= scenarioSeed;
        return [flame];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSteamPuff(uint frame, uint scenarioSeed)
    {
        if (frame != 0)
        {
            return [];
        }

        BrushDrawCommand steam = Create(
            SteamPuffSourceX,
            SteamPuffSourceY,
            GetSteamPuffBrushRadius(),
            materials.Steam,
            0,
            0);
        steam.Density = GetSteamPuffSpawnDensity();
        steam.Seed ^= scenarioSeed;
        return [steam];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSteamJet(uint frame, uint scenarioSeed)
    {
        return CreateSteamJetSource(frame, scenarioSeed, 600);
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSteamObstacle(uint frame, uint scenarioSeed)
    {
        List<BrushDrawCommand> commands = [];
        if (frame == 0)
        {
            // TPT reference: 131×7 ordinary METL, its lower edge is 153 cells
            // above the 384-cell world's bottom (world y=231).
            const int plateLength = 131;
            const int plateBottomY = 231;
            int left = 306 - plateLength / 2;
            commands.AddRange(AddFill(left, plateBottomY - 6, left + plateLength - 1,
                plateBottomY, 7, 5, materials.Metal, 19701));
        }
        if (frame < 1200)
        {
            commands.AddRange(CreateSteamJetSource(frame, scenarioSeed, 1200, 306));
        }
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSteamJetSource(
        uint frame,
        uint scenarioSeed,
        uint duration,
        int? forcedSourceX = null)
    {
        if (frame >= duration)
        {
            return [];
        }
        if (UsesFixedSteamJetInflow())
        {
            return CreateFixedSteamJetInflow(frame, scenarioSeed, forcedSourceX);
        }

        // Match the measured TPT held-brush input: radius 8, full density,
        // one ordinary empty-only material brush per frame. This is source
        // geometry only; gas and air simulation are not changed here.
        BrushDrawCommand steam = Create(
            forcedSourceX ?? GetSteamJetSourceX(),
            GetSteamJetSourceY(),
            SteamJetBrushRadius,
            materials.Steam,
            0,
            0);
        steam.Density = 1;
        steam.Seed ^= scenarioSeed;
        return [steam];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateFixedSteamJetInflow(
        uint frame, uint scenarioSeed, int? forcedSourceX = null)
    {
        // A point brush remains empty-only, so a requested site can already
        // contain steam when the source is saturated. Seven attempts on two
        // frames of each five-frame cycle (5.4 sites/frame) produce the
        // required *measured* 4.2-cell
        // influx without writing over an occupied particle. Points are
        // selected without replacement from its radius-10 compatibility disk.
        // The ordinary radius-8 held brush remains the default source.
        int requestedCells = 5 + (frame % 5 is 3 or 4 ? 1 : 0);
        List<BrushDrawCommand> commands = new(requestedCells);
        int radius = GetFixedSteamJetInflowRadius();
        int diameter = radius * 2 + 1;
        uint state = scenarioSeed ^ (frame * 0x9e3779b9u) ^ 0x53a9f41du;
        int sourceX = forcedSourceX ?? GetSteamJetSourceX();
        int sourceY = GetSteamJetSourceY();
        int selected = 0;
        while (selected < requestedCells)
        {
            state = state * 1664525u + 1013904223u;
            int x;
            int y;
            if (UsesUniformAreaFixedSteamJetInflow())
            {
                float radialUnit = state / ((float)uint.MaxValue + 1.0f);
                state = state * 1664525u + 1013904223u;
                float angleUnit = state / ((float)uint.MaxValue + 1.0f);
                float sampleRadius = radius * MathF.Sqrt(radialUnit);
                float angle = MathF.Tau * angleUnit;
                x = (int)MathF.Round(sampleRadius * MathF.Cos(angle));
                y = (int)MathF.Round(sampleRadius * MathF.Sin(angle));
                if (x * x + y * y > radius * radius)
                {
                    continue;
                }
            }
            else
            {
                x = (int)(state % diameter) - radius;
                state = state * 1664525u + 1013904223u;
                y = (int)(state % diameter) - radius;
                if (x * x + y * y > radius * radius)
                {
                    continue;
                }
            }

            bool duplicate = false;
            foreach (BrushDrawCommand existing in commands)
            {
                if (existing.X == sourceX + x && existing.Y == sourceY + y)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
            {
                continue;
            }

            BrushDrawCommand steam = Create(
                sourceX + x,
                sourceY + y,
                0,
                materials.Steam,
                0,
                0);
            steam.Density = 1;
            steam.Seed = state;
            commands.Add(steam);
            selected++;
        }
        return commands;
    }

    private static int GetFixedSteamJetInflowRadius()
    {
        string? value = Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_FIXED_INFLOW_RADIUS");
        return int.TryParse(value, out int radius) && radius > 0 ? radius : SteamPuffBrushRadius;
    }

    private static bool UsesUniformAreaFixedSteamJetInflow() =>
        Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_FIXED_INFLOW_AREA_UNIFORM") == "1";

    private static bool UsesFixedSteamJetInflow() =>
        Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_FIXED_INFLOW") == "1";

    private static int GetSteamPuffBrushRadius()
    {
        string? value = Environment.GetEnvironmentVariable("PHYXEL_STEAM_PUFF_BRUSH_RADIUS");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int radius) &&
            radius is >= 1 and <= 100
            ? radius
            : SteamPuffBrushRadius;
    }

    private static float GetSteamPuffSpawnDensity()
    {
        string? value = Environment.GetEnvironmentVariable("PHYXEL_STEAM_PUFF_SPAWN_DENSITY");
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float density) &&
            density is > 0 and <= 1
            ? density
            : SteamPuffSpawnDensity;
    }

    public static int GetSteamJetSourceY()
    {
        string? value = Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_SOURCE_Y");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sourceY) &&
            // This is a diagnostic coordinate. A full-resolution acceptance
            // world is 1080 cells tall, so 1000 would silently reject a
            // legitimate near-bottom source and invalidate the no-clip run.
            sourceY is >= 1 and <= 4096
            ? sourceY
            : SteamPuffSourceY;
    }

    public static int GetSteamJetSourceX()
    {
        string? value = Environment.GetEnvironmentVariable("PHYXEL_STEAM_JET_SOURCE_X");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sourceX) &&
            sourceX is >= 1 and <= 1000
            ? sourceX
            : SteamPuffSourceX;
    }

    /// <summary>
    /// Lets the diagnostics reproduce TPT's 128-cell plate while retaining
    /// the 201-cell default acceptance scene. Endpoints are inclusive.
    /// </summary>
    public static (int Left, int Right) GetFireObstaclePlateBounds()
    {
        int width = DefaultFireObstaclePlateWidth;
        if (int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_FIRE_OBSTACLE_PLATE_WIDTH"), out int requestedWidth))
        {
            width = Math.Clamp(requestedWidth, 1, 400);
        }

        int left = FireObstacleSourceX - width / 2;
        return (left, left + width - 1);
    }

    private static IReadOnlyList<BrushDrawCommand> CreateFurnace(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            // A compact two-chamber furnace with a left chimney. Fixture is the
            // only material marked blocks-air, matching TPT's wall map rather
            // than making every ordinary solid an airtight wall.
            AddLine(commands, 90, 220, 380, 220, 5, 6, materials.Fixture, 19201);
            AddLine(commands, 90, 90, 90, 220, 5, 6, materials.Fixture, 19201);
            AddLine(commands, 380, 90, 380, 220, 5, 6, materials.Fixture, 19201);
            // Leave a wide opening over the left side of the firebox so the
            // plume can reach the chimney throat instead of sealing the roof
            // against the chimney wall.
            AddLine(commands, 220, 90, 380, 90, 5, 6, materials.Fixture, 19201);
            // Chimney walls and its throat.
            AddLine(commands, 110, 25, 110, 135, 5, 6, materials.Fixture, 19202);
            AddLine(commands, 180, 25, 180, 135, 5, 6, materials.Fixture, 19202);
            AddLine(commands, 90, 135, 110, 135, 5, 6, materials.Fixture, 19202);
            AddLine(commands, 180, 135, 205, 135, 5, 6, materials.Fixture, 19202);
            // Staggered baffles make the route through the chamber nontrivial.
            AddLine(commands, 250, 150, 250, 205, 5, 6, materials.Fixture, 19203);
            AddLine(commands, 290, 95, 290, 165, 5, 6, materials.Fixture, 19204);
            return commands;
        }
        if (frame < 600)
        {
            BrushDrawCommand flame = Create(320, 185, 7, materials.Fire, 0, 0);
            flame.Density = 0.82f;
            return [flame];
        }
        return [];
    }

    // An intentionally simple open-topped chimney: ordinary METL walls only.
    // Its 38-cell internal bore mirrors the user's TPT reference and does not
    // receive the blocks-air flag used by fixture.
    private static IReadOnlyList<BrushDrawCommand> CreateMetalChimney(uint frame, uint scenarioSeed)
    {
        List<BrushDrawCommand> commands = [];
        if (frame == 0)
        {
            AddLine(commands, 215, 245, 215, 35, 5, 5, materials.Metal, 19301);
            AddLine(commands, 264, 245, 264, 35, 5, 5, materials.Metal, 19301);
            AddLine(commands, 215, 245, 264, 245, 5, 5, materials.Metal, 19301);
        }
        if (frame < 600)
        {
            BrushDrawCommand smoke = Create(240, 222, 5, materials.Resolve(CoreMaterialIds.Smoke), 0, 0);
            smoke.Density = 0.82f;
            smoke.Seed ^= scenarioSeed;
            commands.Add(smoke);
        }
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateTemperatureBrush(uint frame)
    {
        if (frame == 0)
        {
            return
            [
                Create(100, 60, 10, materials.Sand, 0, 0),
                Create(200, 60, 10, materials.Resolve("acceptance:temperature_probe"), 0, 0),
                Create(300, 60, 10, materials.Sand, 0, 0)
            ];
        }
        return frame == 1
            ? [Create(300, 60, 14, materials.Eraser, 1, 0)]
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateTemperatureTool(uint frame)
    {
        if (frame == 1)
        {
            return
            [
                CreateTemperature(240, 135, 15, materials.Sand, 500),
                CreateTemperature(80, 60, 10, materials.Sand, 500)
            ];
        }
        if (frame == 2)
        {
            return [Create(240, 135, 4, materials.Eraser, (uint)BrushCommandMode.Erase, 0)];
        }
        return frame == 131
            ? [CreateTemperature(80, 60, 10, materials.Sand, 500)]
            : [];
    }

    private static BrushDrawCommand CreateTemperature(
        int x,
        int y,
        float radius,
        uint material,
        float temperature)
    {
        BrushDrawCommand command = Create(
            x,
            y,
            radius,
            material,
            (uint)BrushCommandMode.SetTemperature,
            0);
        command.TargetTemperature = temperature;
        return command;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateBowl(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 114, 115, 114, 225, 8, 5, materials.Metal, 1001);
            AddLine(commands, 325, 115, 325, 225, 8, 5, materials.Metal, 1001);
            AddLine(commands, 114, 225, 325, 225, 8, 5, materials.Metal, 1001);
            return commands;
        }
        if (frame == 2)
        {
            return AddFill(121, 170, 318, 218, 8, 5, materials.Water, 0);
        }
        return frame == 130
            ? AddFill(121, 130, 318, 158, 8, 5, materials.Sand, 0)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSolidGravity(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 20, 250, 460, 250, 7, 4, materials.Fixture, 2001);
            AddLine(commands, 215, 180, 215, 250, 7, 4, materials.Fixture, 2002);
            AddLine(commands, 415, 180, 415, 250, 7, 4, materials.Fixture, 2003);
            AddLine(commands, 140, 150, 140, 250, 7, 4, materials.Fixture, 2004);
            AddLine(commands, 35, 100, 35, 250, 7, 4, materials.Fixture, 2005);
            AddLine(commands, 130, 100, 130, 250, 7, 4, materials.Fixture, 2005);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(60, 25, 109, 74, 6, 4, materials.Metal, 2101);
        }
        if (frame == 2)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 220, 60, 410, 60, 7, 5, materials.Stone, 2201);
            return commands;
        }
        if (frame == 3)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 130, 100, 200, 100, 7, 5, materials.Metal, 2301);
            return commands;
        }
        if (frame == 4)
        {
            return AddFill(43, 229, 122, 240, 7, 4, materials.Sand, 0);
        }
        if (frame == 5)
        {
            return AddFill(43, 205, 122, 240, 7, 4, materials.Water, 0);
        }
        if (frame == 30)
        {
            return [Create(165, 100, 6, materials.Eraser, 1, 0)];
        }
        if (frame == 200)
        {
            return [Create(315, 172, 8, materials.Eraser, 1, 0)];
        }
        if (frame == 205)
        {
            return
            [
                Create(415, 183, 17, materials.Eraser, 1, 0),
                Create(415, 210, 11, materials.Eraser, 1, 0),
                Create(415, 232, 11, materials.Eraser, 1, 0),
                Create(415, 247, 8, materials.Eraser, 1, 0)
            ];
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSand(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 80, 245, 400, 245, 7, 4, materials.Fixture, 3001);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand sand = Create(240, 75, 25, materials.Sand, 0, 0);
        sand.Density = 0.51f;
        sand.Seed = 3002;
        return [sand];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateHydro(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 15, 95, 15, 250, 7, 4, materials.Metal, 4001);
            AddLine(commands, 260, 95, 260, 250, 7, 4, materials.Metal, 4001);
            AddLine(commands, 15, 250, 260, 250, 7, 4, materials.Metal, 4001);
            AddLine(commands, 138, 95, 138, 218, 7, 4, materials.Metal, 4001);
            return commands;
        }
        if (frame == 1)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 290, 150, 290, 250, 7, 4, materials.Metal, 4002);
            AddLine(commands, 465, 150, 465, 250, 7, 4, materials.Metal, 4002);
            AddLine(commands, 290, 250, 465, 250, 7, 4, materials.Metal, 4002);
            return commands;
        }
        if (frame == 2)
        {
            return AddFill(25, 155, 128, 240, 7, 5, materials.Water, 0);
        }
        if (frame is >= 3 and <= 20)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 378, 20, 378, 50, 6, 4, materials.Water, 0);
            return commands;
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSlope(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 5, 250, 475, 250, 7, 5, materials.Metal, 5001);
            AddLine(commands, 300, 225, 390, 70, 7, 5, materials.Metal, 5001);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand sand = Create(382, 42, 24, materials.Sand, 0, 0);
        sand.Density = 0.62f;
        sand.Seed = 5002;
        return [sand];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateGasBrushFps(uint frame)
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_GAS_BRUSH_PAUSED") == "1")
        {
            BrushDrawCommand paused = Create(200, 120, 8, materials.Steam, 0, 71001);
            paused.Density = 1;
            if (frame == 0) return [paused];
            if (frame == 30)
            {
                paused.Mode = BrushCommandMode.Erase;
                paused.Radius = 12;
                return [paused];
            }
            return [];
        }
        int fps = int.TryParse(Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"), out int value) ? value : 60;
        if (frame >= fps * 2) return [];
        BrushDrawCommand command = Create(240, 135, 8, materials.Gas, 0, 71001);
        command.Density = 1;
        return [command];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateGas(uint frame, uint scenarioSeed)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 50, 30, 430, 30, 7, 5, materials.Metal, 6001);
            AddLine(commands, 50, 30, 50, 250, 7, 5, materials.Metal, 6001);
            AddLine(commands, 430, 30, 430, 250, 7, 5, materials.Metal, 6001);
            AddLine(commands, 50, 250, 430, 250, 7, 5, materials.Metal, 6001);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand gas = Create(240, 215, 25, materials.Gas, 0, 0);
        gas.Density = 0.72f;
        gas.Seed = 6002 ^ scenarioSeed;
        return [gas];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterStress(uint frame)
    {
        List<BrushDrawCommand> commands = [];
        if (frame == 0)
        {
            AddLine(commands, 24, 1000, 1896, 1000, 16, 8, materials.Metal, 7001);
            AddLine(commands, 24, 96, 24, 1000, 16, 8, materials.Metal, 7001);
            AddLine(commands, 1896, 96, 1896, 1000, 16, 8, materials.Metal, 7001);
            return commands;
        }
        if (frame == 1)
        {
            AddLine(commands, 320, 900, 820, 520, 16, 8, materials.Metal, 7002);
            AddLine(commands, 1100, 520, 1600, 900, 16, 8, materials.Metal, 7002);
            return commands;
        }
        if (frame is >= 2 and <= 18)
        {
            int y = 160 + ((int)frame - 2) * 42;
            AddLine(commands, 100, y, 1820, y, 52, 34, materials.Water, 0);
            return commands;
        }
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalGranular(uint frame)
    {
        uint fixture = materials.Resolve("acceptance:fixture");
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 80, 245, 400, 245, 7, 4, fixture, 0);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand granular = Create(
            240, 75, 25, materials.Resolve("test:granular"), 0, 0);
        granular.Density = 0.51f;
        granular.Seed = 12001;
        return [granular];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalLiquid(uint frame)
    {
        uint fixture = materials.Resolve("acceptance:fixture");
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 40, 120, 40, 245, 7, 5, fixture, 0);
            AddLine(commands, 440, 120, 440, 245, 7, 5, fixture, 0);
            AddLine(commands, 40, 245, 440, 245, 7, 5, fixture, 0);
            return commands;
        }
        return frame == 1
            ? AddFill(70, 155, 210, 230, 8, 5, materials.Resolve("acceptance:liquid"), 0)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalGas(uint frame)
    {
        uint fixture = materials.Resolve("acceptance:fixture");
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 50, 30, 430, 30, 7, 5, fixture, 0);
            AddLine(commands, 50, 30, 50, 250, 7, 5, fixture, 0);
            AddLine(commands, 430, 30, 430, 250, 7, 5, fixture, 0);
            AddLine(commands, 50, 250, 430, 250, 7, 5, fixture, 0);
            return commands;
        }
        if (frame != 1)
        {
            return [];
        }
        BrushDrawCommand gas = Create(240, 215, 25, materials.Resolve("acceptance:gas"), 0, 0);
        gas.Density = 0.72f;
        gas.Seed = 12002;
        return [gas];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateExternalSolids(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(
                commands,
                20, 245, 460, 245, 7, 5, materials.Resolve("acceptance:fixture"), 0);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(
                80, 55, 145, 105, 6, 4, materials.Resolve("acceptance:solid_light"), 13001);
        }
        return frame == 2
            ? AddFill(
                300, 55, 365, 105, 6, 4, materials.Resolve("acceptance:solid_heavy"), 13002)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateUnderwaterGranular(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 30, 70, 30, 250, 7, 4, materials.Fixture, 14001);
            AddLine(commands, 450, 70, 450, 250, 7, 4, materials.Fixture, 14001);
            AddLine(commands, 30, 250, 450, 250, 7, 4, materials.Fixture, 14001);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(50, 125, 430, 230, 15, 11, materials.Water, 0);
        }
        if (frame != 2)
        {
            return [];
        }
        return [Create(240, 80, 20, materials.Resolve("test:granular"), 0, 0)];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateGranularBarrier(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 30, 70, 30, 250, 7, 4, materials.Fixture, 14101);
            AddLine(commands, 450, 70, 450, 250, 7, 4, materials.Fixture, 14101);
            AddLine(commands, 30, 250, 450, 250, 7, 4, materials.Fixture, 14101);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(210, 90, 270, 240, 7, 5, materials.Resolve("test:granular"), 0);
        }
        return frame == 2
            ? AddFill(50, 175, 175, 238, 7, 5, materials.Water, 0)
            : [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateFlatSurface(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 10, 255, 470, 255, 7, 5, materials.Metal, 7101);
            AddLine(commands, 10, 90, 10, 255, 7, 5, materials.Metal, 7101);
            AddLine(commands, 470, 90, 470, 255, 7, 5, materials.Metal, 7101);
            return commands;
        }
        if (frame == 1)
        {
            BrushDrawCommand sand = Create(355, 178, 30, materials.Sand, 0, 0);
            sand.Density = 0.72f;
            sand.Seed = 7102;
            return [sand];
        }
        if (frame == 2)
        {
            return AddFill(275, 165, 455, 245, 8, 5, materials.Water, 0);
        }
        if (frame is >= 3 and <= 300)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 240, 25, 240, 55, 6, 4, materials.Water, 0);
            return commands;
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateWaterDrain(uint frame)
    {
        List<BrushDrawCommand> commands = [];
        if (frame == 0)
        {
            AddLine(commands, 5, 255, 475, 255, 7, 5, materials.Metal, 8001);
            AddLine(commands, 5, 20, 5, 255, 7, 5, materials.Metal, 8001);
            AddLine(commands, 475, 20, 475, 255, 7, 5, materials.Metal, 8001);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(20, 205, 230, 245, 8, 5, materials.Water, 0);
        }
        if (frame == 2)
        {
            return AddFill(238, 205, 460, 245, 8, 5, materials.Water, 0);
        }
        if (frame is >= 30 and <= 75)
        {
            int offset = ((int)frame % 5 - 2) * 4;
            BrushDrawCommand sand = Create(240 + offset, 65, 18, materials.Sand, 0, 0);
            sand.Density = 0.62f;
            sand.Seed = 8002 + frame;
            return [sand];
        }
        return commands;
    }

    private static IReadOnlyList<BrushDrawCommand> CreateCommunicatingVessels(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 25, 35, 25, 252, 7, 5, materials.Stone, 9001);
            AddLine(commands, 455, 25, 455, 252, 7, 5, materials.Stone, 9001);
            AddLine(commands, 25, 252, 455, 252, 7, 5, materials.Stone, 9001);
            AddLine(commands, 105, 35, 105, 218, 7, 5, materials.Stone, 9001);
            AddLine(commands, 275, 85, 275, 232, 7, 5, materials.Stone, 9001);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(295, 40, 440, 190, 10, 6, materials.Water, 0);
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreatePressureTube(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 300, 40, 300, 192, 6, 4, materials.Stone, 9101);
            AddLine(commands, 300, 238, 300, 255, 6, 4, materials.Stone, 9101);
            AddLine(commands, 470, 40, 470, 255, 6, 4, materials.Stone, 9101);
            AddLine(commands, 300, 255, 470, 255, 6, 4, materials.Stone, 9101);
            AddLine(commands, 230, 70, 230, 210, 6, 4, materials.Stone, 9102);
            AddLine(commands, 260, 70, 260, 180, 6, 4, materials.Stone, 9102);
            AddLine(commands, 260, 180, 300, 200, 6, 4, materials.Stone, 9102);
            AddLine(commands, 230, 210, 300, 230, 6, 4, materials.Stone, 9102);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(315, 145, 455, 245, 8, 5, materials.Water, 0);
        }
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateSavedIsolation(uint frame)
    {
        // Saved-scene pressure regressions must preserve the captured mass.
        // Injecting water here can imitate a rising spiral even when pressure
        // routing is stalled, because this coordinate drains into the vessel.
        return [];
    }

    private static IReadOnlyList<BrushDrawCommand> CreateBuoyancy(uint frame)
    {
        if (frame == 0)
        {
            List<BrushDrawCommand> commands = [];
            AddLine(commands, 15, 255, 465, 255, 7, 5, materials.Fixture, 10001);
            AddLine(commands, 55, 75, 185, 75, 7, 5, materials.Metal, 10101);
            AddLine(commands, 55, 75, 55, 140, 7, 5, materials.Metal, 10101);
            AddLine(commands, 185, 75, 185, 140, 7, 5, materials.Metal, 10101);
            AddLine(commands, 55, 140, 185, 140, 7, 5, materials.Metal, 10101);
            AddLine(commands, 215, 75, 215, 140, 7, 5, materials.Metal, 10201);
            AddLine(commands, 330, 75, 330, 140, 7, 5, materials.Metal, 10201);
            AddLine(commands, 215, 140, 330, 140, 7, 5, materials.Metal, 10201);
            return commands;
        }
        if (frame == 1)
        {
            return AddFill(375, 90, 425, 140, 6, 4, materials.Metal, 10301);
        }
        if (frame == 2)
        {
            return AddFill(20, 175, 240, 247, 8, 5, materials.Water, 0);
        }
        if (frame == 3)
        {
            return AddFill(240, 175, 460, 247, 8, 5, materials.Water, 0);
        }
        if (frame == 4)
        {
            return AddFill(235, 92, 310, 128, 6, 4, materials.Sand, 0);
        }
        return [];
    }

    private static List<BrushDrawCommand> AddFill(
        int left,
        int top,
        int right,
        int bottom,
        int spacing,
        float radius,
        uint material,
        uint bodyId)
    {
        List<BrushDrawCommand> commands = [];
        for (int y = top; y <= bottom; y += spacing)
        {
            for (int x = left; x <= right; x += spacing)
            {
                commands.Add(Create(x, y, radius, material, 0, bodyId));
            }
        }
        return commands;
    }

    private static void AddLine(
        List<BrushDrawCommand> commands,
        int startX,
        int startY,
        int endX,
        int endY,
        int spacing,
        float radius,
        uint material,
        uint bodyId)
    {
        int length = Math.Max(Math.Abs(endX - startX), Math.Abs(endY - startY));
        int samples = Math.Max(1, length / spacing);
        for (int sample = 0; sample <= samples; sample++)
        {
            float amount = sample / (float)samples;
            commands.Add(Create(
                (int)MathF.Round(startX + (endX - startX) * amount),
                (int)MathF.Round(startY + (endY - startY) * amount),
                radius,
                material,
                0,
                bodyId));
        }
    }

    private static BrushDrawCommand Create(
        int x,
        int y,
        float radius,
        uint material,
        uint mode,
        uint bodyId)
    {
        return new BrushDrawCommand
        {
            X = x,
            Y = y,
            MaterialIndex = material,
            Radius = radius,
            Density = 1,
            Mode = (BrushCommandMode)mode,
            Seed = (uint)(x + y * 2048),
            Reserved = bodyId
        };
    }
}
