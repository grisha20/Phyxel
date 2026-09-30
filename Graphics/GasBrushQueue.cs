using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Graphics;

// Retain strokes sampled between physical ticks. Repeat only their endpoint
// on catch-up ticks; a low FPS must not refill an entire old mouse segment.
internal sealed class GasBrushQueue
{
    private readonly List<BrushDrawCommand> pending = [];
    private BrushDrawCommand? held;
    private uint strokeSeed;
    private uint strokeTick;
    private bool initialStamp;

    public bool Capture(BrushDrawCommand command)
    {
        bool first = held is null || held.Value.MaterialIndex != command.MaterialIndex ||
            held.Value.Reserved != command.Reserved;
        if (first)
        {
            strokeSeed = command.Seed;
            strokeTick = 0;
            initialStamp = true;
        }
        bool sameEndpoint = held is { } previousHeld && previousHeld.X == command.X &&
            previousHeld.Y == command.Y && previousHeld.EndX == command.EndX &&
            previousHeld.EndY == command.EndY && previousHeld.Radius == command.Radius;
        held = command;
        if (first) return true; // One immediate stamp, including a quick click.
        if (initialStamp && sameEndpoint) return false;
        if (pending.Count > 0)
        {
            BrushDrawCommand previous = pending[^1];
            if (previous.X == command.X && previous.Y == command.Y &&
                previous.EndX == command.EndX && previous.EndY == command.EndY &&
                previous.MaterialIndex == command.MaterialIndex && previous.Radius == command.Radius)
            {
                pending[^1] = command;
                return false;
            }
        }
        if (pending.Count < SimulationSettings.MaximumBrushCommands) pending.Add(command);
        return false;
    }

    public void Release() => held = null;

    public ReadOnlySpan<BrushDrawCommand> Tick()
    {
        strokeTick++;
        if (pending.Count == 0 && !initialStamp && held is { } endpoint)
        {
            if (endpoint.Shape == BrushCommandShape.Segment)
            {
                endpoint.X = endpoint.EndX;
                endpoint.Y = endpoint.EndY;
                endpoint.Shape = BrushCommandShape.Point;
            }
            pending.Add(endpoint);
        }
        initialStamp = false;
        for (int i = 0; i < pending.Count; i++)
        {
            BrushDrawCommand command = pending[i];
            command.Seed = strokeSeed ^ (strokeTick * 0x9e3779b9u);
            pending[i] = command;
        }
        return CollectionsMarshal.AsSpan(pending);
    }

    public void Consumed() => pending.Clear();

    public void Reset()
    {
        pending.Clear();
        held = null;
        strokeSeed = strokeTick = 0;
        initialStamp = false;
    }
}
