using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

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
            ["core:heater"] = "metal.png",
            ["core:cooler"] = "metal.png",
            ["core:stone"] = "stone.png",
            ["core:wood"] = "wood.png",
            ["core:fire"] = "fire.png",
            ["core:coal"] = "charcoal.png",
            ["core:stone_coal"] = "stone_coal.png",
            ["core:gunpowder"] = "gunpowder.png"
        };

    private readonly Dictionary<string, Texture2D> previews =
        new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

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

        previews["core:steel"] = CreateAlloyPreview(graphicsDevice, false);
        previews["core:cast_iron"] = CreateAlloyPreview(graphicsDevice, true);
        Console.WriteLine(
            $"PHYXEL_UI_PREVIEWS loaded={previews.Count} expected={PreviewFileNames.Count + 2} directory={previewDirectory}");
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

    // Native textures: brushed steel and a dark, granular cast surface.
    private static Texture2D CreateAlloyPreview(GraphicsDevice device, bool castIron)
    {
        const int width = 256, height = 160;
        var texture = new Texture2D(device, width, height, false, SurfaceFormat.Color);
        var pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            uint hash = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
            hash ^= hash >> 13; hash *= 1274126177; hash ^= hash >> 16;
            int grain = (int)(hash & 31) - 15;
            float shine = MathF.Max(0, 1 - MathF.Abs((x + .7f * y) / width - .65f));
            int value = castIron ? 75 + grain + (int)(shine * 15) :
                125 + grain / 3 + (y % 3 == 0 ? -8 : 0) + (int)(shine * 78);
            pixels[y * width + x] = new Color(value, value + (castIron ? 2 : 5), value + (castIron ? 4 : 12));
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
        FallbackTexture.Dispose();
    }
}
