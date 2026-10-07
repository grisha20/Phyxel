using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Input;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

internal static class TemperatureSensorOverlay
{
    internal static bool Edit(RawInputSnapshot input, Rectangle worldBounds, SimulationSettings settings)
    {
        var cell = GpuTemperatureProbe.MapPointerToCell(input.MousePosition, worldBounds, settings.Width, settings.Height);
        if (cell is null) return false;
        if (input.RightPressed)
        {
            if (input.ShiftDown)
            {
                bool any = settings.TemperatureSensors.Count > 0;
                settings.TemperatureSensors.Clear(); return any;
            }
            int nearest = -1; float distance = 22 * 22;
            for (int i = 0; i < settings.TemperatureSensors.Count; i++)
            {
                var point = settings.TemperatureSensors[i];
                var screen = SandboxUiCoordinator.GridToScreen(point.X, point.Y, worldBounds, settings);
                float d = Vector2.DistanceSquared(screen, input.MousePosition.ToVector2());
                if (d <= distance) { nearest = i; distance = d; }
            }
            if (nearest < 0) return false;
            settings.TemperatureSensors.RemoveAt(nearest); return true;
        }
        if (!input.LeftPressed || settings.TemperatureSensors.Count >= TemperatureSensorPosition.MaximumCount) return false;
        var position = new TemperatureSensorPosition(cell.Value.X, cell.Value.Y);
        if (settings.TemperatureSensors.Contains(position)) return false;
        settings.TemperatureSensors.Add(position); return true;
    }

    internal static string Label(int number, TemperatureProbeResult? sample, MaterialRegistry registry)
    {
        string name = "Ожидание", value = "—";
        if (sample is { } result)
        {
            name = result.IsActive == 2 ? "Воздух" : result.IsActive == 1 && result.MaterialIndex <= ushort.MaxValue &&
                registry.TryGet((ushort)result.MaterialIndex, out var material) ? material.Name : "Нет данных";
            if (result.IsActive != 0 && float.IsFinite(result.Temperature))
                value = result.Temperature.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " °C";
        }
        return $"{number}: {name}\n{value}";
    }

    internal static void Draw(SpriteBatch batch, SpriteFont font, Texture2D pixel, Rectangle canvas, Rectangle worldBounds,
        SimulationSettings settings, MaterialRegistry registry,
        IReadOnlyDictionary<TemperatureSensorPosition, TemperatureProbeResult> readings)
    {
        List<Rectangle> labels = [];
        for (int i = 0; i < settings.TemperatureSensors.Count; i++)
        {
            var point = settings.TemperatureSensors[i];
            Vector2 screen = SandboxUiCoordinator.GridToScreen(point.X, point.Y, worldBounds, settings);
            if (!canvas.Contains((int)screen.X, (int)screen.Y)) continue;
            string text = Label(i + 1, readings.TryGetValue(point, out var sample) ? sample : null, registry);
            const float scale = .72f;
            Vector2 size = font.MeasureString(text) * scale;
            int w = Math.Min(canvas.Width, (int)Math.Ceiling(size.X) + 14), h = (int)Math.Ceiling(size.Y) + 10;
            int x = (int)screen.X + 12;
            if (x + w > canvas.Right) x = (int)screen.X - w - 12;
            x = Math.Clamp(x, canvas.X, canvas.Right - w);
            int y = Math.Clamp((int)screen.Y - h / 2, canvas.Y, Math.Max(canvas.Y, canvas.Bottom - h));
            Rectangle box = new(x, y, w, h);
            // Avoid the common case of adjacent measurements hiding one another.
            for (int attempt = 0; attempt < labels.Count; attempt++)
            {
                bool collision = false;
                foreach (var other in labels) if (box.Intersects(other)) { collision = true; break; }
                if (!collision) break;
                box.Y = box.Bottom + h + 3 <= canvas.Bottom ? box.Bottom + 3 : Math.Max(canvas.Y, box.Y - h - 3);
            }
            labels.Add(box);
            Vector2 edge = new(Math.Clamp(screen.X, box.Left, box.Right), Math.Clamp(screen.Y, box.Top, box.Bottom));
            Vector2 line = edge - screen;
            Color accent = new(135, 225, 255);
            batch.Draw(pixel, screen, null, accent, MathF.Atan2(line.Y, line.X), Vector2.Zero,
                new Vector2(Math.Max(1, line.Length()), 1), SpriteEffects.None, 0);
            batch.Draw(pixel, box, new Color(7, 12, 18, 235));
            UiIconRenderer.DrawStrokedRectangle(batch, pixel, box, 1, accent * .7f);
            batch.DrawString(font, text, new Vector2(box.X + 7, box.Y + 5), Color.White,
                0, Vector2.Zero, scale, SpriteEffects.None, 0);
            batch.Draw(pixel, new Rectangle((int)screen.X - 4, (int)screen.Y - 4, 9, 9), Color.Black);
            batch.Draw(pixel, new Rectangle((int)screen.X - 2, (int)screen.Y - 2, 5, 5), accent);
        }
    }
}
