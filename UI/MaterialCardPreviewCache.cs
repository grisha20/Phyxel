using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;

namespace Phyxel.UI;

public sealed class MaterialCardPreviewCache : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> PreviewFileNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["core:sand"] = "sand.png",
            ["core:water"] = "water.png",
            ["core:oil"] = "oil.png",
            ["core:steam"] = "steam.png",
            ["core:co2"] = "co2.png",
            ["core:ice"] = "ice.png",
            ["core:metal"] = "metal.png",
            ["core:steel"] = "steel.png",
            ["core:cast_iron"] = "cast_iron.png",
            ["core:copper"] = "copper.png",
            ["core:fixture"] = "fixture.png",
            ["core:heater"] = "heater.png",
            ["core:cooler"] = "cooler.png",
            ["core:stone"] = "stone.png",
            ["core:wood"] = "wood.png",
            ["core:fire"] = "fire.png",
            ["core:fuse"] = "fuse.png",
            ["core:coal"] = "charcoal.png",
            ["core:stone_coal"] = "stone_coal.png",
            ["core:gunpowder"] = "gunpowder.png"
        };

    private readonly Dictionary<string, Texture2D> previews =
        new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private readonly Dictionary<FilterSelection, Texture2D> filterPreviews = new();
    internal static string GetFilterPreviewFileName(FilterSelection brush) => brush switch
    {
        FilterSelection.Steam => "steam.png",
        FilterSelection.Water => "water.png",
        FilterSelection.Oil => "oil.png",
        FilterSelection.Gases => "gases.png",
        FilterSelection.Liquids => "liquids.png",
        FilterSelection.Powders => "powders.png",
        FilterSelection.SelectedMaterial => "selected_material.png",
        FilterSelection.Wall => "wall.png",
        FilterSelection.AirOnly => "air_only.png",
        FilterSelection.NoAir => "no_air.png",
        _ => throw new ArgumentOutOfRangeException(nameof(brush))
    };
    internal bool TryGetFilterPreview(FilterSelection brush, out Texture2D preview) =>
        filterPreviews.TryGetValue(brush, out preview!);

    public MaterialCardPreviewCache(GraphicsDevice graphicsDevice, string previewDirectory)
    {
        FallbackTexture = CreateFallbackTexture(graphicsDevice);

        foreach ((string materialId, string fileName) in PreviewFileNames)
        {
            string path = Path.Combine(previewDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                previews[materialId] = Texture2D.FromStream(graphicsDevice, stream);
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"PHYXEL_UI_PREVIEW_FAILED material={materialId} file={path} error={exception.Message}");
            }
        }

        Console.WriteLine(
            $"PHYXEL_UI_PREVIEWS loaded={previews.Count} expected={PreviewFileNames.Count} directory={previewDirectory}");
        string filterDirectory = Path.Combine(Path.GetDirectoryName(previewDirectory)!, "FilterCards");
        foreach (FilterSelection brush in Enum.GetValues<FilterSelection>())
        {
            string path = Path.Combine(filterDirectory, GetFilterPreviewFileName(brush));
            if (!File.Exists(path)) continue;
            try
            {
                using FileStream stream = File.OpenRead(path);
                filterPreviews[brush] = Texture2D.FromStream(graphicsDevice, stream);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_UI_FILTER_PREVIEW_FAILED brush={brush} file={path} error={exception.Message}");
            }
        }
        Console.WriteLine($"PHYXEL_UI_FILTER_PREVIEWS loaded={filterPreviews.Count} expected={Enum.GetValues<FilterSelection>().Length}");
    }

    public Texture2D FallbackTexture { get; }

    public bool TryGetPreview(string materialId, out Texture2D preview) =>
        previews.TryGetValue(materialId, out preview!);

    public static string? GetPreviewFileName(string materialId) =>
        PreviewFileNames.TryGetValue(materialId, out string? fileName) ? fileName : null;

    private static Texture2D CreateFallbackTexture(GraphicsDevice graphicsDevice)
    {
        const int width = 48;
        const int height = 32;
        Texture2D texture = new(graphicsDevice, width, height, false, SurfaceFormat.Color);
        Color[] pixels = new Color[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool checker = ((x / 8) + (y / 8)) % 2 == 0;
                bool diagonal = Math.Abs((x - y) % 16) <= 1;
                byte alpha = diagonal ? (byte)115 : checker ? (byte)54 : (byte)28;
                // SpriteBatch uses premultiplied alpha, so RGB follows alpha here.
                pixels[y * width + x] = new Color(alpha, alpha, alpha, alpha);
            }
        }

        texture.SetData(pixels);
        return texture;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (Texture2D preview in previews.Values)
        {
            preview.Dispose();
        }
        previews.Clear();
        foreach (Texture2D preview in filterPreviews.Values) preview.Dispose();
        filterPreviews.Clear();
        FallbackTexture.Dispose();
    }
}
