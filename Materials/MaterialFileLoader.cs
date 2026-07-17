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
        public string? Color { get; set; }
        public MaterialPhysicsDocument? Physics { get; set; }
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

    public static IReadOnlyList<MaterialDefinition> Load(
        string directory,
        IReadOnlySet<string> reservedIds,
        int maximumCount)
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
                MaterialDefinition definition = Parse(path);
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
        if (!float.IsFinite(physics.Density) || physics.Density < 0 ||
            !float.IsFinite(physics.Friction) || physics.Friction < 0 ||
            !float.IsFinite(physics.FlowRate) || physics.FlowRate < 0)
        {
            throw new InvalidDataException("Параметры physics должны быть конечными неотрицательными числами.");
        }

        string name = ParseName(document.Name, id);
        MaterialUiDocument ui = document.Ui ?? new MaterialUiDocument();
        return new MaterialDefinition(
            id,
            0,
            name,
            color,
            MaterialRegistry.CreateProperties(0, kind, physics.Density, physics.Friction, physics.FlowRate, color),
            ui.Order,
            ui.Hidden);
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

    private static void LogError(string path, string message)
    {
        Console.Error.WriteLine($"PHYXEL_MATERIAL_ERROR file=\"{path}\" message=\"{message}\"");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*:[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MaterialIdPattern();
}
