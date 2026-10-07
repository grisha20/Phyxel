using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Phyxel.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct TemperatureSensorPosition(int X, int Y)
{
    public const int MaximumCount = 32;

    public static List<TemperatureSensorPosition> Normalize(
        IEnumerable<TemperatureSensorPosition>? points, int width, int height)
    {
        List<TemperatureSensorPosition> result = [];
        if (points is null) return result;
        foreach (var point in points)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height || result.Contains(point)) continue;
            result.Add(point);
            if (result.Count == MaximumCount) break;
        }
        return result;
    }
}
