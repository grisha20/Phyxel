using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;

namespace Phyxel.Materials;

internal static partial class MaterialFileLoader
{
    private sealed class MaterialFileDocument
    {
        public int Schema { get; set; }
        public string? Id { get; set; }
        public JsonElement Name { get; set; }
        public string? Kind { get; set; }
        public string[] Flags { get; set; } = [];
        public string? Color { get; set; }
        public MaterialPhysicsDocument? Physics { get; set; }
        public MaterialGasDocument? Gas { get; set; }
        public MaterialMotionDocument? Motion { get; set; }
        public MaterialThermalDocument? Thermal { get; set; }
        public JsonElement Combustion { get; set; }
        public JsonElement Emissions { get; set; }
        public JsonElement Lifecycle { get; set; }
        public JsonElement ContactTransitions { get; set; }
        public JsonElement Moisture { get; set; }
        public JsonElement FuelAbsorption { get; set; }
        public JsonElement LiquidAbsorption { get; set; }
        public MaterialUiDocument? Ui { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    }

    private sealed class MaterialPhysicsDocument
    {
        public float Density { get; set; } = 1f;
        public float Friction { get; set; }
        public float FlowRate { get; set; }
        public float PressureStrength { get; set; }
        // Deprecated PM field: parsed/validated for external-file compatibility,
        // but direct pressure bursting has no deformation or damage accumulator.
        public float PressurePlasticity { get; set; }
        public JsonElement LiquidFlow { get; set; }
    }

    private sealed class MaterialThermalDocument
    {
        public float InitialTemperature { get; set; } = MaterialRegistry.DefaultInitialTemperature;
        public float Conductivity { get; set; } = MaterialRegistry.DefaultThermalConductivity;
        public float HeatCapacity { get; set; } = MaterialRegistry.DefaultHeatCapacity;
        public JsonElement Transitions { get; set; }
        public JsonElement AmbientCooling { get; set; }
        public JsonElement Regulator { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    }

    private sealed class MaterialUiDocument
    {
        public int Order { get; set; }
        public bool Hidden { get; set; }
        public string? Category { get; set; }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static IReadOnlyList<MaterialDefinition> LoadCore(
        string directory,
        int maximumCount)
    {
        if (!Directory.Exists(directory))
        {
            throw new InvalidDataException($"Core material directory '{directory}' does not exist.");
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Could not enumerate core material files in '{directory}'.", exception);
        }

        if (files.Length == 0)
        {
            throw new InvalidDataException($"Core material directory '{directory}' contains no JSON files.");
        }
        if (files.Length > maximumCount)
        {
            throw new InvalidDataException($"Core material count exceeds the limit of {maximumCount}.");
        }

        List<MaterialDefinition> loaded = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (string path in files)
        {
            try
            {
                MaterialDefinition definition = Parse(path) with
                {
                    IsBundled = true,
                    SourcePath = path
                };
                if (!definition.Id.StartsWith("core:", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Bundled material ID '{definition.Id}' must use the core namespace.");
                }
                if (!string.IsNullOrWhiteSpace(definition.Category) &&
                    !Phyxel.UI.MaterialCategoryResolver.TryResolveExplicitCategory(definition.Category, out _))
                {
                    throw new InvalidDataException(
                        $"ui.category '{definition.Category}' must be one of: powders, liquids, gases, solids, combustion, tools.");
                }
                if (!ids.Add(definition.Id))
                {
                    throw new InvalidDataException($"Duplicate bundled material ID '{definition.Id}'.");
                }

                loaded.Add(definition);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
            {
                throw new InvalidDataException($"Invalid core material file '{path}': {exception.Message}", exception);
            }
        }

        return loaded;
    }

    public static IReadOnlyList<MaterialDefinition> LoadExternal(
        string directory,
        IReadOnlySet<string> reservedIds,
        int maximumCount,
        string? excludedDirectory = null)
    {
        if (!Directory.Exists(directory) || maximumCount <= 0)
        {
            return [];
        }

        List<(string Path, MaterialDefinition Definition)> loaded = [];
        Dictionary<string, string> externalIds = new(StringComparer.Ordinal);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .Where(path => !IsInsideDirectory(path, excludedDirectory))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogError(directory, $"Не удалось перечислить файлы: {exception.Message}");
            return [];
        }

        foreach (string path in files)
        {
            if (loaded.Count >= maximumCount)
            {
                LogError(path, $"Превышен лимит {MaterialRegistry.MaximumMaterials} материалов.");
                continue;
            }

            try
            {
                MaterialDefinition definition = Parse(path) with
                {
                    IsBundled = false,
                    SourcePath = path
                };
                if (definition.Id.StartsWith("core:", StringComparison.Ordinal))
                {
                    LogError(path, $"External material cannot declare the reserved core ID '{definition.Id}'.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(definition.Category) &&
                    !Phyxel.UI.MaterialCategoryResolver.TryResolveExplicitCategory(definition.Category, out _))
                {
                    LogWarning(
                        path,
                        $"ui.category '{definition.Category}' is unknown; category was selected from kind/flags.");
                    definition = definition with { Category = null };
                }
                if (reservedIds.Contains(definition.Id))
                {
                    LogError(path, $"Внешний материал не может заменить встроенный ID '{definition.Id}'.");
                    continue;
                }
                if (externalIds.TryGetValue(definition.Id, out string? firstPath))
                {
                    LogError(path, $"Дублирующий ID '{definition.Id}', впервые объявлен в '{firstPath}'.");
                    continue;
                }

                externalIds.Add(definition.Id, path);
                loaded.Add((path, definition));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
            {
                LogError(path, exception.Message);
            }
        }

        return loaded
            .OrderBy(item => item.Definition.Id, StringComparer.Ordinal)
            .Select(item => item.Definition)
            .ToArray();
    }

    private static bool IsInsideDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static MaterialDefinition Parse(string path)
    {
        string json = File.ReadAllText(path);
        MaterialFileDocument document = JsonSerializer.Deserialize<MaterialFileDocument>(json, SerializerOptions) ??
            throw new InvalidDataException("Файл не содержит описания материала.");
        if (document.Schema != 1)
        {
            throw new InvalidDataException($"Неподдерживаемая schema={document.Schema}; ожидается schema=1.");
        }
        if (string.IsNullOrWhiteSpace(document.Id))
        {
            throw new InvalidDataException("Поле id обязательно.");
        }

        string id = MaterialRegistry.NormalizeId(document.Id);
        if (!MaterialIdPattern().IsMatch(id))
        {
            throw new InvalidDataException(
                $"ID '{document.Id}' должен иметь формат namespace:name и содержать только a-z, 0-9, '.', '_' или '-'.");
        }

        MaterialSimulationKind kind = ParseKind(document.Kind);
        Color color = ParseColor(document.Color ?? "#FFFFFF");
        MaterialPhysicsDocument physics = document.Physics ?? new MaterialPhysicsDocument();
        MaterialGasDocument? gas = document.Gas;
        MaterialMotionDocument motion = document.Motion ?? new MaterialMotionDocument();
        MaterialThermalDocument thermal = document.Thermal ?? new MaterialThermalDocument();
        if (!float.IsFinite(physics.Density) || physics.Density < 0 ||
            physics.Density > MaterialRegistry.MaximumDensity ||
            !float.IsFinite(physics.Friction) || physics.Friction < 0 ||
            !float.IsFinite(physics.FlowRate) || physics.FlowRate < 0 ||
            !float.IsFinite(physics.PressureStrength) || physics.PressureStrength < 0 || physics.PressureStrength > 256 ||
            !float.IsFinite(physics.PressurePlasticity) || physics.PressurePlasticity < 0 || physics.PressurePlasticity > 1 ||
            (physics.PressurePlasticity > 0 && physics.PressureStrength <= 0) ||
            (physics.PressureStrength > 0 && kind != MaterialSimulationKind.Solid))
        {
            throw new InvalidDataException(
                $"Параметры physics должны быть конечными неотрицательными числами; density не должна превышать {MaterialRegistry.MaximumDensity}.");
        }
        if (thermal.UnknownFields is { Count: > 0 })
        {
            throw new InvalidDataException(
                $"Неизвестное поле thermal '{thermal.UnknownFields.Keys.OrderBy(key => key, StringComparer.Ordinal).First()}'.");
        }
        if (gas is not null)
        {
            if (kind != MaterialSimulationKind.Gas)
            {
                throw new InvalidDataException("The gas block is allowed only for kind 'gas'.");
            }
            if (gas.UnknownFields is { Count: > 0 })
            {
                throw new InvalidDataException(
                    $"Unknown gas field '{gas.UnknownFields.Keys.OrderBy(key => key, StringComparer.Ordinal).First()}'.");
            }
            if (!float.IsFinite(gas.Diffusion) ||
                gas.Diffusion < MaterialRegistry.MinimumGasDiffusion ||
                gas.Diffusion > MaterialRegistry.MaximumGasDiffusion)
            {
                throw new InvalidDataException(
                    $"gas.diffusion must be finite and between " +
                    $"{MaterialRegistry.MinimumGasDiffusion} and {MaterialRegistry.MaximumGasDiffusion}.");
            }
            if (!float.IsFinite(gas.Buoyancy) ||
                gas.Buoyancy < MaterialRegistry.MinimumGasBuoyancy ||
                gas.Buoyancy > MaterialRegistry.MaximumGasBuoyancy)
            {
                throw new InvalidDataException(
                    $"gas.buoyancy must be finite and between " +
                    $"{MaterialRegistry.MinimumGasBuoyancy} and {MaterialRegistry.MaximumGasBuoyancy}.");
            }
            if (!float.IsFinite(gas.HotAir) ||
                gas.HotAir < MaterialRegistry.MinimumGasHotAir ||
                gas.HotAir > MaterialRegistry.MaximumGasHotAir)
            {
                throw new InvalidDataException(
                    $"gas.hotAir must be finite and between " +
                    $"{MaterialRegistry.MinimumGasHotAir} and {MaterialRegistry.MaximumGasHotAir}.");
            }
            if (!float.IsFinite(gas.OxidizerDisplacement) || gas.OxidizerDisplacement < 0 || gas.OxidizerDisplacement > 1)
                throw new InvalidDataException("gas.oxidizerDisplacement must be finite and in 0..1.");
            if (!float.IsFinite(gas.HazeStrength) ||
                gas.HazeStrength < MaterialRegistry.MinimumGasHazeStrength ||
                gas.HazeStrength > MaterialRegistry.MaximumGasHazeStrength)
            {
                throw new InvalidDataException(
                    $"gas.hazeStrength must be finite and between " +
                    $"{MaterialRegistry.MinimumGasHazeStrength} and {MaterialRegistry.MaximumGasHazeStrength}.");
            }
        }
        if (motion.UnknownFields is { Count: > 0 })
        {
            throw new InvalidDataException(
                $"Unknown motion field '{motion.UnknownFields.Keys.OrderBy(key => key, StringComparer.Ordinal).First()}'.");
        }
        if (!float.IsFinite(motion.Advection) || motion.Advection < 0 ||
            !float.IsFinite(motion.AirDrag) || motion.AirDrag < 0 ||
            !float.IsFinite(motion.AirLoss) || motion.AirLoss < 0 || motion.AirLoss > 1 ||
            !float.IsFinite(motion.Loss) || motion.Loss < 0 || motion.Loss > 1 ||
            !float.IsFinite(motion.Collision) || motion.Collision < -1 || motion.Collision > 1)
        {
            throw new InvalidDataException(
                "motion requires finite advection/airDrag >= 0, airLoss/loss in [0,1], and collision in [-1,1].");
        }
        if (!float.IsFinite(thermal.InitialTemperature) ||
            thermal.InitialTemperature < MaterialRegistry.MinimumInitialTemperature ||
            thermal.InitialTemperature > MaterialRegistry.MaximumInitialTemperature)
        {
            throw new InvalidDataException(
                $"thermal.initialTemperature должна быть конечным числом от {MaterialRegistry.MinimumInitialTemperature} до {MaterialRegistry.MaximumInitialTemperature}.");
        }
        if (!float.IsFinite(thermal.Conductivity) ||
            thermal.Conductivity < MaterialRegistry.MinimumThermalConductivity ||
            thermal.Conductivity > MaterialRegistry.MaximumThermalConductivity)
        {
            throw new InvalidDataException(
                $"thermal.conductivity должна быть конечным числом от {MaterialRegistry.MinimumThermalConductivity} до {MaterialRegistry.MaximumThermalConductivity}.");
        }
        if (!float.IsFinite(thermal.HeatCapacity) ||
            thermal.HeatCapacity < MaterialRegistry.MinimumHeatCapacity ||
            thermal.HeatCapacity > MaterialRegistry.MaximumHeatCapacity)
        {
            throw new InvalidDataException(
                $"thermal.heatCapacity должна быть конечным числом от {MaterialRegistry.MinimumHeatCapacity} до {MaterialRegistry.MaximumHeatCapacity}.");
        }

        MaterialTransitionDefinitions? transitions = ParseTransitions(
            thermal.Transitions,
            id,
            kind);
        (float ambientTemperature, float ambientCoolingRate) = ParseAmbientCooling(
            thermal.AmbientCooling);

        MaterialFlags flags = ParseFlags(document.Flags, kind);
        if ((flags & MaterialFlags.LiquidConvection) != 0 && kind != MaterialSimulationKind.Liquid)
            throw new InvalidDataException("liquid-convection requires a liquid.");
        MaterialCombustionDefinition? combustion = ParseCombustion(
            document.Combustion,
            id,
            kind);
        MaterialEmissionDefinition? emissions = ParseEmissions(document.Emissions, id, combustion);
        MaterialLifecycleDefinition? lifecycle = ParseLifecycle(document.Lifecycle, id, kind);
        MaterialThermalRegulatorDefinition? regulator = ParseRegulator(thermal.Regulator);
        if ((flags & MaterialFlags.ProgressiveIgnition) != 0 &&
            (kind != MaterialSimulationKind.Solid || (flags & MaterialFlags.MovableSolid) != 0 ||
             (flags & MaterialFlags.SelfOxidizing) == 0 || lifecycle is not null || transitions is not null ||
             combustion is null || combustion.FlameSpreadRate <= 0 || combustion.ContactIgnitionTemperature <= -273.15f))
            throw new InvalidDataException("progressive-ignition requires a fixed self-oxidizing solid with contact combustion/spreadRate, without lifecycle or phases.");

        if (regulator is not null)
        {
            if (kind != MaterialSimulationKind.Solid || (flags & MaterialFlags.MovableSolid) != 0 ||
                lifecycle is not null || transitions is not null || combustion is not null ||
                physics.Density <= 0 || (flags & (MaterialFlags.PhaseEnthalpy | MaterialFlags.FusionEnthalpy)) != 0)
                throw new InvalidDataException("thermal.regulator requires a fixed, positive-density solid without phases, combustion or lifecycle.");
            flags |= regulator.Heating ? MaterialFlags.ThermalHeater : MaterialFlags.ThermalCooler;
        }
        if ((flags & MaterialFlags.PhaseEnthalpy) != 0 &&
            (lifecycle is not null || kind is not (MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas)))
            throw new InvalidDataException("phase-enthalpy requires an infinite-lived liquid or gas.");
        if ((flags & MaterialFlags.FusionEnthalpy) != 0 &&
            (lifecycle is not null ||
             kind is not (MaterialSimulationKind.Solid or MaterialSimulationKind.Liquid)))
            throw new InvalidDataException("fusion-enthalpy requires an infinite-lived solid or liquid.");
        MaterialLiquidContactTransitionDefinition? liquidContactTransition =
            ParseContactTransitions(document.ContactTransitions, id, kind);
        if (regulator is not null && (liquidContactTransition is not null || emissions is not null))
            throw new InvalidDataException("thermal.regulator cannot emit or transform on liquid contact.");
        // Запрет на combustion + movable-solid снят вместе с разрешением
        // горения для granular: шейдер обрабатывает подвижную клетку так же,
        // как неподвижную, потому что проход горения выполняется отдельным
        // диспатчем уже после клеточного движения в кадре.
        // Explicit ignition thresholds allow the validated enthalpy fuel
        // pairs to share phases and combustion without storing a flame latch
        // in PhaseProgress. Other phase/combustion combinations stay invalid.
        bool phasedFuel = combustion is { ContactIgnitionTemperature: > -273.15f } &&
            (flags & (MaterialFlags.PhaseEnthalpy | MaterialFlags.FusionEnthalpy)) != 0 &&
            kind is MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas;
        if (combustion is not null && transitions is not null && !phasedFuel)
        {
            throw new InvalidDataException(
                $"Материал '{id}' не может одновременно иметь combustion и thermal.transitions в schema combustion v1.");
        }
        if (combustion is not null && kind == MaterialSimulationKind.Gas && !phasedFuel)
            throw new InvalidDataException("Gas fuel requires an enthalpy phase pair and explicit contactIgnitionTemperature.");
        if (combustion is not null && physics.Density <= 0)
        {
            throw new InvalidDataException(
                $"Combustible material '{id}' must have density greater than 0.");
        }
        if ((flags & MaterialFlags.MovableSolid) != 0 && physics.Density <= 0)
        {
            throw new InvalidDataException("Материал с flag 'movable-solid' должен иметь density больше 0.");
        }
        if ((flags & MaterialFlags.Flame) != 0 && kind != MaterialSimulationKind.Gas)
        {
            throw new InvalidDataException("Flag 'flame' is allowed only for kind 'gas'.");
        }
        if ((flags & MaterialFlags.Flame) != 0 && lifecycle is null)
        {
            throw new InvalidDataException("A material with flag 'flame' requires lifecycle.");
        }
        string name = ParseName(document.Name, id);
        // FIRE needs the gas gravity increment, but it has never participated
        // in the continuum diffusion exchange. Preserve that existing split
        // while allowing a flame material to declare its gravity.
        float gasDiffusion = kind == MaterialSimulationKind.Gas &&
            (flags & MaterialFlags.Flame) == 0
            ? gas?.Diffusion ?? MaterialRegistry.DefaultGasDiffusion
            : 0;
        float gasBuoyancy = kind == MaterialSimulationKind.Gas
            ? gas?.Buoyancy ?? 0
            : 0;
        float gasHotAir = kind == MaterialSimulationKind.Gas
            ? gas?.HotAir ?? MaterialRegistry.DefaultGasHotAir
            : MaterialRegistry.DefaultGasHotAir;
        float gasHazeStrength = kind == MaterialSimulationKind.Gas
            ? gas?.HazeStrength ?? MaterialRegistry.DefaultGasHazeStrength
            : MaterialRegistry.DefaultGasHazeStrength;
        if (physics.PressureStrength > 0 && (flags & MaterialFlags.MovableSolid) == 0)
            throw new InvalidDataException("pressureStrength requires movable-solid.");
        MaterialUiDocument ui = document.Ui ?? new MaterialUiDocument();
        return new MaterialDefinition(
            id,
            0,
            name,
            color,
            MaterialRegistry.CreateProperties(
                kind,
                flags,
                physics.Density,
                physics.Friction,
                physics.FlowRate,
                thermal.InitialTemperature,
                thermal.Conductivity,
                thermal.HeatCapacity,
                ambientTemperature,
                ambientCoolingRate,
                gasDiffusion,
                gasBuoyancy,
                gasHotAir,
                gasHazeStrength,
                new MaterialMotionDefinition(
                    motion.Advection,
                    motion.AirDrag,
                    motion.AirLoss,
                    motion.Loss,
                    motion.Collision),
                color, regulator, gas?.OxidizerDisplacement ?? 1),
            ui.Order,
            ui.Hidden,
            ui.Category)
        {
            PhaseTransitions = transitions,
            Combustion = combustion,
            Emissions = emissions,
            Lifecycle = lifecycle,
            ThermalRegulator = regulator,
            LiquidFlow = ParseLiquidFlow(physics.LiquidFlow, kind),
            LiquidContactTransition = liquidContactTransition,
            Moisture = ParseMoisture(document.Moisture, kind, lifecycle, transitions),
            FuelAbsorption = ParseLiquidAbsorption(document.LiquidAbsorption, document.FuelAbsorption, kind),
            Gas = gas is null ? null : new MaterialGasDefinition(
                gas.Diffusion,
                gas.Buoyancy,
                gas.HotAir,
                gas.HazeStrength, gas.OxidizerDisplacement),
            Motion = new MaterialMotionDefinition(
                motion.Advection,
                motion.AirDrag,
                motion.AirLoss,
                motion.Loss,
                motion.Collision),
            PressureStrength = physics.PressureStrength,
            SourcePath = path
        };
    }

    private static MaterialLiquidFlowDefinition? ParseLiquidFlow(JsonElement value, MaterialSimulationKind kind)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (kind != MaterialSimulationKind.Liquid || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("physics.liquidFlow requires a liquid and an object.");
        string[] names = ["referenceTemperature", "temperatureSensitivity", "minimumMobility", "maximumMobility"];
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name)) throw new InvalidDataException("Unknown liquidFlow property: " + property.Name);
        float Read(string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetSingle(out float v) && float.IsFinite(v)
            ? v : throw new InvalidDataException("liquidFlow requires finite " + name);
        var result = new MaterialLiquidFlowDefinition(Read(names[0]), Read(names[1]), Read(names[2]), Read(names[3]));
        if (result.ReferenceTemperature < -273.15 || result.ReferenceTemperature > 5000 ||
            result.TemperatureSensitivity <= 0 || result.TemperatureSensitivity > .1 ||
            result.MinimumMobility <= 0 || result.MinimumMobility > 1 ||
            result.MaximumMobility < 1 || result.MaximumMobility > 10)
            throw new InvalidDataException("liquidFlow: reference -273.15..5000, sensitivity (0,.1], minimum (0,1], maximum [1,10].");
        return result;
    }

    private static MaterialThermalRegulatorDefinition? ParseRegulator(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("thermal.regulator must be an object.");
        Dictionary<string, JsonElement> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value) ||
                property.Name.ToLowerInvariant() is not ("mode" or "targettemperature" or "maximumpower"))
                throw new InvalidDataException($"Unknown or duplicate thermal.regulator field '{property.Name}'.");
        if (!fields.TryGetValue("mode", out var mode) || mode.ValueKind != JsonValueKind.String ||
            mode.GetString() is not ("heating" or "cooling") ||
            !fields.TryGetValue("targetTemperature", out var target) ||
            target.ValueKind != JsonValueKind.Number || !target.TryGetSingle(out float temperature) ||
            !float.IsFinite(temperature) || temperature < MaterialRegistry.MinimumInitialTemperature ||
            temperature > MaterialRegistry.MaximumInitialTemperature ||
            !fields.TryGetValue("maximumPower", out var power) || power.ValueKind != JsonValueKind.Number ||
            !power.TryGetSingle(out float maximumPower) || !float.IsFinite(maximumPower) ||
            maximumPower < 0 || maximumPower > ThermalRegulator.MaximumPower)
            throw new InvalidDataException("thermal.regulator requires mode heating/cooling, a valid targetTemperature and maximumPower in 0..3600.");
        return new(mode.GetString() == "heating", temperature, maximumPower);
    }

    private sealed class MaterialGasDocument
    {
        public float Diffusion { get; set; } = MaterialRegistry.DefaultGasDiffusion;
        public float Buoyancy { get; set; }
        public float HotAir { get; set; } = MaterialRegistry.DefaultGasHotAir;
        public float HazeStrength { get; set; } = MaterialRegistry.DefaultGasHazeStrength;
        public float OxidizerDisplacement { get; set; } = 1;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    }

    private sealed class MaterialMotionDocument
    {
        public float Advection { get; set; } = MaterialRegistry.DefaultMotionAdvection;
        public float AirDrag { get; set; } = MaterialRegistry.DefaultMotionAirDrag;
        public float AirLoss { get; set; } = MaterialRegistry.DefaultMotionAirLoss;
        public float Loss { get; set; } = MaterialRegistry.DefaultMotionLoss;
        public float Collision { get; set; } = MaterialRegistry.DefaultMotionCollision;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    }

    private static MaterialFuelAbsorptionDefinition? ParseLiquidAbsorption(JsonElement current, JsonElement legacy, MaterialSimulationKind kind)
    {
        bool present=current.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        if(present && legacy.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new InvalidDataException("Specify liquidAbsorption or legacy fuelAbsorption, not both.");
        return ParseFuelAbsorption(present?current:legacy,kind);
    }

    private static MaterialFuelAbsorptionDefinition? ParseFuelAbsorption(JsonElement json, MaterialSimulationKind kind)
    {
        if (json.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (json.ValueKind != JsonValueKind.Object || kind is not (MaterialSimulationKind.Granular or MaterialSimulationKind.Solid))
            throw new InvalidDataException("fuelAbsorption requires granular or solid material with shared pores.");
        foreach (var p in json.EnumerateObject())
            if (p.Name is not ("liquid" or "capacity" or "absorptionRate" or "saturatedDensity" or "allLiquids"))
                throw new InvalidDataException("Unknown fuelAbsorption field: " + p.Name);
        if (!json.TryGetProperty("liquid", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString())) throw new InvalidDataException("Missing absorbed fuel liquid.");
        float Number(string key)
        {
            if (!json.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float f) ||
                !float.IsFinite(f) || f <= 0 || f > 10) throw new InvalidDataException("Invalid fuelAbsorption parameter: " + key);
            return f;
        }
        bool all=false;
        if(json.TryGetProperty("allLiquids",out var allValue))
        { if(allValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("allLiquids must be boolean."); all=allValue.GetBoolean(); }
        return new(MaterialRegistry.NormalizeId(id.GetString()!), Number("capacity"), Number("absorptionRate"), Number("saturatedDensity"),all);
    }

    private static MaterialMoistureDefinition? ParseMoisture(JsonElement json,
        MaterialSimulationKind kind, MaterialLifecycleDefinition? lifecycle, MaterialTransitionDefinitions? phases)
    {
        if (json.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (json.ValueKind != JsonValueKind.Object || kind is not (MaterialSimulationKind.Granular or MaterialSimulationKind.Solid) ||
            lifecycle is not null || phases is not null)
            throw new InvalidDataException("moisture requires an infinite-lived granular or solid material without phase transitions.");
        foreach (var p in json.EnumerateObject())
            if (p.Name is not ("liquid" or "dry" or "wet" or "capacity" or "absorptionRate" or "dryingRate" or "capillaryRate"))
                throw new InvalidDataException("Unknown moisture field: " + p.Name);
        string Id(string key)
        {
            if (!json.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException("Missing moisture ID: " + key);
            return MaterialRegistry.NormalizeId(value.GetString()!);
        }
        float Number(string key)
        {
            if (!json.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float f) ||
                !float.IsFinite(f) || f <= 0 || f > 10) throw new InvalidDataException("Invalid moisture parameter: " + key);
            return f;
        }
        float capillaryRate = 0;
        if (json.TryGetProperty("capillaryRate", out var capillary) &&
            (capillary.ValueKind != JsonValueKind.Number || !capillary.TryGetSingle(out capillaryRate) ||
             !float.IsFinite(capillaryRate) || capillaryRate < 0 || capillaryRate > 10 ||
             (capillaryRate > 0 && kind != MaterialSimulationKind.Solid)))
            throw new InvalidDataException("capillaryRate must be 0..10 and positive only for porous solids.");
        return new(Id("liquid"), Id("dry"), Id("wet"), Number("capacity"), Number("absorptionRate"), Number("dryingRate"), capillaryRate);
    }

    private static MaterialLiquidContactTransitionDefinition? ParseContactTransitions(
        JsonElement value,
        string sourceId,
        MaterialSimulationKind sourceKind)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("contactTransitions must be an object.");
        }
        if (sourceKind != MaterialSimulationKind.Granular)
        {
            throw new InvalidDataException(
                $"Material '{sourceId}' with contactTransitions must have kind 'granular'.");
        }

        JsonElement liquidElement = default;
        HashSet<string> rootFields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!rootFields.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Duplicate contactTransitions field '{property.Name}'.");
            }
            if (!property.Name.Equals("liquid", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Unknown contactTransitions field '{property.Name}'.");
            }
            liquidElement = property.Value;
        }
        if (liquidElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("contactTransitions.liquid must be an object.");
        }

        JsonElement intoElement = default;
        JsonElement rateElement = default;
        JsonElement withElement = default;
        HashSet<string> liquidFields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in liquidElement.EnumerateObject())
        {
            if (!liquidFields.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Duplicate contactTransitions.liquid field '{property.Name}'.");
            }
            if (property.Name.Equals("into", StringComparison.OrdinalIgnoreCase))
            {
                intoElement = property.Value;
            }
            else if (property.Name.Equals("ratePerSecond", StringComparison.OrdinalIgnoreCase))
            {
                rateElement = property.Value;
            }
            else if (property.Name.Equals("with", StringComparison.OrdinalIgnoreCase))
            {
                withElement = property.Value;
            }
            else
            {
                throw new InvalidDataException(
                    $"Unknown contactTransitions.liquid field '{property.Name}'.");
            }
        }

        if (intoElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(intoElement.GetString()))
        {
            throw new InvalidDataException("contactTransitions.liquid.into is required.");
        }
        string intoId = MaterialRegistry.NormalizeId(intoElement.GetString()!);
        if (!MaterialIdPattern().IsMatch(intoId))
        {
            throw new InvalidDataException(
                $"contactTransitions.liquid.into '{intoElement.GetString()}' is not a valid material ID.");
        }
        if (rateElement.ValueKind != JsonValueKind.Number ||
            !rateElement.TryGetSingle(out float rate) ||
            !float.IsFinite(rate) || rate <= 0 ||
            rate > MaterialRegistry.MaximumContactTransitionRate)
        {
            throw new InvalidDataException(
                $"contactTransitions.liquid.ratePerSecond must be finite, greater than 0, and at most " +
                $"{MaterialRegistry.MaximumContactTransitionRate}.");
        }
        string? withId = null;
        if (withElement.ValueKind != JsonValueKind.Undefined)
        {
            if (withElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(withElement.GetString()))
                throw new InvalidDataException("contactTransitions.liquid.with must be a material ID.");
            withId = MaterialRegistry.NormalizeId(withElement.GetString()!);
            if (!MaterialIdPattern().IsMatch(withId))
                throw new InvalidDataException($"contactTransitions.liquid.with '{withId}' is not a valid material ID.");
        }
        return new MaterialLiquidContactTransitionDefinition(intoId, rate, withId);
    }

    private static (float Temperature, float Rate) ParseAmbientCooling(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return (0, 0);
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("thermal.ambientCooling должен быть объектом.");
        }

        JsonElement temperatureElement = default;
        JsonElement rateElement = default;
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Дублирующее поле thermal.ambientCooling '{property.Name}'.");
            }
            if (property.Name.Equals("temperature", StringComparison.OrdinalIgnoreCase))
            {
                temperatureElement = property.Value;
            }
            else if (property.Name.Equals("rate", StringComparison.OrdinalIgnoreCase))
            {
                rateElement = property.Value;
            }
            else
            {
                throw new InvalidDataException(
                    $"Неизвестное поле thermal.ambientCooling '{property.Name}'.");
            }
        }

        if (temperatureElement.ValueKind != JsonValueKind.Number ||
            !temperatureElement.TryGetSingle(out float temperature) ||
            !float.IsFinite(temperature) ||
            temperature < MaterialRegistry.MinimumInitialTemperature ||
            temperature > MaterialRegistry.MaximumInitialTemperature)
        {
            throw new InvalidDataException(
                $"thermal.ambientCooling.temperature должна быть конечным числом от " +
                $"{MaterialRegistry.MinimumInitialTemperature} до {MaterialRegistry.MaximumInitialTemperature}.");
        }
        if (rateElement.ValueKind != JsonValueKind.Number ||
            !rateElement.TryGetSingle(out float rate) ||
            !float.IsFinite(rate) || rate <= 0 || rate > MaterialRegistry.MaximumAmbientCoolingRate)
        {
            throw new InvalidDataException(
                $"thermal.ambientCooling.rate должна быть конечным числом больше 0 и не больше " +
                $"{MaterialRegistry.MaximumAmbientCoolingRate}.");
        }
        return (temperature, rate);
    }

    private static MaterialCombustionDefinition? ParseCombustion(
        JsonElement value,
        string sourceId,
        MaterialSimulationKind sourceKind)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("combustion должен быть объектом.");
        }
        // Liquid fuel uses a gas-face gate. Gas fuel is accepted only with
        // a validated enthalpy pair and an explicit contact threshold below.
        if (sourceKind != MaterialSimulationKind.Solid &&
            sourceKind != MaterialSimulationKind.Granular &&
            sourceKind != MaterialSimulationKind.Liquid && sourceKind != MaterialSimulationKind.Gas)
        {
            throw new InvalidDataException(
                $"Материал '{sourceId}' с kind '{sourceKind.ToString().ToLowerInvariant()}' " +
                "не может быть source combustion; требуются kind 'solid', 'granular' или 'liquid'.");
        }

        JsonElement ignitionElement = default;
        JsonElement burnRateElement = default;
        JsonElement heatPerMassElement = default;
        JsonElement maximumTemperatureElement = default;
        JsonElement burnedIntoElement = default;
        JsonElement spreadRateElement = default;
        JsonElement pressureElement = default;
        JsonElement flameLifetimeElement = default;
        JsonElement oxidizerPerMassElement = default;
        JsonElement contactIgnitionElement = default;
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Дублирующее поле combustion '{property.Name}'.");
            }

            if (property.Name.Equals("contactIgnitionTemperature", StringComparison.OrdinalIgnoreCase))
            {
                contactIgnitionElement = property.Value;
            }
            else if (property.Name.Equals("ignitionTemperature", StringComparison.OrdinalIgnoreCase))
            {
                ignitionElement = property.Value;
            }
            else if (property.Name.Equals("burnRate", StringComparison.OrdinalIgnoreCase))
            {
                burnRateElement = property.Value;
            }
            else if (property.Name.Equals("heatPerMass", StringComparison.OrdinalIgnoreCase))
            {
                heatPerMassElement = property.Value;
            }
            else if (property.Name.Equals("burnedInto", StringComparison.OrdinalIgnoreCase))
            {
                burnedIntoElement = property.Value;
            }
            else if (property.Name.Equals("maximumTemperature", StringComparison.OrdinalIgnoreCase))
            {
                maximumTemperatureElement = property.Value;
            }
            else if (property.Name.Equals("spreadRate", StringComparison.OrdinalIgnoreCase))
            {
                spreadRateElement = property.Value;
            }
            else if (property.Name.Equals("pressurePerMass", StringComparison.OrdinalIgnoreCase))
            {
                pressureElement = property.Value;
            }
            else if (property.Name.Equals("flameLifetimeMultiplier", StringComparison.OrdinalIgnoreCase))
            {
                flameLifetimeElement = property.Value;
            }
            else if (property.Name.Equals("oxidizerPerMass", StringComparison.OrdinalIgnoreCase))
            {
                oxidizerPerMassElement = property.Value;
            }
            else
            {
                throw new InvalidDataException(
                    $"Неизвестное поле combustion '{property.Name}'.");
            }
        }

        if (ignitionElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Поле combustion.ignitionTemperature обязательно.");
        }
        if (burnRateElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Поле combustion.burnRate обязательно.");
        }
        if (heatPerMassElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Поле combustion.heatPerMass обязательно.");
        }
        if (burnedIntoElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Поле combustion.burnedInto обязательно.");
        }
        if (maximumTemperatureElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Поле combustion.maximumTemperature обязательно.");
        }

        if (ignitionElement.ValueKind != JsonValueKind.Number ||
            !ignitionElement.TryGetSingle(out float ignitionTemperature) ||
            !float.IsFinite(ignitionTemperature) ||
            ignitionTemperature < MaterialRegistry.MinimumInitialTemperature ||
            ignitionTemperature >= MaterialRegistry.MaximumInitialTemperature)
        {
            throw new InvalidDataException(
                $"combustion.ignitionTemperature должна быть конечным числом от {MaterialRegistry.MinimumInitialTemperature} включительно до {MaterialRegistry.MaximumInitialTemperature} исключительно.");
        }
        if (burnRateElement.ValueKind != JsonValueKind.Number ||
            !burnRateElement.TryGetSingle(out float burnRate) ||
            !float.IsFinite(burnRate) ||
            burnRate <= 0 ||
            burnRate > MaterialRegistry.MaximumCombustionBurnRate)
        {
            throw new InvalidDataException(
                $"combustion.burnRate должна быть конечным числом больше 0 и не больше {MaterialRegistry.MaximumCombustionBurnRate}.");
        }
        if (heatPerMassElement.ValueKind != JsonValueKind.Number ||
            !heatPerMassElement.TryGetSingle(out float heatPerMass) ||
            !float.IsFinite(heatPerMass) ||
            heatPerMass <= 0 ||
            heatPerMass > MaterialRegistry.MaximumCombustionHeatPerMass)
        {
            throw new InvalidDataException(
                $"combustion.heatPerMass должна быть конечным числом больше 0 и не больше {MaterialRegistry.MaximumCombustionHeatPerMass}.");
        }
        if (burnedIntoElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(burnedIntoElement.GetString()))
        {
            throw new InvalidDataException("combustion.burnedInto должен быть строковым material ID.");
        }

        string burnedIntoId = MaterialRegistry.NormalizeId(burnedIntoElement.GetString()!);
        if (!MaterialIdPattern().IsMatch(burnedIntoId))
        {
            throw new InvalidDataException(
                $"combustion.burnedInto '{burnedIntoElement.GetString()}' должен иметь формат namespace:name.");
        }
        if (burnedIntoId == sourceId)
        {
            throw new InvalidDataException(
                $"Материал '{sourceId}' не может сгорать сам в себя (combustion.burnedInto).");
        }

        if (maximumTemperatureElement.ValueKind != JsonValueKind.Number ||
            !maximumTemperatureElement.TryGetSingle(out float maximumTemperature) ||
            !float.IsFinite(maximumTemperature) ||
            maximumTemperature <= ignitionTemperature ||
            maximumTemperature > MaterialRegistry.MaximumInitialTemperature)
        {
            throw new InvalidDataException(
                $"combustion.maximumTemperature должна быть больше ignitionTemperature и не больше {MaterialRegistry.MaximumInitialTemperature}.");
        }

        float spreadRate = 0;
        if (spreadRateElement.ValueKind != JsonValueKind.Undefined &&
            (spreadRateElement.ValueKind != JsonValueKind.Number ||
             !spreadRateElement.TryGetSingle(out spreadRate) ||
             !float.IsFinite(spreadRate) || spreadRate < 0 ||
             spreadRate > MaterialRegistry.MaximumFlameSpreadRate))
        {
            throw new InvalidDataException(
                $"combustion.spreadRate must be a finite number from 0 to {MaterialRegistry.MaximumFlameSpreadRate}.");
        }

        float pressurePerMass = 0;
        if (pressureElement.ValueKind != JsonValueKind.Undefined &&
            (pressureElement.ValueKind != JsonValueKind.Number || !pressureElement.TryGetSingle(out pressurePerMass) ||
             !float.IsFinite(pressurePerMass) || pressurePerMass < 0 || pressurePerMass > 16))
            throw new InvalidDataException("combustion.pressurePerMass must be finite, from 0 to 16 (gameplay units).");
        float flameLifetimeMultiplier = 1;
        if (flameLifetimeElement.ValueKind != JsonValueKind.Undefined &&
            (flameLifetimeElement.ValueKind != JsonValueKind.Number || !flameLifetimeElement.TryGetSingle(out flameLifetimeMultiplier) ||
             !float.IsFinite(flameLifetimeMultiplier) || flameLifetimeMultiplier < .1 || flameLifetimeMultiplier > 4))
            throw new InvalidDataException("combustion.flameLifetimeMultiplier must be finite, from 0.1 to 4.");
        float oxidizerPerMass = 20;
        if (oxidizerPerMassElement.ValueKind != JsonValueKind.Undefined &&
            (oxidizerPerMassElement.ValueKind != JsonValueKind.Number ||
             !oxidizerPerMassElement.TryGetSingle(out oxidizerPerMass) ||
             !float.IsFinite(oxidizerPerMass) || oxidizerPerMass <= 0 || oxidizerPerMass > 100))
            throw new InvalidDataException("combustion.oxidizerPerMass must be finite, greater than 0 and at most 100 (gameplay units).");
        float contactIgnitionTemperature = -273.15f;
        if (contactIgnitionElement.ValueKind != JsonValueKind.Undefined &&
            (contactIgnitionElement.ValueKind != JsonValueKind.Number ||
             !contactIgnitionElement.TryGetSingle(out contactIgnitionTemperature) ||
             !float.IsFinite(contactIgnitionTemperature) || contactIgnitionTemperature <= -273.15f ||
             contactIgnitionTemperature > ignitionTemperature))
            throw new InvalidDataException("combustion.contactIgnitionTemperature must be finite, above -273.15 and no higher than ignitionTemperature.");
        return new MaterialCombustionDefinition(
            ignitionTemperature,
            burnRate,
            heatPerMass,
            burnedIntoId,
            spreadRate,
            maximumTemperature,
            pressurePerMass,
            flameLifetimeMultiplier,
            oxidizerPerMass,
            contactIgnitionTemperature);
    }

    private static MaterialEmissionDefinition? ParseEmissions(
        JsonElement value,
        string sourceId,
        MaterialCombustionDefinition? combustion)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (combustion is null)
        {
            throw new InvalidDataException("emissions требует combustion.");
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("emissions должна быть объектом.");
        }

        JsonElement smokeInto = default;
        JsonElement smokeRate = default;
        JsonElement gasInto = default;
        JsonElement gasRate = default;
        JsonElement flameInto = default;
        JsonElement flameRate = default;
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                throw new InvalidDataException($"Дублирующееся поле emissions '{property.Name}'.");
            }
            if (property.Name.Equals("smokeInto", StringComparison.OrdinalIgnoreCase)) smokeInto = property.Value;
            else if (property.Name.Equals("smokeRate", StringComparison.OrdinalIgnoreCase)) smokeRate = property.Value;
            else if (property.Name.Equals("gasInto", StringComparison.OrdinalIgnoreCase)) gasInto = property.Value;
            else if (property.Name.Equals("gasRate", StringComparison.OrdinalIgnoreCase)) gasRate = property.Value;
            else if (property.Name.Equals("flameInto", StringComparison.OrdinalIgnoreCase)) flameInto = property.Value;
            else if (property.Name.Equals("flameRate", StringComparison.OrdinalIgnoreCase)) flameRate = property.Value;
            else throw new InvalidDataException($"Неизвестное поле emissions '{property.Name}'.");
        }

        string smokeId = ParseEmissionTarget(smokeInto, "smokeInto", sourceId);
        string gasId = ParseEmissionTarget(gasInto, "gasInto", sourceId);
        float smokeValue = ParseEmissionRate(smokeRate, "smokeRate");
        float gasValue = ParseEmissionRate(gasRate, "gasRate");
        bool hasFlameTarget = flameInto.ValueKind != JsonValueKind.Undefined;
        bool hasFlameRate = flameRate.ValueKind != JsonValueKind.Undefined;
        if (hasFlameTarget != hasFlameRate)
        {
            throw new InvalidDataException(
                "emissions.flameInto and emissions.flameRate must be specified together.");
        }
        string? flameId = hasFlameTarget
            ? ParseEmissionTarget(flameInto, "flameInto", sourceId)
            : null;
        float flameValue = hasFlameRate ? ParseEmissionRate(flameRate, "flameRate") : 0;
        return new MaterialEmissionDefinition(
            smokeId,
            smokeValue,
            gasId,
            gasValue,
            flameId,
            flameValue);
    }

    private static MaterialLifecycleDefinition? ParseLifecycle(
        JsonElement value,
        string sourceId,
        MaterialSimulationKind sourceKind)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("lifecycle must be an object.");
        }
        if (sourceKind != MaterialSimulationKind.Gas)
        {
            throw new InvalidDataException($"Material '{sourceId}' lifecycle currently requires kind 'gas'.");
        }

        JsonElement minimumElement = default;
        JsonElement maximumElement = default;
        JsonElement decayIntoElement = default;
        JsonElement extinctionElement = default;
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                throw new InvalidDataException($"Duplicate lifecycle field '{property.Name}'.");
            }
            if (property.Name.Equals("minimum", StringComparison.OrdinalIgnoreCase)) minimumElement = property.Value;
            else if (property.Name.Equals("maximum", StringComparison.OrdinalIgnoreCase)) maximumElement = property.Value;
            else if (property.Name.Equals("decayInto", StringComparison.OrdinalIgnoreCase)) decayIntoElement = property.Value;
            else if (property.Name.Equals("extinctionTemperature", StringComparison.OrdinalIgnoreCase)) extinctionElement = property.Value;
            else throw new InvalidDataException($"Unknown lifecycle field '{property.Name}'.");
        }

        float minimum = ParseLifetime(minimumElement, "minimum");
        float maximum = ParseLifetime(maximumElement, "maximum");
        if (minimum > maximum)
        {
            throw new InvalidDataException("lifecycle.minimum must not exceed lifecycle.maximum.");
        }
        if (decayIntoElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(decayIntoElement.GetString()))
        {
            throw new InvalidDataException("lifecycle.decayInto must be a material ID.");
        }
        string decayIntoId = MaterialRegistry.NormalizeId(decayIntoElement.GetString()!);
        if (!MaterialIdPattern().IsMatch(decayIntoId) || decayIntoId == sourceId)
        {
            throw new InvalidDataException($"lifecycle.decayInto '{decayIntoElement.GetString()}' is invalid.");
        }
        float? extinction = null;
        if (extinctionElement.ValueKind != JsonValueKind.Undefined)
        {
            if (extinctionElement.ValueKind != JsonValueKind.Number || !extinctionElement.TryGetSingle(out float temperature) ||
                !float.IsFinite(temperature) || temperature < MaterialRegistry.MinimumInitialTemperature ||
                temperature > MaterialRegistry.MaximumInitialTemperature)
                throw new InvalidDataException("lifecycle.extinctionTemperature must be a finite supported temperature.");
            extinction = temperature;
        }
        return new MaterialLifecycleDefinition(minimum, maximum, decayIntoId, extinction);
    }

    private static float ParseLifetime(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float lifetime) ||
            !float.IsFinite(lifetime) || lifetime <= 0 || lifetime > MaterialRegistry.MaximumLifetime)
        {
            throw new InvalidDataException(
                $"lifecycle.{field} must be a finite number greater than 0 and at most {MaterialRegistry.MaximumLifetime}.");
        }
        return lifetime;
    }

    private static string ParseEmissionTarget(JsonElement value, string field, string sourceId)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"emissions.{field} должен быть строковым material ID.");
        }
        string id = MaterialRegistry.NormalizeId(value.GetString()!);
        if (!MaterialIdPattern().IsMatch(id) || id == sourceId)
        {
            throw new InvalidDataException($"emissions.{field} '{value.GetString()}' имеет недопустимый target ID.");
        }
        return id;
    }

    private static float ParseEmissionRate(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float rate) ||
            !float.IsFinite(rate) || rate <= 0 || rate > MaterialRegistry.MaximumCombustionBurnRate)
        {
            throw new InvalidDataException(
                $"emissions.{field} должна быть конечным числом больше 0 и не больше {MaterialRegistry.MaximumCombustionBurnRate}.");
        }
        return rate;
    }

    private static MaterialTransitionDefinitions? ParseTransitions(
        JsonElement value,
        string sourceId,
        MaterialSimulationKind sourceKind)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("thermal.transitions должен быть объектом.");
        }

        MaterialTransitionRule? below = null;
        MaterialTransitionRule? above = null;
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Дублирующее поле thermal.transitions '{property.Name}'.");
            }

            if (property.Name.Equals("below", StringComparison.OrdinalIgnoreCase))
            {
                below = ParseTransitionDirection(property.Value, "below", sourceId);
            }
            else if (property.Name.Equals("above", StringComparison.OrdinalIgnoreCase))
            {
                above = ParseTransitionDirection(property.Value, "above", sourceId);
            }
            else
            {
                throw new InvalidDataException(
                    $"Неизвестное поле thermal.transitions '{property.Name}'.");
            }
        }

        if (below is null && above is null)
        {
            throw new InvalidDataException(
                "thermal.transitions должен содержать below и/или above.");
        }
        if (sourceKind is MaterialSimulationKind.None or MaterialSimulationKind.Tool)
        {
            throw new InvalidDataException(
                $"Материал '{sourceId}' с kind '{sourceKind.ToString().ToLowerInvariant()}' не может иметь thermal.transitions.");
        }
        if (below is not null && above is not null && below.Temperature >= above.Temperature)
        {
            throw new InvalidDataException(
                "thermal.transitions.below.temperature должен быть меньше thermal.transitions.above.temperature.");
        }

        return new MaterialTransitionDefinitions(below, above);
    }

    private static MaterialTransitionRule ParseTransitionDirection(
        JsonElement value,
        string direction,
        string sourceId)
    {
        string fieldPath = $"thermal.transitions.{direction}";
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{fieldPath} должен быть объектом.");
        }

        JsonElement temperatureElement = default;
        JsonElement intoElement = default;
        JsonElement latentHeatElement = default;
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Дублирующее поле {fieldPath} '{property.Name}'.");
            }

            if (property.Name.Equals("temperature", StringComparison.OrdinalIgnoreCase))
            {
                temperatureElement = property.Value;
            }
            else if (property.Name.Equals("into", StringComparison.OrdinalIgnoreCase))
            {
                intoElement = property.Value;
            }
            else if (property.Name.Equals("latentHeat", StringComparison.OrdinalIgnoreCase))
            {
                latentHeatElement = property.Value;
            }
            else
            {
                throw new InvalidDataException(
                    $"Неизвестное поле {fieldPath} '{property.Name}'.");
            }
        }

        if (temperatureElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Поле {fieldPath}.temperature обязательно.");
        }
        if (intoElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Поле {fieldPath}.into обязательно.");
        }
        if (temperatureElement.ValueKind != JsonValueKind.Number ||
            !temperatureElement.TryGetSingle(out float temperature) ||
            !float.IsFinite(temperature) ||
            temperature < MaterialRegistry.MinimumInitialTemperature ||
            temperature > MaterialRegistry.MaximumInitialTemperature)
        {
            throw new InvalidDataException(
                $"{fieldPath}.temperature должна быть конечным числом от {MaterialRegistry.MinimumInitialTemperature} до {MaterialRegistry.MaximumInitialTemperature}.");
        }
        if (intoElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(intoElement.GetString()))
        {
            throw new InvalidDataException($"{fieldPath}.into должен быть строковым material ID.");
        }

        string targetId = MaterialRegistry.NormalizeId(intoElement.GetString()!);
        if (!MaterialIdPattern().IsMatch(targetId))
        {
            throw new InvalidDataException(
                $"{fieldPath}.into '{intoElement.GetString()}' должен иметь формат namespace:name.");
        }
        if (targetId == sourceId)
        {
            throw new InvalidDataException(
                $"Материал '{sourceId}' не может переходить сам в себя ({fieldPath}.into).");
        }

        float latentHeat = 0;
        if (latentHeatElement.ValueKind != JsonValueKind.Undefined &&
            (direction != "above" || latentHeatElement.ValueKind != JsonValueKind.Number ||
             !latentHeatElement.TryGetSingle(out latentHeat) || !float.IsFinite(latentHeat) ||
             latentHeat <= 0 || latentHeat > 1_000_000f))
        {
            throw new InvalidDataException(
                $"{fieldPath}.latentHeat допустим только для above и должен быть конечным числом больше 0.");
        }

        return new MaterialTransitionRule(temperature, targetId, latentHeat);
    }

    private static MaterialFlags ParseFlags(IEnumerable<string>? values, MaterialSimulationKind kind)
    {
        MaterialFlags flags = MaterialFlags.None;
        foreach (string value in values ?? [])
        {
            MaterialFlags flag = value.Trim().ToLowerInvariant() switch
            {
                "movable-solid" => MaterialFlags.MovableSolid,
                "density-body" => MaterialFlags.DensityBody,
                "flame" => MaterialFlags.Flame,
                "self-oxidizing" => MaterialFlags.SelfOxidizing,
                "progressive-ignition" => MaterialFlags.ProgressiveIgnition,
                "blocks-air" => MaterialFlags.BlocksAir,
                "smoke" => MaterialFlags.Smoke,
                "phase-enthalpy" => MaterialFlags.PhaseEnthalpy,
                "fusion-enthalpy" => MaterialFlags.FusionEnthalpy,
                "non-absorbable-liquid" => kind == MaterialSimulationKind.Liquid
                    ? MaterialFlags.NonAbsorbableLiquid
                    : throw new InvalidDataException("non-absorbable-liquid requires a liquid."),
                "liquid-convection" => MaterialFlags.LiquidConvection,
                _ => throw new InvalidDataException($"Неизвестный flag '{value}'.")
            };
            if ((flags & flag) != 0)
            {
                throw new InvalidDataException($"Дублирующий flag '{value}'.");
            }
            flags |= flag;
        }
        if ((flags & MaterialFlags.MovableSolid) != 0 && kind != MaterialSimulationKind.Solid)
        {
            throw new InvalidDataException("Flag 'movable-solid' разрешён только для kind 'solid'.");
        }
        if ((flags & MaterialFlags.DensityBody) != 0 &&
            (kind != MaterialSimulationKind.Solid || (flags & MaterialFlags.MovableSolid) == 0))
        {
            throw new InvalidDataException("Flag 'density-body' требует kind 'solid' и 'movable-solid'.");
        }
        if ((flags & MaterialFlags.SelfOxidizing) != 0 &&
            kind is not (MaterialSimulationKind.Solid or MaterialSimulationKind.Granular))
        {
            throw new InvalidDataException(
                "Flag 'self-oxidizing' разрешён только для kind 'solid' или 'granular'.");
        }
        if ((flags & MaterialFlags.BlocksAir) != 0 && kind != MaterialSimulationKind.Solid)
        {
            throw new InvalidDataException("Flag 'blocks-air' разрешён только для kind 'solid'.");
        }
        if ((flags & MaterialFlags.Smoke) != 0 && kind != MaterialSimulationKind.Gas)
        {
            throw new InvalidDataException("Flag 'smoke' разрешён только для kind 'gas'.");
        }
        return flags;
    }

    private static MaterialSimulationKind ParseKind(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "none" => MaterialSimulationKind.None,
            "granular" => MaterialSimulationKind.Granular,
            "solid" => MaterialSimulationKind.Solid,
            "tool" => MaterialSimulationKind.Tool,
            "liquid" => MaterialSimulationKind.Liquid,
            "gas" => MaterialSimulationKind.Gas,
            null or "" => throw new InvalidDataException("Поле kind обязательно."),
            _ => throw new InvalidDataException($"Неизвестный kind '{value}'.")
        };
    }

    private static string ParseName(JsonElement value, string id)
    {
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!.Trim();
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("ru", out JsonElement russian) &&
                russian.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(russian.GetString()))
            {
                return russian.GetString()!.Trim();
            }
            if (value.TryGetProperty("en", out JsonElement english) &&
                english.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(english.GetString()))
            {
                return english.GetString()!.Trim();
            }
        }

        return id[(id.IndexOf(':') + 1)..];
    }

    private static Color ParseColor(string value)
    {
        string hex = value.Trim();
        if (!hex.StartsWith('#') || hex.Length is not (7 or 9))
        {
            throw new FormatException($"Цвет '{value}' должен иметь формат #RRGGBB или #RRGGBBAA.");
        }

        byte red = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte green = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte blue = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte alpha = hex.Length == 9
            ? byte.Parse(hex.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : byte.MaxValue;
        return new Color(red, green, blue, alpha);
    }

    internal static void LogError(string path, string message)
    {
        Console.Error.WriteLine($"PHYXEL_MATERIAL_ERROR file=\"{path}\" message=\"{message}\"");
    }

    internal static void LogWarning(string path, string message)
    {
        Console.Error.WriteLine($"PHYXEL_MATERIAL_WARNING file=\"{path}\" message=\"{message}\"");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*:[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MaterialIdPattern();
}
