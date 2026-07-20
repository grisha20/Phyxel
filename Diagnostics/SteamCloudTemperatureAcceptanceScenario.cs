using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class SteamCloudTemperatureAcceptanceScenario
{
    internal const int Left = 20;
    internal const int Top = 20;
    internal const int Right = 459;
    internal const int Bottom = 255;
    internal const int SourceX = 240;
    internal const int SourceY = 214;
    internal const int BatchRadius = 14;

    public static SimulationWorldSnapshot? CreateInitialWorld(
        AcceptanceScenarioMode mode,
        int width,
        int height,
        MaterialRegistry materials)
    {
        if (mode != AcceptanceScenarioMode.SteamCloudTemperature)
        {
            return null;
        }
        if (width < 480 || height < 270)
        {
            throw new InvalidOperationException(
                "steam_cloud_temperature requires at least 480x270 cells.");
        }

        byte[] bytes = new byte[checked(width * height * Marshal.SizeOf<GridCell>())];
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(bytes);
        uint fixture = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        uint steam = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam);
        Fill(cells, width, Left, Top, Right, Top + 3, fixture, 20);
        Fill(cells, width, Left, Bottom - 3, Right, Bottom, fixture, 20);
        Fill(cells, width, Left, Top, Left + 3, Bottom, fixture, 20);
        Fill(cells, width, Right - 3, Top, Right, Bottom, fixture, 20);
        FillDisk(cells, width, SourceX, SourceY, BatchRadius, steam,
            materials[CoreMaterialIds.Steam].Properties.InitialTemperature);
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    public static IReadOnlyList<BrushDrawCommand> CreateCommands(
        uint frame,
        MaterialRegistry materials)
    {
        uint secondBatchFrame = FrameAtSeconds(1.5);
        uint thirdBatchFrame = FrameAtSeconds(2.5);
        if (frame != secondBatchFrame && frame != thirdBatchFrame)
        {
            return [];
        }
        return
        [
            new BrushDrawCommand
            {
                X = frame == secondBatchFrame ? SourceX - 92 : SourceX + 92,
                Y = SourceY,
                MaterialIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Steam),
                Radius = BatchRadius,
                Density = 1,
                Mode = BrushCommandMode.Material,
                Seed = 31000u + frame
            }
        ];
    }

    public static uint PauseFrames => FrameAtSeconds(0.5);

    private static uint FrameAtSeconds(double seconds)
    {
        int fps = int.TryParse(
            Environment.GetEnvironmentVariable("PHYXEL_ACCEPTANCE_TARGET_FPS"),
            out int requested) && requested is >= 1 and <= 1000
                ? requested
                : 60;
        return checked((uint)Math.Round(seconds * fps));
    }

    private static void FillDisk(
        Span<GridCell> cells,
        int width,
        int centerX,
        int centerY,
        int radius,
        uint material,
        float temperature)
    {
        int radiusSquared = radius * radius;
        for (int y = centerY - radius; y <= centerY + radius; y++)
        {
            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                int dx = x - centerX;
                int dy = y - centerY;
                if (dx * dx + dy * dy <= radiusSquared)
                {
                    Set(cells, width, x, y, material, temperature);
                }
            }
        }
    }

    private static void Fill(
        Span<GridCell> cells,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        uint material,
        float temperature)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                Set(cells, width, x, y, material, temperature);
            }
        }
    }

    private static void Set(
        Span<GridCell> cells,
        int width,
        int x,
        int y,
        uint material,
        float temperature) =>
        cells[y * width + x] = new GridCell
        {
            MaterialIndex = material,
            Mass = 1,
            IsActive = 1,
            Temperature = temperature
        };
}
