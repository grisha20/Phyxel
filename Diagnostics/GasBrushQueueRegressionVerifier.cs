using System;
using Phyxel.Graphics;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class GasBrushQueueRegressionVerifier
{
    public static int Run()
    {
        foreach (int fps in new[] { 30, 60, 100 })
        {
            GasBrushQueue queue = new();
            double elapsed = 0;
            int stamps = 0;
            for (int frame = 0; frame < fps; frame++)
            {
                BrushDrawCommand command = Point((uint)frame + 1);
                if (queue.Capture(command)) stamps++;
                elapsed += 1d / fps;
                while (elapsed + 1e-9 >= 1d / 60)
                {
                    elapsed -= 1d / 60;
                    stamps += queue.Tick().Length;
                    queue.Consumed();
                }
            }
            if (stamps != 60) throw new InvalidOperationException($"gas brush fps={fps} stamps={stamps}, expected=60");
        }
        GasBrushQueue stroke = new();
        if (!stroke.Capture(Point(1))) throw new InvalidOperationException("quick click lost");
        stroke.Release();
        if (!stroke.Tick().IsEmpty) throw new InvalidOperationException("released brush repeats");
        stroke.Consumed();
        stroke.Capture(Point(2));
        stroke.Tick();
        stroke.Consumed();
        BrushDrawCommand segment = Point(3);
        segment.Shape = BrushCommandShape.Segment;
        segment.EndX = 30;
        segment.EndY = 40;
        stroke.Capture(segment);
        if (stroke.Tick()[0].Shape != BrushCommandShape.Segment) throw new InvalidOperationException("pending stroke lost");
        stroke.Consumed();
        BrushDrawCommand repeated = stroke.Tick()[0];
        if (repeated.Shape != BrushCommandShape.Point || repeated.X != 30 || repeated.Y != 40)
            throw new InvalidOperationException("catch-up replays old segment");
        stroke.Reset();
        if (!stroke.Tick().IsEmpty) throw new InvalidOperationException("reset retains stroke");
        Console.WriteLine("PHYXEL_GAS_BRUSH_REGRESSION_SUCCESS fps=30,60,100 stamps=60 quickClick=1 endpointRepeat=1 reset=1");
        return 0;
    }

    private static BrushDrawCommand Point(uint seed) => new()
    {
        X = 10, Y = 20, Radius = 8, MaterialIndex = 1,
        Mode = BrushCommandMode.Material, Shape = BrushCommandShape.Point, Seed = seed
    };
}
