using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Phyxel.Materials;
namespace Phyxel.Core;

// Rules for a future barrier overlay. A policy does not occupy a GridCell:
// permitted particles and the barrier must be able to coexist at one position.
public sealed record SelectiveBarrierPolicy(
    IReadOnlySet<MaterialSimulationKind> AllowedKinds,
    IReadOnlySet<string> AllowedIds,
    bool BlockFlame,
    bool AllowsAmbientAir,
    bool AllowsHeat)
{
    public bool Allows(MaterialDefinition material) =>
        !(BlockFlame && (material.Properties.Flags & (uint)MaterialFlags.Flame)!=0) &&
        (AllowedKinds.Contains((MaterialSimulationKind)material.Properties.SimulationKind) || AllowedIds.Contains(material.Id));

    public void Validate(MaterialRegistry registry)
    {
        foreach(var kind in AllowedKinds)
            if(kind is not (MaterialSimulationKind.Gas or MaterialSimulationKind.Liquid or MaterialSimulationKind.Granular))
                throw new ArgumentException("Barrier permeability must name a moving particle kind.");
        foreach(string id in AllowedIds)
            if(id!=MaterialRegistry.NormalizeId(id) || !registry.TryGet(id,out var material) || (MaterialSimulationKind)material.Properties.SimulationKind is not
               (MaterialSimulationKind.Granular or MaterialSimulationKind.Liquid or MaterialSimulationKind.Gas))
                throw new ArgumentException("Unknown or immobile particle in barrier policy: "+id);
    }

    public static SelectiveBarrierPolicy GasOnly { get; } = new(
        new[]{MaterialSimulationKind.Gas}.ToFrozenSet(), Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal),false,true,true);
    public static SelectiveBarrierPolicy WaterOnly { get; } = new(
        Array.Empty<MaterialSimulationKind>().ToFrozenSet(),new[]{CoreMaterialIds.Water}.ToFrozenSet(StringComparer.Ordinal),true,false,true);
}
