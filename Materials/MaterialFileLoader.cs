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
        public MaterialThermalDocument? Thermal { get; set; }
        public JsonElement Combustion { get; set; }
        public JsonElement Emissions { get; set; }
        public JsonElement Lifecycle { get; set; }
        public MaterialUiDocument? Ui { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    }

    private sealed class MaterialPhysicsDocument
    {
        public float Density { get; set; } = 1f;
        public float Friction { get; set; }
        public float FlowRate { get; set; }
    }

    private sealed class MaterialThermalDocument
    {
        public float InitialTemperature { get; set; } = MaterialRegistry.DefaultInitialTemperature;
        public float Conductivity { get; set; } = MaterialRegistry.DefaultThermalConductivity;
        public float HeatCapacity { get; set; } = MaterialRegistry.DefaultHeatCapacity;
        public JsonElement Transitions { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    }

    private sealed class MaterialUiDocument
    {
        public int Order { get; set; }
        public bool Hidden { get; set; }
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
        MaterialThermalDocument thermal = document.Thermal ?? new MaterialThermalDocument();
        if (!float.IsFinite(physics.Density) || physics.Density < 0 ||
            physics.Density > MaterialRegistry.MaximumDensity ||
            !float.IsFinite(physics.Friction) || physics.Friction < 0 ||
            !float.IsFinite(physics.FlowRate) || physics.FlowRate < 0)
        {
            throw new InvalidDataException(
                $"Параметры physics должны быть конечными неотрицательными числами; density не должна превышать {MaterialRegistry.MaximumDensity}.");
        }
        if (thermal.UnknownFields is { Count: > 0 })
        {
            throw new InvalidDataException(
                $"Неизвестное поле thermal '{thermal.UnknownFields.Keys.OrderBy(key => key, StringComparer.Ordinal).First()}'.");
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

        MaterialFlags flags = ParseFlags(document.Flags, kind);
        MaterialCombustionDefinition? combustion = ParseCombustion(
            document.Combustion,
            id,
            kind);
        MaterialEmissionDefinition? emissions = ParseEmissions(document.Emissions, id, combustion);
        MaterialLifecycleDefinition? lifecycle = ParseLifecycle(document.Lifecycle, id, kind);
        if (combustion is not null && (flags & MaterialFlags.MovableSolid) != 0)
        {
            throw new InvalidDataException(
                $"Горение материала '{id}' с flag 'movable-solid' пока не поддерживается.");
        }
        if (combustion is not null && transitions is not null)
        {
            throw new InvalidDataException(
                $"Материал '{id}' не может одновременно иметь combustion и thermal.transitions в schema combustion v1.");
        }
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
                color),
            ui.Order,
            ui.Hidden)
        {
            PhaseTransitions = transitions,
            Combustion = combustion,
            Emissions = emissions,
            Lifecycle = lifecycle,
            SourcePath = path
        };
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
        if (sourceKind != MaterialSimulationKind.Solid)
        {
            throw new InvalidDataException(
                $"Материал '{sourceId}' с kind '{sourceKind.ToString().ToLowerInvariant()}' не может быть source combustion v1; требуется fixed solid.");
        }

        JsonElement ignitionElement = default;
        JsonElement burnRateElement = default;
        JsonElement heatPerMassElement = default;
        JsonElement maximumTemperatureElement = default;
        JsonElement burnedIntoElement = default;
        JsonElement spreadRateElement = default;
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Дублирующее поле combustion '{property.Name}'.");
            }

            if (property.Name.Equals("ignitionTemperature", StringComparison.OrdinalIgnoreCase))
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

        return new MaterialCombustionDefinition(
            ignitionTemperature,
            burnRate,
            heatPerMass,
            burnedIntoId,
            spreadRate,
            maximumTemperature);
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
        return new MaterialLifecycleDefinition(minimum, maximum, decayIntoId);
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
                "flame" => MaterialFlags.Flame,
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

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*:[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MaterialIdPattern();
}
