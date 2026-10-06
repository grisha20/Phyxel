using System;
using System.Collections.Generic;
using Phyxel.Serialization;

namespace Phyxel.Input;

// Snapshots own their byte arrays. Only user edits enter history; physics ticks do not.
internal sealed class WorldEditHistory(long maximumBytes = 256L * 1024 * 1024, int maximumEntries = 32)
{
    private readonly LinkedList<SimulationWorldSnapshot> undo = new();
    private readonly LinkedList<SimulationWorldSnapshot> redo = new();
    public long StoredBytes { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public long MaximumBytes => maximumBytes;

    public static long Size(SimulationWorldSnapshot s) => (long)s.Grid.Length +
        (s.Air?.Length ?? 0) + (s.GasMotion?.Length ?? 0) + (s.Oxidizer?.Length ?? 0) +
        (s.AirThermal?.Length ?? 0) + (s.ReactionPending?.Length ?? 0) +
        (s.ReactionPulse?.Length ?? 0) + (s.Filters?.Length ?? 0);

    public void Clear()
    {
        undo.Clear(); redo.Clear(); StoredBytes = 0;
    }

    public bool Record(SimulationWorldSnapshot before)
    {
        foreach (var s in redo) StoredBytes -= Size(s);
        redo.Clear();
        if (Size(before) > maximumBytes) { Clear(); return false; }
        Add(undo, before);
        return true;
    }

    public bool Restore(bool forward, SimulationWorldSnapshot current, out SimulationWorldSnapshot? restored)
    {
        var source = forward ? redo : undo;
        var destination = forward ? undo : redo;
        restored = null;
        if (source.Last is null) return false;
        if (Size(current) > maximumBytes) { Clear(); return false; }
        restored = source.Last.Value;
        StoredBytes -= Size(restored);
        source.RemoveLast();
        Add(destination, current);
        return true;
    }

    private void Add(LinkedList<SimulationWorldSnapshot> list, SimulationWorldSnapshot snapshot)
    {
        list.AddLast(snapshot); StoredBytes += Size(snapshot);
        while (StoredBytes > maximumBytes || undo.Count + redo.Count > maximumEntries)
        {
            // Prefer dropping the farthest entry on the other side of the cursor.
            var victims = list == undo ? redo : undo;
            if (victims.Count == 0) victims = list;
            StoredBytes -= Size(victims.First!.Value);
            victims.RemoveFirst();
        }
    }
}
