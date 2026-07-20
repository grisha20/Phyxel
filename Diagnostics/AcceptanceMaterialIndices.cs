using Phyxel.Materials;

namespace Phyxel.Diagnostics;

internal sealed class AcceptanceMaterialIndices
{
    private readonly MaterialRegistry registry;

    public AcceptanceMaterialIndices(MaterialRegistry registry)
    {
        this.registry = registry;
        Sand = Resolve(CoreMaterialIds.Sand);
        Water = Resolve(CoreMaterialIds.Water);
        Metal = Resolve(CoreMaterialIds.Metal);
        Stone = Resolve(CoreMaterialIds.Stone);
        Eraser = Resolve(CoreMaterialIds.Eraser);
        Gas = Resolve(CoreMaterialIds.Co2);
        Fixture = Resolve(CoreMaterialIds.Fixture);
        Wood = Resolve(CoreMaterialIds.Wood);
        Coal = Resolve(CoreMaterialIds.Coal);
        Fire = Resolve(CoreMaterialIds.Fire);
    }

    public uint Sand { get; }
    public uint Water { get; }
    public uint Metal { get; }
    public uint Stone { get; }
    public uint Eraser { get; }
    public uint Gas { get; }
    public uint Fixture { get; }
    public uint Wood { get; }
    public uint Coal { get; }
    public uint Fire { get; }
    public uint Resolve(string id) => registry.GetRequiredRuntimeIndex(id);
}
