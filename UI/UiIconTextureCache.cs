using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace Phyxel.UI;

public sealed class UiIconTextureCache : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> FileNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["save"] = "save.png",
            ["load"] = "load.png",
            ["pause"] = "pause.png",
            ["play"] = "play.png",
            ["settings"] = "settings.png",
            ["brush"] = "brush.png",
            ["eraser"] = "eraser.png",
            ["temperature"] = "temperature.png",
            ["line"] = "line.png",
            ["rectangle"] = "rectangle.png",
            ["circle"] = "circle.png",
            ["fill"] = "fill.png",
            ["eyedropper"] = "eyedropper.png",
            ["pan"] = "pan.png",
            ["category_powders"] = "category_powders.png",
            ["category_liquids"] = "category_liquids.png",
            ["category_gases"] = "category_gases.png",
            ["category_solids"] = "category_solids.png",
            ["category_combustion"] = "category_combustion.png",
            ["category_tools"] = "category_tools.png",
            ["reset"] = "reset.png",
            ["clear"] = "clear.png"
        };

    private readonly Dictionary<string, Texture2D> textures =
        new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public UiIconTextureCache(GraphicsDevice graphicsDevice, string iconDirectory)
    {
        foreach ((string key, string fileName) in FileNames)
        {
            string path = Path.Combine(iconDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                textures[key] = Texture2D.FromStream(graphicsDevice, stream);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"PHYXEL_UI_ICON_FAILED key={key} file={path} error={exception.Message}");
            }
        }

        Console.WriteLine($"PHYXEL_UI_ICONS loaded={textures.Count} expected={FileNames.Count} directory={iconDirectory}");
    }

    public bool TryGet(string key, out Texture2D texture) => textures.TryGetValue(key, out texture!);

    internal static string? GetFileName(string key) =>
        FileNames.TryGetValue(key, out string? fileName) ? fileName : null;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (Texture2D texture in textures.Values)
        {
            texture.Dispose();
        }
        textures.Clear();
    }
}
