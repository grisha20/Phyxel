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
            ["core:steam"] = "steam.png",
            ["core:co2"] = "co2.png",
            ["core:ice"] = "ice.png",
            ["core:metal"] = "metal.png",
            ["core:stone"] = "stone.png",
            ["core:wood"] = "wood.png",
            ["core:fire"] = "fire.png",
            ["core:coal"] = "charcoal.png",
            ["core:stone_coal"] = "stone_coal.png"
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

        Console.WriteLine(
            $"PHYXEL_UI_PREVIEWS loaded={previews.Count} expected={PreviewFileNames.Count} directory={previewDirectory}");
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
        FallbackTexture.Dispose();
    }
}
