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
        Gas = Resolve(CoreMaterialIds.Gas);
        Fixture = Resolve(CoreMaterialIds.Fixture);
    }

    public uint Sand { get; }
    public uint Water { get; }
    public uint Metal { get; }
    public uint Stone { get; }
    public uint Eraser { get; }
    public uint Gas { get; }
    public uint Fixture { get; }
    public uint Resolve(string id) => registry.GetRequiredRuntimeIndex(id);
}
